using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// The size set run headlessly on an unpacked mod — <c>Proteus.Refit --size-set</c>, for a batch over many packs. It
/// builds the request the in-game panel builds (<see cref="Gui.SizeSetPanel"/>) from plain files: the bodies from the
/// folders under Penumbra's mod folder, the game's data from the game, the men's skeleton from the game or a file.
/// <para/>
/// The mod is a work copy outside that folder, and a v3 one is converted first (<see cref="PenumbraModMeta.MigrateWorkCopy"/>)
/// — only when it has pieces to size, so a mod with none is left exactly as it was. Every body mod is only read.
/// <para/>
/// One difference from the panel: in game, Penumbra's mod list includes the mod being sized, so a body mod could be its
/// own garments' source. Here the mod is never one of the bodies.
/// </summary>
internal static class SizeSetBatch
{
    /// <param name="ModRoot">The unpacked mod: a work copy, outside <paramref name="ModsRoot"/>.</param>
    /// <param name="ModsRoot">Penumbra's mod folder: where the bodies are found, by their files.</param>
    /// <param name="Keys">The women's and men's bodies asked for (<see cref="ParseKeys"/>).</param>
    /// <param name="Men">Men's versions of the women's pieces, and men's pieces onto the men's bodies.</param>
    /// <param name="Ends">Shoes and gloves too.</param>
    /// <param name="MensSkeleton">A men's base skeleton file the men's versions are made for; null for the game's own.</param>
    /// <param name="ModDirs">The folders under <paramref name="ModsRoot"/> to look for bodies in; null for all of them.
    /// For tests.</param>
    internal sealed record Inputs(string ModRoot, string ModsRoot, IReadOnlyList<string> Keys, bool Men, bool Ends,
                                  Func<string, byte[]?> ReadGameFile, UVRemapService? UvRemap, IPluginLog Log,
                                  string? MensSkeleton = null, IReadOnlyCollection<string>? ModDirs = null);

    /// <summary>A body file the run found for a key, by its folder; null when none is installed.</summary>
    internal sealed record BodyMod(string Key, string Slot, bool Yas, string? Folder);

    /// <summary>What a run did, beside its <see cref="SizeSetRefit.RunLog"/>.</summary>
    /// <param name="RefusalCode">Set when nothing ran: "manifest-unreadable", "version-too-old" or "migration-failed".</param>
    internal sealed record Outcome(IReadOnlyList<string> Keys, string? RefusalCode, string? Refusal,
                                   PenumbraModMeta.MigrationOutcome? Migrated, bool Cancelled,
                                   IReadOnlyList<BodyMod> BodyMods, IReadOnlyList<string> MissingBodies,
                                   double Seconds, double ScanSeconds, double MigrateSeconds);

    /// <summary>The women's bodies, by key, in the table's order: what "all" means.</summary>
    internal static IReadOnlyList<string> WomensKeys
        => SupportedBodies.Choices.Where(c => !c.Male).Select(c => c.Key).ToList();

