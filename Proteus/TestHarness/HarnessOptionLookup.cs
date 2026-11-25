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
        var o = Pick(options.ToList(), option, "option", g.Name);
        if (o.Error is { } oe) return oe;
        return new Result(g.Name, o.Name, 200, null, null);
    }

    private static (string? Name, Result? Error) Pick(List<string> names, string wanted, string what, string? inGroup = null)
    {
        string where = inGroup == null ? "" : $" in group {inGroup}";
        if (names.Contains(wanted, StringComparer.Ordinal)) return (wanted, null);
        var loose = names.Where(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).ToList();
        return loose.Count switch
        {
            1 => (loose[0], null),
            0 => (null, Result.Fail(404, "option-not-found", $"the mod has no {what} '{wanted}'{where}")),
            _ => (null, Result.Fail(400, "invalid-field", $"'{wanted}' matches more than one {what}{where} when case is ignored ({string.Join(", ", loose)}); give the exact name")),
        };
    }

    /// <summary>A mod's option group: its name, whether it takes any number of options (<c>Type</c> Multi, Imc or Combining;
    /// every other type, or none, is Penumbra's Single: exactly one), and its option names.</summary>
    internal sealed record ModGroup(string Name, bool Multi, IReadOnlyList<string> Options);

    /// <summary>A hold's selection resolved in the mod's spelling (group → options), or the refusal.</summary>
    internal readonly record struct SelectionResult(Dictionary<string, List<string>>? Selection, int Status, string? Code,
                                                    string? Message)
    {
        public bool Ok => Code == null;
        public static SelectionResult Fail(int status, string code, string message) => new(null, status, code, message);
    }

    /// <summary>
    /// The groups and options a mod-settings hold asks for, resolved against the mod's own groups: names as
    /// <see cref="Resolve"/> takes them (exact, else one case-insensitive match, in the mod's spelling); a Single group
    /// takes exactly one option, a Multi/Imc/Combining group any number; no option twice, no group named twice.
    /// <paramref name="groups"/> null: 409 <c>mod-unreadable</c>. An unknown group's 404 lists the mod's groups, an unknown
    /// option's lists its group's options. The messages carry the mod's names: the caller scrubs them.
    /// </summary>
    public static SelectionResult ResolveSelection(IReadOnlyList<ModGroup>? groups,
                                                   IReadOnlyList<(string Group, IReadOnlyList<string> Options)> asked)
    {
        if (groups == null)
            return SelectionResult.Fail(409, "mod-unreadable", "the mod's option groups could not be read");
        var selection = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (groupName, options) in asked)
        {
            var g = Pick(groups.Select(x => x.Name).ToList(), groupName, "group");
            if (g.Error is { } ge)
                return SelectionResult.Fail(ge.Status, ge.Code!, ge.Code == "option-not-found"
                    ? $"{ge.Message}; its groups: {(groups.Count == 0 ? "none" : string.Join(", ", groups.Select(x => x.Name)))}"
                    : ge.Message!);
            if (selection.ContainsKey(g.Name!))
                return SelectionResult.Fail(400, "invalid-field", $"group {g.Name} is named more than once");
            var group = groups.First(x => x.Name == g.Name);
            var chosen = new List<string>();
            foreach (var option in options)
            {
                var o = Pick(group.Options.ToList(), option, "option", group.Name);
                if (o.Error is { } oe)
                    return SelectionResult.Fail(oe.Status, oe.Code!, oe.Code == "option-not-found"
                        ? $"{oe.Message}; its options: {string.Join(", ", group.Options)}"
                        : oe.Message!);
                if (chosen.Contains(o.Name!, StringComparer.Ordinal))
                    return SelectionResult.Fail(400, "invalid-field", $"option {o.Name} of group {group.Name} is given more than once");
                chosen.Add(o.Name!);
            }
            if (!group.Multi && chosen.Count != 1)
                return SelectionResult.Fail(400, "invalid-field", $"group {group.Name} takes exactly one option");
            selection[group.Name] = chosen;
        }
        return new SelectionResult(selection, 200, null, null);
    }

    /// <summary>Whether a group <c>Type</c> takes any number of options (Multi, Imc, Combining).</summary>
    private static bool IsMultiType(string type)
        => type.Equals("Multi", StringComparison.OrdinalIgnoreCase) || type.Equals("Imc", StringComparison.OrdinalIgnoreCase)
           || type.Equals("Combining", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The mod's groups with their option names (see <see cref="ReadModGroups"/>). Null when unreadable.
    /// Disk I/O: call off the framework thread.
    /// </summary>
    public static List<(string Name, IReadOnlyList<string> Options)>? ReadGroups(string modRoot)
        => ReadModGroups(modRoot)?.Select(g => (g.Name, g.Options)).ToList();

    /// <summary>
    /// The mod's groups with their kind and option names, in either manifest format: <c>meta.json</c>'s <c>Groups</c>,
    /// else the legacy <c>group_*.json</c> files. Null when unreadable (no <c>meta.json</c>, or a malformed group file).
    /// Disk I/O: call off the framework thread.
    /// </summary>
    public static List<ModGroup>? ReadModGroups(string modRoot)
    {
        try
        {
            var meta = Path.Combine(modRoot, PenumbraModMeta.MetaFile);
            if (!File.Exists(meta)) return null;
            // Read here rather than through TryReadGroups, whose null means both "no Groups array" (the legacy layout)
            // and "unparsable": a corrupt meta.json is unreadable (409), never an empty mod (404).
            using (var doc = JsonDocument.Parse(File.ReadAllText(meta)))
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    return groups.EnumerateArray()
                                 .Where(g => g.ValueKind == JsonValueKind.Object && g.TryGetProperty("Name", out var n)
                                             && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 })
                                 .Select(g => new ModGroup(g.GetProperty("Name").GetString()!,
                                                           IsMultiType(PenumbraModMeta.TypeOf(g)),
                                                           PenumbraModMeta.ReadOptionNames(g)))
                                 .ToList();
            var result = new List<ModGroup>();
            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name)
                    result.Add(new ModGroup(name, IsMultiType(PenumbraModMeta.TypeOf(doc.RootElement)),
                                            PenumbraModMeta.ReadOptionNames(doc.RootElement)));
            }
            return result;
        }
        // InvalidOperationException: valid JSON of the wrong shape (a group file that is an array, a Name that is not a
        // string, an option that is not an object) — unreadable as groups, as malformed JSON is.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
