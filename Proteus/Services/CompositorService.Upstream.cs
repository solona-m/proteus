using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CheapLoc;
using Dalamud.Game.Text.SeStringHandling;
using Penumbra.Api.Enums;

namespace Proteus.Services;

using static Proteus.Services.InertModDiagnosis;

public partial class CompositorService
{
    /// <summary>
    /// How long <see cref="VerifyRedirectsLive"/> may wait for the published manifest to go live before calling the result
    /// undetermined. Shorter than <see cref="PrimeLiveTimeout"/>: this only decides a log line.
    /// </summary>
    private static readonly TimeSpan VerifySettleTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// After publishing, ask Penumbra what it actually resolves each redirected path to, and name the winning file when
    /// we lost a path (by priority, per path). Anchored on the expected target: a path resolving to our file proves the
    /// reload landed; a non-matching path must repeat its answer before it is called lost (the rebuild is progressive,
    /// see <see cref="SettleUpstreams"/>). <paramref name="manifestConfirmedLive"/> is a second liveness source, so being
    /// outranked everywhere is reportable. What stays unproven is reported as undetermined, never as a loss.
    /// </summary>
    private void VerifyRedirectsLive(IDictionary<string, string> redirects, bool manifestConfirmedLive,
                                     CancellationToken ct)
    {
        if (redirects.Count == 0 || ct.IsCancellationRequested) return;

        // What each path should resolve to, canonicalised once (as in WaitForManifestLive).
        var expected = new Dictionary<string, string>(redirects.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (gamePath, rel) in redirects)
            if (TryCanonicalise(Path.Combine(managedModDir, rel)) is { } full)
                expected[gamePath] = full;
        if (expected.Count == 0) return;

        // The raw resolve per path from the final round, so warnings quote what Penumbra said.
        var raw  = new Dictionary<string, string?>(expected.Count, StringComparer.OrdinalIgnoreCase);
        var prev = new Dictionary<string, string?>(expected.Count, StringComparer.OrdinalIgnoreCase);
        var matched  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unstable = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            matched.Clear();
            unstable.Clear();
            foreach (var (gamePath, full) in expected)
            {
                var disk = penumbra.ResolvePlayer(gamePath);
                if (TryCanonicalise(disk) is { } got
                    && string.Equals(got, full, StringComparison.OrdinalIgnoreCase))
                {
                    // Already the end state; no repeat read can improve on it.
                    matched.Add(gamePath);
                }
                else if (!prev.TryGetValue(gamePath, out var was)
                      || !string.Equals(was, disk, StringComparison.OrdinalIgnoreCase))
                {
                    // A path that is not ours has to say so twice (mid-rebuild answers are transient).
                    unstable.Add(gamePath);
                }
                prev[gamePath] = disk;
                raw[gamePath]  = disk;
            }

            // A verdict needs the manifest live (from the caller's probe or a local match) and every non-matching path stable.
            // Normally everything matches on the first pass: one IPC per redirect, no sleep.
            bool live = manifestConfirmedLive || matched.Count > 0;
            if ((live && unstable.Count == 0) || sw.Elapsed >= VerifySettleTimeout) break;
            if (ct.IsCancellationRequested) return;
            Thread.Sleep((int)PrimeSettleInterval.TotalMilliseconds);
        }

        if (!manifestConfirmedLive && matched.Count == 0)
        {
            // Does not claim which of the two faults (reload not landed, or outranked everywhere) caused this.
            log.Warning("[Proteus] redirect check UNDETERMINED after {0}ms — not one of {1} published path(s) "
                      + "resolves to the file we wrote for it. EITHER the reload has not landed yet, OR we "
                      + "are outranked on every path we publish; this check cannot tell them apart. A "
                      + "\"manifest live after Nms\" line above means the reload DID land, so the cause is "
                      + "priority.",
                sw.ElapsedMilliseconds, expected.Count);
            return;
        }

