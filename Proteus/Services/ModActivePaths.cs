using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Proteus.Services;

/// <summary>
/// What a Penumbra mod puts into the game for ONE option selection: the game paths it redirects and the
/// objects its metadata edits reach. The design binding uses it to decide which mods are "on the character"
/// and which of them contest a path with each other.
/// <para/>
/// Mirrors Penumbra's <c>Mod.GetData</c> — default data, the selected option of a Single group, the ticked
/// options of a Multi group, the one Containers entry a Combining group's ticked flags index — with one
/// deliberate difference: option CONDITIONS are ignored, so a conditional option counts whenever it is
/// ticked. That over-approximates, which is the safe direction for both callers: a conflict that isn't
/// real only costs a priority bump that changes nothing on screen.
/// <para/>
/// The manifest is parsed once per mod and cached against the manifest files' size and timestamp, because
/// the binding asks this of every enabled mod in the collection and players keep hundreds of them. Resolving
/// a selection against the cached contents never touches the disk.
/// </summary>
internal static class ModActivePaths
{
    /// <summary>
    /// One object a metadata edit reaches, as the letter and id it wears in a game path — <c>e6255</c> for
    /// equipment set 6255, <c>h0104</c> for hair 104. A <see cref="Kind"/> of <c>'\0'</c> is an edit that reaches
    /// every character rather than one object (scaling, global EQP, attachment points, an unslotted shape).
    /// </summary>
    /// <param name="Race">The gender-race code the edit is limited to, as game paths spell it — 201 for
    /// <c>c0201</c>, a Midlander woman. 0 when it applies to every race. Without this, a Roegadyn alt's scaling
    /// mod read as reaching every character in the collection.</param>
    internal readonly record struct MetaTarget(char Kind, ushort Id, ushort Race = 0)
    {
        internal static readonly MetaTarget RaceWide = new('\0', 0);

        internal static MetaTarget Everyone(ushort race) => new('\0', 0, race);

        internal bool IsEveryone => Kind == '\0';

        /// <summary>Same object, and races that can coincide.</summary>
        internal bool Meets(MetaTarget other)
            => Kind == other.Kind && Id == other.Id && (Race == 0 || other.Race == 0 || Race == other.Race);
    }

    /// <summary>
    /// What a character is drawing, as metadata edits see it: the objects its game paths name and the
    /// gender-race codes they are drawn for.
    /// </summary>
    internal sealed record CharacterTargets(HashSet<(char Kind, ushort Id)> Objects, HashSet<ushort> Races)
    {
        /// <summary>Whether an edit to <paramref name="t"/> changes this character.</summary>
        internal bool IsReachedBy(MetaTarget t)
        {
            if (!t.IsEveryone && !Objects.Contains((t.Kind, t.Id))) return false;
            // No race read from the paths at all is "unknown", which must not filter everything out.
            return t.Race == 0 || Races.Count == 0 || Races.Contains(t.Race);
        }

        internal bool IsReachedByAny(IEnumerable<MetaTarget> targets)
        {
            foreach (var t in targets)
                if (IsReachedBy(t))
                    return true;
            return false;
        }
    }

    /// <summary>What one selection writes. Both sets are empty, never null.</summary>
    internal sealed record Contribution(HashSet<string> GamePaths, HashSet<MetaTarget> Meta)
    {
        internal static Contribution Empty() => new(NewPathSet(), []);

        /// <summary>Whether the two write any of the same game paths or metadata objects.</summary>
        internal bool Meets(Contribution other)
        {
            if (GamePaths.Overlaps(other.GamePaths)) return true;
            if (Meta.Count == 0 || other.Meta.Count == 0) return false;
            foreach (var a in Meta)
                foreach (var b in other.Meta)
                    if (a.Meets(b))
                        return true;
            return false;
        }
    }

    // ── Parsed, selection-independent contents ──────────────────────────────────

    internal sealed class Container
    {
        public HashSet<string> GamePaths { get; } = NewPathSet();
        public HashSet<MetaTarget> Meta { get; } = [];
    }

