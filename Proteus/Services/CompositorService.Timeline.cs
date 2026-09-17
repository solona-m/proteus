using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Penumbra.Api.Enums;

namespace Proteus.Services;

using static Proteus.Services.InertModDiagnosis;

using static Proteus.Services.DrawnModelPaths;

public partial class CompositorService
{
    // ── Refresh timeline (instrumentation) ─────────────────────────────────────────────────────────────
    // Triggers no composite has answered yet. A run claims them on entering Recomposite and hands them back if it
    // ends without an outcome, so a superseded run's wait is charged to its replacement.
    private readonly object _refreshBurstLock = new();
    private long _refreshBurstStart;   // Stopwatch timestamp; 0 = nothing pending
    private int _refreshBurstTriggers;
    private readonly List<string> _refreshBurstReasons = [];

    /// <summary>A trigger nothing ever claimed must not stretch the next refresh's total.</summary>
    private const double RefreshBurstStaleMs = 60_000;

    private void NoteRefreshTrigger(string reason)
    {
        lock (_refreshBurstLock)
        {
            if (_refreshBurstStart == 0 || PhaseCounter.MsSince(_refreshBurstStart) > RefreshBurstStaleMs)
            {
                _refreshBurstStart = PhaseCounter.Begin();
                _refreshBurstTriggers = 0;
                _refreshBurstReasons.Clear();
            }
            _refreshBurstTriggers++;
            // Distinct and bounded: a colour drag fires dozens of identical triggers.
            var shortReason = reason.Split(':')[0];
            if (!_refreshBurstReasons.Contains(shortReason) && _refreshBurstReasons.Count < 4)
                _refreshBurstReasons.Add(shortReason);
        }
    }

    /// <summary>The run that most recently claimed the pending triggers — the one a superseded run's wait is
    /// handed to if it is still in flight.</summary>
    private RefreshTimeline? _latestRefresh;

    private RefreshTimeline ClaimRefresh()
    {
        lock (_refreshBurstLock)
        {
            // Nothing pending: the superseded run hands its start over when it unwinds (see ReturnRefresh).
            var start = _refreshBurstStart != 0 ? _refreshBurstStart : PhaseCounter.Begin();
            var tl = new RefreshTimeline(start, _refreshBurstTriggers, string.Join(",", _refreshBurstReasons));
            _refreshBurstStart = 0;
            _refreshBurstTriggers = 0;
            _refreshBurstReasons.Clear();
            _latestRefresh = tl;
            return tl;
        }
    }