        int lost = 0;
        var selfServed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unclaimed = new List<string>();
        foreach (var gamePath in expected.Keys)
        {
            if (matched.Contains(gamePath))
            {
                // Re-arm the chat notice, so losing this path again is reported again.
                _reportedRedirectLosses.TryRemove(gamePath, out _);
                continue;
            }
            if (unstable.Contains(gamePath)) continue;

            var disk = raw[gamePath];

            // Our own folder is not a conflict and must never reach the owner set, or TryRaisePriorityAbove raises us above
            // ourselves forever. It means Penumbra still serves an earlier manifest's file; the next reload settles it.
            if (string.Equals(ModFolderOf(disk), SidecarDiscoveryService.ManagedModDir,
                              StringComparison.OrdinalIgnoreCase))
            {
                selfServed.Add(gamePath);
                log.Warning("[Proteus] redirect not yet current: {0} resolves to {1} — our own mod, but not "
                          + "the file this manifest names, so Penumbra is still serving an earlier publish "
                          + "for it. Not a conflict; no priority change can affect it.", gamePath, disk!);
                continue;
            }

            lost++;

            // Two different faults: Penumbra echoing the game path (or null) means nothing provides it (not published to the
            // collection); anything else is another mod winning.
            bool nobodyProvides = disk == null
                               || string.Equals(disk, gamePath, StringComparison.OrdinalIgnoreCase);

            if (nobodyProvides)
            {
                unclaimed.Add(gamePath);
                log.Warning("[Proteus] redirect NOT live: {0} resolves to nothing — our entry is not in the "
                          + "winning collection at all, so this is not another mod outranking us. The managed "
                          + "mod is disabled, or the path was withdrawn between publish and check.", gamePath);
                continue;
            }

            log.Warning("[Proteus] redirect NOT live: {0} resolves to {1} — another mod wins this path, so "
                      + "what we composited for it cannot render. Raise the Proteus managed mod's priority "
                      + "in Penumbra above the mod that owns it.", gamePath, disk!);

            if (ModFolderOf(disk) is { } owner) owners.Add(owner);
            else NotifyRedirectLost(gamePath, disk);   // a real file, but not under the mod root
        }

