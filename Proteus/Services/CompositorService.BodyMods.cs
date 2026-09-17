using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Penumbra.Api.Enums;

namespace Proteus.Services;

public partial class CompositorService
{
    // ── Body-mod detection ──────────────────────────────────────────────────────
    // Whether a mod redirects skin-surface files, which can make _activeMtrlSnapshot wrong without a redraw.
    // Detected from the mod's manifests on disk (no IPC), evaluated off the framework thread.

    // Bump whenever BodyMaterialPattern changes what it matches or a BodyModCacheEntry verdict gains a new meaning:
    // it seeds every fingerprint, so raising it forces one re-scan per mod. Do not remove a bump because the pattern
    // is unchanged: 4 is for AffectsComposite/BaseKeysHash, which older configs deserialise as (false, 0).
    private const int SurfaceModClassifierVersion = 4;

    private static readonly Regex BodyMaterialPattern = new(
        // Every skin surface and any file under it, not just body materials: face and hair mods usually ship
        // textures alone, and they move "is this material on the character" just as a body mod does.
        @"obj[/\\](body|face|hair|tail|zear)[/\\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A mod's own redirect manifest(s): meta.json (v4), or the legacy default_mod.json/group_*.json pair.
    private static bool IsModManifestFile(string fileName)
        => string.Equals(fileName, PenumbraModMeta.MetaFile, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, PenumbraModMeta.LegacyDefaultMod, StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("group_", StringComparison.OrdinalIgnoreCase);

    // Sums size + mtime over a mod's manifest files, so a mod update is detected without a restart.
    private static long ComputeModFingerprint(string modRoot)
    {
        // Seeded with the classifier version, not 0, so widening what counts as a surface material retires
        // every cached verdict at once (see SurfaceModClassifierVersion).
        long fp = SurfaceModClassifierVersion;
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (!IsModManifestFile(Path.GetFileName(file))) continue;
                var info = new FileInfo(file);
                fp = unchecked(fp * 31 + info.Length + info.LastWriteTimeUtc.Ticks);
            }
        }
        catch { /* modRoot missing/unreadable */ }
        return fp;
    }