    internal sealed class Group
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = "Single";
        public List<string> OptionNames { get; } = [];
        public List<Container> Options { get; } = [];
        /// <summary>Combining groups only: one entry per combination of ticked flags.</summary>
        public List<Container>? Containers { get; set; }
        /// <summary>Imc groups: what the group's own identifier reaches whenever the group is live.</summary>
        public HashSet<MetaTarget> GroupMeta { get; } = [];
        /// <summary>The author's default, as Penumbra stores it: an index for Single, a bitmask otherwise.</summary>
        public ulong DefaultSettings { get; init; }
    }

    internal sealed class Contents
    {
        public Container Default { get; } = new();
        public List<Group> Groups { get; } = [];
    }

    // ── Cached entry point ──────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, (long Fingerprint, Contents? Contents)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What <paramref name="modRoot"/> writes with <paramref name="selection"/> ticked (group name → option
    /// names, as Penumbra reports settings). Null when the manifest can't be read — "unknown", which a caller
    /// must never read as "writes nothing".
    /// </summary>
    internal static Contribution? Read(string modRoot, IReadOnlyDictionary<string, List<string>>? selection)
        => LoadContents(modRoot) is { } c ? Resolve(c, selection) : null;

    /// <summary>The parsed manifest, from the cache while its files are unchanged.</summary>
    internal static Contents? LoadContents(string modRoot)
    {
        var fp = Fingerprint(modRoot);
        if (Cache.TryGetValue(modRoot, out var hit) && hit.Fingerprint == fp)
            return hit.Contents;

        var contents = ParseContents(modRoot);
        Cache[modRoot] = (fp, contents);
        return contents;
    }

    private static long Fingerprint(string modRoot)
    {
        long fp = 17;
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (!string.Equals(name, PenumbraModMeta.MetaFile, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, PenumbraModMeta.LegacyDefaultMod, StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("group_", StringComparison.OrdinalIgnoreCase))
                    continue;
                var info = new FileInfo(file);
                fp = unchecked(fp * 31 + name.GetHashCode(StringComparison.OrdinalIgnoreCase));
                fp = unchecked(fp * 31 + info.Length + info.LastWriteTimeUtc.Ticks);
            }
        }
        catch { return 0; /* folder gone or unreadable — parse will answer null */ }
        return fp;
    }

    // ── Parsing ─────────────────────────────────────────────────────────────────

    /// <summary>Parse a mod folder in whichever manifest layout it uses. Null when unreadable.</summary>
    internal static Contents? ParseContents(string modRoot)
    {
        try
        {
            var manifest = PenumbraModMeta.ReadManifest(modRoot);
            if (manifest.Count == 0) return null;

            var contents = new Contents();
            if (PenumbraModMeta.FileVersionOf(manifest) >= PenumbraModMeta.SingleFileVersion)
            {
                if (manifest.TryGetValue("DefaultData", out var dd)) FillContainer(dd, contents.Default);
                if (manifest.TryGetValue("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray())
                        if (ParseGroup(g) is { } group)
                            contents.Groups.Add(group);
                return contents;
            }

            var legacy = Path.Combine(modRoot, PenumbraModMeta.LegacyDefaultMod);
            if (File.Exists(legacy))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                FillContainer(doc.RootElement, contents.Default);
            }
            // group_001_name.json, group_002_… — the number is the group's position, so name order is it.
            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json")
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (ParseGroup(doc.RootElement) is { } group)
                    contents.Groups.Add(group);
            }
            return contents;
        }
        catch { return null; }
    }

    private static Group? ParseGroup(JsonElement g)
    {
        if (g.ValueKind != JsonValueKind.Object) return null;

        var group = new Group
        {
            Name = g.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "",
            Type = g.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "Single" : "Single",
            DefaultSettings = g.TryGetProperty("DefaultSettings", out var d) && d.ValueKind == JsonValueKind.Number
                              && d.TryGetUInt64(out var dv) ? dv : 0,
        };

        if (g.TryGetProperty("Options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            foreach (var o in opts.EnumerateArray())
            {
                group.OptionNames.Add(o.ValueKind == JsonValueKind.Object && o.TryGetProperty("Name", out var on)
                    ? on.GetString() ?? "" : "");
                var c = new Container();
                FillContainer(o, c);
                group.Options.Add(c);
            }

        if (g.TryGetProperty("Containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
        {
            group.Containers = [];
            foreach (var ce in containers.EnumerateArray())
            {
                var c = new Container();
                FillContainer(ce, c);
                group.Containers.Add(c);
            }
        }

        if (string.Equals(group.Type, "Imc", StringComparison.OrdinalIgnoreCase)
            && g.TryGetProperty("Identifier", out var id) && id.ValueKind == JsonValueKind.Object)
        {
            if (ImcTarget(id) is { } target) group.GroupMeta.Add(target);
        }

        return group;
    }

    private static readonly string[] PathMaps = ["Files", "FileSwaps"];

    /// <summary>Files and FileSwaps keys are both game paths the container claims; Manipulations become targets.</summary>
    private static void FillContainer(JsonElement owner, Container into)
    {
        if (owner.ValueKind != JsonValueKind.Object) return;

        foreach (var key in PathMaps)
            if (owner.TryGetProperty(key, out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var p in map.EnumerateObject())
                {
                    if (p.Name.Length == 0) continue;
                    // An identity swap (A -> A) redirects nothing; overlay packs carry one only so Penumbra
                    // doesn't see an empty mod. See PenumbraModMeta.HasGameContent.
                    if (key == "FileSwaps" && p.Value.ValueKind == JsonValueKind.String
                        && string.Equals(p.Value.GetString(), p.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    into.GamePaths.Add(NormalizePath(p.Name));
                }

        if (owner.TryGetProperty("Manipulations", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var e in m.EnumerateArray())
                if (ManipulationTarget(e) is { } target)
                    into.Meta.Add(target);
    }

    // ── Metadata targets ────────────────────────────────────────────────────────

    /// <summary>
    /// The object one manipulation reaches, or null when it can't be placed — which leaves it out rather
    /// than guessing, so a mod is never pulled onto the character by an edit nobody can attribute.
    /// </summary>
    internal static MetaTarget? ManipulationTarget(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var type = e.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (!e.TryGetProperty("Manipulation", out var m) || m.ValueKind != JsonValueKind.Object) m = default;

        switch (type)
        {
            case "GlobalEqp":
                return MetaTarget.RaceWide;

            case "Rsp":
                // Keyed by clan and an attribute whose name carries the gender (MaleMaxSize, BustMinX…).
                return MetaTarget.Everyone(RspRace(ReadString(m, "SubRace"), ReadString(m, "Attribute")));

            case "Atch":
                return MetaTarget.Everyone(GenderRaceCode(ReadString(m, "Gender"), ReadString(m, "Race")));

            case "Eqp":
                return ReadId(m, "SetId") is { } eqp ? new MetaTarget(SlotKind(ReadString(m, "Slot")) ?? 'e', eqp) : null;

            case "Eqdp":
                return ReadId(m, "SetId") is { } eq
                    ? new MetaTarget(SlotKind(ReadString(m, "Slot")) ?? 'e', eq,
                        GenderRaceCode(ReadString(m, "Gender"), ReadString(m, "Race")))
                    : null;

            case "Gmp":
                return ReadId(m, "SetId") is { } gmp ? new MetaTarget('e', gmp) : null;

            case "Est":
            {
                // Hair and face rows are keyed by the hair/face id; head and body rows by the equipment set.
                if (ReadId(m, "SetId") is not { } est) return null;
                var race = GenderRaceCode(ReadString(m, "Gender"), ReadString(m, "Race"));
                return ReadString(m, "Slot") switch
                {
                    "Hair" => new MetaTarget('h', est, race),
                    "Face" => new MetaTarget('f', est, race),
                    _      => new MetaTarget('e', est, race),
                };
            }

            case "Imc":
                return ImcTarget(m);

            case "Shp":
            case "Atr":
            {
                // Slotted to one item when it names both; otherwise it reaches everyone — of one gender-race when
                // it carries a condition.
                var race = ReadId(m, "GenderRaceCondition") ?? 0;
                if (ReadId(m, "Id") is { } sid && ReadString(m, "Slot") is { } slot)
                    return SlotKind(slot) is { } k ? new MetaTarget(k, sid, race) : null;
                return MetaTarget.Everyone(race);
            }

            default:
                return null;
        }
    }

    // Model races in the order the GAME'S gender-race codes number them — Midlander 01/02, Highlander 03/04,
    // Elezen 05/06, Miqo'te 07/08, Roegadyn 09/10, Lalafell 11/12, … NOT Penumbra's ModelRace enum order, which
    // puts Lalafell before Miqo'te and would read every Miqo'te edit as a Roegadyn one.
    private static readonly string[] ModelRaces =
        ["Midlander", "Highlander", "Elezen", "Miqote", "Roegadyn", "Lalafell", "AuRa", "Hrothgar", "Viera"];

    /// <summary>
    /// The gender-race code (201 for <c>c0201</c>) Penumbra's Gender/Race pair stands for, or 0 — "every race" —
    /// when either is unknown. Penumbra writes these as display names ("Miqo'te", "Au Ra") in some versions and
    /// member names in others, so both are matched with the punctuation and spaces dropped.
    /// </summary>
    internal static ushort GenderRaceCode(string? gender, string? race)
    {
        var r = Array.FindIndex(ModelRaces, n => string.Equals(n, Letters(race), StringComparison.OrdinalIgnoreCase));
        if (r < 0) return 0;
        var suffix = Letters(gender)?.ToLowerInvariant() switch
        {
            "male"   => (Female: false, Npc: false),
            "female" => (Female: true,  Npc: false),
            "malechild" or "malenpc"     => (Female: false, Npc: true),
            "femalechild" or "femalenpc" => (Female: true,  Npc: true),
            _ => ((bool Female, bool Npc)?)null,
        };
        if (suffix is not { } s) return 0;
        return (ushort)(((r + 1) * 2 - (s.Female ? 0 : 1)) * 100 + (s.Npc ? 4 : 1));
    }

    /// <summary>An RSP row's gender-race: the clan's model race, and the gender its attribute is for.</summary>
    private static ushort RspRace(string? subRace, string? attribute)
    {
        var race = Letters(subRace)?.ToLowerInvariant() switch
        {
            "midlander"                           => "Midlander",
            "highlander"                          => "Highlander",
            "wildwood" or "duskwight"             => "Elezen",
            "plainsfolk" or "dunesfolk"           => "Lalafell",
            "seekerofthesun" or "keeperofthemoon" => "Miqote",
            "seawolf" or "hellsguard"             => "Roegadyn",
            "raen" or "xaela"                     => "AuRa",
            "helion" or "hellion" or "lost"       => "Hrothgar",
            "rava" or "veena"                     => "Viera",
            _                                     => null,
        };
        if (race == null || attribute == null) return 0;
        var gender = attribute.StartsWith("Male", StringComparison.OrdinalIgnoreCase)   ? "Male"
                   : attribute.StartsWith("Female", StringComparison.OrdinalIgnoreCase)
                  || attribute.StartsWith("Bust", StringComparison.OrdinalIgnoreCase)   ? "Female"
                   : null;
        return GenderRaceCode(gender, race);
    }

    private static string? Letters(string? s)
        => s == null ? null : new string(s.Where(char.IsLetter).ToArray());

    private static MetaTarget? ImcTarget(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object || ReadId(m, "PrimaryId") is not { } id) return null;
        var kind = ReadString(m, "ObjectType") switch
        {
            "Equipment" => 'e',
            "Accessory" => 'a',
            "Weapon"    => 'w',
            "Monster"   => 'm',
            "DemiHuman" => 'd',
            "Character" => ReadString(m, "BodySlot") switch
            {
                "Hair" => 'h',
                "Face" => 'f',
                "Tail" => 't',
                "Zear" or "Ear" => 'z',
                "Body" => 'b',
                _ => '\0',
            },
            _ => '\0',
        };
        return kind == '\0' ? null : new MetaTarget(kind, id);
    }

    /// <summary>Equipment and accessory slots share set ids, so the slot says which one an id belongs to.</summary>
    private static char? SlotKind(string? slot) => slot switch
    {
        "Head" or "Body" or "Hands" or "Legs" or "Feet" or "FullBody" or "HeadBody" or "BodyHandsLegsFeet"
            or "LegsFeet" or "BodyLegsFeet" or "BodyHands" => 'e',
        "Ears" or "Neck" or "Wrists" or "RFinger" or "LFinger" => 'a',
        "Hair" => 'h',
        "Face" => 'f',
        "Ear" or "Zear" => 'z',
        _ => null,
    };

    private static ushort? ReadId(JsonElement m, string name)
    {
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt16(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && ushort.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static string? ReadString(JsonElement m, string name)
        => m.ValueKind == JsonValueKind.Object && m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    // ── Selection ───────────────────────────────────────────────────────────────

    /// <summary>
    /// What <paramref name="contents"/> writes with <paramref name="selection"/> ticked. A group missing from
    /// the selection falls back to the author's default, as Penumbra does for a mod it has never configured.
    /// </summary>
    internal static Contribution Resolve(Contents contents, IReadOnlyDictionary<string, List<string>>? selection)
    {
        var result = Contribution.Empty();
        Add(result, contents.Default);

        foreach (var g in contents.Groups)
        {
            List<string>? ticked = null;
            selection?.TryGetValue(g.Name, out ticked);

            if (string.Equals(g.Type, "Single", StringComparison.OrdinalIgnoreCase))
            {
                int index = ticked is { Count: > 0 } ? g.OptionNames.IndexOf(ticked[0]) : -1;
                if (index < 0) index = (int)Math.Min(g.DefaultSettings, int.MaxValue);
                if (index < g.Options.Count) Add(result, g.Options[index]);
                continue;
            }

            ulong mask = ticked != null ? MaskOf(g, ticked) : g.DefaultSettings;

            if (g.Containers != null)
            {
                // Combining: the ticked flags, read as a number, index the one container that applies.
                if (mask < (ulong)g.Containers.Count) Add(result, g.Containers[(int)mask]);
                continue;
            }

            if (string.Equals(g.Type, "Imc", StringComparison.OrdinalIgnoreCase))
            {
                result.Meta.UnionWith(g.GroupMeta);
                continue;
            }

            // Multi, and anything unrecognised that still carries options: every ticked option.
            for (int i = 0; i < g.Options.Count && i < 64; i++)
                if ((mask & (1UL << i)) != 0)
                    Add(result, g.Options[i]);
        }

        return result;
    }

    private static ulong MaskOf(Group g, List<string> ticked)
    {
        ulong mask = 0;
        foreach (var name in ticked)
        {
            var i = g.OptionNames.IndexOf(name);
            if (i is >= 0 and < 64) mask |= 1UL << i;
        }
        return mask;
    }

    private static void Add(Contribution into, Container c)
    {
        into.GamePaths.UnionWith(c.GamePaths);
        into.Meta.UnionWith(c.Meta);
    }

    // ── Character side ──────────────────────────────────────────────────────────

    // e6255, a0053, h0104, c0201b0001 → b0001. Letters are the object kinds game paths use; the lookbehind keeps
    // "_bibo" and friends from matching, and allows a digit before so "c0201e6255" still yields e6255.
    private static readonly Regex ObjectIdPattern =
        new(@"(?<![a-z])([eawhftzbmd])(\d{4})(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // c0201, the gender-race a human model is drawn for.
    private static readonly Regex GenderRacePattern =
        new(@"(?<![a-z])c(\d{4})(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// What a character drawing <paramref name="gamePaths"/> can be reached by: every object id its paths name,
    /// and every gender-race they are drawn for. A Highlander wearing gear with no Highlander model draws the
    /// Midlander one, so both codes can appear — which only ever widens the match.
    /// </summary>
    internal static CharacterTargets TargetsOf(IEnumerable<string> gamePaths)
    {
        var objects = new HashSet<(char, ushort)>();
        var races   = new HashSet<ushort>();
        foreach (var p in gamePaths)
        {
            foreach (Match m in ObjectIdPattern.Matches(p))
                objects.Add((char.ToLowerInvariant(m.Groups[1].Value[0]), ushort.Parse(m.Groups[2].Value)));
            foreach (Match m in GenderRacePattern.Matches(p))
                races.Add(ushort.Parse(m.Groups[1].Value));
        }
        return new CharacterTargets(objects, races);
    }

    internal static string NormalizePath(string gamePath) => gamePath.Replace('\\', '/').ToLowerInvariant();

    internal static HashSet<string> NewPathSet() => new(StringComparer.OrdinalIgnoreCase);
}