        // One line for all unclaimed paths, not one per path.
        if (unclaimed.Count > 0 && _reportedRedirectLosses.TryAdd("\0unclaimed", "1"))
        {
            var msg = string.Format(Loc.Localize("Chat.UnclaimedRedirects.Fmt",
                "[Proteus] {0} of the files Proteus publishes aren't reaching the game — they resolve to "
                + "nothing at all, which usually means the \"Proteus\" mod is disabled in your current "
                + "Penumbra collection. Check it is enabled there."), unclaimed.Count);
            _ = Plugin.Framework.RunOnFrameworkThread(
                () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 17).Build()));
        }
        if (unclaimed.Count == 0) _reportedRedirectLosses.TryRemove("\0unclaimed", out _);

        // Raising is the fix, so do it when we know which mod to outrank.
        if (owners.Count > 0 && !TryRaisePriorityAbove(owners))
            foreach (var gamePath in expected.Keys)
                if (!matched.Contains(gamePath) && !unstable.Contains(gamePath)
                    && !selfServed.Contains(gamePath))
                    NotifyRedirectLost(gamePath, raw[gamePath]);

        if (unstable.Count > 0)
            log.Warning("[Proteus] redirect check UNDETERMINED for {0} path(s) after {1}ms — they do not "
                      + "resolve to our output but the answer was still changing, so this is NOT evidence of "
                      + "a loss: {2}", unstable.Count, sw.ElapsedMilliseconds, string.Join(", ", unstable));

        if (selfServed.Count > 0)
            log.Warning("[Proteus] {0} path(s) resolve to an earlier publish of our own mod rather than the "
                      + "file this manifest names: {1}", selfServed.Count, string.Join(", ", selfServed));

        if (lost == 0 && unstable.Count == 0 && selfServed.Count == 0)
        {
            // Winning everything re-arms the raise latch, as it re-arms the per-path notice.
            _highestPriorityRaiseAttempted = int.MinValue;
            log.Debug("[Proteus] all {0} redirect(s) live — every path we publish resolves to our output",
                expected.Count);
        }
    }

    /// <summary>
    /// The upstream <see cref="PrimeUpstreamCache"/> settled for a path this composite, or null; never a live resolve.
    /// For the body models smoothing republishes, where a live answer mid-rebuild is unsafe (<see cref="ResolveUpstream"/>).
    /// </summary>
    private string? SettledUpstream(string gamePath)
        => _upstreamSettled.ContainsKey(gamePath)
           && _upstreamByGamePath.TryGetValue(gamePath, out var disk)
           && !IsOwnOutput(disk) && File.Exists(disk)
            ? disk
            : null;

    /// <summary>
    /// Resolve <paramref name="gamePath"/> to the mod file a composite should read as its base, never our own previous
    /// output. Every base texture/material load goes through this. Returns null when there is no known upstream (fall
    /// through to game data). Timing shim; the body is in <c>…Core</c>.
    /// </summary>
    private string? ResolveUpstream(string gamePath)
    {
        var t0 = PhaseCounter.Begin();
        try { return ResolveUpstreamCore(gamePath); }
        finally { blendResolveStats.Stop(t0); }
    }

    /// <summary>
    /// Time a base-texture load into the same counter as <see cref="ResolveUpstream"/>; they never nest (the resolve is an argument).
    /// </summary>
    private (byte[] rgba, int width, int height)? TimedLoadBaseTexture(string? disk, string gamePath)
    {
        var t0 = PhaseCounter.Begin();
        try { return textureLoader.LoadBaseTexture(disk, gamePath); }
        finally { blendBaseLoadStats.Stop(t0); }
    }

    private string? ResolveUpstreamCore(string gamePath)
    {
        var disk = penumbra.ResolvePlayer(gamePath);

        if (disk != null && !IsOwnOutput(disk))
        {
            // A settled value wins over a live one for the rest of the composite: the prime's reload leaves the collection
            // rebuilding, and a contested path resolves transiently. Only SettleUpstreams sets it; InvalidateUpstreamCache clears it.
            if (_upstreamSettled.ContainsKey(gamePath)
                && _upstreamByGamePath.TryGetValue(gamePath, out var settled)
                && !string.Equals(settled, disk, StringComparison.OrdinalIgnoreCase)
                && File.Exists(settled))
            {
                log.Debug("[Proteus] resolve for {0} returned {1}, disagreeing with the settled upstream {2} "
                        + "— keeping the settled one", gamePath, disk, settled);
                return settled;
            }

            _upstreamByGamePath[gamePath] = disk;
            return disk;
        }

        if (_upstreamByGamePath.TryGetValue(gamePath, out var prev) && File.Exists(prev))
        {
            if (disk != null)
                log.Debug("[Proteus] resolve for {0} still pointed at our output — using upstream {1}",
                          gamePath, prev);
            return prev;
        }

        if (disk != null)
            log.Warning("[Proteus] ResolvePlayer returned our own managed file for {0} and no upstream is "
                      + "known — falling back to game data", gamePath);
        return null;
    }

    /// <summary>
    /// How long the prime may wait for Penumbra's resolution to settle (the winner stops moving) before giving up on a path.
    /// Paid only on a cold cache, and only by paths already unmasked (see <see cref="PrimeNarrowTimeout"/>).
    /// </summary>
    private static readonly TimeSpan PrimeLiveTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long the sample must stay unchanged before we believe it. See <see cref="SettleUpstreams"/>.</summary>
    private static readonly TimeSpan PrimeSettleInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long a path gets to shed our own redirect after the reload before we give up on it. Separate from
    /// <see cref="PrimeLiveTimeout"/>: while the narrow window is open the character renders un-composited.
    /// </summary>
    private static readonly TimeSpan PrimeNarrowTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// What the last composite did to each body material's channels, filtered and sorted for the status window; the
    /// counterpart of <see cref="BaseUpstreams"/>. Inert materials are dropped. Empty while a composite is in flight or
    /// after one cancels or throws.
    /// </summary>
    public IReadOnlyList<ChannelContribution> ChannelContributions() => _channelContributions;

    private volatile IReadOnlyList<ChannelContribution> _channelContributions = [];

    /// <summary>
    /// Find every enabled mod that contributed nothing to this composite, work out why, publish it for the status window
    /// and log it once. Must run after sibling synthesis and the second <c>maskShellMods</c> loop. A mod that reached the
    /// gear phase and lost its shell is <see cref="SecondSkinService.UnwearableContent"/>'s job
    /// (<see cref="GetUnwearableContentReason"/>); the <c>contributing</c> set is the boundary.
    /// </summary>
    private void ExplainInertMods(
        List<OverlayEntry> entries,
        Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> byMaterial,
        List<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays,
        List<(OverlayEntry Entry, ResolvedContent Content)> contentLayers,
        HashSet<string> maskShellMods,
        Dictionary<string, OverlayDescriptor> maskDescByMod,
        Dictionary<string, ResolutionDiagnostic> resolution,
        HashSet<string> filteredOut,
        HashSet<string>? wornCharCodes,
        HashSet<string>? wornHeadCodes,
        HashSet<string> activeBodyTypes,
        List<(OverlayEntry Entry, ResolvedOverlay Overlay)> allOverlays)
    {
        // The four destinations a mod can reach, unioned in one pass. AO top-up materials carry empty lists and contribute no mod.
        var contributing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in byMaterial.Values)
            foreach (var (e, _) in list) contributing.Add(e.ModDirectory);
        foreach (var (e, _) in gearOverlays)  contributing.Add(e.ModDirectory);
        foreach (var (e, _) in contentLayers) contributing.Add(e.ModDirectory);
        foreach (var m in maskShellMods)      contributing.Add(m);

        var inert = entries.Where(e => !contributing.Contains(e.ModDirectory)).ToList();
        if (inert.Count == 0)
        {
            // Publish the empty map: a mod the user just fixed must lose its warning.
            if (_inertMods.Count > 0)
                _inertMods = new Dictionary<string, InertReason>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        // Only for mods that came out empty is it worth asking Penumbra anything extra.
        var collId = penumbra.GetPlayerCollectionId();

        // Both halves of the wearer in one phrase: the head's code is not the body's, and a message that named
        // only one of them would be describing half a character (a Miqo'te is c0801 over a c0201 body).
        var wornAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wornCharCodes != null) wornAll.UnionWith(wornCharCodes);
        if (wornHeadCodes != null) wornAll.UnionWith(wornHeadCodes);
        var have = wornAll.Count > 0 ? Describe(activeBodyTypes, wornAll) : "";

        var next = new Dictionary<string, InertReason>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inert)
        {
            var diag = resolution.TryGetValue(entry.ModDirectory, out var d) ? d : ResolutionDiagnostic.None;

            // No collection is the same failure the resolvers report as Unavailable; fold it in, since packs that short-circuit
            // before the IPC never found out.
            if (!collId.HasValue) diag = diag with { Settings = SettingsRead.Unavailable };

            // The group names were already parsed from meta.json; answer the mask question from them.
            var (maskGroup, masksOn) = collId.HasValue
                ? discovery.MaskSelectionState(entry, collId.Value, diag.PenumbraGroups)
                : (false, 0);

            // What this pack paints, read off its own descriptors (the surviving set is empty).
            var mine = allOverlays.Where(p => string.Equals(p.Entry.ModDirectory, entry.ModDirectory,
                                                            StringComparison.OrdinalIgnoreCase))
                                  .SelectMany(p => p.Overlay.Descriptor.MaterialGamePaths)
                                  .ToList();
            var mineTypes = new HashSet<string>(mine.Select(UVRemapService.InferBodyType).OfType<string>(),
                                                StringComparer.OrdinalIgnoreCase);
            var mineCodes = new HashSet<string>(mine.Select(ExtractHumanCharCode).OfType<string>(),
                                                StringComparer.OrdinalIgnoreCase);
            var wants = mine.Count > 0 ? Describe(mineTypes, mineCodes) : "";

            bool maskGear = maskDescByMod.TryGetValue(entry.ModDirectory, out var md)
                         && md.Layer == OverlayLayer.Gear;

            var reason = Explain(diag, maskGroup, masksOn, maskGear,
                                 filteredOut.Contains(entry.ModDirectory), wants, have,
                                 PaintsAnotherBody(mine, mineTypes, wornCharCodes, wornHeadCodes, activeBodyTypes));
            next[entry.ModDirectory] = reason;

            if (_inertReported.TryAdd(
                    $"{entry.ModDirectory}\0{reason.Cause}\0{reason.Groups}\0{reason.Wants}\0{reason.Have}", 0))
                log.Information("[Proteus] {0} is enabled but contributes nothing: {1}",
                    entry.ModDirectory, EnglishInert(reason));
        }

        _inertMods = next;
    }

    /// <summary>
    /// What each base game path currently resolves to, as (path, mod folder, settled): the files the composite stands on,
    /// shown in the status window since a wrong base looks like a correct composite. Shell model/material paths are excluded.
    /// </summary>
    public IReadOnlyList<(string GamePath, string Source, bool Settled)> BaseUpstreams()
        => _upstreamByGamePath
            .Where(kv => kv.Key.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, ModFolderOf(kv.Value) ?? kv.Value, !_upstreamUnsettled.ContainsKey(kv.Key)))
            .ToList();

    /// <summary>
    /// The mod folder a file on disk belongs to (the name the user can search for in Penumbra). Null when the file isn't
    /// under the mod directory; the caller should then show the full path.
    /// </summary>
    private string? ModFolderOf(string? diskPath)
    {
        var root = modsRoot;
        if (string.IsNullOrEmpty(diskPath) || string.IsNullOrEmpty(root)
            || !diskPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;

        var rel = diskPath[root.Length..].TrimStart('/', '\\');
        var cut = rel.IndexOfAny(['/', '\\']);
        var folder = cut > 0 ? rel[..cut] : rel;
        return folder.Length > 0 ? folder : null;
    }

    /// <summary>
    /// Base paths whose last prime never settled, so their <see cref="_upstreamByGamePath"/> entry must not be trusted.
    /// Membership forces the next composite to prime the path again, since the memo is otherwise permanent.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _upstreamUnsettled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Base paths whose <see cref="_upstreamByGamePath"/> entry came from <see cref="SettleUpstreams"/>.
    /// <see cref="ResolveUpstream"/> will not overwrite them with a live answer (mid-rebuild after a prime). Cleared by
    /// <see cref="InvalidateUpstreamCache"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _upstreamSettled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Base paths a settled read showed no mod provides: the game's own file is the upstream. Distinguishes "vanilla" from
    /// "never learned", which <see cref="_upstreamByGamePath"/> cannot; a rewritten skin material depends on it. Cleared
    /// with the rest of the upstream memo.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _upstreamIsGameData = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve <paramref name="paths"/> to their real upstreams while our own redirects are narrowed away, waiting until
    /// the answer stops changing: mid-rebuild, a contested path resolves to whichever contributor is applied so far.
    /// Requires two consecutive identical reads a quiet interval apart, tracked per path.
    /// </summary>
    /// <returns>The settled upstreams. Paths that never settled are absent, so the caller can retry them.</returns>
    private Dictionary<string, string> SettleUpstreams(IReadOnlyList<string> paths, System.Diagnostics.Stopwatch sw)
    {
        // Per path, the previous answer of an unbroken streak of unmasked reads. Absent = no streak; a null value means
        // "nobody provides this", which two reads may legitimately agree on.
        var previous = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var settled  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending  = new List<string>(paths);
        var unmasked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string>? stuck = null;
        int samples  = 0;

        // Re-established below only by a read that settles on "nobody provides this".
        foreach (var p in paths) _upstreamIsGameData.TryRemove(p, out _);

        // Reported separately: never unmasked (the reload didn't reach it) vs kept changing (still rebuilding). Both retry next composite.
        void WarnUnresolved(List<string>? neverUnmasked, List<string>? stillMoving)
        {
            if (neverUnmasked is { Count: > 0 })
                log.Warning("[Proteus] upstream prime: {0} path(s) never shed our own redirect within {1}ms — "
                          + "Penumbra did not apply the narrowed manifest to them; leaving them unresolved so "
                          + "the next composite retries: {2}",
                    neverUnmasked.Count, (int)PrimeNarrowTimeout.TotalMilliseconds, string.Join(", ", neverUnmasked));
            if (stillMoving is { Count: > 0 })
                log.Warning("[Proteus] upstream did NOT settle for {0} path(s) within {1}ms — the resolved "
                          + "winner was still changing; leaving them unresolved so the next composite "
                          + "retries: {2}",
                    stillMoving.Count, sw.ElapsedMilliseconds, string.Join(", ", stillMoving));
        }

        while (true)
        {
            samples++;
            bool narrowExpired = sw.Elapsed >= PrimeNarrowTimeout;

            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                var d = penumbra.ResolvePlayer(p);

                // Still masked or no answer: break the streak, so reads from before and after the narrow cannot pair.
                if (d == null || IsOwnOutput(d))
                {
                    previous.Remove(p);

                    // Out of narrow budget without ever unmasking: drop the path so the narrow isn't held for an answer that isn't coming.
                    if (narrowExpired && !unmasked.Contains(p))
                    {
                        (stuck ??= []).Add(p);
                        pending.RemoveAt(i);
                    }
                    continue;
                }

                unmasked.Add(p);

                // An unredirected path echoes the game path back: "nobody provides this", not a disk file.
                var value = string.Equals(d, p, StringComparison.OrdinalIgnoreCase) ? null : d;

                if (previous.TryGetValue(p, out var prev)
                    && string.Equals(prev, value, StringComparison.OrdinalIgnoreCase))
                {
                    if (value != null) settled[p] = value;
                    else _upstreamIsGameData[p] = 0;
                    pending.RemoveAt(i);
                    continue;
                }

                previous[p] = value;
            }

            if (pending.Count == 0)
            {
                WarnUnresolved(stuck, null);
                log.Debug("[Proteus] upstream settled: {0} of {1} path(s) resolved after {2} sample(s) in {3}ms",
                    settled.Count, paths.Count, samples, sw.ElapsedMilliseconds);
                return settled;
            }

            if (sw.Elapsed >= PrimeLiveTimeout)
            {
                // Only what is still moving is dropped; paths that stood still keep their answers.
                WarnUnresolved(stuck, pending);
                return settled;
            }

            Thread.Sleep((int)PrimeSettleInterval.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Make sure every game path we redirect and are about to read has a known upstream, so the composite reads the
    /// user's body mod rather than our own last output. Narrows only the paths not already remembered, keeps the shell
    /// and EQDP published, and restores the full manifest quickly. Normally a no-op (needPrime.Count == 0).
    /// </summary>
    /// <param name="materialPaths">The material game paths this composite will read as bases.</param>
    /// <returns>
    /// Every base path considered, sorted: the live manifest's readable keys plus <paramref name="materialPaths"/>. The
    /// fingerprint reports upstream identity for this set, which depends only on this composite's inputs.
    /// </returns>
    private List<string> PrimeUpstreamCache(IEnumerable<string> materialPaths)
    {
        // Only paths we might read as a base matter. Shell files are write-only, except an append host's model (merged
        // into, so its upstream must survive). Carrier hosts are not admitted: never read, and narrowing would blink the
        // shell out. OwnedTextureRoot paths have no upstream and could never settle. Republished (smoothed) bodies are
        // admitted, or the shell would keep reading a stale body.
        var appendHosts = _appendHostModelPaths;
        var republishedBodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool IsReadableBase(string p)
            => !p.StartsWith(OwnedTextureRoot, StringComparison.OrdinalIgnoreCase)
            && ((!p.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase)
              && !p.StartsWith("chara/accessory/", StringComparison.OrdinalIgnoreCase))
             || appendHosts.Contains(p) || republishedBodies.Contains(p));

        List<string> baseKeys;

        // The lock covers the read-modify-write only, not the trailing resolve loop (dozens of IPC calls, no manifest writes).
        lock (_manifestLock)
        {
            // What is published right now. Null means unreadable ("unknown", not "empty").
            var live = PenumbraModMeta.TryReadDefaultData(managedModDir);

            var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in materialPaths) keys.Add(p);
            if (live is { } l)
            {
                // Recognised by the published file, which smoothing always names smoothed_{path}_{hash}.mdl.
                foreach (var (p, file) in l.Files)
                    if (p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(file).StartsWith("smoothed_", StringComparison.OrdinalIgnoreCase))
                        republishedBodies.Add(p);
                foreach (var p in l.Files.Keys)
                    if (IsReadableBase(p)) keys.Add(p);
            }
            baseKeys = [.. keys];

            // A path needs the narrow-and-restore only if our own manifest masks it. _upstreamUnsettled re-admits paths whose
            // memo came from a mid-rebuild read.
            var needPrime = live is { } lv
                ? baseKeys.Where(p => lv.Files.ContainsKey(p)
                                   && (!_upstreamByGamePath.ContainsKey(p) || _upstreamUnsettled.ContainsKey(p)))
                          .ToList()
                : [];

            if (needPrime.Count > 0)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var files = live!.Value.Files;
                var manips = live.Value.Manipulations;
                var narrowed = new Dictionary<string, string>(files, StringComparer.OrdinalIgnoreCase);
                foreach (var p in needPrime) narrowed.Remove(p);

                try
                {
                    // Manipulations are carried across verbatim: dropping them un-loads the shell's host accessory.
                    WriteManagedModJson(narrowed, manips);
                    penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);

                    // Wait for the answer to stop moving, not merely for our redirect to disappear (see SettleUpstreams).
                    var settled = SettleUpstreams(needPrime, sw);

                    int missed = 0;
                    foreach (var p in needPrime)
                    {
                        if (settled.TryGetValue(p, out var disk))
                        {
                            // Log the transition: a genuine skin change and a racy read produce identical output.
                            if (_upstreamByGamePath.TryGetValue(p, out var was)
                                && !string.Equals(was, disk, StringComparison.OrdinalIgnoreCase))
                                log.Information("[Proteus] base upstream CHANGED: {0}\n    was {1}\n    now {2}",
                                    p, was, disk);
                            else
                                log.Information("[Proteus] base upstream: {0} <- {1}", p, disk);

                            _upstreamByGamePath[p] = disk;
                            _upstreamSettled[p] = 0;
                            _upstreamUnsettled.TryRemove(p, out _);
                        }
                        else
                        {
                            // Unsettled or provided by nobody: don't memoise a guess; mark for retry and drop any earlier settled mark.
                            missed++;
                            _upstreamUnsettled[p] = 0;
                            _upstreamSettled.TryRemove(p, out _);
                        }
                    }

                    log.Information("[Proteus] upstream prime: {0} path(s) in {1}ms{2}",
                        needPrime.Count, sw.ElapsedMilliseconds,
                        missed > 0 ? $" — {missed} still unresolved (will composite on game data, retrying next composite)" : "");
                }
                finally
                {
                    // Always, even if the prime threw: the narrowed manifest must never stay live.
                    WriteManagedModJson(files, manips);
                    penumbra.ReloadModDirectory(SidecarDiscoveryService.ManagedModDir);
                }
            }
        }

        // Outside the lock. Resolve the remaining keys now so every key the fingerprint reports has its value before the gate reads it.
        foreach (var p in baseKeys)
            if (!_upstreamByGamePath.ContainsKey(p))
                ResolveUpstream(p);

        return baseKeys;
    }

    /// <summary>Root of the texture game paths Proteus invents; nothing upstream is behind them (see
    /// <see cref="PrimeUpstreamCache"/>). Under <c>chara/</c> because the game picks the resource category from the first
    /// segment.</summary>
    internal const string OwnedTextureRoot = "chara/proteus/";
}
