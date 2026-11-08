using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// What the design-restore sweep decides, as pure functions over what was read: which drawn mods a design doesn't have
/// must stay on because something the design keeps needs a file only they supply and the game has none of, which
/// holds the player put back, and whether the evidence can be trusted at all. Every doubt about the kept mods'
/// evidence fails toward holding nothing — a stray left drawn is a cosmetic slip, a provider held off is an
/// invisible body.
/// </summary>
internal static partial class DesignRestoreSweep
{
    /// <summary>Default-data paths sampled per held bound mod for <see cref="ResolveLooksSane"/>.</summary>
    internal const int SanitySamplePerMod = 4;

    /// <summary>Retries a window gets after transient blocks.</summary>
    internal const int MaxBlockRetriesPerWindow = 2;

    /// <summary>What needed a spared mod's file: a kept (or spared) mod's directory, or <see cref="CompositeNeed"/>.</summary>
    internal readonly record struct SweepNeed(string GamePath, string NeededBy);

    internal const string CompositeNeed = "Proteus composite";

    /// <summary>
    /// One sweep's evidence. <see cref="Candidates"/> are the drawn mods the design lacks plus the strays a same-design
    /// re-apply carried; <see cref="Held"/> the strays the sweep holds off now. <see cref="Resolved"/> is keyed by
    /// normalised game path and holds what the player's collection resolves it to (null: nothing).
    /// <see cref="StaysOn"/> are strays an earlier sweep left on or the player put back: never held, but what their
    /// active files need still counts, so a chain through them holds across sweeps. They never block a round.
    /// </summary>
    internal sealed record SweepInputs(
        string ModsRoot,
        string ManagedDir,
        IReadOnlyDictionary<string, ModDirFiles> Kept,
        IReadOnlySet<string> SanityDirs,
        IReadOnlyDictionary<string, ModDirFiles> Candidates,
        IReadOnlyDictionary<string, ModDirFiles> Held,
        IReadOnlyDictionary<string, string?> Resolved,
        IReadOnlyList<(string GamePath, string Source)> Upstreams,
        Func<string, bool> GameFileExists,
        IReadOnlyDictionary<string, ModDirFiles>? StaysOn = null);

    internal enum SweepBlock { None, MapUnreadable, SettingsUnreadable, KeptManifest, KeptActiveFileUnreadable, ResolveFailed, ResolveInsane }

    /// <summary>Every block worth another try later in the window.</summary>
    internal static bool IsTransient(SweepBlock b) => b != SweepBlock.None;

    // ── normalisation ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A file path for comparison: forward slashes, Penumbra's leading <c>|…|</c> marker stripped, trimmed.
    /// Compare OrdinalIgnoreCase.</summary>
    internal static string NormFile(string path)
    {
        var p = path.Trim();
        if (p.StartsWith('|'))
        {
            int close = p.IndexOf('|', 1);
            if (close > 0) p = p[(close + 1)..];
        }
        return p.Replace('\\', '/');
    }

    /// <summary>A game path for comparison and lookup: lower case, forward slashes, trimmed.</summary>
    internal static string NormGame(string gamePath) => gamePath.Trim().Replace('\\', '/').ToLowerInvariant();

    [GeneratedRegex(@"^mt_c\d{4}b\d{4}_(?<suffix>.+)\.mtrl$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkinMaterial();

    /// <summary>
    /// How a material is matched between a model's declared name and a mod's material path. A shared-body skin material
    /// (<c>mt_c####b####_suffix.mtrl</c>) has the wearer's race and body swapped in by the game, so it matches by suffix
    /// alone; every other material by its exact file name.
    /// </summary>
    internal static string MaterialKey(string nameOrPath)
    {
        var p = nameOrPath.Replace('\\', '/');
        var name = p[(p.LastIndexOf('/') + 1)..].ToLowerInvariant();
        var m = SkinMaterial().Match(name);
        return m.Success ? "skin:" + m.Groups["suffix"].Value : "file:" + name;
    }

    private static readonly IReadOnlyDictionary<string, ModDirFiles> Empty = new Dictionary<string, ModDirFiles>();

    private static bool IsMaterial(string gamePath) => gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase);
    private static bool IsModel(string gamePath) => gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);

