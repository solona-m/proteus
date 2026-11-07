using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>
/// #84: a binding route's <c>group</c>/<c>option</c> resolved against the mod's own Penumbra groups, so a misspelt or
/// wrong-case name is answered instead of reading as an empty option (whose null rows made a "live == stored" check pass
/// vacuously). An exact name wins; otherwise one case-insensitive match is taken in the mod's own spelling.
/// </summary>
internal static class HarnessOptionLookup
{
    /// <summary>The resolved names in the mod's spelling, or the refusal (status, code, message).</summary>
    internal readonly record struct Result(string? Group, string? Option, int Status, string? Code, string? Message)
    {
        public bool Ok => Code == null;
        public static Result Fail(int status, string code, string message) => new(null, null, status, code, message);
    }

    /// <summary>
    /// Resolves <paramref name="group"/> then <paramref name="option"/> within it. <paramref name="groups"/> null means
    /// the mod's groups could not be read (409 <c>mod-unreadable</c>); a mod with no groups has no such option (404).
    /// </summary>
    public static Result Resolve(IReadOnlyList<(string Name, IReadOnlyList<string> Options)>? groups, string group, string option)
    {
        if (groups == null)
            return Result.Fail(409, "mod-unreadable", "the mod's option groups could not be read");
        var g = Pick(groups.Select(x => x.Name).ToList(), group, "group");
        if (g.Error is { } ge) return ge;
        var options = groups.First(x => x.Name == g.Name).Options;
        var o = Pick(options.ToList(), option, $"option in group {g.Name}");
        if (o.Error is { } oe) return oe;
        return new Result(g.Name, o.Name, 200, null, null);
    }

    private static (string? Name, Result? Error) Pick(List<string> names, string wanted, string what)
    {
        if (names.Contains(wanted, StringComparer.Ordinal)) return (wanted, null);
        var loose = names.Where(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToList();
        return loose.Count switch
        {
            1 => (loose[0], null),
            0 => (null, Result.Fail(404, "option-not-found", $"the mod has no {what} '{wanted}'")),
            _ => (null, Result.Fail(400, "invalid-field", $"'{wanted}' matches more than one {what} when case is ignored ({string.Join(", ", loose)}); give the exact name")),
        };
    }

    /// <summary>
    /// The mod's groups with their option names, in either manifest format: <c>meta.json</c>'s <c>Groups</c>, else the
    /// legacy <c>group_*.json</c> files. Null when unreadable (no <c>meta.json</c>, or a malformed group file).
    /// Disk I/O: call off the framework thread.
    /// </summary>
    public static List<(string Name, IReadOnlyList<string> Options)>? ReadGroups(string modRoot)
    {
        if (PenumbraModMeta.TryReadGroups(modRoot) is { } v4)
            return v4.Select(g => (g.Name, (IReadOnlyList<string>)PenumbraModMeta.ReadOptionNames(g.Group))).ToList();
        try
        {
            if (!File.Exists(Path.Combine(modRoot, PenumbraModMeta.MetaFile))) return null;
            var result = new List<(string, IReadOnlyList<string>)>();
            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name)
                    result.Add((name, PenumbraModMeta.ReadOptionNames(doc.RootElement)));
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
