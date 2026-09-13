using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Proteus.Services;

/// <summary>
/// Reads and writes Penumbra's root <c>meta.json</c> mod manifest.
///
/// Penumbra's FileVersion 4 folded the whole mod layout into this one file: option groups moved out of
/// per-group <c>group_NNN_name.json</c> files into a <c>Groups</c> array, and <c>default_mod.json</c>
/// became the <c>DefaultData</c> object. Group ORDER is now the array index — the old filename number is
/// gone — but the meaning is unchanged: lower = higher priority.
///
/// Reads are two-tier everywhere: v4 first, falling back to the v3 layout for folders an older Penumbra
/// wrote and never migrated. That tier stays — a <c>.pmp</c> downloaded from a mod site is frequently v3
/// inside, and it is not Proteus's to rewrite.
///
/// WRITES ARE v4 ONLY, and there are two halves to that:
/// <list type="bullet">
/// <item>Proteus never AUTHORS v3. <see cref="NewMetaJson"/> stamps <see cref="SingleFileVersion"/>, and
/// the format-specific writers below have no legacy arm left to take.</item>
/// <item>Proteus never EDITS a v3 folder. Every write entry point refuses one through
/// <see cref="IsLegacyFolder"/> — see <see cref="LegacyFolderException"/> for why it throws rather than
/// quietly doing nothing.</item>
/// </list>
/// Writes used to follow whatever format the folder was already in, on the reasoning that the two are not
/// mutually legible and a Penumbra too old for <c>DefaultData</c> would silently apply no redirects at
/// all. That Penumbra is gone. What remains true is the half that makes the refusal cheap for the user: a
/// current Penumbra migrates a v3 folder up on load, so the fix for a folder Proteus declines is simply to
/// let Penumbra see it once.
/// </summary>
internal static class PenumbraModMeta
{
    public const string MetaFile         = "meta.json";
    public const string LegacyDefaultMod = "default_mod.json";

    /// <summary>The version that moved groups and the default option into meta.json.</summary>
    public const int SingleFileVersion = 4;
    /// <summary>
    /// The format Proteus reads but will not write. Also what <see cref="FileVersionOf"/> reports for a
    /// manifest that declares no version at all, which is why <see cref="IsLegacyFolder"/> asks
    /// <see cref="HasReadableManifest"/> first — "no manifest yet" is a folder being created, not an old one.
    /// </summary>
    public const int LegacyFileVersion = 3;

    // Encoder: these are Penumbra's own files, and Penumbra writes non-ASCII names as themselves. Without
    // it a rewrite here turns a mod's 正常 into "正常" in its manifest. See ProteusJson.
    private static readonly JsonSerializerOptions WriteOptions =
        new() { WriteIndented = true, Encoder = ProteusJson.Encoder };

    /// <summary>
    /// Whether the folder has a manifest that actually parses. False both when there is none and when
    /// it is corrupt — a caller repairing a manifest needs those two separated from "readable", which
    /// <see cref="ReadFileVersion"/> cannot give it: that collapses missing and unparseable into the
    /// same <see cref="LegacyFileVersion"/>. The distinction matters because from
    /// <see cref="SingleFileVersion"/> on, the manifest is also where the redirects live, so
    /// overwriting a READABLE one throws away live published state.
    /// </summary>
    public static bool HasReadableManifest(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, MetaFile);
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    /// <summary>
    /// Thrown when a write is asked for against a mod folder still in Penumbra's pre-v4 layout.
    /// <para/>
    /// An exception rather than a quiet no-op, and that is deliberate. This file's whole history is failures
    /// that looked like success — a v4 manifest an old Penumbra read as empty, a stale <c>default_mod.json</c>
    /// mistaken for the live set — where the log stayed clean and the user's mod simply did nothing. A write
    /// that cannot happen has to say so loudly enough that a caller is forced to have an answer for it.
    /// </summary>
    public sealed class LegacyFolderException(string modRoot)
        : InvalidOperationException(
            $"{modRoot} is a pre-v{SingleFileVersion} Penumbra mod folder, which Proteus does not write to.");

    /// <summary>
    /// Whether this folder is one Proteus will read but not edit — see the type remarks.
    /// <para/>
    /// <see cref="HasReadableManifest"/> comes first and is load-bearing: <see cref="ReadFileVersion"/>
    /// collapses "no manifest" and "unreadable manifest" into <see cref="LegacyFileVersion"/>, so without it
    /// every folder an importer is part-way through creating would refuse its own first write.
    /// </summary>
    public static bool IsLegacyFolder(string modRoot)
        => HasReadableManifest(modRoot) && ReadFileVersion(modRoot) < SingleFileVersion;

    /// <summary>
    /// Read the manifest, and throw <see cref="LegacyFolderException"/> if the folder is one Proteus will not
    /// write to. Every writer's way in — it hands back the manifest it read so the write can preserve the
    /// keys it does not own without parsing the file again.
    /// <para/>
    /// One read, not three. <see cref="IsLegacyFolder"/> parses twice by itself — once to ask whether a
    /// manifest is even there, once for its version — and the writers then parsed a third time to get the
    /// keys. That is billed on every composite: the compositor rewrites the managed mod's redirects through
    /// <see cref="WriteRedirects"/> on every run, and that manifest grows with the redirect set.
    /// <para/>
    /// Guards the point where a writer commits to a format, not the top of the method — a call that would
    /// write nothing anyway (no options, no redirects) stays the no-op it always was rather than becoming
    /// a throw.
    /// </summary>
    private static Dictionary<string, JsonElement> ReadManifestForWrite(string modRoot)
    {
        var manifest = ReadManifest(modRoot, out bool readable);
        // Readable FIRST, for the reason IsLegacyFolder documents: FileVersionOf reports a manifest that is
        // missing and one that declares no version as the same thing, and a folder an importer is part-way
        // through creating has no manifest at all.
        if (readable && FileVersionOf(manifest) < SingleFileVersion)
            throw new LegacyFolderException(modRoot);
        return manifest;
    }