    private static Func<string, bool> Safe(Func<string, bool> exists) => p =>
    {
        try { return exists(p); }
        catch { return false; }   // can't tell: treat as absent, which spares
    };

    // ── evidence checks ─────────────────────────────────────────────────────────────────────────────────────────

    private static string? Owner(SweepInputs i, string normGame)
        => i.Resolved.TryGetValue(normGame, out var r) && r != null
            ? DesignBindingService.OwningMod(i.ModsRoot, NormFile(r))
            : null;

    private static bool Active(SweepInputs i, ModFile f)
        => i.Resolved.TryGetValue(NormGame(f.GamePath), out var r) && r != null
           && string.Equals(NormFile(r), NormFile(f.FullPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>The paths the phase-2 resolve asks about, bounded: kept materials and models, a few default-data paths of
    /// each sanity mod, and the strays' materials, models and game-less files.</summary>
    internal static IReadOnlyList<string> ResolveQuery(SweepInputs i)
    {
        var exists = Safe(i.GameFileExists);
        var q = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var files in (i.StaysOn ?? Empty).Values)
            foreach (var f in files.Files)
            {
                var g = NormGame(f.GamePath);
                if (IsMaterial(g) || IsModel(g)) q.Add(g);
            }
        foreach (var (dir, files) in i.Kept)
        {
            foreach (var f in files.Files)
            {
                var g = NormGame(f.GamePath);
                if (IsMaterial(g) || IsModel(g)) q.Add(g);
            }
            if (i.SanityDirs.Contains(dir))
                foreach (var g in SanitySample(files)) q.Add(g);
        }
        foreach (var files in i.Candidates.Values.Concat(i.Held.Values))
            foreach (var f in files.Files)
            {
                var g = NormGame(f.GamePath);
                if (IsMaterial(g) || IsModel(g) || !exists(g)) q.Add(g);
            }
        return q.ToList();
    }

    private static IEnumerable<string> SanitySample(ModDirFiles files)
        => files.Files.Where(f => f.DefaultData).Select(f => NormGame(f.GamePath))
                .Distinct().Order(StringComparer.Ordinal).Take(SanitySamplePerMod);

    /// <summary>
    /// Whether the resolve looks like the design's collection: every held bound mod with default data has at least one
    /// sampled default path resolving into a kept mod. "Enable Mods" off, a fallback collection or a zone change make
    /// none resolve. Proteus mods, the managed mod and option-only mods never count.
    /// </summary>
    internal static bool ResolveLooksSane(SweepInputs i)
    {
        foreach (var dir in i.SanityDirs)
        {
            if (!i.Kept.TryGetValue(dir, out var files) || files.ManifestFailed) continue;
            var sample = SanitySample(files).ToList();
            if (sample.Count == 0) continue;
            if (!sample.Any(g => Owner(i, g) is { } o && i.Kept.ContainsKey(o))) return false;
        }
        return true;
    }

    /// <summary>Why the kept evidence can't be trusted this round, and the mod it is about; the managed mod never
    /// blocks (its game-less bases come through the compositor's upstreams).</summary>
    internal static (SweepBlock Block, string? Dir) Blocked(SweepInputs i)
    {
        foreach (var (dir, files) in i.Kept)
        {
            if (string.Equals(dir, i.ManagedDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (files.ManifestFailed) return (SweepBlock.KeptManifest, dir);
        }
        foreach (var (dir, files) in i.Kept)
        {
            if (string.Equals(dir, i.ManagedDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (files.Files.Any(f => f.Unreadable && Active(i, f))) return (SweepBlock.KeptActiveFileUnreadable, dir);
        }
        return ResolveLooksSane(i) ? (SweepBlock.None, null) : (SweepBlock.ResolveInsane, null);
    }

    /// <summary>
    /// The strays that supply a game-less file something kept needs, each with what needed which path. Needs start from
    /// the kept mods' active files (texture paths of their materials, material keys of their models) and grow with each
    /// stray found to be a provider until nothing changes. A stray supplies a path when the collection resolves it into
    /// that stray (a candidate), resolves it to nothing (a held stray, whose files would come back), or resolves it into
    /// the managed mod with the compositor naming the stray as that base's upstream.
    /// </summary>
    internal static Dictionary<string, List<SweepNeed>> FindProviders(SweepInputs i)
    {
        var exists = Safe(i.GameFileExists);
        var providers = new Dictionary<string, List<SweepNeed>>(StringComparer.OrdinalIgnoreCase);
        var textureNeeds = new Dictionary<string, string>(StringComparer.Ordinal);   // game path → needed by
        var keyNeeds = new Dictionary<string, string>(StringComparer.Ordinal);       // material key → needed by

        void AddNeeds(string dir, ModFile f)
        {
            var g = NormGame(f.GamePath);
            if (IsMaterial(g))
                foreach (var t in f.Refs) textureNeeds.TryAdd(NormGame(t), dir);
            else if (IsModel(g))
                foreach (var m in f.Refs) keyNeeds.TryAdd(MaterialKey(m), dir);
        }

        foreach (var (dir, files) in i.Kept.Concat(i.StaysOn ?? Empty))
            foreach (var f in files.Files)
                if (Active(i, f)) AddNeeds(dir, f);

        var strays = new Dictionary<string, (ModDirFiles Files, bool Held)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, files) in i.Candidates) strays[dir] = (files, false);
        foreach (var (dir, files) in i.Held) strays.TryAdd(dir, (files, true));

        bool Supplies(string dir, bool held, string g)
        {
            var owner = Owner(i, g);
            if (owner == null) return held;
            if (string.Equals(owner, dir, StringComparison.OrdinalIgnoreCase)) return !held;
            if (string.Equals(owner, i.ManagedDir, StringComparison.OrdinalIgnoreCase))
                return i.Upstreams.Any(u => string.Equals(NormGame(u.GamePath), g, StringComparison.Ordinal)
                                            && string.Equals(u.Source, dir, StringComparison.OrdinalIgnoreCase));
            return false;
        }

        bool Counts(string dir, bool held, ModFile f)
            => held ? Owner(i, NormGame(f.GamePath)) == null : Active(i, f);

        void MarkProvider(string dir, SweepNeed need)
        {
            if (!providers.TryGetValue(dir, out var needs)) providers[dir] = needs = [];
            needs.Add(need);
        }

        // The compositor's upstreams: a game-less base it stands on whose real supplier is a stray.
        foreach (var (gamePath, source) in i.Upstreams)
        {
            if (source.IndexOfAny(['/', '\\', ':']) >= 0 || !strays.ContainsKey(source)) continue;
            var g = NormGame(gamePath);
            if (!exists(g)) MarkProvider(source, new SweepNeed(g, CompositeNeed));
        }
        foreach (var dir in providers.Keys.ToList())
            foreach (var f in strays[dir].Files.Files)
                if (Counts(dir, strays[dir].Held, f)) AddNeeds(dir, f);

        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (dir, (files, held)) in strays)
            {
                if (providers.ContainsKey(dir)) continue;
                var found = new List<SweepNeed>();
                foreach (var f in files.Files)
                {
                    var g = NormGame(f.GamePath);
                    string? by = textureNeeds.TryGetValue(g, out var t) ? t
                               : IsMaterial(g) && keyNeeds.TryGetValue(MaterialKey(g), out var k) ? k
                               : null;
                    if (by == null || exists(g) || !Supplies(dir, held, g)) continue;
                    found.Add(new SweepNeed(g, by));
                }
                if (found.Count == 0) continue;
                providers[dir] = found.DistinctBy(n => n.GamePath).ToList();
                foreach (var f in files.Files)
                    if (Counts(dir, held, f)) AddNeeds(dir, f);
                changed = true;
            }
        }
        return providers;
    }

    // ── holds the player put back ───────────────────────────────────────────────────────────────────────────────

    internal enum HoldVerdict { Keep, Released, Forget }

    /// <summary>
    /// What became of each hold the sweep placed. Released: the player put it back (dropped the hold, or turned the held
    /// mod on). Forget: it vanished after another plugin had taken it over. Keep: still ours and off, someone else's,
    /// or Penumbra couldn't answer. <paramref name="sourcesSeen"/> is the source to remember per dir.
    /// </summary>
    internal static Dictionary<string, HoldVerdict> ClassifySweepHolds(
        IReadOnlyDictionary<string, PenumbraBridge.TemporaryHold?> holds, IReadOnlyDictionary<string, string> lastSource,
        string ourSource, out Dictionary<string, string> sourcesSeen)
    {
        var verdicts = new Dictionary<string, HoldVerdict>(StringComparer.OrdinalIgnoreCase);
        sourcesSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, hold) in holds)
        {
            lastSource.TryGetValue(dir, out var last);
            bool lastOurs = last == null || string.Equals(last, ourSource, StringComparison.OrdinalIgnoreCase);
            if (hold is not { } h) { verdicts[dir] = HoldVerdict.Keep; continue; }
            if (!h.Present)
            {
                verdicts[dir] = lastOurs ? HoldVerdict.Released : HoldVerdict.Forget;
                continue;
            }
            sourcesSeen[dir] = h.Source;
            bool ours = string.Equals(h.Source, ourSource, StringComparison.OrdinalIgnoreCase);
            verdicts[dir] = ours && h.Enabled ? HoldVerdict.Released : HoldVerdict.Keep;
        }
        return verdicts;
    }

    /// <summary>Whether every one of the design's own holds is gone at once — Penumbra dropped its temporary settings,
    /// not the player. False when there were none to lose or the settings are unreadable.</summary>
    internal static bool DesignHoldsLost(IReadOnlyCollection<string> designHoldDirs,
                                         IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot>? effective)
    {
        if (effective == null || designHoldDirs.Count == 0) return false;
        return designHoldDirs.All(d => !effective.TryGetValue(d, out var s) || !s.Temporary);
    }

    /// <summary>The mods the design keeps drawn: its bound mods and Proteus mods that are on, and the managed mod.</summary>
    internal static HashSet<string> KeptDirs(IReadOnlySet<string> bound, IReadOnlySet<string> proteusDirs, string managedDir,
                                             IReadOnlyDictionary<string, PenumbraBridge.ModSettingsSnapshot> effective)
    {
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { managedDir };
        foreach (var d in bound.Concat(proteusDirs))
            if (effective.TryGetValue(d, out var s) && s.Enabled) kept.Add(d);
        return kept;
    }

    /// <summary>
    /// A same-design re-apply's carry: the strays the sweep still held, joined with the ones an earlier re-apply carried
    /// that no completed check has decided yet — filtered by the same rules, so a second quick re-apply can't drop them.
    /// </summary>
    internal static HashSet<string> MergeCarry(IEnumerable<string> fresh, IReadOnlySet<string> pending,
                                            IReadOnlySet<string> bound, IReadOnlySet<string> proteusDirs,
                                            IReadOnlySet<string> installed, string managedDir,
                                            IReadOnlySet<string> playerReleased, IReadOnlyDictionary<string, string> lastSource,
                                            string ourSource)
    {
        var merged = new HashSet<string>(fresh, StringComparer.OrdinalIgnoreCase);
        foreach (var d in pending)
        {
            if (bound.Contains(d) || proteusDirs.Contains(d) || !installed.Contains(d) || playerReleased.Contains(d)) continue;
            if (string.Equals(d, managedDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (lastSource.TryGetValue(d, out var src) && !string.Equals(src, ourSource, StringComparison.OrdinalIgnoreCase)) continue;
            merged.Add(d);
        }
        return merged;
    }
}