    private void ReturnRefresh(RefreshTimeline tl)
    {
        lock (_refreshBurstLock)
        {
            // Superseded, not cancelled: the replacement already claimed the pending set, so hand the start to it.
            if (_latestRefresh is { Completed: false } newer && !ReferenceEquals(newer, tl))
            {
                newer.Absorb(tl);
                return;
            }
            if (_refreshBurstStart == 0 || tl.Start < _refreshBurstStart) _refreshBurstStart = tl.Start;
            _refreshBurstTriggers += tl.Triggers;
            foreach (var r in tl.Reasons.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (!_refreshBurstReasons.Contains(r) && _refreshBurstReasons.Count < 4) _refreshBurstReasons.Add(r);
        }
    }

    /// <summary>What happened between a trigger firing and <see cref="Recomposite"/> starting, as timestamps
    /// so the timeline can place them after it claims the burst.</summary>
    private readonly record struct RefreshPreamble(long Triggered, long Debounced, long RaceSettled,
                                                   long DrawSettled, long SnapshotReady);

    /// <summary>
    /// Set by any forced <see cref="TriggerRecomposite"/>, cleared only once a composite publishes. While
    /// it is set, even an ambient composite runs — the forced work it superseded is still owed.
    /// </summary>
    private int _forcePending;

    /// <summary>
    /// The subset of <see cref="_forcePending"/> that the SKIN reuse gate must honour: a forced trigger
    /// whose effect on the skin the fingerprint cannot see. Cleared with <see cref="_forcePending"/>, at
    /// the same publish.
    /// </summary>
    private int _skinForcePending;

    /// <summary>
    /// The composite inputs behind the currently published manifest, or null when unknown. An ambient trigger whose
    /// inputs hash to this can skip. Written only after a successful publish; overlapping composites are last-writer-wins.
    /// </summary>
    private volatile string? _lastCompositeFingerprint;

    /// <summary>
    /// The skin half of one published composite's inputs, and what the skin phase produced: fingerprint, redirects,
    /// glow targets and contributions, all of which reuse must reinstate. One immutable record so overlapping
    /// composites (<see cref="_compositesInFlight"/>) never see a new fingerprint beside old redirects.
    /// </summary>
    private sealed record SkinPublish(
        string Fingerprint,
        IReadOnlyDictionary<string, string> Redirects,
        IReadOnlyList<ChannelContribution> Contributions,
        IReadOnlyDictionary<(string, string?, string?), List<Proteus.Interop.SkinGlowTarget>> GlowTargets);

    /// <summary>
    /// The last successfully published <see cref="SkinPublish"/>, or null; lets an outfit change reuse the skin.
    /// Trusted only when its files are still on disk (<see cref="SkinOutputStillOnDisk"/>): a dangling redirect makes the body invisible.
    /// </summary>
    private volatile SkinPublish? _lastSkinPublish;

    /// <summary>
    /// Whether every texture a remembered skin publish points at is still on disk and intact
    /// (<see cref="AlreadyWritten"/> validates the length against the .tex header).
    /// </summary>
    private bool SkinOutputStillOnDisk(IReadOnlyDictionary<string, string> skinRedirects)
    {
        foreach (var rel in skinRedirects.Values)
        {
            var disk = Path.Combine(managedModDir, rel.Replace('/', Path.DirectorySeparatorChar));
            // A skin publish can include a doubled face's rewritten .mdl (FaceUvDoublingService), which is not a .tex.
            if (!disk.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                try { if (new FileInfo(disk) is { Exists: true, Length: > 0 }) continue; }
                catch { /* unreadable — treat as missing, below */ }
                return false;
            }
            if (!AlreadyWritten(disk)) return false;
        }
        return true;
    }

    /// <summary>
    /// Everything that decides what this composite will produce, as one comparable string, built once inputs are
    /// settled and before expensive work so an ambient trigger with an unchanged world can return.
    /// Excludes the raw material snapshot (weapon paths churn) and all Configuration values.
    /// STANDING REQUIREMENT: a config knob that affects output must trigger with force: true and must not pass
    /// skinFingerprintAuthoritative.
    /// </summary>
    private string BuildCompositeFingerprint(
        Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> byMaterial,
        List<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays,
        Dictionary<string, List<string>> maskPathsByMod,
        Dictionary<string, List<(string MaskPath, string? NormalPath, string? IndexPath)>> maskAssetsByMod,
        Dictionary<string, Dictionary<int, ColorTableRowOverride>> maskRowsByMod,
        Dictionary<string, OverlayDescriptor> maskDescByMod,
        HashSet<string> maskShellMods,
        List<string> baseKeys,
        List<(OverlayEntry Entry, ResolvedContent Content)> contentLayers,
        HashSet<string> toeCapMods,
        bool skinOnly = false)
    {
        var sb = new System.Text.StringBuilder();

        static void Pair(System.Text.StringBuilder b, OverlayEntry e, ResolvedOverlay o)
            => b.Append(e.ModDirectory).Append('#').Append(e.Priority).Append('#')
               .Append(o.OptionGroup).Append('/').Append(o.Option).Append('#').Append(o.GroupOrder).Append('#')
               .Append(JsonSerializer.Serialize(o.Descriptor)).Append('#')
               .Append(o.ColorTableRows == null ? "-" : JsonSerializer.Serialize(o.ColorTableRows)).Append(';');

        // Iterate materials sorted by key but not within a list: the list order is the stack.
        foreach (var mtrl in byMaterial.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("mtrl:").Append(mtrl).Append('{');
            foreach (var (e, o) in byMaterial[mtrl]) Pair(sb, e, o);
            sb.Append("}\n");
        }

        // Under skinOnly a gear overlay's rows reduce to each sub-row's blend, the only thing the skin reads
        // (GarmentSilhouette). Promotion stays covered by the descriptor's Layer.
        sb.Append("gear:");
        foreach (var (e, o) in gearOverlays)
        {
            if (!skinOnly) { Pair(sb, e, o); continue; }
            sb.Append(e.ModDirectory).Append('#').Append(e.Priority).Append('#')
              .Append(o.OptionGroup).Append('/').Append(o.Option).Append('#').Append(o.GroupOrder).Append('#')
              .Append(JsonSerializer.Serialize(o.Descriptor)).Append('#');
            if (o.ColorTableRows == null) sb.Append('-');
            else
                foreach (var p in o.ColorTableRows)
                    sb.Append(p.Row).Append(':')
                      .Append((int)(p.SubRowA?.Blend ?? RowBlend.Paint)).Append('/')
                      .Append((int)(p.SubRowB?.Blend ?? RowBlend.Paint)).Append(',');
            sb.Append(';');
        }
        sb.Append('\n');

        // Imported geometry, in placement order; a selection change must not hash identically. Shell-only.
        if (!skinOnly)
        {
            sb.Append("content:");
            foreach (var (e, c) in contentLayers)
                sb.Append(e.ModDirectory).Append('#').Append(e.Priority).Append('#')
                  .Append(c.OptionGroup).Append('/').Append(c.Option).Append('#').Append(c.GroupOrder).Append('#')
                  .Append(JsonSerializer.Serialize(c.Piece)).Append('#')
                  .Append(c.ColorTableRows == null ? "-" : JsonSerializer.Serialize(c.ColorTableRows)).Append(';');
            sb.Append('\n');
        }

        foreach (var mod in maskPathsByMod.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            sb.Append("mask:").Append(mod).Append('=')
              .Append(string.Join(",", maskPathsByMod[mod])).Append('\n');

        foreach (var mod in maskAssetsByMod.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            sb.Append("maskasset:").Append(mod).Append('=')
              .Append(string.Join(",", maskAssetsByMod[mod].Select(a => $"{a.MaskPath}|{a.NormalPath}|{a.IndexPath}")))
              .Append('\n');

        // The toe cap, which the mask lines above strip. Not skinOnly-gated: a cap promotes skin overlays to gear.
        foreach (var mod in toeCapMods.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            sb.Append("cap:").Append(mod).Append('\n');

        // Shell-only for a mask-shell mod: its Masks colorset paints only the shell's material, and every skin consumer
        // skips those mods. `maskshell:` covers a mod entering or leaving the set.
        foreach (var mod in maskRowsByMod.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (skinOnly && maskShellMods.Contains(mod)) continue;
            sb.Append("maskrow:").Append(mod).Append('=')
              .Append(JsonSerializer.Serialize(maskRowsByMod[mod].OrderBy(kv => kv.Key)
                          .ToDictionary(kv => kv.Key, kv => kv.Value)))
              .Append('\n');
        }

        // The Masks tab's render mode (layer, shader, scroll effect, speed, tiling). Shell-only: it reaches the skin only
        // through maskShellMods membership, which `maskshell:` hashes.
        if (!skinOnly)
            foreach (var mod in maskDescByMod.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                sb.Append("maskdesc:").Append(mod).Append('=')
                  .Append(JsonSerializer.Serialize(maskDescByMod[mod])).Append('\n');

        sb.Append("maskshell:")
          .Append(string.Join(",", maskShellMods.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))).Append('\n');

        // Recomputed, not read from _lastEquipSignature (written only by the redraw hook). Under skinOnly only the
        // bare-body half is kept: which bare parts are drawn changes the seam map. _humanPartModels is in on both
        // sides: the shell is cut from face/hair meshes and the face is a skin surface.
        sb.Append("equip:")
          .Append(skinOnly
              ? EquipSignature(null, null, null, _bareBodyModels, _humanPartModels)
              : EquipSignature(_equippedPartModels, _equippedAccessoryModels, _equippedMetModels,
                               _bareBodyModels, _humanPartModels))
          .Append('\n');
        sb.Append("shape:").Append(BodyShapeSignature(_bodyShapeSnapshot)).Append('\n');
        sb.Append("bodytype:").Append(_lastCompositedBodyType).Append('\n');
        // The face-doubling switch decides the layout every face texture is published in, so toggling it must re-blend the face.
        sb.Append("faceuv:").Append(config.FaceUvInPlace ? '1' : '0').Append('\n');
        sb.Append("charcodes:").Append(_lastCompositedCharCodes).Append('\n');
        sb.Append("glamcode:").Append(_glamourerCharCode).Append('\n');
        sb.Append("race:").Append(_drawnRaceCode).Append('\n');

        // Which file each base path resolves to, plus size and mtime: switching body mods must not hash identically.
        // baseKeys comes from PrimeUpstreamCache, not _upstreamByGamePath's keys, which churn and would never settle.
        foreach (var gamePath in baseKeys)
        {
            sb.Append("base:").Append(gamePath).Append('=')
              .Append(_upstreamByGamePath.TryGetValue(gamePath, out var disk) ? disk : "(game data)");
            if (disk == null) { sb.Append('\n'); continue; }
            try
            {
                var fi = new FileInfo(disk);
                if (fi.Exists) sb.Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks);
            }
            catch { /* unreadable — the path alone still distinguishes a different mod */ }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Deep copy, so a binding's gear override never mutates the mod's own metadata objects.</summary>
    private static OverlayDescriptor CloneDescriptor(OverlayDescriptor d)
        => JsonSerializer.Deserialize<OverlayDescriptor>(JsonSerializer.Serialize(d))!;

    /// <summary>
    /// Record the race code a model walk saw, keeping the last known one when the walk carried no human model,
    /// but only while it belongs to the same character. <paramref name="owner"/> must be read on the framework
    /// thread in the walk that produced <paramref name="modelPaths"/>. A changed owner takes the new reading even when null.
    /// </summary>
    private void UpdateDrawnRaceCode(HashSet<string>? modelPaths, string? owner)
    {
        var code = DrawnRaceCodeFromModels(modelPaths);
        // Read-decide-write over both fields, so it runs under the lock as one step. See _drawnRaceLock.
        lock (_drawnRaceLock)
        {
            // Null owner (draw object gone mid-switch) counts as a different character: drop the cached code.
            if (owner == null || !string.Equals(owner, _drawnRaceOwner, StringComparison.Ordinal))
            {
                if (_drawnRaceCode != null && code == null)
                    Plugin.Log.Information("[Proteus] drawn race code c{0} dropped — read off {1}, now {2}",
                        _drawnRaceCode, _drawnRaceOwner ?? "(nobody)", owner ?? "(nobody)");
                _drawnRaceCode  = code;
                _drawnRaceOwner = owner;
                return;
            }
            _drawnRaceCode = code ?? _drawnRaceCode;
        }
    }
}
