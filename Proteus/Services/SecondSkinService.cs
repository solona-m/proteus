using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using Proteus.Localization;

namespace Proteus.Services;

using static Proteus.Services.ContentPieceResolver;

using static Proteus.Services.PenumbraManipulations;

using static Proteus.Services.ShellColorRows;

/// <summary>
/// Builds the "second skin": every <see cref="OverlayLayer.Gear"/> overlay becomes a copy of the skin mesh,
/// pushed out along its normals and drawn as gear so it can run a gear shader (color table, sphere maps, emissive).
/// </summary>
public sealed partial class SecondSkinService
{
    private readonly PenumbraBridge penumbra;
    private readonly TextureLoader textureLoader;
    private readonly SidecarDiscoveryService discovery;
    private readonly UVRemapService uvRemap;
    private readonly Configuration config;
    private readonly IPluginLog log;

    /// <summary>
    /// Resolve a game path to the file to read as a BASE: never our own previous output, never past a mod the
    /// player installed (see CompositorService.ResolveUpstream). Null falls back to a plain resolve plus the own-output guard.
    /// </summary>
    private readonly Func<string, string?>? resolveUpstream;

    /// <summary>
    /// The upstream the compositor's prime settled for a path this composite, or null (see
    /// CompositorService.SettledUpstream). Used for body models only.
    /// </summary>
    private readonly Func<string, string?>? settledUpstream;

    /// <summary>The smallest sheet a shell is ever baked at, and what it stays at unless the art asks for more.</summary>
    internal const int TexSizeFloor = 2048;

    /// <summary>
    /// The largest sheet a shell is baked at, the same ceiling as the skin path (<see cref="TextureLoader.BaseTargetSize"/>).
    /// A ceiling, not a target: <see cref="ChooseTexSize"/> only reaches it for art that carries the detail.
    /// </summary>
    internal const int TexSizeCap = 4096;

    // The sheet a build bakes at is a LOCAL of Build, passed down by parameter — never a field: layers index each
    // other's buffers at one size per build, and builds overlap on this shared instance.

    /// <summary>Coverage only decides whether a whole triangle survives, so it can be coarse.</summary>
    private const int CoverageSize = 256;

    /// <summary>
    /// The toe-cap mask is sampled per VERTEX, so it needs more resolution than coverage but far less than the art.
    /// </summary>
    private const int ToeCapSize = 512;

    /// <summary>
    /// How much of the capped area a shell must paint before it gets a toe cap. Deliberately low: every shell with
    /// geometry over the toes must be capped, or its sleeved toes show through the capped shell.
    /// </summary>
    private const float MinToeCoverage = 0.02f;

    /// <summary>
    /// Every body-UV image a shell build will load, across all layers: the input to <see cref="ChooseTexSize"/> and to
    /// the compositor's prefetch, so they cannot drift. Masks are included; scroll maps are not.
    /// </summary>
    internal static IEnumerable<string?> ShellArtPaths(
        IReadOnlyList<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays,
        SidecarDiscoveryService discovery)
    {
        foreach (var (entry, overlay) in gearOverlays)
        {
            var d = overlay.Descriptor;
            if (d.Diffuse != null) yield return Path.Combine(entry.SidecarRoot, d.Diffuse);
            if (d.Normal  != null) yield return Path.Combine(entry.SidecarRoot, d.Normal);
            if (d.Mask    != null) yield return Path.Combine(entry.SidecarRoot, d.Mask);
            if (d.Index   != null) yield return Path.Combine(entry.SidecarRoot, d.Index);

            // Already absolute — ResolveActiveMaskAssets resolves against the mod folder itself.
            foreach (var (maskPath, normalPath, indexPath) in discovery.ResolveActiveMaskAssets(entry))
            {
                yield return maskPath;
                yield return normalPath;
                yield return indexPath;
            }
        }
    }

    /// <summary>
    /// The sheet size a build should bake at: the largest dimension any of the art carries, rounded up to a power of
    /// two and clamped to [<see cref="TexSizeFloor"/>, <see cref="TexSizeCap"/>]. Sized by header probe, before loading.
    /// Scroll maps must stay excluded: they tile through uv1 and say nothing about the sheet.
    /// </summary>
    internal static int ChooseTexSize(IEnumerable<string?> artPaths) => ChooseTexSize(artPaths, out _);

    /// <inheritdoc cref="ChooseTexSize(IEnumerable{string})"/>
    /// <param name="largestArt">
    /// The largest dimension found before floor and cap, 0 when nothing could be probed. The scan stops at the cap,
    /// so this is "at least this big", for logging only.
    /// </param>
    internal static int ChooseTexSize(IEnumerable<string?> artPaths, out int largestArt)
    {
        largestArt = 0;
        foreach (var p in artPaths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (TextureLoader.ProbeSize(p) is not { } s) continue;
            largestArt = Math.Max(largestArt, Math.Max(s.Width, s.Height));
            // Nothing further can raise the answer; stop probing.
            if (largestArt >= TexSizeCap) break;
        }
        if (largestArt <= TexSizeFloor) return TexSizeFloor;
        if (largestArt >= TexSizeCap) return TexSizeCap;

        // Round up to a power of two: block compression and the coverage grid need power-of-two sheets.
        int pow = TexSizeFloor;
        while (pow < largestArt) pow <<= 1;
        return Math.Min(pow, TexSizeCap);
    }

    /// <summary>Number of single-char base-36 shell disk ids (0-9a-z) — the ceiling on placeable layers,
    /// so an id never runs past 'z'.</summary>
    private const int DiskIdSpace = 36;

    /// <summary>Encode a layer's global index as a base-36 disk id char (0-9 then a-z). Digits-first keeps it
    /// ASCII-monotonic ('0'&lt;'9'&lt;'a'&lt;'z'), so the ghost/highlighter's char comparison still orders the stack.</summary>
    private static char DiskId(int d) => (char)(d < 10 ? '0' + d : 'a' + (d - 10));

    // A head/facewear "_met" model smaller than this is treated as an invisible/degenerate item: the shell
    // REPLACES it instead of appending, since a merge into a near-empty model won't render.
    private const int DegenerateModelBytes = 3000;

    /// <summary>
    /// The Emperor's New Ring, invisible. One source of truth with the resolver: this set id ties the published path,
    /// the EQDP entry and the Glamourer item together.
    /// </summary>
    private const int EmperorSetId = InvisibleRing.EmperorSetId;

    /// <summary>
    /// Every skin part is MERGED into the one ring model; a part × layer group carries that layer's material.
    /// Parts the character isn't drawing are skipped.
    /// </summary>
    private static readonly string[] Parts = ["top", "dwn", "glv", "sho"];

    /// <summary>Body ids tried by the whole-body fallback, in preference order. b0001 is the standard
    /// body; a few race/gender combos ship b0101 instead.</summary>
    private static readonly string[] WholeBodyIds = ["b0001", "b0101"];

    public SecondSkinService(
        PenumbraBridge penumbra, TextureLoader textureLoader, SidecarDiscoveryService discovery,
        UVRemapService uvRemap, Configuration config, IPluginLog log,
        Func<string, string?>? resolveUpstream = null, Func<string, string?>? settledUpstream = null)
    {
        this.penumbra = penumbra;
        this.textureLoader = textureLoader;
        this.discovery = discovery;
        this.uvRemap = uvRemap;
        this.config = config;
        this.log = log;
        this.resolveUpstream = resolveUpstream;
        this.settledUpstream = settledUpstream;
    }

    /// <summary>
    /// The hand-modelled toe boxes shipped beside the plugin, read once. Empty when missing or unparseable; additive.
    /// </summary>
    private readonly List<SecondSkinWriter.AuthoredCapSet> authoredCaps = [];
    private bool authoredCapTried;

