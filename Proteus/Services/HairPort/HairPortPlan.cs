using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Proteus.Services;

/// <summary>
/// The decisions of a hair port, with no I/O beyond reading a mod's manifest: which races a hairstyle may go to, which
/// race it comes from, which of the author's options it goes into, and when an earlier port is out of date.
/// <para/>
/// A hair mod replaces one race's model of one hairstyle — <c>c0801h0130_hir.mdl</c> — so a Midlander wearing the
/// same hairstyle gets the game's own. A port bends the mod's model onto the wearer's race and publishes it from the
/// SAME option of the SAME mod: switching options or the mod off takes the port with it, and no group moves.
/// </summary>
internal static partial class HairPortPlan
{
    /// <summary>The folder every port is written under, inside the mod.</summary>
    public const string Folder = "Hair Ports";

    /// <summary>A hair model's game path: <c>chara/human/c0801/obj/hair/h0130/model/c0801h0130_hir.mdl</c>.</summary>
    public static string HairPath(ushort race, int id)
        => $"chara/human/c{race:D4}/obj/hair/h{id:D4}/model/c{race:D4}h{id:D4}_hir.mdl";

    /// <summary>The race and hairstyle of a hair model's game path; null for any other path.</summary>
    public static (ushort Race, int Id)? Parse(string gamePath)
    {
        var m = HairModel().Match(gamePath.Replace('\\', '/'));
        if (!m.Success || m.Groups[1].Value != m.Groups[3].Value || m.Groups[2].Value != m.Groups[4].Value) return null;
        return (ushort.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    [GeneratedRegex(@"^chara/human/c(\d{4})/obj/hair/h(\d{4})/model/c(\d{4})h(\d{4})_hir\.mdl$", RegexOptions.IgnoreCase)]
    private static partial Regex HairModel();

    /// <summary>A playable race's code, 101 to 1801.</summary>
    public static bool Playable(ushort race) => race % 100 == 1 && race / 100 is >= 1 and <= 18;

    /// <summary>Every other playable race of <paramref name="race"/>'s sex — the races a hairstyle may be ported to.
    /// Never across sexes: a woman's hairstyle is not fitted onto a man.</summary>
    public static IEnumerable<ushort> SameGenderRaces(ushort race)
    {
        if (!Playable(race)) yield break;
        for (int n = race / 100 % 2 == 0 ? 2 : 1; n <= 18; n += 2)
            if (n * 100 + 1 != race) yield return (ushort)(n * 100 + 1);
    }

    /// <summary>How many fall-through steps apart two races are, through the race both fall to; a large number when
    /// they share none.</summary>
    public static int Hops(ushort a, ushort b)
    {
        ushort root = RacialModelBake.CommonAncestor(a, b);
        if (root == 0) return 99;
        return Steps(a, root) + Steps(b, root);

        static int Steps(ushort from, ushort to)
        {
            int steps = 0;
            for (int cur = from / 100; cur != 0 && cur != to / 100 && steps < 8; cur = ModelRace.Fallback(cur)) steps++;
            return steps;
        }
    }

    /// <summary>
    /// The races a hairstyle could be ported from onto <paramref name="target"/>, best first: the fewest fall-through
    /// steps (the smallest bend), then the lowest race number. Only the same sex.
    /// </summary>
    public static IEnumerable<ushort> OrderSources(ushort target, IEnumerable<ushort> candidates)
        => candidates.Where(c => c != target && c / 100 % 2 == target / 100 % 2)
                     .OrderBy(c => Hops(c, target)).ThenBy(c => c);

    /// <summary>The game's face models to measure a race's head with, most usual first: not every race has
    /// <c>f0001</c>.</summary>
    public static IEnumerable<string> HeadPaths(ushort race)
    {
        foreach (int bank in new[] { 0, 100 })
            for (int f = 1; f <= 8; f++)
                yield return $"chara/human/c{race:D4}/obj/face/f{bank + f:D4}/model/c{race:D4}f{bank + f:D4}_fac.mdl";
    }

    /// <summary>
    /// Where a port's file goes inside the mod: under <see cref="Folder"/>, then the place it is published from (so two
    /// options' ports of one hairstyle never share a file), then the game path. An option's folder ends in a short hash
    /// of its group and option names, since the readable part alone collides: group "A - B" with option "C" and group
    /// "A" with option "B - C", or names that differ only by characters a folder cannot hold.
    /// </summary>
    public static string PortFile(Owner owner, string gamePath)
    {
        string folder = "Default";
        if (!owner.IsDefault)
        {
            var safe = string.Concat($"{owner.Group} - {owner.Option}"
                                     .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{owner.Group.ToLowerInvariant()}\0{owner.Option.ToLowerInvariant()}")))[..6]
                .ToLowerInvariant();
            folder = $"{safe} {hash}".Trim();
        }
        return $"{Folder}/{folder}/{gamePath.Replace('\\', '/')}";
    }

    /// <summary>Whether a mod-relative file is one of the ports Proteus wrote — never a source for another.</summary>
    public static bool IsPortFile(string rel)
        => rel.Replace('\\', '/').StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>One place in a mod that publishes files: the default data (empty group), or one option.</summary>
    /// <param name="Group">"" for the mod's default data.</param>
    /// <param name="Type">The group's type ("Single", "Multi", "Combining", …); "" for the default data.</param>
    /// <param name="Files">Game path to mod-relative file, as the manifest has it.</param>
    /// <param name="Manipulations">Its metadata edits as written; null when not read.</param>
    public sealed record Owner(string Group, string Option, string Type, IReadOnlyDictionary<string, string> Files,
                               IReadOnlyList<JsonElement>? Manipulations = null)
    {
        public bool IsDefault => Group.Length == 0;
        public bool IsCombining => string.Equals(Type, "Combining", StringComparison.OrdinalIgnoreCase);
        public bool IsMulti => string.Equals(Type, "Multi", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every place the mod publishes files from, in manifest order, the default data first; null when the manifest is
    /// unreadable or a pre-v4 folder. A Combining group's containers are listed as its options, by number, so a port
    /// can see them and refuse them.
    /// </summary>
    public static List<Owner>? ReadOwners(string modRoot)
    {
        if (!PenumbraModMeta.HasReadableManifest(modRoot) || PenumbraModMeta.IsLegacyFolder(modRoot)) return null;
        var owners = new List<Owner>();
        if (PenumbraModMeta.TryReadDefaultData(modRoot) is { } dd)
            owners.Add(new Owner("", "", "", dd.Files, dd.Manipulations.OfType<JsonElement>().ToList()));
        foreach (var (name, group) in PenumbraModMeta.TryReadGroups(modRoot) ?? [])
        {
            string type = PenumbraModMeta.TypeOf(group);
            if (group.TryGetProperty("Containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
            {
                type = "Combining";
                int i = 0;
                foreach (var c in containers.EnumerateArray())
                    owners.Add(new Owner(name, $"#{++i}", type, FilesOf(c)));
            }
            foreach (var option in PenumbraModMeta.FileOptionsOf(group) ?? [])
                owners.Add(new Owner(name, option.Name, type, option.Files,
                                     (option.Manipulations ?? []).OfType<JsonElement>().ToList()));
        }
        return owners;
    }

    private static Dictionary<string, string> FilesOf(JsonElement owner)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("Files", out var f)
            && f.ValueKind == JsonValueKind.Object)
            foreach (var p in f.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } rel)
                    files[p.Name] = rel;
        return files;
    }

    /// <summary>The file an owner publishes for a game path, compared without regard to case or slashes.</summary>
    public static string? FileFor(Owner owner, string gamePath)
    {
        string want = gamePath.Replace('\\', '/');
        foreach (var (path, rel) in owner.Files)
            if (string.Equals(path.Replace('\\', '/'), want, StringComparison.OrdinalIgnoreCase)) return rel;
        return null;
    }

    /// <summary>
    /// Whether a port of <paramref name="target"/> may be added to <paramref name="into"/>. Not when something already
    /// publishes that path whenever <paramref name="into"/> is on — the default data, the option itself, an option of
    /// another group, or another option of a group that can have several on: the author covered it, or the two would
    /// fight. Another option of the same pick-one group never is on at the same time, so it does not stand in the way.
    /// </summary>
    public static bool CanPortInto(IReadOnlyList<Owner> owners, Owner into, string target)
    {
        foreach (var owner in owners)
        {
            if (FileFor(owner, target) is not { } rel) continue;
            if (Same(owner, into)) return false;
            // Another option's port of its own variant: options outrank the default data and one another by group
            // priority, exactly as the source they were made from does.
            if (IsPortFile(rel)) continue;
            if (owner.IsDefault) return false;
            if (!string.Equals(owner.Group, into.Group, StringComparison.OrdinalIgnoreCase)) return false;
            if (into.IsMulti) return false;
        }
        return true;
    }

    /// <summary>Whether two owners are the same place in the mod, by name.</summary>
    public static bool Same(Owner a, Owner b)
        => string.Equals(a.Group, b.Group, StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Option, b.Option, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the mod already publishes <paramref name="gamePath"/> anywhere, port or not.</summary>
    public static bool Covers(IReadOnlyList<Owner> owners, string gamePath, bool countPorts)
        => owners.Any(o => FileFor(o, gamePath) is { } rel && (countPorts || !IsPortFile(rel)));

    /// <summary>
    /// Every race of the mod's hairstyle <paramref name="id"/> that the mod's author covers — its own files, not ports —
    /// the sources a port can be made from.
    /// </summary>
    public static IEnumerable<ushort> AuthoredRaces(IReadOnlyList<Owner> owners, int id)
    {
        var races = new SortedSet<ushort>();
        foreach (var owner in owners)
            foreach (var (path, rel) in owner.Files)
                if (!IsPortFile(rel) && Parse(path) is { } at && at.Id == id) races.Add(at.Race);
        return races;
    }

    /// <summary>
    /// The extra skeleton a place in the mod gives <paramref name="race"/> for hairstyle <paramref name="id"/>: the
    /// <c>Est</c> entry naming another hairstyle's skeleton (h0162 moving with h0170's bones), or null for none. Physics
    /// hair is made this way — the bones are the game's own, borrowed per race.
    /// </summary>
    public static int? HairEst(Owner owner, ushort race, int id) => HairEsts(owner, race, id).Cast<int?>().FirstOrDefault();

    /// <summary>Every hair <c>Est</c> entry a place gives <paramref name="race"/> for hairstyle <paramref name="id"/>,
    /// in manifest order.</summary>
    public static IEnumerable<int> HairEsts(Owner owner, ushort race, int id)
    {
        foreach (var m in owner.Manipulations ?? [])
            if (HairEstEntry(m, race, id) is { } entry) yield return entry;
    }

    /// <summary>
    /// The skeleton the mod gives the source race for hairstyle <paramref name="id"/> while <paramref name="owner"/> is
    /// on: its own entry, else the default data's — 0 included, which Penumbra applies as "none". Else, from
    /// <paramref name="owners"/>, the one entry every other place that can be on with it agrees on: authors put the
    /// entry in a "core files" option of another group as often as beside the model ([SME] Vale: the models in one
    /// group, the h0178 → 127 entry in another). Null when none has one, or they disagree, and the game's table decides.
    /// </summary>
    public static int? SourceEst(Owner owner, Owner? defaults, ushort race, int id, IReadOnlyList<Owner>? owners = null)
    {
        if ((HairEst(owner, race, id) ?? (defaults != null ? HairEst(defaults, race, id) : null)) is { } near) return near;
        // A sibling of a pick-one group is never on with the owner.
        var elsewhere = (owners ?? [])
            .Where(o => !o.IsDefault && !Same(o, owner)
                        && !(string.Equals(o.Group, owner.Group, StringComparison.OrdinalIgnoreCase) && !o.IsMulti))
            .SelectMany(o => HairEsts(o, race, id))
            .Distinct().ToList();
        return elsewhere.Count == 1 ? elsewhere[0] : null;
    }

    /// <summary>Every hair <c>Est</c> entry a place gives <paramref name="race"/>, as (hairstyle, skeleton).</summary>
    public static IEnumerable<(int Set, int Entry)> RaceHairEsts(Owner owner, ushort race)
    {
        foreach (var m in owner.Manipulations ?? [])
            if (HairEstSet(m) is { } set && HairEstEntry(m, race, set) is { } entry)
                yield return (set, entry);

        static int? HairEstSet(JsonElement m)
        {
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("Manipulation", out var e)
                || e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("SetId", out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
            return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s) ? s : null;
        }
    }

    /// <summary>
    /// The entry of one manipulation when it is the hair <c>Est</c> of <paramref name="race"/> and hairstyle
    /// <paramref name="id"/>, else null. Numbers are read as written either way — mods spell set ids as numbers and as
    /// strings (<c>"SetId": "162"</c>), and Penumbra reads both.
    /// </summary>
    public static int? HairEstEntry(JsonElement m, ushort race, int id)
    {
        int n = race / 100;
        if (n is < 1 or > 18) return null;
        string wantRace = ModelRace.Names[(n - 1) / 2], wantGender = n % 2 == 1 ? "Male" : "Female";
        if (m.ValueKind != JsonValueKind.Object || !Is(Str(m, "Type"), "Est")
            || !m.TryGetProperty("Manipulation", out var e) || e.ValueKind != JsonValueKind.Object) return null;
        if (!Is(Str(e, "Slot"), "Hair") || !Is(Str(e, "Race"), wantRace) || !Is(Str(e, "Gender"), wantGender)) return null;
        if (Num(e, "SetId") != id) return null;
        return Num(e, "Entry");

        static string? Str(JsonElement e, string key)
            => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // Penumbra reads enum names without regard to case.
        static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static int? Num(JsonElement e, string key)
        {
            if (!e.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s)) return s;
            return null;
        }
    }

    /// <summary>The game path of a race's skeleton for hairstyle <paramref name="id"/>'s extra bones.</summary>
    public static string SkeletonPath(ushort race, int id)
        => $"chara/human/c{race:D4}/skeleton/hair/h{id:D4}/skl_c{race:D4}h{id:D4}.sklb";

    /// <summary>The game path of the physics that swings that skeleton's extra bones, beside it.</summary>
    public static string PhysicsPath(ushort race, int id)
        => $"chara/human/c{race:D4}/skeleton/hair/h{id:D4}/phy_c{race:D4}h{id:D4}.phyb";

    /// <summary>The game path of the kinematics data some skeletons ship beside them, loaded with the skeleton.</summary>
    public static string KinematicsPath(ushort race, int id)
        => $"chara/human/c{race:D4}/skeleton/hair/h{id:D4}/kdi_c{race:D4}h{id:D4}.kdb";

    /// <summary>
    /// The extra bones (<c>j_ex_…</c>) a skeleton file names, read as the plain strings the file carries them as — no
    /// Havok parse needed to know which bones a race's skeleton has. Empty for no file.
    /// </summary>
    public static HashSet<string> SkeletonBones(byte[]? sklb)
    {
        var bones = new HashSet<string>(StringComparer.Ordinal);
        if (sklb == null) return bones;
        var prefix = "j_ex_"u8;
        for (int i = sklb.AsSpan().IndexOf(prefix); i >= 0;)
        {
            // Newer skeletons ("blks0031") put each name's length, doubled, in the byte before it — a printable byte, so
            // the names would run together; older ones end each name with a NUL.
            int end = i;
            int length = i > 0 ? sklb[i - 1] : 0;
            if (length % 2 == 0 && length / 2 >= prefix.Length && i + length / 2 <= sklb.Length
                && sklb.AsSpan(i, length / 2).IndexOfAnyExceptInRange((byte)0x21, (byte)0x7e) < 0)
                end = i + length / 2;
            else
                while (end < sklb.Length && sklb[end] is >= 0x21 and < 0x7f) end++;
            bones.Add(System.Text.Encoding.ASCII.GetString(sklb, i, end - i));
            int next = sklb.AsSpan(end).IndexOf(prefix);
            i = next < 0 ? -1 : end + next;
        }
        return bones;
    }

    /// <summary>The first eight hex digits of a file's SHA-256: enough to tell the author changed it since a port.</summary>
    public static string Sha8(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..8].ToLowerInvariant();

    /// <summary>A port is out of date when the file it was made from changed since, or it was made by an older bend.</summary>
    public static bool IsStale(HairPortRecord.Entry entry, byte[] source)
        => entry.PortVersion < HairPortBaker.Version || !string.Equals(entry.SourceSha8, Sha8(source), StringComparison.OrdinalIgnoreCase);

    /// <summary>The key a declined automatic port is remembered by: the game path it would have published.</summary>
    public static string DeclineKey(string targetGamePath) => targetGamePath.Replace('\\', '/').ToLowerInvariant();
}