    /// <summary>
    /// Bring a folder PROTEUS OWNS up to <see cref="SingleFileVersion"/>, folding its
    /// <c>default_mod.json</c> into <c>DefaultData</c> and preserving every other key. A no-op on a folder
    /// already at v4 or with no manifest at all.
    /// <para/>
    /// Only for folders Proteus created — the managed mod, and anything the importers or the Create tab
    /// laid down. Every one of those was stamped v3 by a build older than this one, and the managed mod in
    /// particular is rewritten on EVERY composite, so leaving them to <see cref="ReadManifestForWrite"/> would
    /// break compositing outright for anyone whose Penumbra had not happened to migrate the folder first.
    /// <para/>
    /// Deliberately NOT used on a mod belonging to someone else. Refusing a stranger's folder is a choice
    /// about not rewriting what we did not write; migrating it silently is the opposite of that, and
    /// Penumbra does it properly — groups and all — the moment it loads the mod.
    /// <para/>
    /// Groups are not folded, because a folder Proteus owns has none in the v3 layout: every group writer
    /// here has always gone through <see cref="WriteGroupIntoManifest"/> on a v4 folder or a legacy file on
    /// a v3 one, and the mods that carry Proteus groups are v4 by construction.
    /// </summary>
    public static void MigrateToCurrent(string modRoot)
    {
        if (!IsLegacyFolder(modRoot)) return;

        var manifest = ReadManifest(modRoot);
        var (files, manips) = TryReadDefaultData(modRoot)
                           ?? (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []);

        // Swaps, under its v3 name. WriteDefaultData writes it back as FileSwaps, which is the rename v4
        // made — reading it here is the only place the old spelling still has to be understood.
        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (File.Exists(legacy))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("Swaps", out var s)
                    && s.ValueKind == JsonValueKind.Object)
                    foreach (var p in s.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } to)
                            swaps[p.Name] = to;
            }
        }
        catch { /* unreadable — the redirects above are still worth carrying over */ }

        var name = manifest.TryGetValue("Name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? Path.GetFileName(modRoot)
            : Path.GetFileName(modRoot);

        // WriteDefaultData stamps SingleFileVersion and preserves every key it does not own, so this is the
        // migration in one call. CleanLegacyFiles then drops the default_mod.json it just absorbed.
        WriteDefaultData(modRoot, name, manifest, files, swaps, manips);
        CleanLegacyFiles(modRoot);
    }

    /// <summary>
    /// The mod's option groups in <c>Groups</c> array order, as (name, raw element) pairs. Null — not an
    /// empty list — when there is no v4 <c>Groups</c> array to read, which is the caller's signal to fall
    /// back to the v3 <c>group_*.json</c> layout. An empty list means "v4, and it genuinely has no groups".
    /// </summary>
    public static List<(string Name, JsonElement Group)>? TryReadGroups(string modRoot)
    {
        try
        {
            var path = Path.Combine(modRoot, MetaFile);
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Groups", out var groups)
                || groups.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<(string, JsonElement)>();
            foreach (var g in groups.EnumerateArray())
                if (g.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name)
                    // Clone: the JsonDocument is disposed when this method returns.
                    result.Add((name, g.Clone()));
            return result;
        }
        catch { return null; /* missing or malformed — fall back to v3 */ }
    }

    /// <summary>
    /// The mod's always-applied redirects and metadata edits as they are ON DISK right now — the inverse
    /// of <see cref="WriteRedirects"/>, reading whichever format the folder is in. Null when there is no
    /// manifest or it can't be parsed, which the caller must treat as "unknown", never as "empty".
    ///
    /// Manipulations come back as boxed <see cref="JsonElement"/>s rather than a typed model on purpose:
    /// the only thing that consumes them is <see cref="WriteRedirects"/>, which serialises each entry by
    /// its runtime type, and a JsonElement round-trips through that verbatim. So a read→write cycle
    /// preserves EQDP rows (and any future manipulation kind) without this file having to understand them.
    /// </summary>
    public static (Dictionary<string, string> Files, List<object> Manipulations)? TryReadDefaultData(string modRoot)
    {
        try
        {
            var manifest = ReadManifest(modRoot);
            if (FileVersionOf(manifest) >= SingleFileVersion)
            {
                if (!manifest.TryGetValue("DefaultData", out var dd) || dd.ValueKind != JsonValueKind.Object)
                    return null;
                return ReadFilesAndManipulations(dd);
            }

            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (!File.Exists(legacy)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return ReadFilesAndManipulations(doc.RootElement);
        }
        catch { return null; /* missing or malformed — "unknown", and the caller must not guess */ }
    }

    /// <summary>Shared shape of the v3 root object and the v4 <c>DefaultData</c> object.</summary>
    private static (Dictionary<string, string> Files, List<object> Manipulations) ReadFilesAndManipulations(
        JsonElement root)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("Files", out var f) && f.ValueKind == JsonValueKind.Object)
            foreach (var p in f.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } rel)
                    files[p.Name] = rel;

        var manips = new List<object>();
        if (root.TryGetProperty("Manipulations", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var e in m.EnumerateArray())
                manips.Add(e.Clone());   // the JsonDocument is disposed when the caller returns

        return (files, manips);
    }

    /// <summary>
    /// Whether the mod puts anything of its OWN into the game — a file redirect, a metadata manipulation,
    /// an IMC group, or a file swap that actually goes somewhere — in its default data or in any option of
    /// any group. Reads whichever format the folder is in.
    ///
    /// This separates the two kinds of folder a Proteus sidecar can sit in: a pure overlay pack, whose
    /// entire visible effect is what Proteus composites for it, and a mod that ALSO ships gear, a body or
    /// textures. Switching the first off in Penumbra costs nothing but its overlays; switching the second
    /// off takes the author's actual mod down with them. <c>DesignBindingService.Restore</c> is the caller
    /// that has to tell them apart.
    ///
    /// An identity swap (A -> A) does not count. Several overlay packs carry exactly one, purely so
    /// Penumbra doesn't see an empty mod, and it redirects nothing.
    ///
    /// True — "has content", so leave it alone — when the manifest is missing or unreadable. The only
    /// caller uses a false to justify DISABLING the mod, and a folder we couldn't read is not one to
    /// disable on a guess.
    /// </summary>
    public static bool PublishesGameContent(string modRoot)
    {
        try
        {
            var manifest = ReadManifest(modRoot);
            if (manifest.Count == 0) return true;   // unreadable — see the remarks

            if (FileVersionOf(manifest) >= SingleFileVersion)
            {
                if (manifest.TryGetValue("DefaultData", out var dd) && HasGameContent(dd)) return true;
                if (manifest.TryGetValue("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray())
                        if (GroupHasGameContent(g))
                            return true;
                return false;
            }

            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (File.Exists(legacy))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                if (HasGameContent(doc.RootElement)) return true;
            }

            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (GroupHasGameContent(doc.RootElement)) return true;
            }

            return false;
        }
        catch { return true; /* see the remarks — an unreadable folder is not one to disable */ }
    }

    /// <summary>
    /// One group, in either format. Three shapes, because Penumbra's group kinds carry their redirects in
    /// three different places: an <c>Imc</c> group edits the game by existing at all; a <c>Combining</c>
    /// group's options are bare flag labels and every redirect sits in a parallel <c>Containers</c> array,
    /// one per combination; every other kind carries them on the options themselves.
    /// </summary>
    private static bool GroupHasGameContent(JsonElement group)
    {
        if (group.ValueKind != JsonValueKind.Object) return false;

        if (group.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String
            && string.Equals(t.GetString(), "Imc", StringComparison.OrdinalIgnoreCase))
            return true;

        if (group.TryGetProperty("Containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
            foreach (var c in containers.EnumerateArray())
                if (HasGameContent(c))
                    return true;

        if (!group.TryGetProperty("Options", out var opts) || opts.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var o in opts.EnumerateArray())
            if (HasGameContent(o))
                return true;
        return false;
    }

    /// <summary>Shared shape of one option, the v3 root object and the v4 <c>DefaultData</c> object.</summary>
    private static bool HasGameContent(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return false;

        if (o.TryGetProperty("Files", out var f) && f.ValueKind == JsonValueKind.Object
            && f.EnumerateObject().Any())
            return true;

        if (o.TryGetProperty("Manipulations", out var m) && m.ValueKind == JsonValueKind.Array
            && m.EnumerateArray().Any())
            return true;

        if (o.TryGetProperty("FileSwaps", out var s) && s.ValueKind == JsonValueKind.Object)
            foreach (var p in s.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String
                    && !string.Equals(p.Value.GetString(), p.Name, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }

    /// <summary>
    /// One file the mod publishes: the game path it claims, the file backing it (relative to the mod root),
    /// and where in the mod that claim is made.
    /// </summary>
    /// <param name="Source">"" for the mod's default data, else "Group" or "Group / Option" — display only,
    /// so the user can tell two files claiming one game path apart.</param>
    public readonly record struct Redirect(string GamePath, string File, string Source);

    /// <summary>
    /// Every file redirect in the mod, wherever it is declared — default data, and every option (or
    /// Combining container) of every group — in whichever format the folder is in.
    /// <para/>
    /// Deliberately NOT deduplicated by game path. Two options claiming one path is the normal shape of a
    /// mod with variants, and both files are equally real: which one wins is Penumbra's business at draw
    /// time, while an edit that changes the geometry has to reach ALL of them or the toggle works on some
    /// of the mod's options and not others.
    /// <para/>
    /// Empty rather than null on an unreadable manifest. The only callers list files for the user to pick
    /// from, and "this mod publishes nothing we can read" is a list with no rows, not an error state.
    /// </summary>
    public static List<Redirect> ReadAllRedirects(string modRoot)
    {
        var found = new List<Redirect>();
        try
        {
            var manifest = ReadManifest(modRoot);
            if (FileVersionOf(manifest) >= SingleFileVersion)
            {
                if (manifest.TryGetValue("DefaultData", out var dd)) AddFiles(dd, "", found);
                if (manifest.TryGetValue("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray())
                        AddGroup(g, found);
                return found;
            }

            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (File.Exists(legacy))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                AddFiles(doc.RootElement, "", found);
            }
            foreach (var file in Directory.EnumerateFiles(modRoot, "group_*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                AddGroup(doc.RootElement, found);
            }
        }
        catch { /* missing or malformed — the caller shows an empty list */ }
        return found;
    }

    private static void AddGroup(JsonElement group, List<Redirect> into)
    {
        if (group.ValueKind != JsonValueKind.Object) return;
        var name = group.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";

        // A Combining group's options are bare flag labels; its files hang off a parallel Containers array,
        // one entry per COMBINATION of those flags. Named by ordinal because a container has no name of its
        // own — see PenumbraPackage.ReadGroup, which refuses to import them for the same reason.
        if (group.TryGetProperty("Containers", out var containers)
            && containers.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var c in containers.EnumerateArray())
                AddFiles(c, $"{name} / #{++i}", into);
        }

        if (!group.TryGetProperty("Options", out var opts) || opts.ValueKind != JsonValueKind.Array) return;
        foreach (var o in opts.EnumerateArray())
        {
            var option = o.ValueKind == JsonValueKind.Object && o.TryGetProperty("Name", out var on)
                ? on.GetString() ?? "" : "";
            AddFiles(o, option.Length > 0 ? $"{name} / {option}" : name, into);
        }
    }

    private static void AddFiles(JsonElement owner, string source, List<Redirect> into)
    {
        if (owner.ValueKind != JsonValueKind.Object) return;
        if (!owner.TryGetProperty("Files", out var f) || f.ValueKind != JsonValueKind.Object) return;
        foreach (var p in f.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } rel)
                into.Add(new Redirect(p.Name, rel, source));
    }

    /// <summary>The <c>Options[].Name</c> values of <paramref name="group"/>, in order.</summary>
    public static List<string> ReadOptionNames(JsonElement group)
    {
        var names = new List<string>();
        if (!group.TryGetProperty("Options", out var opts) || opts.ValueKind != JsonValueKind.Array)
            return names;
        foreach (var o in opts.EnumerateArray())
            if (o.TryGetProperty("Name", out var n) && n.GetString() is { } s)
                names.Add(s);
        return names;
    }

    /// <summary>
    /// The manifest's top-level keys, cloned so they outlive the parse. Empty when there is no manifest
    /// or it can't be read. Read once per write and threaded through, so a single recomposite doesn't
    /// parse meta.json twice.
    /// </summary>
    private static Dictionary<string, JsonElement> ReadManifest(string modRoot)
        => ReadManifest(modRoot, out _);

    /// <inheritdoc cref="ReadManifest(string)"/>
    /// <param name="readable">Whether a manifest was actually there and parsed as an object — what
    /// <see cref="HasReadableManifest"/> answers, returned alongside the contents so a caller that needs
    /// both does not read the file twice. An empty dictionary alone cannot say: a folder with no manifest
    /// and one holding <c>{}</c> both produce one, and only the second is a mod Proteus must refuse.</param>
    private static Dictionary<string, JsonElement> ReadManifest(string modRoot, out bool readable)
    {
        readable = false;
        var preserved = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(modRoot, MetaFile);
            if (!File.Exists(path)) return preserved;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return preserved;

            readable = true;
            foreach (var p in doc.RootElement.EnumerateObject())
                preserved[p.Name] = p.Value.Clone();
        }
        catch { /* unreadable — caller falls back to the older, universally-legible format */ }
        return preserved;
    }

    /// <summary>The <c>FileVersion</c> in an already-read manifest, defaulting to <see cref="LegacyFileVersion"/>.</summary>
    private static int FileVersionOf(Dictionary<string, JsonElement> manifest)
        => manifest.TryGetValue("FileVersion", out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var n) ? n : LegacyFileVersion;

    /// <summary>
    /// The folder's declared <c>FileVersion</c>, or <see cref="LegacyFileVersion"/> when there is no
    /// manifest or it can't be read. Penumbra rewrites this field when it migrates a mod on load, so it
    /// is a reliable statement of which format the INSTALLED Penumbra speaks — no version table needed.
    /// </summary>
    public static int ReadFileVersion(string modRoot)
        => FileVersionOf(ReadManifest(modRoot));

    /// <summary>
    /// A fresh manifest, at <see cref="SingleFileVersion"/>. See the type remarks.
    /// <para/>
    /// This is the one every importer and <c>ModCreationService</c> writes before its first
    /// <see cref="WriteRedirects"/>, so it is also what keeps those folders clear of
    /// <see cref="IsLegacyFolder"/>: the manifest exists and already declares v4 by the time any writer
    /// looks at it.
    /// </summary>
    /// <param name="version">The mod's own version string, when the source carries one (an imported pack).</param>
    /// <param name="website">The mod's home page, when the source carries one.</param>
    public static string NewMetaJson(string name, string author, string description,
        string? version = null, string? website = null)
        => JsonSerializer.Serialize(new
        {
            FileVersion = SingleFileVersion,
            Name        = name,
            Author      = author,
            Description = description,
            Version     = version ?? "",
            Website     = website ?? "",
            ModTags     = Array.Empty<string>(),
        }, WriteOptions);

    /// <summary>
    /// Writes one single-select option group, in whichever format the folder is already in — the group
    /// counterpart to <see cref="WriteRedirects"/>, and the only group writer. Every option is empty of
    /// redirects: the group exists purely so Penumbra shows a selector, and Proteus reads the SELECTION
    /// back through <c>OverlayOptionGroup.PenumbraGroupName</c> to decide which overlays to composite.
    /// <para/>
    /// <paramref name="index"/> is the group's ordinal (0-based); LOWER means higher priority.
    /// <c>SidecarDiscoveryService.ReadGroupOrder</c> reads it from the ARRAY POSITION in v4 and from the
    /// <c>group_NNN_</c> FILENAME in v3, and the two formats can only honour it to different degrees:
    /// <list type="bullet">
    /// <item><b>v4</b> splices at exactly <paramref name="index"/>, shifting the groups after it. Past the
    /// end appends.</item>
    /// <item><b>v3</b> takes file number <c>index + 1</c> if it is free, else the next free number above
    /// it. It CANNOT insert between two existing groups, because that would mean renumbering files this
    /// mod's author owns and Proteus didn't write. So on a populated v3 folder a colliding ordinal lands
    /// AFTER the group already holding it, not before.</item>
    /// </list>
    /// On a folder with no other groups — the importer's case, and the only one Proteus creates — both
    /// formats give the same answer.
    /// </summary>
    public static void WriteSingleSelectGroup(
        string modRoot, int index, string name, IReadOnlyList<string> optionNames, int defaultIndex)
    {
        if (optionNames.Count == 0) return;
        if (defaultIndex < 0 || defaultIndex >= optionNames.Count) defaultIndex = 0;
        Write(modRoot, index, name, optionNames, "Single", defaultIndex);
    }

    /// <summary>
    /// The multi-select counterpart: every option is independently on or off, and
    /// <paramref name="defaultSettings"/> is a BITMASK over them rather than an index — bit 0 is the first
    /// option. 0 leaves everything switched off.
    /// <para/>
    /// Used for the group the content importer synthesizes so individual pieces of a pack can be picked.
    /// Everything <see cref="WriteSingleSelectGroup"/> documents about ordinals, v3/v4 and same-name
    /// replacement applies here unchanged.
    /// </summary>
    public static void WriteMultiSelectGroup(
        string modRoot, int index, string name, IReadOnlyList<string> optionNames, ulong defaultSettings = 0)
    {
        if (optionNames.Count == 0) return;
        Write(modRoot, index, name, optionNames, "Multi", (long)defaultSettings);
    }

    /// <summary>
    /// Writes an <c>Imc</c> group: a set of checkboxes over one item's ten attribute bits, which is how the
    /// game itself switches parts of a model on and off.
    /// <para/>
    /// Unlike every other group Proteus writes, this one is not a selector Proteus reads back — it edits the
    /// game directly, and keeps working with Proteus switched off entirely. That is the whole point of it.
    /// <para/>
    /// <paramref name="entry"/> must be the item's REAL entry (see <see cref="ImcEntrySource"/>) with the new
    /// bits cleared. Penumbra replaces the whole entry, so every field of it that is not the attribute mask
    /// has to arrive unchanged or the item's material variant, decal or sound changes with it.
    /// <para/>
    /// Each option carries a single bit that is NOT in <paramref name="entry"/>'s mask, and that constraint
    /// is load-bearing: it makes the group's meaning the same whether Penumbra combines a selection with the
    /// default by OR or by XOR. Bits placed inside the default mask behave differently under the two, and
    /// nothing in this codebase is in a position to settle which one Penumbra does.
    /// </summary>
    /// <param name="defaultSettings">Bitmask over the options — bit 0 is the first. Ship this with every bit
    /// set so a mod gains switches without changing how it looks until one is unticked.</param>
    /// <param name="priority">
    /// Must beat any other <c>Imc</c> group in the mod that edits the SAME identifier. Penumbra keeps only
    /// the first group it reaches for one identifier and orders them by descending priority, so a group that
    /// loses that race is not merely overruled — it is never applied. See
    /// <see cref="ImcEntrySource.MaxPriorityFor"/>.
    /// </param>
    public static void WriteImcGroup(
        string modRoot, int index, string name, ImcIdentifier identifier, ImcEntry entry,
        IReadOnlyList<(string Name, ushort Mask)> options, ulong defaultSettings, int priority = 0)
    {
        if (options.Count == 0) return;

        var group = new Dictionary<string, object>
        {
            ["Type"] = "Imc",
            ["Name"] = name,
            ["Description"] = "",
            ["Priority"] = priority,
            ["DefaultSettings"] = defaultSettings,

            // Every variant of the item, attributes only.
            //
            // AllVariants because the variant an item is worn at cannot be known from a mod folder — it is
            // read off a material path if the mod happens to publish one, and defaults to 1 otherwise. An
            // edit pinned to the wrong variant produces a group whose checkboxes are present and inert,
            // which is indistinguishable from the switch being broken.
            //
            // OnlyAttributes because that breadth would otherwise be dangerous: without it Penumbra writes
            // this whole entry to every variant, so variant 2's material id would be replaced by variant
            // 1's and the item would load the wrong textures. With it, Penumbra sources each variant's own
            // entry and replaces nothing but the attribute mask — which is all a geometry switch wants.
            // DefaultEntry below remains the fallback for a variant the game has no entry for.
            ["AllVariants"] = true,
            ["OnlyAttributes"] = true,
            ["Identifier"] = new Dictionary<string, object>
            {
                ["ObjectType"] = identifier.ObjectType,
                ["PrimaryId"] = identifier.PrimaryId,
                ["Variant"] = identifier.Variant,
                ["EquipSlot"] = identifier.EquipSlot,
            },
            ["DefaultEntry"] = new Dictionary<string, object>
            {
                ["MaterialId"] = entry.MaterialId,
                ["DecalId"] = entry.DecalId,
                ["VfxId"] = entry.VfxId,
                ["MaterialAnimationId"] = entry.MaterialAnimationId,
                ["AttributeMask"] = entry.AttributeMask,
                ["SoundId"] = entry.SoundId,
            },
            ["Options"] = options
                .Select(o => new Dictionary<string, object> { ["Name"] = o.Name, ["AttributeMask"] = o.Mask })
                .ToList(),
        };

        if (index < 0) index = 0;

        WriteGroupIntoManifest(modRoot, ReadManifestForWrite(modRoot), index, name, _ => group);
    }

    /// <summary>
    /// Put <paramref name="ours"/> into an IMC group the mod ALREADY has, leaving everything else about the
    /// group exactly as its author wrote it.
    /// <para/>
    /// The alternative — writing a second group at a higher priority — cannot work, and that is the whole
    /// reason this exists. Penumbra keeps only one group per IMC identifier (see
    /// <see cref="ImcEntrySource.AppliedGroupFor"/>), so outranking an author's group does not overrule it,
    /// it deletes it: their switches stay listed in the mod's settings and stop doing anything, and every
    /// bit they drove freezes at whatever the surviving group's default says.
    /// <para/>
    /// Every property the group has is carried over as its raw <see cref="JsonElement"/> —
    /// <c>Priority</c>, <c>Identifier</c>, <c>AllVariants</c>, <c>OnlyAttributes</c>, <c>Description</c>,
    /// <c>Image</c>, <c>Page</c>, and anything a future Penumbra adds that this file has never heard of.
    /// Only <c>Options</c>, <c>DefaultSettings</c> and <c>DefaultEntry.AttributeMask</c> are rewritten.
    /// </summary>
    /// <param name="target">The group as read. Rewritten in place, at its own ordinal and under its own name.</param>
    /// <param name="ours">Our options, in the order they should appear, each carrying a single bit.</param>
    /// <param name="ownedNames">Option names Proteus owns. Dropped BEFORE <paramref name="ours"/> is
    /// appended, which is what makes writing twice replace our options rather than duplicate them — the
    /// caller re-emits its whole set every time.</param>
    /// <param name="entryMask">What <c>DefaultEntry</c>'s <c>AttributeMask</c> becomes: our bits cleared on
    /// a write, the author's original restored on a revert. Null leaves it as it is.</param>
    /// <param name="entryIfAbsent">Used only when the group carries no <c>DefaultEntry</c> object at all,
    /// which Penumbra's own serializer never produces. One has to exist for the invariant
    /// <see cref="WriteImcGroup"/> documents: our bit must sit OUTSIDE the default mask, or the option's
    /// meaning depends on whether Penumbra combines by OR or by XOR.</param>
    /// <returns>False when the merge would leave the group with no options at all — nothing is written, and
    /// the caller deletes the group instead.</returns>
    internal static bool MergeImcGroup(
        string modRoot, GroupRef target,
        IReadOnlyList<(string Name, ushort Mask)> ours,
        IReadOnlySet<string> ownedNames,
        ushort? entryMask,
        ImcEntry entryIfAbsent)
    {
        // Read — and refuse — up front, then hand the same manifest to the write below. The refusal has to
        // come before any of the option work, so a folder this will not touch is turned away rather than
        // rebuilt and then turned away; reusing the one read is what keeps that free.
        var manifest = ReadManifestForWrite(modRoot);

        // Kept options, each with the index it USED to sit at, so DefaultSettings can follow them.
        var kept = new List<(JsonElement Option, int OldIndex)>();
        if (target.Group.TryGetProperty("Options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            int at = 0;
            foreach (var o in opts.EnumerateArray())
            {
                int old = at++;
                var name = o.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (name != null && ownedNames.Contains(name)) continue;
                kept.Add((o, old));
            }
        }
        if (kept.Count == 0 && ours.Count == 0) return false;

        // DefaultSettings is a bitmask over option INDEX, so removing an option shifts every bit above it.
        // Rebuilt rather than masked: each survivor carries its old bit to its new position, and everything
        // of ours ships ticked — the same rule WriteImcGroup applies, so adding switches to a mod changes
        // nothing about how it looks until one is unticked.
        ulong oldDefaults = target.Group.TryGetProperty("DefaultSettings", out var ds)
                         && ds.ValueKind == JsonValueKind.Number && ds.TryGetUInt64(out var v) ? v : 0;
        ulong defaults = 0;
        for (int i = 0; i < kept.Count; i++)
            if (kept[i].OldIndex < 64 && (oldDefaults & (1UL << kept[i].OldIndex)) != 0)
                defaults |= 1UL << i;
        for (int i = 0; i < ours.Count; i++)
        {
            int at = kept.Count + i;
            if (at < 64) defaults |= 1UL << at;
        }

        var options = new List<object>(kept.Count + ours.Count);
        foreach (var (o, _) in kept) options.Add(o);
        foreach (var (name, mask) in ours)
            options.Add(new Dictionary<string, object> { ["Name"] = name, ["AttributeMask"] = mask });

        var merged = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var p in target.Group.EnumerateObject())
            if (p.Name is not ("Options" or "DefaultSettings" or "DefaultEntry"))
                merged[p.Name] = p.Value;

        merged["DefaultSettings"] = defaults;
        merged["Options"] = options;
        if (MergedEntry(target.Group, entryMask, entryIfAbsent) is { } defaultEntry)
            merged["DefaultEntry"] = defaultEntry;

        // Replaced by name and spliced at its own ordinal, which together are a no-op on position:
        // WriteGroupIntoManifest drops the same-named group from the array first, so every group before
        // this one keeps its index and inserting at that index puts it back exactly where it was.
        WriteGroupIntoManifest(modRoot, manifest, target.Index, target.Name, _ => merged);
        return true;
    }

    /// <summary>
    /// The group's own <c>DefaultEntry</c> with its attribute mask replaced, or a fresh one from
    /// <paramref name="fallback"/> when it has none. Every other field is carried over untouched: Penumbra
    /// replaces the whole entry, so inventing a <c>MaterialId</c> would point the item at a different
    /// material variant folder.
    /// </summary>
    private static object? MergedEntry(JsonElement group, ushort? mask, ImcEntry fallback)
    {
        // No entry and nothing to put in one. Only a WRITE needs the invariant that our bit sits outside
        // the default mask; a revert is taking options out, and synthesising an entry from a default-valued
        // fallback would hand the item MaterialId 0 — pointing it at a material folder that does not exist.
        if (mask is null
            && (!group.TryGetProperty("DefaultEntry", out var none) || none.ValueKind != JsonValueKind.Object))
            return null;

        if (!group.TryGetProperty("DefaultEntry", out var e) || e.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, object>
            {
                ["MaterialId"] = fallback.MaterialId,
                ["DecalId"] = fallback.DecalId,
                ["VfxId"] = fallback.VfxId,
                ["MaterialAnimationId"] = fallback.MaterialAnimationId,
                ["AttributeMask"] = (ushort)((mask ?? fallback.AttributeMask) & 0x3FF),
                ["SoundId"] = fallback.SoundId,
            };

        var entry = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
            if (p.Name != "AttributeMask")
                entry[p.Name] = p.Value;

        entry["AttributeMask"] = mask is { } m
            ? (ushort)(m & 0x3FF)
            : (ushort)((e.TryGetProperty("AttributeMask", out var a) && a.TryGetInt32(out var cur) ? cur : 0)
                       & 0x3FF);
        return entry;
    }

    /// <summary>
    /// How many option groups the mod has — what a caller wanting to append one at the end should pass as
    /// its ordinal.
    /// <para/>
    /// Zero covers both "no groups" and "no readable <c>Groups</c> array", which is the same answer for the
    /// only thing this is used for: a folder with nothing to count appends at the front, and a folder that
    /// is v3 never reaches a writer at all.
    /// </summary>
    public static int GroupCount(string modRoot) => TryReadGroups(modRoot)?.Count ?? 0;

    /// <summary>Which item an IMC edit names. Equipment and accessories only — see <see cref="ImcEntrySource.ImcPathFor"/>.</summary>
    public readonly record struct ImcIdentifier(string ObjectType, int PrimaryId, int Variant, string EquipSlot);

    /// <summary>
    /// One option group as it sits in the manifest, with what a rewrite needs to put it back where it was.
    /// </summary>
    /// <param name="Index">Its position in the whole <c>Groups</c> array — not its position among groups of
    /// its own kind, which is what a filtered enumeration would otherwise hand back.</param>
    /// <param name="Group">The raw element. Carried whole so a rewrite can preserve every field it does not
    /// itself own — see <see cref="MergeImcGroup"/>.</param>
    public readonly record struct GroupRef(string Name, int Index, JsonElement Group);

    /// <summary>
    /// Remove the group of this name. Used to undo a group Proteus wrote; a name that isn't there is not an
    /// error, since the point is to end up without it.
    /// </summary>
    public static void DeleteGroup(string modRoot, string name)
    {
        var manifest = ReadManifestForWrite(modRoot);
        if (!manifest.TryGetValue("Groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return;
        var others = groups.EnumerateArray()
            .Where(g => !(g.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                          && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, ProteusJson.WriterOptions))
        {
            w.WriteStartObject();
            foreach (var (key, value) in manifest)
            {
                if (key == "Groups") continue;
                w.WritePropertyName(key);
                value.WriteTo(w);
            }
            w.WritePropertyName("Groups");
            w.WriteStartArray();
            foreach (var g in others) g.WriteTo(w);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        AtomicWrite(Path.Combine(modRoot, MetaFile), System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void Write(
        string modRoot, int index, string name, IReadOnlyList<string> optionNames, string type, long defaultSettings)
    {
        if (index < 0) index = 0;

        WriteGroupIntoManifest(modRoot, ReadManifestForWrite(modRoot), index, name,
            slot => BuildGroup(slot, name, optionNames, type, defaultSettings));
    }

    /// <summary>
    /// The shape a group has on disk. <c>DefaultSettings</c> is the selected option's INDEX for a Single
    /// group (it is a bitmask only for Multi), and each option carries no redirects of its own.
    /// </summary>
    private static object BuildGroup(
        int index, string name, IReadOnlyList<string> optionNames, string type, long defaultSettings)
        => new
        {
            Version         = 0,
            Name            = name,
            Description     = "",
            Image           = "",
            Page            = 0,
            Priority        = index,
            Type            = type,
            DefaultSettings = defaultSettings,
            Options         = optionNames.Select(o => new
            {
                Name          = o,
                Description   = "",
                Files         = new Dictionary<string, string>(),
                FileSwaps     = new Dictionary<string, string>(),
                Manipulations = Array.Empty<object>(),
            }).ToArray(),
        };

    /// <summary>
    /// Splices the group into a v4 manifest's <c>Groups</c> array AT <paramref name="index"/>, replacing any
    /// group of the same name and preserving every other key — the same care <see cref="WriteDefaultData"/>
    /// takes, and for the same reason: <c>Identifier</c> is how Penumbra keys the mod.
    /// </summary>
    /// <param name="build">See <see cref="WriteLegacyGroupFile"/>.</param>
    private static void WriteGroupIntoManifest(
        string modRoot, Dictionary<string, JsonElement> preserved,
        int index, string name, Func<int, object> build)
    {
        // The surviving groups in order. Any group of the same name is DROPPED rather than kept alongside
        // the new one — a duplicate name is something Penumbra would have to disambiguate, and it would
        // also make the ordinal this method promises ambiguous.
        var others = new List<JsonElement>();
        if (preserved.TryGetValue("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var g in groups.EnumerateArray())
                if (!(g.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                      && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase)))
                    others.Add(g);

        // Clamped, not rejected: an index past the end is the ordinary "put it last" request, and a caller
        // that removed a group since computing the index shouldn't fail over an off-by-one.
        var slot = Math.Clamp(index, 0, others.Count);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, ProteusJson.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("FileVersion", SingleFileVersion);
            w.WriteString("Identifier",
                preserved.TryGetValue("Identifier", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()!
                    : Guid.NewGuid().ToString());

            foreach (var (key, value) in preserved)
            {
                if (key is "FileVersion" or "Identifier" or "Groups") continue;
                w.WritePropertyName(key);
                value.WriteTo(w);
            }

            w.WritePropertyName("Groups");
            w.WriteStartArray();
            for (int i = 0; i < others.Count; i++)
            {
                if (i == slot) JsonSerializer.Serialize(w, build(slot));
                others[i].WriteTo(w);
            }
            if (slot >= others.Count)
                JsonSerializer.Serialize(w, build(slot));
            w.WriteEndArray();

            w.WriteEndObject();
        }

        AtomicWrite(Path.Combine(modRoot, MetaFile), System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>
    /// Writes the mod's always-applied redirects into the manifest's <c>DefaultData</c> object.
    ///
    /// This is the ONLY entry point for writing redirects. <see cref="WriteDefaultData"/> is private on
    /// purpose — calling it directly skips the legacy refusal, which is how a v3 folder would end up with
    /// a <c>DefaultData</c> its own Penumbra has never heard of and silently ignores.
    /// </summary>
    public static void WriteRedirects(
        string modRoot, string modName,
        IDictionary<string, string> files,
        IDictionary<string, string>? swaps = null,
        IReadOnlyList<object>? manipulations = null)
    {
        WriteDefaultData(modRoot, modName, ReadManifestForWrite(modRoot), files, swaps, manipulations);
        // A folder Penumbra migrated keeps its old default_mod.json; drop it so a stale copy can't be
        // mistaken for the live redirect set.
        CleanLegacyFiles(modRoot);
    }

    /// <summary>
    /// Replaces the manifest's <c>DefaultData</c>, preserving every other key — critically
    /// <c>Identifier</c>, which is how Penumbra keys the mod, and any <c>Groups</c>/<c>ModTags</c>/
    /// <c>Image</c> the user set in Penumbra's UI. Creates the manifest if it is missing.
    /// <paramref name="preserved"/> is the already-read manifest, so this doesn't re-parse it.
    /// </summary>
    private static void WriteDefaultData(
        string modRoot, string modName,
        Dictionary<string, JsonElement> preserved,
        IDictionary<string, string> files,
        IDictionary<string, string>? swaps = null,
        IReadOnlyList<object>? manipulations = null)
    {
        var path = Path.Combine(modRoot, MetaFile);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, ProteusJson.WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("FileVersion", SingleFileVersion);
            w.WriteString("Identifier",
                preserved.TryGetValue("Identifier", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()!
                    : Guid.NewGuid().ToString());

            foreach (var (key, value) in preserved)
            {
                // Rewritten below or above; everything else passes through untouched.
                if (key is "FileVersion" or "Identifier" or "DefaultData") continue;
                w.WritePropertyName(key);
                value.WriteTo(w);
            }
            if (!preserved.ContainsKey("Name")) w.WriteString("Name", modName);

            w.WritePropertyName("DefaultData");
            w.WriteStartObject();
            w.WritePropertyName("Files");
            w.WriteStartObject();
            foreach (var (gamePath, relPath) in files) w.WriteString(gamePath, relPath);
            w.WriteEndObject();
            w.WritePropertyName("FileSwaps");
            w.WriteStartObject();
            if (swaps != null) foreach (var (from, to) in swaps) w.WriteString(from, to);
            w.WriteEndObject();
            w.WritePropertyName("Manipulations");
            w.WriteStartArray();
            if (manipulations != null)
                foreach (var m in manipulations)
                    JsonSerializer.Serialize(w, m, m.GetType());
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteEndObject();
        }

        AtomicWrite(path, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>
    /// Writes via a sibling temp file + atomic move, retrying with backoff: Penumbra's own file watcher
    /// can hold the target open for a moment right after a reload.
    /// <para/>
    /// The temp file is flushed to the DEVICE before the move, not merely handed to the OS cache. A
    /// rename is ordered, the data behind it is not: NTFS can commit the directory entry while the
    /// file's blocks are still unwritten, so a crash or power loss in that window leaves a file of the
    /// right length filled with zeros. Penumbra reports reading one as
    /// <c>'0x00' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0</c> and drops
    /// the mod — after which Proteus publishes redirects into a mod Penumbra has never heard of, and
    /// every path we own silently resolves elsewhere. Cheap insurance: these files are a few KB, and
    /// they are written once per composite at most.
    /// </summary>
    /// <param name="maxRetries">
    /// How many times to re-attempt the MOVE, sleeping 50 ms and doubling between tries. The default
    /// spends up to ~1.55 s outlasting Penumbra's watcher, which is the right trade for a background
    /// write — nobody is waiting on it.
    /// <para/>
    /// A caller on the ImGui draw thread or the framework thread must pass a small budget instead: those
    /// sleeps are frozen frames, and the editor provokes exactly the contention this loop waits out (it
    /// saves and then immediately recomposites, so Penumbra reloads and grabs the file just as the next
    /// edit lands). Losing that race costs one save that the next edit rewrites anyway; spending a second
    /// and a half of someone's colour-slider drag to win it does not pay.
    /// </param>
    public static void AtomicWrite(string target, string contents, int maxRetries = 5)
        // UTF-8 without a BOM, matching what File.WriteAllText would have produced.
        => AtomicWrite(target, new System.Text.UTF8Encoding(false).GetBytes(contents), maxRetries);

    /// <summary>
    /// The binary form, for the same reason the text one exists: a mod's own model file is rewritten in
    /// place while Penumbra may be serving it, and a torn write there is a model that fails to load.
    /// </summary>
    public static void AtomicWrite(string target, byte[] bytes, int maxRetries = 5)
    {
        var tmp = target + "." + Guid.NewGuid().ToString("N") + TempSuffix;

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                   bufferSize: 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }

        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, target, overwrite: true); return; }
            // Shift clamped at 5 so an oversized budget backs off to 1.6 s a try, never overflows into one.
            catch (Exception) when (i < maxRetries) { Thread.Sleep(50 << Math.Min(i, 5)); }
            catch { try { File.Delete(tmp); } catch { } throw; }      // don't leave the temp behind
        }
    }

    /// <summary>Extension <see cref="AtomicWrite"/> gives its sibling temp file, which is named
    /// <c>&lt;target&gt;.&lt;32 hex&gt;.tmp</c>. Shared with the sweep so the two can't drift.</summary>
    private const string TempSuffix = ".tmp";

    /// <summary>
    /// Removes a v3 <c>default_mod.json</c> now superseded by <c>DefaultData</c>, plus any orphaned temp
    /// files left by an interrupted <see cref="AtomicWrite"/>.
    /// </summary>
    public static void CleanLegacyFiles(string modRoot)
    {
        try
        {
            var legacy = Path.Combine(modRoot, LegacyDefaultMod);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch { /* best effort — a locked legacy file must not skip the sweep below */ }

        // EVERY AtomicWrite target, not just default_mod.json's: the sweep used to name that one file, so
        // once meta.json and metadata.json started going through AtomicWrite their temps were stranded for
        // good. meta.json sits in the mod root, metadata.json one level down in the sidecar folder.
        SweepAtomicTemps(modRoot);
        SweepAtomicTemps(Path.Combine(modRoot, SidecarDiscoveryService.SidecarSubdir));
    }

    /// <summary>
    /// Deletes <see cref="AtomicWrite"/> temp files orphaned in <paramref name="dir"/> (non-recursive).
    /// Matched on the full <c>&lt;name&gt;.&lt;32 hex&gt;.tmp</c> shape rather than a bare <c>*.tmp</c>,
    /// so a mod that ships a .tmp of its own is never touched.
    /// </summary>
    private static void SweepAtomicTemps(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var tmp in Directory.EnumerateFiles(dir, "*" + TempSuffix))
            {
                var stem = Path.GetFileNameWithoutExtension(tmp);   // drops ".tmp"
                int dot = stem.LastIndexOf('.');
                if (dot < 1 || stem.Length - dot - 1 != 32) continue;
                bool hex = true;
                for (int i = dot + 1; i < stem.Length && hex; i++) hex = Uri.IsHexDigit(stem[i]);
                if (!hex) continue;
                try { File.Delete(tmp); } catch { }
            }
        }
        catch { /* best effort — leftovers are inert */ }
    }
}