    // Every game path a manifest redirects; one quoted-string match over the raw text covers both layouts.
    private static readonly Regex ManifestGamePathPattern = new(
        // Not anchored to "chara/": skin mods invent paths outside the usual trees, and over-matching is harmless
        // (the set is only tested for overlap). The backslash exclusion drops a Files entry's local disk path.
        @"""([^""\\]+\.(?:tex|mtrl|mdl))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// One pass over a mod's manifests: does it touch a skin surface (<paramref name="Surface"/>), and which game
    /// paths does it provide (<paramref name="Paths"/>).
    /// </summary>
    private static (bool Surface, HashSet<string> Paths) ScanModManifests(string modRoot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var surface = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(modRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (!IsModManifestFile(Path.GetFileName(file))) continue;
                var text = File.ReadAllText(file);
                // Only until true: skips redundant regex passes on multi-group mods.
                if (!surface && BodyMaterialPattern.IsMatch(text)) surface = true;
                foreach (Match m in ManifestGamePathPattern.Matches(text))
                    paths.Add(m.Groups[1].Value);
            }
        }
        catch { /* modRoot missing/unreadable */ }
        return (surface, paths);
    }

    /// <summary>
    /// Classify a mod: does it touch a skin surface, and does it provide any base path this composite reads
    /// (the recomposite gate).
    /// </summary>
    private (bool Surface, bool Affects) ClassifySurfaceMod(string modDir)
    {
        // Our own sidecar/overlay mods reference body materials as their redirect target; they are handled by the
        // HasSidecar-gated path, so exclude them here.
        if (HasSidecar(modDir)) return (false, false);

        var modRoot = Path.Combine(modsRoot, modDir);
        var fingerprint = ComputeModFingerprint(modRoot);
        // One read of the pair, so the hash always describes the set the verdict is computed from.
        var bases   = _compositeBaseKeys;
        var version = bases?.Hash ?? 0;
        if (_bodyModCache.TryGetValue(modDir, out var cached)
            && cached.Fingerprint == fingerprint && cached.BaseKeysHash == version)
            return (cached.IsSurfaceMod, cached.AffectsComposite);

        var (surface, paths) = ScanModManifests(modRoot);

        // Fail open (any surface mod recomposites) until this session knows what it reads.
        var affects = bases is not { Paths.Count: > 0 } ? surface : paths.Overlaps(bases.Paths);

        _bodyModCache[modDir] = (surface, affects, version, fingerprint);
        lock (_bodyModConfigLock)
        {
            // Only save when a verdict moved: config.Save serialises everything, and a changed base set reclassifies
            // dozens of mods at once. A hash-only refresh is deliberately not "changed".
            var stale = !config.KnownBodyMods.TryGetValue(modDir, out var prev)
                     || prev.IsBodyMod != surface
                     || prev.AffectsComposite != affects
                     || prev.Fingerprint != fingerprint;
            config.KnownBodyMods[modDir] = new BodyModCacheEntry
            {
                IsBodyMod        = surface,
                AffectsComposite = affects,
                BaseKeysHash     = version,
                Fingerprint      = fingerprint,
            };
            if (stale) config.Save();
        }
        return (surface, affects);
    }

    /// <summary>
    /// Record the base paths a composite reads, so <see cref="ClassifySurfaceMod"/> can tell a mod that feeds us
    /// from one that merely touches a skin surface. Accumulates while <paramref name="signature"/> (hash of the
    /// targeted material paths) holds, since a cold-cache run reports fewer paths; is replaced when it changes, so
    /// paths can retire. Only an <paramref name="authoritative"/> record may retire paths.
    /// </summary>
    private void RecordCompositeBaseKeys(IEnumerable<string> baseKeys, int signature, bool authoritative)
    {
        // Read-modify-write under the persisted copy's lock, so overlapping composites cannot lose a union.
        lock (_bodyModConfigLock)
        {
            var prev = _compositeBaseKeys;
            var next = new HashSet<string>(baseKeys, StringComparer.OrdinalIgnoreCase);

            // The one moment a path may be dropped: an authoritative record for a different shape.
            var retire = authoritative && prev is not null && prev.Signature != signature;

            if (prev is not null && !retire)
            {
                next.UnionWith(prev.Paths);
                // A union can only equal the old count when it added nothing, so this is "no new paths".
                if (next.Count == prev.Paths.Count) return;
            }

            // A non-authoritative record may add to the shape but not redefine it.
            var sig = prev is null || retire ? signature : prev.Signature;

            _compositeBaseKeys = new BaseKeySet(next, ComputeBaseKeysHash(next), sig);
            config.CachedCompositeBaseKeys = [.. next];
            config.CachedCompositeBaseSignature = sig;
            config.Save();
            log.Debug("[Proteus] composite base set {0}: {1} path(s), hash {2}, shape {3}",
                retire ? "replaced (composite shape changed)" : "now", next.Count,
                _compositeBaseKeys.Hash, sig);
        }
    }

    // Order-independent, case-insensitive and stable across sessions (string.GetHashCode is randomised per process).
    private static int ComputeBaseKeysHash(IEnumerable<string> baseKeys)
    {
        var h = 17;
        var n = 0;
        foreach (var p in baseKeys.Select(k => k.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal))
        {
            foreach (var c in p)
                h = unchecked(h * 31 + c);
            // Terminator, or the concatenation alone collides: {"ab","c"} would hash as {"a","bc"}.
            h = unchecked(h * 31 + '\n');
            n++;
        }
        h = unchecked(h * 31 + n);
        // Never 0: that is the "no set recorded" value of an entry restored from an older config.
        return h == 0 ? 1 : h;
    }
}