    /// <summary>
    /// The bodies asked for, as the table's keys in its order: case, spaces and repeats do not matter, "all" is every
    /// women's body. Null, with why, when a key is not the table's or none is given.
    /// </summary>
    internal static IReadOnlyList<string>? ParseKeys(string list, out string? error)
    {
        error = null;
        var asked = list.Split(',').Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).ToList();
        if (asked.Count == 0)
        {
            error = $"--bodies names no body; one or more of: {string.Join(", ", SupportedBodies.Choices.Select(c => c.Key))}, or all";
            return null;
        }
        if (asked.Contains("all")) asked = [.. asked.Where(k => k != "all"), .. WomensKeys];
        var unknown = asked.Where(k => SupportedBodies.Choices.All(c => c.Key != k)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            error = $"--bodies: no body called {string.Join(", ", unknown)}; one or more of: "
                  + $"{string.Join(", ", SupportedBodies.Choices.Select(c => c.Key))}, or all";
            return null;
        }
        return SupportedBodies.Choices.Select(c => c.Key).Where(asked.Contains).ToList();
    }

    /// <summary>Why a run cannot start, or null. Nothing is read beyond what the check needs and nothing is written.</summary>
    internal static string? Validate(string modRoot, string modsRoot, string game, string? skeleton)
    {
        if (File.Exists(modRoot))
            return modRoot.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase) || modRoot.EndsWith(".ttmp2", StringComparison.OrdinalIgnoreCase)
                ? $"--size-set {modRoot} is a pack: unpack it first, into a work folder"
                : $"--size-set {modRoot} is a file, not a mod folder";
        if (!Directory.Exists(modRoot)) return $"--size-set {modRoot} does not exist";
        if (!File.Exists(Path.Combine(modRoot, PenumbraModMeta.MetaFile)))
            return $"--size-set {modRoot} has no {PenumbraModMeta.MetaFile}: not a mod folder";
        if (!Directory.Exists(modsRoot)) return $"--mods-root {modsRoot} does not exist";
        if (PenumbraModMeta.IsAtOrUnder(modRoot, modsRoot))
            return $"--size-set {modRoot} is inside --mods-root: size a work copy, never an installed mod";
        if (!Directory.Exists(Path.Combine(game, "ffxiv")))
            return $"--game {game} is not the game's sqpack folder (it has no ffxiv folder)";
        if (skeleton != null && !File.Exists(skeleton)) return $"--skeleton {skeleton} does not exist";
        return null;
    }

    /// <summary>The keys a run goes through for a mod of <paramref name="pieces"/>: what is offered for it, in the
    /// panel's order, of what was asked — and with <paramref name="men"/>, the men's bodies, for men's pieces and for the
    /// women's pieces' men's versions both.</summary>
    internal static IReadOnlyList<string> RunKeys(IReadOnlyList<SizeSetRefit.Piece> pieces, IReadOnlyList<string> asked, bool men)
    {
        var wanted = new HashSet<string>(asked, StringComparer.Ordinal);
        if (men)
            foreach (var (key, _, _) in SupportedBodies.Choices.Where(c => c.Male))
            {
                wanted.Add(key);
                wanted.Add(key + SizeSetRefit.AcrossSuffix);
            }
        return SizeSetRefit.Offered(pieces).Select(c => c.Key).Where(wanted.Contains).ToList();
    }

    /// <summary>
    /// Make every size. Long: minutes per piece. Fills <paramref name="runLog"/> as it goes, so a run stopped by
    /// <paramref name="ct"/> still tells what it saved; the stop is reported, not thrown.
    /// </summary>
    internal static Outcome Run(Inputs inputs, SizeSetRefit.RunLog runLog, Action<string> progress, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        string root = inputs.ModRoot;
        Outcome Refused(string code, string why, IReadOnlyList<string>? keys = null)
            => new(keys ?? [], code, why, null, false, [], [], total.Elapsed.TotalSeconds, 0, 0);
        if (!PenumbraModMeta.HasReadableManifest(root))
            return Refused("manifest-unreadable", $"{PenumbraModMeta.MetaFile} could not be read");
        if (PenumbraModMeta.ReadFileVersion(root) is var version && version < PenumbraModMeta.LegacyFileVersion)
            return Refused("version-too-old", $"FileVersion {version} is older than {PenumbraModMeta.LegacyFileVersion}");

        // ── the bodies: every folder under the mod folder, by name, read as the panel's index reads them ──
        progress("Finding the bodies");
        var scan = Stopwatch.StartNew();
        var bodies = ScanBodies(inputs.ModsRoot, inputs.ModDirs);
        double scanSeconds = scan.Elapsed.TotalSeconds;

        // ── the pieces, read off the mod as it is: v3 reads too ──
        var pieces = SizeSetRefit.PiecesOf(root, inputs.ReadGameFile, inputs.Ends);
        var keys = RunKeys(pieces, inputs.Keys, inputs.Men);
        var bodyKeys = keys.Select(k => k.Replace(SizeSetRefit.AcrossSuffix, "", StringComparison.Ordinal)).Distinct().ToList();
        var bodyMods = BodyModsOf(bodyKeys, bodies);
        var missing = bodyKeys.Where(k => !SupportedBodies.Installed(k, bodies)).ToList();
        if (pieces.Count == 0)
        {
            // Nothing to size: said as the panel says it, and the mod left exactly as it was — not even converted.
            SizeSetRefit.NoPieces(runLog);
            return new Outcome(keys, null, null, null, false, bodyMods, missing, total.Elapsed.TotalSeconds, scanSeconds, 0);
        }

        // ── a v3 work copy becomes v4, now that there is something to write ──
        // The pieces read off it are only used for what the mod offers; the run reads them again from the v4 manifest.
        PenumbraModMeta.MigrationOutcome? migrated = null;
        var migrate = Stopwatch.StartNew();
        if (PenumbraModMeta.IsLegacyFolder(root))
            try
            {
                migrated = PenumbraModMeta.MigrateWorkCopy(root, inputs.ModsRoot);
                if (migrated is { Leftover.Count: > 0 } left)
                    runLog.Warnings.Add($"v3 files left after the conversion (no longer read): {string.Join(", ", left.Leftover)}");
            }
            catch (PenumbraModMeta.MigrationRefusedException e)
            {
                return Refused(e.Code == "forbidden-root" ? "migration-failed" : e.Code, e.Message, keys);
            }
        double migrateSeconds = migrate.Elapsed.TotalSeconds;

        bool cancelled = false;
        try
        {
            SizeSetRefit.Run(Request(inputs, keys, bodies), runLog, inputs.UvRemap, inputs.Log, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        return new Outcome(keys, null, null, migrated, cancelled, bodyMods, missing, total.Elapsed.TotalSeconds,
                           scanSeconds, migrateSeconds);
    }

    private static SizeSetRefit.Request Request(Inputs inputs, IReadOnlyList<string> keys, IReadOnlyList<BodyModIndex.Entry> bodies)
    {
        var gameBodies = SizeSetRefit.GameBodies(keys, inputs.ReadGameFile, inputs.Ends);
        string mensBase = OtherRaceWearer.BaseSkeletonPath(CrossSexRefit.Men);
        var across = SizeSetRefit.MensKeys(keys).Count > 0
            ? new SizeSetRefit.AcrossInputs(path => inputs.MensSkeleton != null
                                                    && string.Equals(path, mensBase, StringComparison.OrdinalIgnoreCase)
                                                    ? inputs.MensSkeleton : null)
            : null;
        return new SizeSetRefit.Request(inputs.ModRoot, keys, Yas: true, Remake: false, bodies, inputs.ReadGameFile,
                                        inputs.Ends, race => gameBodies.GetValueOrDefault(race), Piercings: false, across);
    }

    /// <summary>
    /// The body mods under Penumbra's mod folder, read as the panel's index reads Penumbra's list: by folder NAME — what
    /// a refit records, and what may travel in a pack — each called what its manifest calls it.
    /// </summary>
    /// <param name="modDirs">The folders to look in; null for every one.</param>
    internal static IReadOnlyList<BodyModIndex.Entry> ScanBodies(string modsRoot, IReadOnlyCollection<string>? modDirs = null)
    {
        var dirs = (modDirs ?? Directory.GetDirectories(modsRoot).Select(Path.GetFileName).OfType<string>().ToList())
                   .Select(d => Path.GetFileName(d.TrimEnd('\\', '/')))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToDictionary(d => d, d => NameOf(Path.Combine(modsRoot, d)) ?? d, StringComparer.OrdinalIgnoreCase);
        return new BodyModIndex().Refresh(dirs, modsRoot);
    }

    /// <summary>Which folder each body's plain and YAS rows resolve to, as the run finds them.</summary>
    internal static List<BodyMod> BodyModsOf(IEnumerable<string> keys, IReadOnlyList<BodyModIndex.Entry> bodies)
    {
        var rows = new List<BodyMod>();
        foreach (string key in keys)
            foreach (string slot in new[] { "_top", "_dwn" })
                foreach (bool yas in new[] { false, true })
                    if (SupportedBodies.Of(key, slot, yas) is { } body)
                        rows.Add(new BodyMod(key, slot, yas, SupportedBodies.Find(body, bodies)?.Dir));
        return rows;
    }

    /// <summary>What Penumbra would call a mod: its manifest's Name, or null when that cannot be read.</summary>
    private static string? NameOf(string modRoot)
    {
        try
        {
            string path = Path.Combine(modRoot, PenumbraModMeta.MetaFile);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Name", out var n)
                   && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