    private IReadOnlyList<SecondSkinWriter.AuthoredCapSet> AuthoredCaps()
    {
        if (authoredCapTried) return authoredCaps;
        authoredCapTried = true;
        try
        {
            var dir = discovery.AssemblyDir;
            if (dir == null) return authoredCaps;
            // One cap per body (toecap.mdl / toecap.<body>.mdl), each with its binding; a binding carries a cap across foot
            // models of its body, not to another body. The writer picks by which binding places best.
            var meshDir = Path.Combine(dir, "Meshes");
            if (Directory.Exists(meshDir))
                foreach (var mp in Directory.GetFiles(meshDir, "toecap*.mdl").OrderBy(x => x))
                {
                    var bindPath = Path.ChangeExtension(mp, ".bind");
                    authoredCaps.Add(new SecondSkinWriter.AuthoredCapSet(
                        File.ReadAllBytes(mp),
                        File.Exists(bindPath) ? File.ReadAllBytes(bindPath) : null,
                        Path.GetFileNameWithoutExtension(mp)));
                    log.Information("[Proteus] second skin: toe cap {0} loaded{1}",
                        Path.GetFileName(mp),
                        File.Exists(bindPath) ? "" : " (no binding — fits one foot only)");
                }
            if (authoredCaps.Count == 0)
                log.Debug("[Proteus] second skin: no authored toe cap in {0}", meshDir);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] second skin: could not load the authored toe cap");
        }
        return authoredCaps;
    }

    /// <summary>Last cap lines logged, so a recomposite doesn't repeat them.</summary>
    private string? lastCapDeclined, lastCapUsed;

    /// <summary>
    /// The last redundancy tally reported, so the line is not repeated on every rebuild.
    /// </summary>
    private string? lastRedundant;

    /// <summary>
    /// Files to redirect, plus the metadata edits that make the shells load.
    /// <paramref name="ShellChanged"/> is true when the model, a material or a texture differs from disk.
    /// <paramref name="ModelChanged"/> narrows that to the .mdl, the only change that forces a full redraw.
    /// </summary>
    public sealed record Result(
        Dictionary<string, string> Redirects, List<object> Manipulations, bool ShellChanged,
        Dictionary<(string ModDir, string? Group, string? Option), List<string>> ShellMaterials,
        bool ModelChanged,
        // The game model paths hosting a shell this composite. When this set shrinks, the vacated accessory needs a
        // full redraw to reload its real model.
        List<string> HostModelPaths,
        // The subset of HostModelPaths we APPEND into (a worn item whose model is the merge base). Only these have an
        // upstream worth recovering, so only these may be unpublished by PrimeUpstreamCache.
        List<string> AppendHostModelPaths,
        // Mod directory → the content materials (mod-relative) that back at least one DRAWN mesh this composite: what
        // the colour editor offers a grid for. Drawn, not declared, and not "placed on a host".
        Dictionary<string, HashSet<string>> ContentMaterials,
        // Shell material disk leaf (ss_{letter}.mtrl) → what its rows want from the scene light. Only materials that ask
        // for something appear.
        Dictionary<string, ShellLightProfile> ShellLight,
        // Per mod, the content MODEL files (mod-relative, forward slashes) whose meshes went into this build; the only
        // record that an imported garment is worn.
        Dictionary<string, HashSet<string>> ContentModels);

    /// <summary>
    /// One surface, resolved: the geometry a shell is cut from, sharing one UV layout and one race code.
    /// The race code decides the host's EQDP: a body (cut from c0201 space) needs the wearer's entry emptied so the
    /// game deforms it; a face (authored at the character's race) needs it set so it loads natively.
    /// </summary>
    private sealed record ResolvedSurface(
        ShellSurfaceKey Key,
        IReadOnlyList<SecondSkinWriter.SourceSpec> Sources,
        IReadOnlyList<string> SourcePaths,
        string CutCode,
        string? UvSpace);

    /// <summary>
    /// One MATERIAL an imported content pack publishes, and every mesh drawn with it; the allocation unit, since a
    /// material costs a host slot. <paramref name="Owners"/> is every (mod, group, option) it serves; each needs the
    /// material registered under its own key.
    /// </summary>
    private sealed record ContentUnit(
        byte[] Mtrl,
        /// <summary>The source .mtrl, relative to the mod root: what the colour editor knows this material by.</summary>
        string MtrlRel,
        Dictionary<int, GearColorRow>? Rows,
        /// <summary>The animated glow, or null to publish the pack's material as authored. Set means the material is
        /// rebuilt onto characterscroll.</summary>
        GearSettingsPreset? Glow,
        ShellSurfaceKey Surface,
        List<ContentGeometry> Geometries,
        List<(OverlayEntry Entry, ResolvedContent Content)> Owners,
        /// <summary>Texture game path → the file this pack's selection supplies for it, republished under Proteus's mod.
        /// Empty leaves every texture to Penumbra.</summary>
        Dictionary<string, string> TexFiles)
    {
        /// <summary>The entry any per-mod lookup should use; merged owners are all one mod.</summary>
        public OverlayEntry Entry => Owners[0].Entry;
    }

    /// <summary>
    /// The body models as the user installed them, by game path, remembered before the nipple smooth republishes any,
    /// so later composites never read our own output. Known limit: a body mod swapped while smoothing is on is not seen.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte[]> _upstreamBodies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The mod file each of <see cref="_upstreamBodies"/> was read from, by game path, which decides whether
    /// smoothing may touch it. Concurrent because composites overlap.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _upstreamBodyDisks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Garments already reported as left unsmoothed, so a recomposite doesn't repeat the line.</summary>
    private readonly ConcurrentDictionary<string, byte> _smoothSkippedLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each body part's redundancy measurement, against the exact bytes it was measured from. Validated by
    /// reference, then by content hash. Session-lifetime, bounded by body paths; concurrent because composites overlap.
    /// </summary>
    private readonly ConcurrentDictionary<string, (byte[] Src, ulong Hash, SecondSkinWriter.ConnectorProfile P)>
        _connectorProfiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// This part's redundancy measurement, taken once. Null when the model cannot be read ("measure it yourself").
    /// </summary>
    private SecondSkinWriter.ConnectorProfile? ConnectorProfileFor(string path, byte[] bytes)
        => CachedConnectorProfile(_connectorProfiles, path, bytes, b => SecondSkinWriter.ReadConnectorProfile(b));

    /// <summary>
    /// The cache's behaviour as a function of its store, for testing. <paramref name="read"/> is the measurement.
    /// </summary>
    internal static SecondSkinWriter.ConnectorProfile? CachedConnectorProfile(
        ConcurrentDictionary<string, (byte[] Src, ulong Hash, SecondSkinWriter.ConnectorProfile P)> store,
        string path, byte[] bytes, Func<byte[], SecondSkinWriter.ConnectorProfile?> read)
    {
        if (store.TryGetValue(path, out var e))
        {
            if (ReferenceEquals(e.Src, bytes)) return e.P;
            ulong h = Hash(bytes);
            if (h == e.Hash)
            {
                // Same content through a new array: adopt it so the reference check wins next time.
                store[path] = (bytes, h, e.P);
                return e.P;
            }
        }

        var profile = read(bytes);
        if (profile == null) return null;
        store[path] = (bytes, Hash(bytes), profile);
        return profile;
    }

    /// <summary>
    /// Write only when the bytes differ, atomically (temp file and move), since a live redirect may point at the file.
    /// Internal because <see cref="FaceUvDoublingService"/> publishes under the same rules.
    /// </summary>
    internal static bool WriteIfChanged(string path, byte[] data)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
                return false;
        }
        catch { /* unreadable — fall through and rewrite */ }

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, data);
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return true; }
            catch (Exception) when (i < 5) { Thread.Sleep(50 << i); }  // the game may hold it open mid-load
            catch { try { File.Delete(tmp); } catch { } throw; }       // don't leave the temp behind
        }
    }

    /// <summary>
    /// Content hash of each shell texture last written, to tell a real change from a rewrite of identical bytes.
    /// Concurrent because shell layers bake in parallel (see the Parallel.For in ShellTextureBake) while the content
    /// writers use the same memo: a plain Dictionary here was only safe as long as every caller agreed to lock it,
    /// and they did not.
    /// </summary>
    private readonly ConcurrentDictionary<string, ulong> _texHashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copy a pack file into the output, doing nothing when the same source is already there (memoised on a source
    /// stamp, not by reading both files). Returns true when the bytes on disk changed.
    /// Shares <see cref="_texHashes"/> with the generated writers on purpose: the same ss_{letter}_{slot}.tex path can
    /// belong to a shell one composite and a content unit the next. An unstat-able source falls through to the copy.
    /// </summary>
    private bool CopyPackFile(string srcDisk, string dstDisk)
        => CopyPackFile(_texHashes, srcDisk, dstDisk);

    /// <summary>The body of <see cref="CopyPackFile(string,string)"/> with its memo passed in, for testing.</summary>
    internal static bool CopyPackFile(IDictionary<string, ulong> memo, string srcDisk, string dstDisk)
    {
        ulong? stamp = null;
        try
        {
            var info = new FileInfo(srcDisk);
            if (info.Exists) stamp = StampHash(srcDisk, info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch { /* unreadable — fall through and copy, which reports properly */ }

        if (stamp is { } s && memo.TryGetValue(dstDisk, out var prev) && prev == s && File.Exists(dstDisk))
            return false;

        var wrote = WriteIfChanged(dstDisk, File.ReadAllBytes(srcDisk));
        // Recorded only once the copy is through, or a failed copy would be skipped next time.
        if (stamp is { } ok) memo[dstDisk] = ok;
        return wrote;
    }

    /// <summary>
    /// Where a smoothed body is published: content-addressed, because the game caches models by resolved path.
    /// </summary>
    private static string SmoothedBodyPath(string modelsDir, string gamePath, byte[] content)
        => Path.Combine(modelsDir,
                        $"smoothed_{CompositorService.SanitizeName(gamePath)}_{Hash(content):x16}.mdl");

    private static ulong StampHash(string src, long ticks, long length)
    {
        ulong h = 14695981039346656037;   // FNV-1a, as Hash
        foreach (var c in src) { h ^= char.ToLowerInvariant(c); h *= 1099511628211; }
        h ^= (ulong)ticks;  h *= 1099511628211;
        h ^= (ulong)length; h *= 1099511628211;
        return h ^ 0x5350414D_5354414Dul;   // "STAMP" salt
    }

    // Layer count last warned about as over the host's material budget, so the notice prints once per change.
    // -1 = not over budget.
    private int _lastOverBudgetLayers = -1;

    // Content pieces last warned about as unplaceable, printed once per change. -1 = everything fits.
    private int _lastUnhostedContent = -1;

    /// <summary>
    /// Mod directory → why none of that pack's content pieces can be worn by this character, as the panel shows it.
    /// Instance state because <see cref="Build"/> returns null when no host took anything. Swapped in as one reference.
    /// </summary>
    public volatile IReadOnlyDictionary<string, string> UnwearableContent =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Surfaces last warned about as unhostable, joined, keyed on the set rather than a count. Null = none.
    private string? _lastUnhostedSurfaces;

    /// <summary>Surfaces last reported as not drawn at all, joined, printed once per change. Null = none.
    /// Only a composite that could see the character writes here, so a mid-redraw composite cannot re-arm it.</summary>
    private string? _lastUnresolvedSurfaces;

    /// <summary>Carrier slots last reported as belonging to another mod, joined, so the notice prints once
    /// per changed situation. Null = none currently claimed.</summary>
    private string? _lastClaimedCarriers;

    /// <summary>
    /// Say once, in chat, that a mod is on an Emperor's New accessory Proteus would have used, and that Proteus left
    /// it alone; otherwise the user's content vanishes with nothing pointing at the fix.
    /// </summary>
    private void NotifyCarriersClaimed(IReadOnlyList<(HostAccessory Host, string By)> claimed)
    {
        // The mod FOLDER, not the file inside it: that is what the user sees in Penumbra.
        string Owner(string disk)
        {
            var root = penumbra.GetModDirectory();
            try
            {
                if (root != null)
                {
                    var rel = Path.GetRelativePath(root, disk);
                    if (!rel.StartsWith("..", StringComparison.Ordinal))
                        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                }
            }
            catch { /* fall through to the file name */ }
            return Path.GetFileName(disk);
        }

        var owners = claimed.Select(c => Owner(c.By)).Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var slots  = claimed.Select(c => c.Host.Slot).Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var key = string.Join("|", owners) + "#" + string.Join("|", slots);
        if (string.Equals(_lastClaimedCarriers, key, StringComparison.Ordinal)) return;
        _lastClaimedCarriers = key;

        var msg = string.Format(Loc.Localize("Chat.CarrierClaimed.Fmt",
            "[Proteus] \"{0}\" puts its own model on an Emperor's New accessory ({1}), so Proteus left that "
          + "slot alone rather than replacing it. Free a different ring or bracelet slot if you want Proteus "
          + "to have one to build on."), string.Join("\", \"", owners), string.Join(", ", slots));
        _ = Plugin.Framework.RunOnFrameworkThread(
            () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
    }

    /// <summary>Content hash for a published file's name. Every publisher must hash the same way (see
    /// <see cref="SmoothedBodyPath"/>).</summary>
    internal static ulong Hash(byte[] data)
    {
        ulong h = 14695981039346656037;   // FNV-1a
        foreach (var b in data) { h ^= b; h *= 1099511628211; }
        return h;
    }

    /// <summary>
    /// Change detection for an in-memory texture buffer, NEVER a file name (that is <see cref="Hash"/>). Chunked and
    /// parallel; the chunk size, not the thread count, decides the answer, so it is stable within a process.
    /// </summary>
    internal static ulong SlotHash(byte[] data)
    {
        const int Chunk = 4 << 20;
        const ulong P1 = 0x9E3779B185EBCA87ul, P2 = 0xC2B2AE3D27D4EB4Ful, P3 = 0x165667B19E3779F9ul;
        int chunks = Math.Max(1, (data.Length + Chunk - 1) / Chunk);
        var parts = new ulong[chunks];
        Parallel.For(0, chunks, c =>
        {
            int from = c * Chunk, len = Math.Min(Chunk, data.Length - from);
            var span = data.AsSpan(from, Math.Max(0, len));
            ulong h = P3 ^ (ulong)c;
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(span);
            foreach (var w in words)
                h = System.Numerics.BitOperations.RotateLeft(h ^ (w * P2), 31) * P1;
            for (int i = words.Length * 8; i < span.Length; i++)
                h = System.Numerics.BitOperations.RotateLeft(h ^ (span[i] * P3), 11) * P1;
            parts[c] = h;
        });
        ulong acc = (ulong)data.Length * P1;
        foreach (var p in parts)
        {
            acc ^= p;
            acc = System.Numerics.BitOperations.RotateLeft(acc, 27) * P1 + P2;
        }
        acc ^= acc >> 33; acc *= P2; acc ^= acc >> 29;
        return acc;
    }

    /// <summary>The colour-table index slot, whose texels name rows rather than carrying colour.</summary>
    private static bool IsIndexSlot(string slot) => string.Equals(slot, "id", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How each index texture is published, keyed by the content hash of the buffer handed in. The verdict is needed on
    /// every composite (it goes into the skip-check hash, which is read before the write is skipped), but the trial
    /// encodes behind it are worth running only once per distinct index texture. The snapped BUFFER is not cached — it
    /// is the size of the texture, and it is only needed on the composites that actually write.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, TextureLoader.IndexPlan> _indexPlans = new();

    /// <summary>
    /// How an index texture is published: BC5 when a trial encode decodes back to the same row pair and sub-row side
    /// for every texel, BC5 over a snapped buffer when that is what it takes (see
    /// <see cref="TextureLoader.SnapIndexForBc5"/>), uncompressed when neither holds. BC5 is the vanilla index format
    /// and quarters the file — which matters most for the sync plugins, whose own "compress uncompressed textures" pass
    /// would otherwise make the same conversion unchecked, and who count what they transfer against a VRAM budget.
    /// The plan is decided here and reported once. <paramref name="snapped"/> comes back non-null only on the pass
    /// that computed it — a memo hit carries the verdict, not the buffer, which would be the size of the texture.
    /// A caller that still has to write then snaps for itself, which only happens when the output went missing.
    /// </summary>
    private TextureLoader.IndexPlan IndexPlanFor(
        ulong content, byte[] data, int w, int h, string disk, out byte[]? snapped)
    {
        snapped = null;
        if (_indexPlans.TryGetValue(content, out var cached)) return cached;

        var decided = textureLoader.PlanIndexBc5(data, w, h);
        _indexPlans[content] = decided.Plan;
        snapped = decided.Snapped;

        var name = Path.GetFileName(disk);
        switch (decided.Plan)
        {
            case TextureLoader.IndexPlan.Bc5:
                log.Debug("[Proteus] index BC5: {0} verified lossless for row selection", name);
                break;
            case TextureLoader.IndexPlan.Bc5AfterSnap:
                log.Information("[Proteus] index BC5: {0} snapped {1} texel(s) ({2} to another row) so its blocks "
                              + "encode exactly", name, decided.SnappedTexels, decided.SnappedRows);
                break;
            default:
                log.Information("[Proteus] index BC5: {0} still moves {1} texel(s) to another colour-table row "
                              + "after snapping — writing it uncompressed", name, decided.Flipped);
                break;
        }

        return decided.Plan;
    }

    /// <summary>
    /// Republish one of a content pack's own textures RE-ENCODED, for art the author shipped uncompressed. Index art
    /// goes through the same verified BC5 path as our own (row selection has to survive, and an author's index is no
    /// different from ours in that respect); every other slot takes BC7, as our own do.
    /// <para/>
    /// Returns null when the file cannot be converted — undecodable, or an index that fails the trial — and the caller
    /// then republishes the author's bytes unchanged. Otherwise whether the output on disk changed.
    /// Memoised on the SOURCE file's stamp in <see cref="_texHashes"/>, like <see cref="CopyPackFile(string,string)"/>,
    /// with a marker folded in so toggling compression rewrites rather than reusing a byte copy.
    /// </summary>
    internal bool? RepublishCompressed(bool isIndex, string srcFile, string dstDisk)
    {
        ulong stamp;
        try
        {
            var info = new FileInfo(srcFile);
            if (!info.Exists) return null;
            stamp = StampHash(srcFile, info.LastWriteTimeUtc.Ticks, info.Length) ^ 0x9E3779B97F4A7C15ul;
        }
        catch { return null; }

        if (_texHashes.TryGetValue(dstDisk, out var prev) && prev == stamp && File.Exists(dstDisk))
            return false;

        if (textureLoader.LoadTexAsRgba(srcFile) is not { } decoded) return null;
        var (rgba, w, h) = decoded;

        var encoding = TexEncoding.Bc7;
        if (isIndex)
        {
            var plan = IndexPlanFor(SlotHash(rgba), rgba, w, h, dstDisk, out var snapped);
            // An index that cannot survive the trial stays exactly as the author wrote it: a wrong row is worse than
            // a big file, and re-encoding it ourselves would only move the damage from their machine to ours.
            if (plan == TextureLoader.IndexPlan.Uncompressed) return null;
            if (plan == TextureLoader.IndexPlan.Bc5AfterSnap)
                rgba = snapped ?? TextureLoader.SnapIndexForBc5(rgba, w, h, out _, out _);
            encoding = TexEncoding.Bc5;
        }

        if (!textureLoader.WriteTex(rgba, w, h, dstDisk, encoding))
        {
            log.Warning("[Proteus] content: could not re-encode {0} — republishing the author's bytes",
                        Path.GetFileName(srcFile));
            return null;
        }

        _texHashes[dstDisk] = stamp;
        log.Debug("[Proteus] content: re-encoded {0} as {1}", Path.GetFileName(srcFile), encoding);
        return true;
    }

    /// <summary>
    /// Whether a texture path names an index map. Deliberately the SAME token set a sync plugin's own classifier
    /// uses (<c>_id.</c>, <c>_id_</c>, <c>_idx</c>, <c>_index</c>, <c>index_</c>), so anything one of them would
    /// re-encode as an index goes through our verified path instead of taking an unchecked BC7.
    /// </summary>
    private static readonly string[] IndexPathTokens = ["_id.", "_id_", "_idx", "_index", "index_"];

    internal static bool IsIndexTexturePath(string path)
    {
        var leaf = Path.GetFileName(path.Replace('\\', '/'));
        if (string.IsNullOrEmpty(leaf)) return false;
        foreach (var token in IndexPathTokens)
            if (leaf.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Republish <paramref name="srcDisk"/> into <paramref name="dstDisk"/>, re-encoded when we are compressing and
    /// the author left it in a format a viewer's sync client would convert on their machine. Falls back to copying
    /// the bytes. Returns whether the file on disk changed. The one door every pack texture goes through.
    /// </summary>
    internal bool RepublishPackTexture(string? modRoot, string texGamePath, string srcDisk, string dstDisk)
    {
        if (config.EnableCompression && textureLoader.CompressionAffordable()
         && TextureLoader.IsSyncRecompressible(srcDisk))
        {
            var isIndex = IsIndexTexturePath(texGamePath);

            // The sidecar copy first: built at import, and built here when it is missing — which is the check on
            // composition for a pack imported before compression was on, or one whose art has changed since.
            if (PackTextureCompressionCache.TryEnsure(modRoot, srcDisk, isIndex, textureLoader, log, out var cached))
                return CopyPackFile(cached, dstDisk);

            // No sidecar to keep it in (a pack with no mod root): convert straight into the output instead.
            if (RepublishCompressed(isIndex, srcDisk, dstDisk) is { } converted)
                return converted;
        }

        return CopyPackFile(srcDisk, dstDisk);
    }

    /// <summary>True when the blue channel (byte 2 of each RGBA quad) is 255 across the whole buffer — i.e.
    /// the normal carries no transparency gate, so BC5 (which drops blue) is lossless for it.</summary>
    private static bool IsBlueAllWhite(byte[] rgba)
    {
        for (int i = 2; i < rgba.Length; i += 4)
            if (rgba[i] != 255) return false;
        return true;
    }

    /// <summary>The mirrored vanilla layout, compared the way every other body-type string in here is.</summary>
    private static bool IsGen2(string? uv) => string.Equals(uv, "gen2", StringComparison.OrdinalIgnoreCase);

    internal static string? SkinBodyType(byte[] model)
    {
        try
        {
            // DRAWN materials, not declared ones: an emptied mesh still bound to _a.mtrl must not make a bibo part read as vanilla.
            return SecondSkinWriter.DrawnMaterialNames(model)
                .Select(SecondSkinWriter.SkinMaterialBodyType)
                .FirstOrDefault(t => t != null);
        }
        catch { return null; }
    }

    private static string? InferOverlayBodyType(OverlayDescriptor d)
    {
        string? found = null;
        foreach (var p in d.MaterialGamePaths)
        {
            var t = UVRemapService.InferBodyType(p);
            if (t == null) continue;
            if (found == null) found = t;
            else if (!string.Equals(found, t, StringComparison.OrdinalIgnoreCase)) return null;   // mixed
        }
        return found;
    }

    /// <summary>
    /// Load an overlay image and remap it into the body's UV space (the shell inherits the body's UVs).
    /// Mirrors CompositorService.RemapIfNeeded; keep them in step.
    /// </summary>
    private byte[]? LoadRemapped(string? rel, string sidecarRoot, string? srcType, string? dstType, int w, int h,
                                 ResampleFilter filter = ResampleFilter.Auto)
    {
        if (rel == null) return null;
        // Extension tolerance is handled in TextureLoader.LoadPngAsRgba, so skin and gear resolve identically.
        var path = Path.Combine(sidecarRoot, rel);
        return RemapPath(path, srcType, dstType, w, h, filter);
    }

    /// <summary>
    /// Remapped buffers for the composite in flight, keyed by every input the result depends on. Cleared at the top
    /// of <see cref="Build"/>. Consumers clone before mutating. Concurrent because builds overlap on this instance.
    /// </summary>
    // The filter is part of the key: an index map and a mask can be the same file.
    private readonly ConcurrentDictionary<(string Path, string? Src, string? Dst, int W, int H, ResampleFilter F), byte[]?> remapCache = new();

    private byte[]? RemapPath(string path, string? srcType, string? dstType, int w, int h,
                              ResampleFilter filter = ResampleFilter.Auto)
    {
        var png = textureLoader.LoadPngAsRgba(path, w, h, filter);
        if (png == null || srcType == null || dstType == null) return png;
        if (string.Equals(srcType, dstType, StringComparison.OrdinalIgnoreCase)) return png;

        // Only the remapping path is memoized; the returns above are already cached by the decoder.
        var key = (path, srcType, dstType, w, h, filter);
        if (remapCache.TryGetValue(key, out var hit)) return hit;
        var result = RemapPathCore(path, png, srcType, dstType, w, h, filter);
        remapCache[key] = result;
        return result;
    }

    private byte[]? RemapPathCore(string path, byte[] png, string srcType, string dstType, int w, int h,
                                  ResampleFilter filter = ResampleFilter.Auto)
    {
        // Bring a 4096² intermediate back to (w, h) with the caller's filter, so index maps are never interpolated.
        byte[] ToTarget(byte[] src, int sw, int sh)
            => TextureLoader.Resample(src, sw, sh, w, h, filter);

        // Any source -> gen2 (vanilla): vanilla UV is the RIGHT HALF of bibo UV space, so convert to
        // bibo first (via transfer map when needed), crop, then resize.
        if (string.Equals(dstType, "gen2", StringComparison.OrdinalIgnoreCase))
        {
            var native = textureLoader.LoadPngAsRgba(path, 4096, 4096, filter);
            if (native == null) return png;
            byte[] biboSpace;
            if (string.Equals(srcType, "bibo", StringComparison.OrdinalIgnoreCase))
            {
                biboSpace = native;
            }
            else
            {
                var converted = uvRemap.Remap(native, 4096, 4096, srcType, "bibo");
                if (ReferenceEquals(converted, native)) return png;   // no transfer map — leave it alone
                biboSpace = converted;
            }
            var rightHalf = UVRemapService.CropRightHalf(biboSpace, 4096, 4096);
            return ToTarget(rightHalf, 2048, 4096);
        }

        // gen2 -> an asymmetric space: the inverse of the crop above. Vanilla art describes both sides with one layout,
        // so it is spread over both halves; uvRemap.Remap has no gen2_to_* map.
        if (UVRemapService.DoubledSpaceOf(srcType) is { } srcDoubled)
        {
            // Landing in the doubled space itself needs no transfer map, so it runs straight at the requested size.
            if (string.Equals(dstType, srcDoubled, StringComparison.OrdinalIgnoreCase))
                return UVRemapService.ExpandMirrored(png, w, h, w, h);

            var expanded = UVRemapService.ExpandMirrored(png, w, h, 4096, 4096);
            var moved = uvRemap.Remap(expanded, 4096, 4096, srcDoubled, dstType);
            if (ReferenceEquals(moved, expanded)) return png;   // no transfer map — leave it alone
            return ToTarget(moved, 4096, 4096);
        }

        // Transfer maps operate at 4096x4096: remap at full res, then resize (a no-op at the cap).
        if (w != 4096 || h != 4096)
        {
            var native4k = textureLoader.LoadPngAsRgba(path, 4096, 4096, filter);
            if (native4k == null) return png;
            var remapped = uvRemap.Remap(native4k, 4096, 4096, srcType, dstType);
            if (ReferenceEquals(remapped, native4k)) return png;
            return ToTarget(remapped, 4096, 4096);
        }
        return uvRemap.Remap(png, w, h, srcType, dstType);
    }

    // ── Build instrumentation ──────────────────────────────────────────────────────────────────────────
    // Where the shell build's time goes. Reset at the top of Build and read back by the compositor.
    private readonly PhaseCounter statsCoverage       = new();   // coverage + sibling relief pre-pass
    private readonly PhaseCounter statsLayerTextures  = new();   // per layer: WriteTextures
    private readonly PhaseCounter statsLayerMaterial  = new();   // per layer: material build + write
    private readonly PhaseCounter statsBodySmooth     = new();   // body relax republish
    private readonly PhaseCounter statsWriter         = new();   // per host: SecondSkinWriter.Build
    private readonly PhaseCounter statsDump           = new();   // per host: the opt-in shell dumps
    private readonly PhaseCounter statsModelWrite     = new();   // per host: write + read-back
    private readonly PhaseCounter statsDeferredNormals = new();
    private readonly SecondSkinWriter.BuildTimings writerTimings = new();
    private readonly PhaseCounter statsWriterReused = new();   // hosts whose shell came from _shellMemo

    // ── Built shell memo ───────────────────────────────────────────────────────────────────────────────
    // The last shell built per host index, with the key of everything it was built from, so a colour edit does not
    // re-run the writer. Keyed by host index because that names the file; the key includes every layer's material name.
    private readonly Dictionary<int, (string Key, byte[] Shell, SecondSkinWriter.Stats Stats)> _shellMemo = new();

    /// <summary>The first line of two <see cref="ShellGeometryKey"/>s that differs, old and new, each cut short.</summary>
    internal static string FirstKeyDifference(string was, string now)
    {
        var a = was.Split('\n');
        var b = now.Split('\n');
        static string Cut(string s) => s.Length <= 200 ? s : s[..200] + "…";
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var la = i < a.Length ? a[i] : "(none)";
            var lb = i < b.Length ? b[i] : "(none)";
            if (!string.Equals(la, lb, StringComparison.Ordinal))
                return $"line {i}: was [{Cut(la)}] now [{Cut(lb)}]";
        }
        return "keys identical";
    }

    /// <summary>
    /// Everything <see cref="SecondSkinWriter.Build"/> reads, as one comparable string, or null when some input cannot
    /// be described (in which case the build is not cached). Authored caps are omitted: they load once per session.
    /// </summary>
    internal static string? ShellGeometryKey(IReadOnlyList<SecondSkinWriter.SourceSpec> sources,
                                             IReadOnlyList<SecondSkinLayer> layers, byte[]? baseModel)
    {
        var sb = new System.Text.StringBuilder();
        static string Set(IEnumerable<string>? s)
            => s == null ? "-" : string.Join(",", s.OrderBy(x => x, StringComparer.Ordinal));
        static string Bytes(byte[]? b) => b == null ? "-" : $"{b.Length}:{SlotHash(b):x16}";

        sb.Append("base=").Append(Bytes(baseModel)).Append('\n');
        foreach (var s in sources)
        {
            if (s.DelegateKey == null) return null;
            // The key must agree with the delegates actually present, in both directions, or trust neither.
            bool keyHasKeep = !s.DelegateKey.StartsWith("keep:-|", StringComparison.Ordinal);
            bool keyHasUv   = !s.DelegateKey.EndsWith("|uv:-", StringComparison.Ordinal);
            if (keyHasKeep != (s.KeepMaterial != null) || keyHasUv != (s.UvConv != null)) return null;
            sb.Append("src=").Append(Bytes(s.Model)).Append('|').Append(s.DelegateKey)
              .Append("|shapes=").Append(Set(s.EnabledShapes)).Append("|hidden=").Append(Set(s.HiddenAttributes))
              .Append("|drop=").Append(s.DropConnectors).Append("|unmirror=").Append(s.UnmirrorSides)
              .Append("|profile=").Append(s.Profile != null).Append('\n');
            // A profile is not an extra input: it is derived from the model bytes already in the key.
        }
        foreach (var l in layers)
        {
            if (l.Geometry.Count > 0) return null;
            sb.Append("layer=").Append(l.MaterialName)
              .Append("|cov=").Append(l.CoverageWidth).Append('x').Append(l.CoverageHeight).Append(':').Append(Bytes(l.Coverage))
              .Append("|cap=").Append(l.ToeCapWidth).Append('x').Append(l.ToeCapHeight).Append(':').Append(Bytes(l.ToeCap))
              .Append("|capS=").Append(l.ToeCapStrength.ToString("R"))
              .Append("|reinforce=").Append(l.ToeReinforceSize)
              .Append("|bust=").Append(l.BustBridgeStrength.ToString("R"))
              .Append("|nipple=").Append(l.NippleSmoothStrength.ToString("R"))
              .Append("|cleft=").Append(l.CleftBridgeStrength.ToString("R"))
              .Append("|fold=").Append(l.FoldSmoothStrength.ToString("R"))
              .Append("|push=").Append(l.PushScale.ToString("R")).Append('\n');
        }
        return sb.ToString();
    }

    private void ResetBuildStats()
    {
        statsCoverage.Reset(); statsLayerTextures.Reset(); statsLayerMaterial.Reset(); statsBodySmooth.Reset();
        statsWriter.Reset(); statsDump.Reset(); statsModelWrite.Reset(); statsDeferredNormals.Reset();
        statsWriterReused.Reset();
        writerTimings.Reset();
    }

    /// <summary>
    /// "coverage 0/6 | textures 3150/7 | …" for the last <see cref="Build"/>. "rest" is the total less everything measured.
    /// </summary>
    internal string DescribeBuildStats(double totalMs)
    {
        double measured = statsCoverage.Ms + statsLayerTextures.Ms + statsLayerMaterial.Ms + statsBodySmooth.Ms
                        + statsWriter.Ms + statsDump.Ms + statsModelWrite.Ms + statsDeferredNormals.Ms;
        static string C(PhaseCounter c) => $"{c.Ms:F0}/{c.Calls}";
        return $"coverage {C(statsCoverage)} | textures {C(statsLayerTextures)} | material {C(statsLayerMaterial)} | "
             + $"body smooth {C(statsBodySmooth)} | writer {C(statsWriter)}, {statsWriterReused.Calls} reused "
             + $"[{writerTimings.Describe()}] | "
             + $"dump {C(statsDump)} | model write {C(statsModelWrite)} | deferred normals {C(statsDeferredNormals)} | "
             + $"rest {Math.Max(0, totalMs - measured):F0}";
    }

    public Result? Build(
        string charCode,
        IReadOnlyList<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays,
        string outputRoot,
        string? bodyType,
        string? effectsFolder,
        IReadOnlyDictionary<string, string>? equippedPartModels = null,
        IReadOnlyDictionary<string, string>? equippedAccessories = null,
        Func<string, bool>? gen2Allowed = null,
        int? invisibleGlassesSet = null,
        IReadOnlyList<string>? metModels = null,
        // Shape keys the game has enabled per body-model stem (see BodyShapeReader), baked into the shell.
        IReadOnlyDictionary<string, HashSet<string>>? enabledBodyShapes = null,
        // Mods carrying a dedicated top mask shell (OverlayDescriptor.IsMaskShell); their other shells must not merge the masks.
        IReadOnlySet<string>? maskShellMods = null,
        // The bare-body e0000 models the game is drawing, per slot (DrawnModelPaths.BareBodyModelsFromModels). Null/absent slots fall back to the model code.
        IReadOnlyDictionary<string, string>? bareBodyModels = null,
        // The character's real race code, off a drawn chara/human model; null falls back to charCode (the shared body code).
        string? drawnRaceCode = null,
        // The .mtrl game paths the character is drawing, read for the material variant folder a host loads under (see VariantFolderFor).
        IReadOnlySet<string>? activeMaterials = null,
        // The Emperor's New carriers' material variants, off their sheets (see HostAccessory.KnownVariant).
        int? emperorRingVariant = null,
        int? invisibleGlassesVariant = null,
        // The character's own face/hair/tail/ear models from the live walk; the only source of non-body geometry.
        IReadOnlyList<string>? humanPartModels = null,
        // Imported content geometry this composite, appended verbatim; allocated to hosts after the gear shells.
        IReadOnlyList<(OverlayEntry Entry, ResolvedContent Content)>? contentLayers = null,
        // Every mod in the look, not just those contributing a shell (a toe cap belongs to the foot).
        IReadOnlyList<OverlayEntry>? allEntries = null,
        // Pristine bytes of human part models Proteus republished this composite (faces rewritten by FaceUvDoublingService).
        IReadOnlyDictionary<string, byte[]>? pristineHumanModels = null,
        // The sheet size to bake at, as already chosen by the caller's prefetch; null recomputes.
        int? shellTexSize = null,
        // The item variant of every drawn gear slot, for the material folder of a worn host not yet in activeMaterials (see VariantFolderFor).
        IReadOnlyList<Interop.EquippedSlotVariants.Slot>? equippedSlotVariants = null)
    {
        return new ShellSetBuild(this, charCode, gearOverlays, outputRoot, bodyType, effectsFolder, equippedPartModels, equippedAccessories, gen2Allowed, invisibleGlassesSet, metModels, enabledBodyShapes, maskShellMods, bareBodyModels, drawnRaceCode, activeMaterials, emperorRingVariant, invisibleGlassesVariant, humanPartModels, contentLayers, allEntries, pristineHumanModels, shellTexSize, equippedSlotVariants).Run();
    }

    private static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('/', '\\');

    /// <summary>
    /// The overlay's coverage, exactly as the skin layer computes it: the art's alpha shaped by the mod's selected
    /// "Masks" options (W *= (1-a), T += gray*a, result baseAlpha*W/255 + T). One byte per texel, or null when nothing
    /// bounds it. Mirrors CompositorService.CombinedMaskAt / ApplyCoverageMask; keep them in step.
    /// </summary>
    private byte[]? BuildAlpha(
        OverlayDescriptor d, OverlayEntry entry, string? srcType, string? dstType, int w, int h,
        bool maskAdds = true)
    {
        var artPath = d.Diffuse ?? d.Normal ?? d.Mask;
        var masks = discovery.ResolveActiveMasks(entry);
        if (artPath == null && masks.Count == 0)
            return null;   // empty overlay (no art, no masks) — caller drops the shell

        int n = w * h;
        var alpha = new byte[n];

        if (artPath != null)
        {
            var art = LoadRemapped(artPath, entry.SidecarRoot, srcType, dstType, w, h);
            if (art == null)
            {
                log.Warning("[Proteus] gear art failed to load: {0} (mod {1}) — dropping this shell",
                    artPath, entry.ModDirectory);
                return null;
            }
            // Every pass below is per-texel with no carried state, so partitioning cannot change a byte.
            var al = alpha; var ar = art;
            OverlayBlend.ParallelPixels(0, n, 1, (from, to) =>
            { for (int i = from; i < to; i++) al[i] = ar[i * 4 + 3]; });
        }
        else
        {
            Array.Fill(alpha, (byte)255);
        }

        // Combine the selected masks into weight/target, then apply.
        byte[]? wArr = null, tArr = null;
        for (int p = masks.Count - 1; p >= 0; p--)
        {
            var m = RemapPath(masks[p], srcType, dstType, w, h);   // masks share the overlay's UV space
            if (m == null) continue;
            if (wArr == null)
            {
                var w0 = wArr = new byte[n];
                var t0 = tArr = new byte[n];
                OverlayBlend.ParallelPixels(0, n, 1, (from, to) =>
                {
                    for (int i = from; i < to; i++)
                    {
                        int o = i * 4, a = m[o + 3];
                        int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;   // luminance
                        w0[i] = (byte)(255 - a);
                        t0[i] = (byte)(g * a / 255);
                    }
                });
            }
            else
            {
                var w0 = wArr; var t0 = tArr!;
                OverlayBlend.ParallelPixels(0, n, 1, (from, to) =>
                {
                    for (int i = from; i < to; i++)
                    {
                        int o = i * 4, a = m[o + 3];
                        int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;
                        int inv = 255 - a;
                        t0[i] = (byte)(t0[i] * inv / 255 + g * a / 255);
                        w0[i] = (byte)(w0[i] * inv / 255);
                    }
                });
            }
        }

        if (wArr != null)
        {
            var al = alpha; var w0 = wArr; var t0 = tArr;
            OverlayBlend.ParallelPixels(0, n, 1, (from, to) =>
            {
                for (int i = from; i < to; i++)
                {
                    if (al[i] == 0) continue;                      // no base coverage -> mask has no say
                    int v = al[i] * w0[i] / 255 + (maskAdds ? t0![i] : 0);
                    al[i] = (byte)(v > 255 ? 255 : v);
                }
            });
        }

        long opaque = 0, clear = 0;
        foreach (var a in alpha) { if (a == 0) clear++; else if (a == 255) opaque++; }
        log.Information(
            "[Proteus] gear coverage: art={0} masks={1} [{2}] -> {3:F1}% clear, {4:F1}% opaque, {5:F1}% partial",
            artPath ?? "none", masks.Count,
            string.Join(", ", masks.Select(Path.GetFileNameWithoutExtension)),
            clear * 100.0 / n, opaque * 100.0 / n, (n - clear - opaque) * 100.0 / n);

        return alpha;
    }

    /// <summary>
    /// Coverage for a dedicated mask shell: the mod's active masks combined and remapped into body UV. Unlike
    /// <see cref="BuildAlpha"/>, the mask IS the shape (absent mask ⇒ nothing renders). Null when no mask resolves,
    /// in which case the shell would cover the whole body, so callers gate on mask assets first.
    /// </summary>
    private byte[]? BuildMaskCoverage(OverlayEntry entry, string? srcType, string? dstType, int w, int h)
    {
        int n = w * h;
        byte[]? cov = null;
        // Combine TOP-TERRITORY-WINS, matching CombinedMaskAt: at each pixel the topmost mask with alpha decides the
        // coverage (its grayscale). Bottom masks first so the top one lands last; a black mask in its territory is a hole.
        var assets = discovery.ResolveActiveMaskAssets(entry);
        for (int mi = assets.Count - 1; mi >= 0; mi--)
        {
            var m = RemapPath(assets[mi].MaskPath, srcType, dstType, w, h);   // masks share the overlay's UV space
            if (m == null) continue;
            cov ??= new byte[n];
            for (int i = 0; i < n; i++)
            {
                int o = i * 4, a = m[o + 3];
                if (a == 0) continue;                                          // outside this mask's territory
                int g = (m[o] * 77 + m[o + 1] * 150 + m[o + 2] * 29) >> 8;     // luminance
                cov[i] = (byte)(cov[i] * (255 - a) / 255 + g * a / 255);       // territory alpha-over
            }
        }
        return cov;
    }

    /// <summary>
    /// Write out exactly what the writer is handed, so the offline harness can rebuild the same shell.
    /// Enabled by creating %TEMP%\proteus-shell-dump; delete the folder to turn it off.
    /// </summary>
    private void DumpShellInputs(int host, IReadOnlyList<SecondSkinWriter.SourceSpec> sources,
                                 IReadOnlyList<SecondSkinLayer> layers, byte[]? baseModel)
    {
        var dir = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        if (!Directory.Exists(dir)) return;
        try
        {
            var pre = Path.Combine(dir, $"host{host}_");
            for (int i = 0; i < sources.Count; i++) File.WriteAllBytes($"{pre}body{i}.mdl", sources[i].Model);
            if (baseModel != null) File.WriteAllBytes($"{pre}base.mdl", baseModel);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"bodies={sources.Count}");
            // The folder is never cleaned; this line says whether a base.mdl there belongs to this dump.
            sb.AppendLine($"base={(baseModel != null ? "yes" : "no")}");
            for (int i = 0; i < sources.Count; i++)
            {
                var sp = sources[i];
                sb.AppendLine($"source[{i}] dropRedundant={sp.DropConnectors} uvConv={(sp.UvConv == null ? "none" : "yes")} "
                            + $"shapes={(sp.EnabledShapes is { } sk ? string.Join(',', sk) : "")} "
                            + $"hiddenAttrs={(sp.HiddenAttributes is { } ha ? string.Join(',', ha) : "")}");
            }
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                sb.AppendLine($"layer[{i}] material={l.MaterialName} "
                            + $"coverage={(l.Coverage == null ? "none" : $"{l.CoverageWidth}x{l.CoverageHeight}")} "
                            + $"toeCap={(l.ToeCap == null ? "none" : $"{l.ToeCapWidth}x{l.ToeCapHeight}")} strength={l.ToeCapStrength} "
                            + $"bustBridge={l.BustBridgeStrength} nippleSmooth={l.NippleSmoothStrength} "
                            // The body passes are recorded so a dump shows whether they ran.
                            + $"cleftBridge={l.CleftBridgeStrength} smoothFold={l.FoldSmoothStrength}");
                if (l.ToeCap != null) File.WriteAllBytes($"{pre}layer{i}_toecap.raw", l.ToeCap);
                if (l.Coverage != null) File.WriteAllBytes($"{pre}layer{i}_coverage.raw", l.Coverage);
            }
            File.WriteAllText($"{pre}inputs.txt", sb.ToString());
            log.Information("[Proteus] second skin: dumped build inputs for host {0} to {1}", host, dir);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] second skin: could not dump build inputs");
        }
    }

    /// <summary>
    /// The push sweep ladder, when <c>%TEMP%\proteus-push-sweep.txt</c> exists. Announced at Information and in chat,
    /// because a sweep left on looks like a regression.
    /// </summary>
    private PushSweep? LoadPushSweep()
    {
        try
        {
            var problems = new List<string>();
            var sweep = PushSweep.LoadFromTemp(problems);
            foreach (var p in problems)
                log.Warning("[Proteus] second skin: push sweep {0}", p);
            if (sweep == null) return null;

            log.Information("[Proteus] second skin: PUSH SWEEP ON — {0}. Delete %TEMP%\\{1} to turn it off.",
                sweep.DescribeLadder(), PushSweep.FileName);
            return sweep;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] second skin: could not read the push sweep");
            return null;
        }
    }

    /// <summary>
    /// The finished shell, beside the inputs that produced it: the mesh the game actually published. Same opt-in as
    /// <see cref="DumpShellInputs"/>.
    /// </summary>
    private void DumpShellOutput(int host, byte[] shell)
    {
        var dir = Path.Combine(Path.GetTempPath(), "proteus-shell-dump");
        if (!Directory.Exists(dir)) return;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, $"host{host}_shell.mdl"), shell);
            log.Information("[Proteus] second skin: dumped built shell for host {0} to {1}", host, dir);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] second skin: could not dump built shell");
        }
    }

    /// <summary>
    /// Load this shell's toe-cap map as a single-channel mask in the BODY's UV space (red channel). Null, and no cap,
    /// when the file won't load or the map is all black.
    /// </summary>
    private byte[]? ReadToeCap(string path, string? srcType, string? dstType)
    {
        var rgba = RemapPath(path, srcType, dstType, ToeCapSize, ToeCapSize);
        if (rgba == null)
        {
            log.Warning("[Proteus] second skin: toe cap map {0} failed to load — shells built without a cap", path);
            return null;
        }

        var mask = new byte[ToeCapSize * ToeCapSize];
        bool any = false;
        for (int p = 0; p < mask.Length; p++)
        {
            mask[p] = rgba[p * 4];
            if (mask[p] != 0) any = true;
        }
        return any ? mask : null;   // all black = untouched everywhere; keep the build byte-identical
    }

    /// <summary>
    /// The toe cap for one shell: its option's own map, otherwise the shared map. Returned only when this shell's art
    /// reaches the toes, or the cap would carve a hole and fill it with fabric.
    /// </summary>
    // texSize is the build's sheet size (what `alpha` is square at); the cap map stays at ToeCapSize.
    private byte[]? ToeCapFor(OverlayDescriptor d, OverlayEntry entry, string? srcType, string? dstType,
                              byte[]? shared, byte[]? alpha, int texSize)
    {
        if ((d.ToeCapStrength ?? 1f) <= 0f) return null;

        var mask = d.ToeCap != null
            ? ReadToeCap(Path.Combine(entry.SidecarRoot, d.ToeCap), srcType, dstType)
            : shared;
        if (mask == null || alpha == null) return null;

        // How much of the capped area this shell actually paints, sampling the coverage under the map.
        int over = 0, painted = 0;
        int step = texSize / ToeCapSize;
        for (int y = 0; y < ToeCapSize; y++)
            for (int x = 0; x < ToeCapSize; x++)
            {
                if (mask[y * ToeCapSize + x] < 128) continue;
                over++;
                if (alpha[(y * step) * texSize + x * step] >= 32) painted++;
            }
        float share = over == 0 ? 0f : (float)painted / over;
        if (share < MinToeCoverage)
        {
            log.Debug("[Proteus] second skin: shell covers {0:P0} of the toe cap area — below {1:P0}, left uncapped",
                share, MinToeCoverage);
            return null;
        }

        log.Information("[Proteus] second skin: toe cap at strength {0:0.##}, shell covers {1:P0} of it",
            d.ToeCapStrength ?? 1f, share);
        return mask;
    }

    /// <summary>
    /// A REINFORCED TOE: push the capped area toward opaque, for the denser toe box of real hosiery. Returns a new
    /// alpha plane (<paramref name="alpha"/> is the shell's coverage, the normal's BLUE gate); the input is not mutated.
    /// It cannot create coverage; the boost is density × feathered cap weight; at 100 it reaches fully opaque on the
    /// same curve as a positive row Opacity.
    /// </summary>
    internal static byte[] ReinforceToeCap(byte[] alpha, byte[] cap, int texSize, int capSize,
                                           int density, int feather)
    {
        var dst = (byte[])alpha.Clone();
        if (density <= 0 || texSize <= 0 || capSize <= 0) return dst;
        if (cap.Length < capSize * capSize || alpha.Length < texSize * texSize) return dst;

        // Feathered here, on the small map, OUTWARD ONLY: the larger of the map and its blur, so the footprint keeps full
        // strength and the ramp lies outside it.
        byte[] soft;
        if (feather > 0)
        {
            soft = OverlayBlend.BlurCoverage(cap, capSize, capSize, feather);
            for (int i = 0; i < soft.Length && i < cap.Length; i++)
                if (cap[i] > soft[i]) soft[i] = cap[i];
        }
        else soft = cap;

        // Map proportionally, not by an integer stride: a stride is wrong whenever the sheet is smaller than the map.
        float amount = Math.Clamp(density, 0, 100) / 100f;

        OverlayBlend.ParallelPixels(0, texSize * texSize, 1, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                float a = dst[i] / 255f;
                if (a <= 0f) continue;                      // no fabric here; nothing to reinforce

                int cx = (int)((long)(i % texSize) * capSize / texSize);
                int cy = (int)((long)(i / texSize) * capSize / texSize);
                if (cx >= capSize || cy >= capSize) continue;

                float w = soft[cy * capSize + cx] / 255f;
                if (w <= 0f) continue;                      // outside the cap and its fade

                float op = amount * w;
                float newA = a + (1f - a) * op;             // the positive branch of the row-opacity curve
                dst[i] = (byte)(Math.Clamp(newA, 0f, 1f) * 255f + 0.5f);
            }
        });
        return dst;
    }

    /// <summary>Box-downsample the coverage for triangle trimming; it only decides keep/drop.</summary>
    private static byte[]? Downsample(byte[]? src, int w, int h, int size)
    {
        if (src == null) return null;
        var dst = new byte[size * size];
        int sx = w / size, sy = h / size;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int max = 0;   // keep a texel if ANY source texel under it is visible
                for (int j = 0; j < sy; j++)
                    for (int i = 0; i < sx; i++)
                        max = Math.Max(max, src[(y * sy + j) * w + x * sx + i]);
                dst[y * size + x] = (byte)max;
            }
        return dst;
    }

    /// <summary>
    /// The shared skin mask every vanilla body material points at, used verbatim when a skin shell's overlay ships no
    /// mask of its own.
    /// </summary>
    private const string VanillaSkinMask = "chara/common/texture/skin_mask.tex";

    /// <summary>A flat RGBA texture at <paramref name="size"/> square: the fallback for a slot the source doesn't
    /// fill.</summary>
    // The size is a parameter: some of these become buffers later passes write into, so they must match the sheet.
    private static byte[] Solid(byte r, byte g, byte b, byte a, int size)
    {
        var t = new byte[size * size * 4];
        for (int i = 0; i < t.Length; i += 4) { t[i] = r; t[i + 1] = g; t[i + 2] = b; t[i + 3] = a; }
        return t;
    }

    private List<string>? WriteTextures(
        OverlayEntry entry, OverlayDescriptor d, string shader, string texPrefix,
        string texturesDir, Dictionary<string, string> redirects, char letter, byte[]? alpha,
        string? srcType, string? dstType, List<ColorTableRowPreset>? rows, string? effectsFolder,
        // The build's sheet size (see Build); every buffer here, and the caller's `alpha`/`siblingReliefs`, is square at it.
        int texSize,
        ref bool texturesChanged, bool mergeMasks = true,
        IReadOnlyList<byte[]>? siblingReliefs = null,   // each: a normal RGBA with coverage in its alpha lane
        // The template's own texture paths, in slot order; a slot that cannot be fabricated inherits the template's.
        IReadOnlyList<string>? templateTextures = null,
        // Non-null for a reinforced toe: the normal's bytes are handed back instead of written, since the region exists
        // only after the model writer places the cap.
        DeferredShellNormal? deferNormal = null,
        // The mod's active mask assets, when already resolved; the parallel texture build must not ask Penumbra from several threads.
        List<(string MaskPath, string? NormalPath, string? IndexPath)>? maskAssets = null)
    {
        return new ShellTextureWrite(this, entry, d, shader, texPrefix, texturesDir, redirects, letter, alpha, srcType, dstType, rows, effectsFolder, texSize, mergeMasks, siblingReliefs, templateTextures, deferNormal, maskAssets).Run(ref texturesChanged);
    }

    /// <summary>
    /// A shell normal whose write was held back for the reinforced toe; filled in WriteTextures, written by
    /// <see cref="WriteDeferredNormals"/>.
    /// </summary>
    private sealed class DeferredShellNormal
    {
        public byte[]? Norm;
        public string GamePath = "";
        public string TexturesDir = "";
        public string OutputRoot = "";
        public char Letter;
        public int Size;
    }

    /// <summary>
    /// Write one shell texture slot, skipping it when nothing changed. Shared by WriteTextures and the held-back normal
    /// so they agree on compression and on "unchanged".
    /// </summary>
    private bool WriteShellSlot(string slot, byte[] data, int w, int h, string disk, bool compress,
                                ref bool texturesChanged)
    {
        // Compression (opt-in). "id" is compressed only when the trial encode proves it keeps every texel's row
        // (see IndexPlanFor): lossy error there picks the wrong row. The normal uses BC5 only when its blue (the
        // transparency gate) is uniformly 255, else BC7. Everything else is BC7.
        var content = SlotHash(data);
        var plan = TextureLoader.IndexPlan.Uncompressed;
        byte[]? snapped = null;
        var encoding = TexEncoding.Uncompressed;
        if (compress)
        {
            if (IsIndexSlot(slot))
            {
                plan = IndexPlanFor(content, data, w, h, disk, out snapped);
                encoding = plan == TextureLoader.IndexPlan.Uncompressed ? TexEncoding.Uncompressed : TexEncoding.Bc5;
            }
            else
            {
                encoding = string.Equals(slot, "norm", StringComparison.OrdinalIgnoreCase)
                    ? (IsBlueAllWhite(data) ? TexEncoding.Bc5 : TexEncoding.Bc7)
                    : TexEncoding.Bc7;
            }
        }

        // Skip the write when content, encoding and size all match what we last wrote, or every recomposite forces a
        // redraw. SlotHash (memory only). Hashed on the buffer handed in, before any snap: the snap is a function of
        // it, so identical input still means identical output.
        var hash = content
                 ^ ((ulong)((int)encoding + 1) * 0x9E3779B97F4A7C15ul)
                 ^ ((ulong)w * 0xBF58476D1CE4E5B9ul)
                 ^ ((ulong)h * 0x94D049BB133111EBul);
        bool same = _texHashes.TryGetValue(disk, out var prev) && prev == hash && File.Exists(disk);
        if (!same)
        {
            // The plan already built the snapped buffer to verify it. Only a memo hit arrives here without one, and
            // then the sheet has to be snapped again — which costs a pass, but only when the output went missing.
            if (plan == TextureLoader.IndexPlan.Bc5AfterSnap)
                data = snapped ?? TextureLoader.SnapIndexForBc5(data, w, h, out _, out _);

            if (!textureLoader.WriteTex(data, w, h, disk, encoding))
            {
                log.Error("[Proteus] second skin: failed to write {0}", disk);
                return false;
            }
            _texHashes[disk] = hash;
            texturesChanged = true;
        }
        return true;
    }

    /// <summary>
    /// Write every normal WriteTextures held back, reinforcing the toe over the cap's actual footprint. EVERY pending
    /// normal is written, since its material already names the path. Published content-addressed (see
    /// <see cref="ShellTextureNames"/>), because the game caches a texture by the file it resolved to.
    /// </summary>
    private void WriteDeferredNormals(
        List<(string Material, int Density, DeferredShellNormal Normal)> pending,
        Dictionary<string, (byte[] Mask, int Size)> regions, Dictionary<string, string> redirects,
        ref bool texturesChanged)
    {
        bool compress = config.EnableCompression && textureLoader.CompressionAffordable();
        foreach (var (material, density, pn) in pending)
        {
            if (pn.Norm == null || pn.Size <= 0) continue;
            var norm = pn.Norm;
            int n = pn.Size * pn.Size;

            if (regions.TryGetValue(material, out var fp) && norm.Length >= n * 4)
            {
                var plane = new byte[n];
                for (int i = 0; i < n; i++) plane[i] = norm[i * 4 + 2];
                // No feather: the region already carries its own soft band, in the foot's units (see ToeLine).
                var boosted = ReinforceToeCap(plane, fp.Mask, pn.Size, fp.Size, density, feather: 0);

                // Logged: how much of the sheet the region marks, and how much painted area it moved. Near 100 means the region is wrong.
                int lit = 0;
                foreach (byte px in fp.Mask) if (px >= 128) lit++;
                int painted = 0, moved = 0;
                for (int i = 0; i < n; i++)
                {
                    if (plane[i] != 0)
                    {
                        painted++;
                        if (boosted[i] != plane[i]) moved++;
                    }
                    norm[i * 4 + 2] = boosted[i];
                }
                log.Information("[Proteus] second skin: reinforced toe at {0}% up to the line across the cap's rim — "
                              + "region is {1:P1} of the sheet, moved {2:P1} of this shell's painted texels ({3}/{4})",
                    density, fp.Mask.Length == 0 ? 0f : (float)lit / fp.Mask.Length,
                    painted == 0 ? 0f : (float)moved / painted, moved, painted);
            }
            else
            {
                log.Information("[Proteus] second skin: reinforced toe at {0}% found no placed cap on {1} — the cap "
                              + "was not emitted for this shell, so its toe keeps the fabric's own density",
                    density, material);
            }

            // The name moves with the bytes and the compression setting.
            ulong nameHash = Hash(norm) ^ (compress ? 0xC0FFEE_0000_C0DEul : 0ul);
            var disk = Path.Combine(pn.TexturesDir, ShellTextureNames.ContentAddressedNormal(pn.Letter, nameHash));
            if (WriteShellSlot("norm", norm, pn.Size, pn.Size, disk, compress, ref texturesChanged))
            {
                redirects[pn.GamePath] = Rel(pn.OutputRoot, disk);
                continue;
            }

            // The write failed, but the material already names this path and a missing resource fails the whole material:
            // serve the shell's last normal instead (wrong density for one composite rather than no shell).
            if (LastNormalFor(pn.TexturesDir, pn.Letter, except: disk) is { } previous)
            {
                redirects[pn.GamePath] = Rel(pn.OutputRoot, previous);
                log.Error("[Proteus] second skin: could not write the reinforced-toe normal for {0} — serving the "
                        + "last one written for it ({1}) until the next composite", material, Path.GetFileName(previous));
            }
            else
            {
                log.Error("[Proteus] second skin: could not write the reinforced-toe normal for {0}, and no earlier "
                        + "one exists — this shell's material will fail to load until the next composite", material);
            }
        }
    }

    /// <summary>
    /// The newest normal already on disk for shell <paramref name="letter"/>, in either name form, or null; for
    /// recovering from a failed write. <paramref name="except"/> is the file whose write just failed (possibly partial).
    /// </summary>
    private static string? LastNormalFor(string texturesDir, char letter, string except)
    {
        try
        {
            var stem = $"ss_{letter}";
            return Directory.EnumerateFiles(texturesDir, $"ss_{letter}_norm*.tex")
                .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(except), StringComparison.OrdinalIgnoreCase))
                .Where(f => ShellTextureNames.TryNormalStem(Path.GetFileName(f), out var s)
                         && string.Equals(s, stem, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuild an imported pack's material onto <c>characterscroll.shpk</c> so its meshes can carry the animated glow.
    /// Null when it cannot be done; the caller then publishes the pack's own material. A base texture is lost:
    /// characterscroll has no slot for it (see <see cref="GearMaterialWriter.TextureOrder"/>).
    /// </summary>
    private byte[]? BuildContentGlowMaterial(
        ContentUnit unit, string texPrefix, string texturesDir, char letter, string? effectsFolder,
        // The build's sheet size (see Build); only the flat fallbacks use it.
        int texSize,
        Dictionary<string, string> redirects, ref bool texturesChanged)
    {
        var glow = unit.Glow;
        // ToScrollSettings returns null exactly when Scroll is empty, so a non-null result also pins the effect name.
        if (glow?.ToScrollSettings() is not { } scrollSettings || glow.Scroll is not { } effectName) return null;

        // The scroll map, from the mod's own Effects/ folder then the user's library, as a shell's glow looks it up.
        var effectPath = SidecarDiscoveryService.ResolveEffectPath(unit.Entry, effectsFolder, effectName);
        if (effectPath == null)
        {
            // Name the library folder too: it is null while Penumbra's IPC is unavailable, which looks like a missing file.
            log.Warning("[Proteus] content: {0} wants effect \"{1}\", which is in neither its own Effects "
                      + "folder nor the library ({2}) — publishing the pack's own material instead",
                unit.Entry.ModDirectory, effectName, effectsFolder ?? "(library unavailable)");
            return null;
        }
        // At its own resolution, like the shell's scroll map (see WriteTextures).
        var scrollNative = TextureLoader.ProbeSize(effectPath);
        int scrollW = scrollNative?.Width ?? texSize, scrollH = scrollNative?.Height ?? texSize;
        var scroll = textureLoader.LoadPngAsRgba(effectPath, scrollW, scrollH);
        if (scroll == null)
        {
            log.Warning("[Proteus] content: effect \"{0}\" could not be decoded ({1})", effectName, effectPath);
            return null;
        }

        var template = textureLoader.LoadRawMtrl(null, GearMaterialWriter.TemplateFor(RenderModeInference.GlowShader));
        if (template == null)
        {
            log.Error("[Proteus] content: missing the {0} template material", RenderModeInference.GlowShader);
            return null;
        }

        // The pack's own texture paths; a slot the pack doesn't name gets WriteTextures' fallback.
        var packTex = TextureLoader.ParseMtrlBytes(unit.Mtrl);
        var outputRoot = Directory.GetParent(texturesDir)!.FullName;
        // A ref parameter can't be captured by a local function; folded back into the caller's flag below.
        bool wroteAnything = false;

        string? Publish(string slot, byte[] rgba, int w, int h)
        {
            var gamePath = texPrefix + slot + ".tex";
            var disk = Path.Combine(texturesDir, $"ss_{letter}_{slot}.tex");
            // Same rules as the shell path: "id" only when the trial encode proves it, BC7 for the continuous slots.
            var content = Hash(rgba);
            var plan = TextureLoader.IndexPlan.Uncompressed;
            byte[]? snapped = null;
            var encoding = TexEncoding.Uncompressed;
            if (config.EnableCompression && textureLoader.CompressionAffordable())
            {
                if (IsIndexSlot(slot))
                {
                    plan = IndexPlanFor(content, rgba, w, h, disk, out snapped);
                    encoding = plan == TextureLoader.IndexPlan.Uncompressed
                        ? TexEncoding.Uncompressed
                        : TexEncoding.Bc5;
                }
                else
                {
                    encoding = TexEncoding.Bc7;
                }
            }

            var hash = content
                     ^ ((ulong)((int)encoding + 1) * 0x9E3779B97F4A7C15ul)
                     ^ ((ulong)w * 0xBF58476D1CE4E5B9ul)
                     ^ ((ulong)h * 0x94D049BB133111EBul);
            if (!(_texHashes.TryGetValue(disk, out var prev) && prev == hash && File.Exists(disk)))
            {
                // Only on a real write — see WriteShellSlot.
                if (plan == TextureLoader.IndexPlan.Bc5AfterSnap)
                    rgba = snapped ?? TextureLoader.SnapIndexForBc5(rgba, w, h, out _, out _);

                if (!textureLoader.WriteTex(rgba, w, h, disk, encoding))
                {
                    log.Error("[Proteus] content: failed to write {0}", disk);
                    return null;
                }
                _texHashes[disk] = hash;
                wroteAnything = true;
            }
            redirects[gamePath] = Rel(outputRoot, disk);
            return gamePath;
        }

        // Republish the pack's own textures under paths Proteus owns: a pack's invented texture paths are not unique, and
        // another mod can win them. Copied by BYTES, not re-encoded.
        var packRoot = unit.Entry.ModRoot;
        string? Republish(string slot, string? packPath, byte[] fallback)
        {
            // Nothing named at all: the shell's own fallback for an empty slot.
            if (packPath == null) return Publish(slot, fallback, texSize, texSize);

            // The pack's own selection first (see SelectedTextureFiles), before Penumbra or a folder name search.
            var file = unit.TexFiles.TryGetValue(packPath, out var chosen) ? chosen
                     : packRoot == null ? null
                     : ContentTextureFile(packRoot, packPath);

            // Named but not shipped by the pack: it may be a vanilla texture, so keep the author's path.
            if (file == null) return packPath;

            try
            {
                var disk = Path.Combine(texturesDir, $"ss_{letter}_{slot}.tex");
                var gamePath = texPrefix + slot + ".tex";
                // Through the memo, like the non-glow path: re-reading the pack's art is what dominates a composite.
                if (RepublishPackTexture(packRoot, gamePath, file, disk)) wroteAnything = true;

                redirects[gamePath] = Rel(outputRoot, disk);
                return gamePath;
            }
            catch (Exception ex)
            {
                log.Warning("[Proteus] content: could not republish {0} ({1}) — naming the pack's own path "
                          + "instead: {2}", slot, file, ex.Message);
                return packPath;
            }
        }

        // Fallbacks match the shell path exactly: a white mask and a row-16-A index.
        var norm = Republish("norm", packTex.Normal, Solid(128, 128, 255, 255, texSize));
        var mask = Republish("mask", packTex.Mask,   Solid(255, 255, 255, 255, texSize));
        var id   = Republish("id",   packTex.Index,  Solid(255, 255, 0, 255, texSize));
        var catc = Publish("catc", scroll, scrollW, scrollH);
        texturesChanged |= wroteAnything;
        if (norm == null || mask == null || id == null || catc == null) return null;

        // Built with no rows, then the pack's colour table grafted on, then the user's rows over that. Grafting after Build
        // keeps the author's table and restores the tile alpha Build zeroes.
        var built = GearMaterialWriter.Build(template, [norm, mask, id, catc], rows: null, scroll: scrollSettings);
        var grafted = GearMaterialWriter.CopyColorTable(built, unit.Mtrl);
        return GearMaterialWriter.PatchColorTable(grafted, unit.Rows, isScroll: true);
    }

    /// <summary>
    /// The material file this piece should publish now, out of the game paths its pack redirects the leaf under, or
    /// null to fall back on the importer's record. Asked after <see cref="SelectedMaterialFile"/>, for layouts the option
    /// map cannot describe. Only answers from inside the mod's own folder are accepted.
    /// </summary>
    /// <param name="cache">
    /// Per-build memo keyed by mod root and game path; the answer cannot change within a build.
    /// </param>
    private string? ContentMaterialFile(
        string modRoot, IReadOnlyList<string> gamePaths, Dictionary<string, string?> cache)
    {
        foreach (var gamePath in gamePaths)
        {
            var key = modRoot + '\u0000' + gamePath;
            if (!cache.TryGetValue(key, out var hit))
            {
                var viaPenumbra = penumbra.ResolvePlayer(gamePath);
                // ResolvePlayer echoes the request back when nothing redirects it, which is not a file.
                cache[key] = hit =
                    viaPenumbra != null
                 && !string.Equals(viaPenumbra, gamePath, StringComparison.OrdinalIgnoreCase)
                 && File.Exists(viaPenumbra)
                 && IsUnder(modRoot, viaPenumbra)
                        ? viaPenumbra
                        : null;
            }
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>
    /// The file inside a pack that backs one of the textures its material names. A fallback after <c>unit.TexFiles</c>.
    /// Penumbra's answer is only accepted from inside THIS pack. Null when the pack ships nothing by that name,
    /// which is normal for a vanilla texture.
    /// </summary>
    private string? ContentTextureFile(string modRoot, string gamePath)
    {
        var viaPenumbra = penumbra.ResolvePlayer(gamePath);
        // ResolvePlayer echoes the request back when nothing redirects it, which is not a file.
        if (viaPenumbra != null
            && !string.Equals(viaPenumbra, gamePath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(viaPenumbra)
            && IsUnder(modRoot, viaPenumbra))
            return viaPenumbra;

        try
        {
            var leaf = Path.GetFileName(gamePath.Replace('\\', '/'));
            return leaf.Length == 0
                ? null
                : Directory.EnumerateFiles(modRoot, leaf, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// The accessory a second skin rides on. For an already-equipped item <see cref="BaseModel"/> holds its model bytes
    /// (the shell is appended) and <see cref="BaseMatCount"/> its material count; for a carrier both are 0/null.
    /// </summary>
    // ModelPath is the ACTUAL loaded game path for an equipped host, used as the redirect key so the char code matches
    // what the game requested. Null for the Emperor fallback, whose EQDP edit makes the rebuilt path correct.
    private readonly record struct HostAccessory(
        int SetId, string Slot, string EqdpSlot, byte[]? BaseModel, int BaseMatCount, string Tree, char Prefix,
        string? ModelPath = null,
        // The ITEM variant, off the item sheet, for a CARRIER (equipped after the shell is built, so nothing drawn can
        // answer). Null for the player's own items (VariantFolderFor reads the draw object). The IMC entry maps it to a folder.
        int? KnownVariant = null);

    /// <summary>
    /// Every model the shell can be hosted on, in FILL priority (policy in the method body); a host holds up to
    /// <c>MaxMaterials - BaseMatCount</c> layers. <paramref name="cutCode"/> is the race code the shell geometry is in;
    /// a CARRIER may load under another code, a worn item may not. <paramref name="equipCode"/> predicts where our
    /// not-yet-loaded injected glasses will appear.
    /// </summary>
    private List<HostAccessory> ChooseHosts(string cutCode, string equipCode, string wearerCode,
        IReadOnlyDictionary<string, string>? equipped,
        IReadOnlyList<string>? metModels, int? invisibleGlassesSet, string outputRoot,
        IReadOnlySet<string> hostedPackRoots,
        int? emperorRingVariant, int? invisibleGlassesVariant,
        out List<(HostAccessory Host, string By)> claimedCarriersOut)
    {
        return new HostChooser(this, cutCode, equipCode, wearerCode, equipped, metModels, invisibleGlassesSet, outputRoot, hostedPackRoots, emperorRingVariant, invisibleGlassesVariant).Run(out claimedCarriersOut);
    }

}
