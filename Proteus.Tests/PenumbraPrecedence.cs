using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Proteus.Tests;

/// <summary>
/// Which file Penumbra serves at a game path for a mod with given options ticked — written from Penumbra's own code, for
/// the tests alone, sharing nothing with <c>BodyRetargetWriter.Outranking</c>, so a wrong rule there is caught here.
/// <para/>
/// <c>Mod.GetData</c>: the groups walked in reversed array order, stable-sorted by priority, highest first (a tie goes to
/// the later group), the default data last. <c>SingleModGroup</c>: the ticked option. <c>MultiModGroup.AddData</c>: the
/// ticked options stable-sorted by option priority, highest first (a tie goes to the earlier option).
/// <c>CombiningModGroup.AddData</c>: the one container whose index has bit i set for each ticked option i.
/// <c>SubMod.AddContainerTo</c>: files, then file swaps, each claiming a path only if nothing has yet.
/// </summary>
internal static class PenumbraPrecedence
{
    /// <summary>The file (or swap target) served at <paramref name="gamePath"/>, or null for none.</summary>
    /// <param name="ticked">Per group name, the option names ticked; a group not named has none ticked.</param>
    internal static string? Winner(string modRoot, string gamePath, IReadOnlyDictionary<string, IReadOnlyCollection<string>> ticked)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modRoot, "meta.json")));
        var root = doc.RootElement;
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var groups = root.TryGetProperty("Groups", out var g) ? g.EnumerateArray().ToList() : [];
        var order = groups.Select((group, index) => (group, index)).Reverse()
                          .OrderByDescending(x => Int(x.group, "Priority")).ToList();
        foreach (var (group, _) in order)
        {
            string name = Str(group, "Name") ?? "";
            var on = ticked.TryGetValue(name, out var t) ? t : [];
            var options = group.TryGetProperty("Options", out var o) ? o.EnumerateArray().ToList() : [];
            switch (Str(group, "Type"))
            {
                case "Single":
                    foreach (var option in options.Where(x => on.Contains(Str(x, "Name") ?? "")))
                        Add(option, claimed);
                    break;
                case "Multi":
                    foreach (var option in options.Where(x => on.Contains(Str(x, "Name") ?? ""))
                                                  .OrderByDescending(x => Int(x, "Priority")))
                        Add(option, claimed);
                    break;
                case "Combining":
                    int mask = 0;
                    for (int i = 0; i < options.Count; i++)
                        if (on.Contains(Str(options[i], "Name") ?? "")) mask |= 1 << i;
                    var containers = group.TryGetProperty("Containers", out var c) ? c.EnumerateArray().ToList() : [];
                    if (mask < containers.Count) Add(containers[mask], claimed);
                    break;
            }
        }
        if (root.TryGetProperty("DefaultData", out var defaults)) Add(defaults, claimed);
        return claimed.GetValueOrDefault(gamePath);
    }

    private static void Add(JsonElement container, Dictionary<string, string> claimed)
    {
        foreach (string map in new[] { "Files", "FileSwaps" })
            if (container.TryGetProperty(map, out var m) && m.ValueKind == JsonValueKind.Object)
                foreach (var f in m.EnumerateObject())
                    claimed.TryAdd(f.Name.Replace('\\', '/'), (f.Value.GetString() ?? "").Replace('\\', '/'));
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
