using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CheapLoc;
using Dalamud.Game.Text.SeStringHandling;
using Penumbra.Api.Enums;

namespace Proteus.Services;

using static Proteus.Services.IslandBlur;

using static Proteus.Services.OverlayBlend;

public partial class CompositorService
{
    // ── Compositing ──────────────────────────────────────────────────────────

    // Load the base normal texture through ResolveUpstream (never our own output; falls back to the last known upstream).
    // After loading, resets alpha to 0 if >50% of pixels are 255, a fingerprint of our own stale output.
    private byte[] LoadBaseNormal(string gamePath, ref int w, ref int h)
    {
        var loaded = TimedLoadBaseTexture(ResolveUpstream(gamePath), gamePath);
        if (!loaded.HasValue) return Array.Empty<byte>();

        var rgba = loaded.Value.rgba;
        w = loaded.Value.width;
        h = loaded.Value.height;
        return rgba;
    }

    /// <summary>
    /// Whether a material is painted in the body's UV space, where gear shells and Masks coverage art are authored.
    /// <see cref="UVRemapService.InferBodyType"/> recognises body-UV suffixes anywhere (body mods route skin through
    /// <c>chara/equipment/</c>); the <c>/obj/body/</c> fallback catches unclassified suffixes.
    /// </summary>
    internal static bool IsBodyUvMaterial(string mtrlGamePath)
        => UVRemapService.InferBodyType(mtrlGamePath) != null
        || mtrlGamePath.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The UV space an overlay's art was painted for: its declared <see cref="OverlayDescriptor.SourceBodyType"/>, else
    /// inferred from its target materials. Null for art naming no body layout (assumed already in the destination space).
    /// </summary>
    internal static string? SrcBodyTypeOf(OverlayDescriptor d)
    {
        if (d.SourceBodyType != null) return d.SourceBodyType;
        if (d.MaterialGamePaths.Any(p => p.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)))
            return "bibo";
        if (d.MaterialGamePaths.Any(p => UVRemapService.InferBodyType(p) == "gen3")) return "gen3";
        return null;
    }

    /// <summary>
    /// Whether any vanilla/gen2 body surface is on the character (a mirrored layout that folds asymmetric art). Presence,
    /// not exclusivity: gear ships its own vanilla skin beside a bibo body. InferBodyType only answers gen2 under
    /// <c>/obj/body/</c>, so a hit is a real body surface.
    /// </summary>
    /// <remarks>
    /// Takes the concrete <see cref="HashSet{T}"/> so <c>Contains</c> uses its OrdinalIgnoreCase comparer, not LINQ's.
    /// </remarks>
    internal static bool HasMirroredBodySurface(HashSet<string> activeBodyTypes)
        => activeBodyTypes.Contains("gen2");

    /// <summary>
    /// Whether this overlay can only keep both of its sides on a shell (the third reason
    /// <see cref="RenderModeInference.ShouldPromoteToGear"/> promotes a Skin overlay): a mirrored body folds asymmetric
    /// art, and only a shell can map both halves. <paramref name="wearingMirroredBody"/> is passed in so the compositor
    /// judges every overlay against one reading.
    /// </summary>
    /// <param name="faceHandledInPlace">
    /// Face materials whose model was rewritten into the doubled layout (<see cref="FaceUvDoublingService"/>); art on them
    /// needs no shell. All of an overlay's materials or none.
    /// </param>
    internal static bool NeedsUnmirroredShell(OverlayDescriptor d, bool wearingMirroredBody,
                                              IReadOnlySet<string>? faceHandledInPlace = null)
    {
        if (d.AsymmetricArt != true) return false;
        // The art needs two sides to spread: art authored in a mirrored space has only one.
        var src = d.SourceBodyType ?? SrcBodyTypeOf(d);
        if (src == null || UVRemapService.DoubledSpaceOf(src) != null) return false;

        // A doubled face sheet always needs the shell: the vanilla face layout is mirrored on every character.
        if (string.Equals(src, UVRemapService.FaceSplitSpace, StringComparison.OrdinalIgnoreCase))
            return faceHandledInPlace == null
                || d.MaterialGamePaths.Count == 0
                || !d.MaterialGamePaths.All(faceHandledInPlace.Contains);

        return wearingMirroredBody;
    }

    /// <summary>
    /// Which enabled mods have a toe cap selected; public so the editor asks the same promotion predicate. Per mod, so a
    /// stale selection bit can only promote its own mod's overlays.
    /// </summary>
    public HashSet<string> ToeCapWanted(IEnumerable<OverlayEntry> allEntries)
    {
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The collection is read once, not per mod. Null means no player, so nothing is on.
        var collId = penumbra.GetPlayerCollectionId();
        if (collId != null)
            foreach (var e in allEntries)
                if (e.Enabled && discovery.ResolveActiveToeCap(e, collId.Value) != null)
                    want.Add(e.ModDirectory);
        _toeCapWantedSnapshot = want;
        return want;
    }

    /// <summary>
    /// The last composite's answer for one mod, for the editor. False until a composite has run.
    /// </summary>
    public bool ToeCapWantedFor(string modDirectory) => _toeCapWantedSnapshot.Contains(modDirectory);

    /// <summary>
    /// Replaced wholesale, never mutated. The composite is the only writer; readers are on the framework thread.
    /// </summary>
    private volatile HashSet<string> _toeCapWantedSnapshot = new(StringComparer.OrdinalIgnoreCase);

    public bool NeedsUnmirroredShell(OverlayDescriptor d)
    {
        var snapshot = _activeMtrlSnapshot;
        if (snapshot == null) return false;   // nothing known about the worn body — keep today's behaviour
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in snapshot)
        {
            var bt = UVRemapService.InferBodyType(m);
            if (bt != null) types.Add(bt);
        }
        // The last composite's doubled face materials, so the editor's badge says what was composited.
        return NeedsUnmirroredShell(d, HasMirroredBodySurface(types), _faceDoubledMaterials);
    }

    /// <summary>
    /// Whether a shell can be cut for this overlay at all (the precondition on
    /// <see cref="RenderModeInference.ShouldPromoteToGear"/>): body, face, hair, tail and ears. Path-based only, never
    /// live state, so the editor's controls don't flicker. An overlay naming no material stays shellable.
    /// </summary>
    internal static bool CanRenderAsShell(OverlayDescriptor d)
        => ShellSurface.CanShell(d.MaterialGamePaths);

    /// <summary>
    /// The models that own a skin material's UV layout: the c####e0000 top/dwn/glv/sho parts the body is drawn as, not
    /// chara/human/…/c####b####.mdl (body replacers leave that vanilla). Null for anything but a human body material.
    /// </summary>
    internal static string[]? BodyModelPathsFor(string mtrlGamePath)
    {
        if (string.IsNullOrEmpty(mtrlGamePath)) return null;
        var m = BodyMaterialPath.Match(mtrlGamePath.Replace('\\', '/'));
        if (!m.Success) return null;
        string code = m.Groups[1].Value;
        return [$"chara/equipment/e0000/model/c{code}e0000_top.mdl",
                $"chara/equipment/e0000/model/c{code}e0000_dwn.mdl",
                $"chara/equipment/e0000/model/c{code}e0000_glv.mdl",
                $"chara/equipment/e0000/model/c{code}e0000_sho.mdl"];
    }

    private static readonly Regex BodyMaterialPath =
        new(@"^chara/human/c(\d{4})/obj/body/b(\d{4})/material/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Island labellings, reused because they depend only on the island mask and plane size. Instance-scoped and capped
    /// (each entry is large); keyed on a checksum of the mask so a replaced transfer map isn't served a stale labelling.
    /// </summary>
    private readonly List<((string BodyType, int W, int H, long Sum) Key, (int[] Labels, int[] Owner, int Count) Value)>
        islandLabelCache = new();
    private const int MaxCachedLabelings = 2;
    private readonly object islandLabelLock = new();

    internal (int[] Labels, int[] Owner, int Count) IslandLabelsFor(string bodyType, byte[] inside, int w, int h)
    {
        // Cheap but content-sensitive: the mask is 0/255, so a strided sum distinguishes any real change.
        long sum = 0;
        for (int p = 0; p < w * h; p += 97) sum += inside[p];
        var key = (bodyType, w, h, sum);

        lock (islandLabelLock)
        {
            for (int i = 0; i < islandLabelCache.Count; i++)
                if (islandLabelCache[i].Key == key) return islandLabelCache[i].Value;
        }

        // Built outside the lock; a concurrent duplicate build wastes work, never misleads.
        var labels = LabelIslands(inside, w, h, out int count);
        var built = (labels, NearestIslandOwner(labels, w, h), count);

        lock (islandLabelLock)
        {
            for (int i = 0; i < islandLabelCache.Count; i++)
                if (islandLabelCache[i].Key == key) return islandLabelCache[i].Value;
            if (islandLabelCache.Count >= MaxCachedLabelings) islandLabelCache.RemoveAt(0);
            islandLabelCache.Add((key, built));
        }
        return built;
    }

    /// <summary>
    /// The highest priority <see cref="TryRaisePriorityAbove"/> has tried this session: stops re-attempting a write
    /// Penumbra accepts but does not apply. Reset when every path resolves to us again. Composite thread only.
    /// </summary>
    private int _highestPriorityRaiseAttempted = int.MinValue;

    /// <summary>
    /// Raise the managed mod above every mod in <paramref name="owners"/>, which were confirmed to take paths Proteus
    /// publishes (<see cref="VerifyRedirectsLive"/>). Returns whether the priority was changed. Latched on what was
    /// attempted (<see cref="_highestPriorityRaiseAttempted"/>), since a temporary collection may not reflect the write.
    /// </summary>
    private bool TryRaisePriorityAbove(IReadOnlyCollection<string> owners)
    {
        if (!config.AutoRaiseModPriority) return false;

        var collId = penumbra.GetPlayerCollectionId();
        if (!collId.HasValue) return false;

        int current = penumbra.GetModSettings(collId.Value, SidecarDiscoveryService.ManagedModDir)?.Priority
                   ?? config.ManagedModPriority;

        // The highest priority among the mods taking paths from us; folders with unreadable settings are skipped.
        int highest = int.MinValue;
        string? highestOwner = null;
        int outranked = 0;
        foreach (var owner in owners)
        {
            // Never outrank ourselves: the target would move up with us forever.
            if (string.Equals(owner, SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            {
                log.Warning("[Proteus] the managed mod appeared in its own outranked set — ignoring. A path "
                          + "resolving to our own folder is a stale publish, not a conflict.");
                continue;
            }

            if (penumbra.GetModSettings(collId.Value, owner)?.Priority is not { } p) continue;
            outranked++;
            if (p > highest) { highest = p; highestOwner = owner; }
        }
        if (highestOwner == null) return false;

        if (highest == int.MaxValue)
        {
            log.Warning("[Proteus] \"{0}\" sits at the maximum priority ({1}), so the managed mod cannot be "
                      + "raised above it — move that mod down instead.", highestOwner, int.MaxValue);
            return false;
        }

        int target = highest + 1;
        if (target <= current) return false;   // already above it; whatever beat us wasn't priority

        if (target <= _highestPriorityRaiseAttempted)
        {
            log.Warning("[Proteus] already raised the managed mod to {0} this session and \"{1}\" still wins "
                      + "its paths — the priority change is not taking effect (a temporary collection, e.g. "
                      + "one Mare created, cannot always be written to). Not retrying; set it by hand in "
                      + "Penumbra if the overlays stay missing.", target, highestOwner);
            return false;
        }
        _highestPriorityRaiseAttempted = target;

        var ec = penumbra.SetModPriority(collId.Value, SidecarDiscoveryService.ManagedModDir, target);
        if (ec != PenumbraApiEc.Success)
        {
            log.Warning("[Proteus] could not raise the managed mod's priority to {0}: {1}", target, ec);
            return false;
        }

        // Accepted is not applied: say so here.
        if (penumbra.GetModSettings(collId.Value, SidecarDiscoveryService.ManagedModDir)?.Priority is { } after
            && after != target)
            log.Warning("[Proteus] Penumbra accepted the priority change to {0} but still reports {1} — the "
                      + "collection in use may be a temporary one that cannot be written to.", target, after);

        config.ManagedModPriority = target;
        config.Save();
        log.Information("[Proteus] raised managed mod priority {0} -> {1}, above \"{2}\" ({3})",
            current, target, highestOwner, highest);

        // Two whole phrases rather than an inline "(s)", which is the only form that translates.
        var names = outranked == 1
            ? string.Format(Loc.Localize("Chat.PriorityRaised.One.Fmt", "\"{0}\""), highestOwner)
            : string.Format(Loc.Localize("Chat.PriorityRaised.Many.Fmt", "\"{0}\" and {1} more"),
                highestOwner, outranked - 1);
        var msg = string.Format(Loc.Localize("Chat.PriorityRaised.Fmt",
            "[Proteus] {0} was overriding the skin textures Proteus composites your overlays into, so they "
            + "could not show up. Raised Proteus' Penumbra priority to {1} to fix it. (Turn off "
            + "\"Auto-raise mod priority\" in Proteus' settings if you'd rather it didn't.)"), names, target);
        _ = Plugin.Framework.RunOnFrameworkThread(
            () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 45).Build()));

        // Only who wins the redirects changed; a recomposite makes Penumbra recompute. The guard above prevents a loop.
        TriggerRecomposite("priority-raised", force: true);
        return true;
    }

    /// <summary>
    /// Which mod folder was last reported as taking each path, so the notice fires on a change of state. Cleared when we
    /// win a path back.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _reportedRedirectLosses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Say in chat that another mod has taken a path Proteus composites: it looks like Proteus being broken, and naming
    /// the culprit is the fix. Once per path per change of owner. Marshalled onto the framework thread (ChatGui's queue).
    /// </summary>
    private void NotifyRedirectLost(string gamePath, string? winningDisk)
    {
        var owner = ModFolderOf(winningDisk) ?? winningDisk;
        if (string.IsNullOrEmpty(owner)) return;   // nothing provides it — not another mod's doing

        if (_reportedRedirectLosses.TryGetValue(gamePath, out var reported)
            && string.Equals(reported, owner, StringComparison.OrdinalIgnoreCase))
            return;
        _reportedRedirectLosses[gamePath] = owner;

        // The file name alone fits a chat line; the log line above carries the full path.
        var msg = string.Format(Loc.Localize("Chat.RedirectLost.Fmt",
            "[Proteus] \"{0}\" overrides {1}, which Proteus composites your overlays into — so they will "
            + "not show up. Fix: in Penumbra, raise the \"Proteus\" mod's priority above \"{0}\"."),
            owner, Path.GetFileName(gamePath));
        _ = Plugin.Framework.RunOnFrameworkThread(
            () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 17).Build()));
    }

    private void NotifyGlowPromoted(OverlayEntry entry, List<ColorTableRowPreset>? rows)
    {
        if (!HasEmissiveRow(rows) || !_glowPromotedMods.TryAdd(entry.ModDirectory, 0)) return;

        // Log only: in chat this fired every session for mods that ship a skin glow on purpose.
        log.Information("[Proteus] \"{0}\" sets Glow on a skin layer; rendering that option as a cloth layer",
            entry.ModName);
    }

    /// <summary>
    /// A skin overlay asked for something only a shell renders (glow, sphere, metal, specular, or above gear) but paints a
    /// surface no shell can be cut from (face, hair, tail). It stays skin and loses the feature.
    /// </summary>
    private void NotifyNoShellSurface(OverlayEntry entry, List<ColorTableRowPreset>? rows, OverlayDescriptor d)
    {
        if (!_noShellMods.TryAdd(entry.ModDirectory, 0)) return;

        log.Information("[Proteus] no shell surface for \"{0}\" (layer {1}): overlay targets [{2}] — not a "
            + "surface a shell can be cut from, so it stays skin and its Glow/Cloth features go unrendered",
            entry.ModName, d.Layer, string.Join(", ", d.MaterialGamePaths));

        // Chat only when a glow is being lost, the visible symptom.
        if (!HasEmissiveRow(rows)) return;

        var msg = string.Format(Loc.Localize("Chat.NoShellSurface.Fmt",
            "[Proteus] \"{0}\" sets Glow on an overlay Proteus cannot build a layer for. Glow needs a layer "
            + "over the mesh, and that only works on your own skin — body, face, hair, tail or ears — not "
            + "on gear, accessories or weapons."), entry.ModName);
        _ = Plugin.Framework.RunOnFrameworkThread(
            () => Plugin.ChatGui.Print(new SeStringBuilder().AddUiForeground(msg, 25).Build()));
    }

    /// <summary>
    /// Key for the inherited-mask-colorset table, one per (body material, mod). NUL-joined; the dictionary is OrdinalIgnoreCase.
    /// </summary>
    private static string MaskFallbackKey(string mtrlGamePath, string modDir) => mtrlGamePath + '\0' + modDir;

    internal static string? BodyCodeFromCustomize(byte race, byte tribe, byte sex)
    {
        bool f = sex == 1;
        if (race == 1) return (tribe == 2, f) switch // Hyur: tribe 2 = Highlander
        {
            (false, false) => "c0101",
            (false, true)  => "c0201",
            (true,  false) => "c0301",
            _              => "c0401",
        };
        return race switch
        {
            2 or 3 or 4 or 5 => f ? "c0201" : "c0101", // Elezen/Lalafell/Miqo'te/Roegadyn share mid bodies
            6 => f ? "c1401" : "c1301", // Au Ra
            7 => f ? "c1601" : "c1501", // Hrothgar
            8 => f ? "c1801" : "c1701", // Viera
            _ => null,
        };
    }

    internal static string SanitizeName(string gamePath)
    {
        var name = Path.GetFileNameWithoutExtension(gamePath);
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }

    /// <summary>
    /// A texture every material naming it samples (<c>chara/common/texture/skin_mask.tex</c>, solid-colour placeholders,
    /// eye maps). Redirecting one leaks to every character, and Penumbra refuses the worst of them.
    /// </summary>
    internal static bool IsSharedTexturePath(string gamePath)
        => gamePath.StartsWith("chara/common/", StringComparison.OrdinalIgnoreCase)
        || gamePath.StartsWith("common/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The private game path a composite publishes <paramref name="slot"/> of <paramref name="mtrlGamePath"/> at, in place
    /// of a shared texture (<see cref="IsSharedTexturePath"/>). Stable per material and slot, not content-addressed, so the
    /// rewritten material doesn't change with every pixel; the disk file is content-addressed.
    /// </summary>
    internal static string OwnedTexturePath(string mtrlGamePath, string slot)
    {
        var lower = mtrlGamePath.Replace('\\', '/').ToLowerInvariant();
        var hash = (uint)SecondSkinService.Hash(System.Text.Encoding.UTF8.GetBytes(lower));
        return $"{OwnedTextureRoot}{SanitizeName(lower)}_{hash:x8}_{slot}.tex";
    }

    /// <summary>
    /// Eight hex chars identifying <paramref name="data"/> (plus <paramref name="salt"/> values that change its encoding):
    /// the filename suffix for every skin texture and material we write. Changed bytes get a new name (the game caches
    /// by path); unchanged bytes keep theirs, so sync plugins don't re-transfer.
    /// </summary>
    internal static string ContentTag(byte[] data, params int[] salt)
    {
        // FNV-1a over 8 bytes at a time; only needs to distinguish one texture's previous content, not be globally collision-resistant.
        ulong h = 14695981039346656037;
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(data);
        foreach (var w in words) { h ^= w; h *= 1099511628211; }
        for (int i = words.Length * 8; i < data.Length; i++) { h ^= data[i]; h *= 1099511628211; }
        foreach (var s in salt) { h ^= (ulong)s; h *= 1099511628211; }
        return h.ToString("x16")[..8];
    }
}
