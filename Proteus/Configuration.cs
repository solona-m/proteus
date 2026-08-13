using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace Proteus;

/// <summary>Which sibling body materials Proteus synthesizes for a mod's overlays.</summary>
public enum SiblingSynthesisMode
{
    /// <summary>No sibling synthesis at all (neither gen3 nor vanilla).</summary>
    Off = 0,
    /// <summary>gen3 (_b.mtrl) and bibo (_bibo) bake only — the legacy default; no vanilla.</summary>
    BiboGen3Only = 1,
    /// <summary>gen3 (_b.mtrl), bibo (_bibo.mtrl) bake plus vanilla (gen2 _a.mtrl) generation.</summary>
    AllBodies = 2,
}

/// <summary>Which body's redundant connector submeshes to skip when building the second-skin shell.</summary>
public enum ConnectorMeshMode
{
    /// <summary>Emit every skin submesh — the default, correct for vanilla/Bibo/etc.</summary>
    Off = 0,
    /// <summary>Skip Neolithe's joint-connector submeshes, which overlap its already-complete body.</summary>
    Neolithe = 1,
}

/// <summary>Cached classification of one mod directory for <see cref="Configuration.KnownBodyMods"/>.</summary>
[Serializable]
public class BodyModCacheEntry
{
    public bool IsBodyMod { get; set; }
    public long Fingerprint { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>
    /// Current config schema version. Bump whenever a STORED value has to be reinterpreted on load, and
    /// add the step to <see cref="Migrate"/>. Note the property default below is this constant, not 1: a
    /// brand-new config is stamped current and so never runs a migration written for settings it was
    /// never saved with.
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public bool PluginEnabled { get; set; } = true;

    public bool DisableAutoRedraw { get; set; } = false;

    /// <summary>
    /// Let a composite triggered by an ambient event — zoning, a redraw, Glamourer re-asserting temporary
    /// settings — return early when its inputs are identical to the composite already published. On by
    /// default: those triggers fire several times per zone and re-run a multi-second pipeline to produce
    /// byte-identical output, which is what "my mods disappear when I zone" was.
    ///
    /// Anything the user or a plugin explicitly asks for is never skipped, regardless of this setting.
    /// Turn it off only to rule the gate out when diagnosing a change that isn't taking effect.
    /// </summary>
    public bool SkipUnchangedComposites { get; set; } = true;

    /// <summary>
    /// Block-compress the baked output textures. Cuts each to ~1 byte/pixel (a 4K RGBA 64 MB → 16 MB) on
    /// disk and in VRAM. Off = uncompressed B8G8R8A8 (byte-identical to legacy output).
    /// <para/>
    /// BC7, not BC5, for every SKIN channel — the skin normal carries data in its B/A channels too, and
    /// BC5 is two-channel, so it would corrupt them (see CompositorService's blend loop). The shell path
    /// is narrower: BC5 for its normal only when that normal's blue is uniformly opaque (blue is the gear
    /// transparency gate, so there is nothing to lose at 255), BC7 otherwise, and the index texture is
    /// never compressed at all — its red/green encode discrete colour-table row selectors, where any
    /// lossy error crosses a bucket boundary and picks the wrong row.
    /// </summary>
    public bool EnableCompression { get; set; } = false;

    /// <summary>
    /// Render shell coverage as a HARD alpha-test cutout (g_AlphaThreshold left at the template's 0) instead
    /// of smooth alpha blending (threshold 1). Cutout renders more like opaque geometry, which lets sphere
    /// maps and metalness survive gpose's transparent pass — at the cost of hard/aliased sheer edges. Off
    /// (default) keeps the smooth transparency that sheer fabrics need. Experimental gpose-reflection lever.
    /// </summary>
    public bool GearCutoutAlpha { get; set; } = false;

    /// <summary>
    /// Prefer Glamourer's in-place equipment reload (ReapplyState) over a full Penumbra redraw when
    /// refreshing composited textures. Avoids the despawn/respawn flicker. Falls back to a full
    /// redraw automatically when Glamourer is unavailable or has no state for the player.
    /// </summary>
    public bool UseInPlaceReload { get; set; } = true;

    // SyncSettleRedraw lived here until 2026-08-09: a full redraw a few seconds after edits stopped, so
    // sync plugins would pick the composite up. Removed after measuring that they already do — see the
    // note above CompositorService's decode-cache fields. A stale value left in an existing config.json
    // is ignored; the deserializer drops properties it doesn't know.

    public int ManagedModPriority { get; set; } = 900;

    /// <summary>
    /// How strongly to suppress skin-tone tinting on opaque overlay pixels (0–1), by fading the
    /// normal map's skin-color-influence channel under the overlay. 1 = overlays keep their authored
    /// color on any skin tone (but those pixels read slightly shinier, since the channel also softens
    /// the skin's specular/subsurface response). 0 = disabled — overlays are tinted by skin tone as
    /// the game normally does, and Proteus no longer rewrites the normal for diffuse-only overlays.
    /// </summary>
    public float SkinColorSuppression { get; set; } = 1f;

    /// <summary>
    /// Strength of the ambient-occlusion contact shadow baked onto the skin diffuse just outside strap /
    /// garment edges (0–2). 0 = off. Applied per mod from its mask, or from the garment's own coverage
    /// when it ships no mask (so non-masked straps like a bralette cast a shadow too).
    /// </summary>
    public float AmbientOcclusionStrength { get; set; } = 1f;

    /// <summary>
    /// How far the ambient-occlusion shadow spreads from an edge, as a fraction of the skin texture width
    /// (blur radius = width × this). UI range 0.001–0.005 (~4–20 px at 4K). Larger = wider/softer. Shared
    /// by the shadow and the normal indent.
    /// </summary>
    public float AmbientOcclusionSoftness { get; set; } = 0.003f;

    /// <summary>
    /// Depth of the normal-map indentation ("Skindenting") baked at strap / garment edges, so the skin reads
    /// as pressed in under the strap (0–10). 0 = off. Uses the same edge silhouette and softness as the AO
    /// shadow; tilts the skin normal toward the strap (a concave groove). FFXIV uses OpenGL-style green-up
    /// normals.
    /// </summary>
    public float AmbientOcclusionNormalDepth { get; set; } = 7f;

    /// <summary>
    /// Skip a body's redundant connector rings when building the second-skin shell. Some bodies
    /// (Neolithe) reinforce each joint (wrist/ankle/…) with a small extra submesh that overlaps an
    /// already-complete main body; on a semi-transparent gear shell that overlap doubles the alpha and
    /// shows as a more-opaque seam. The connector is the mesh's last submesh, so we drop that one only.
    /// Off by default — on most bodies the last submesh is real skin.
    /// </summary>
    public ConnectorMeshMode HideConnectorMeshes { get; set; } = ConnectorMeshMode.Off;

    /// <summary>When true, saving a Glamourer design auto-captures the current Proteus state bound to it.</summary>
    public bool DesignBindingEnabled { get; set; } = true;

    /// <summary>
    /// Also treat Glamourer's automation applies (gearset / job change) as design applications.
    /// Glamourer raises no apply signal at all on that path — see
    /// <c>DesignBindingService.IsInferredAutomationApply</c> for the Reapply→Gearset pairing this
    /// infers it from. Restore-only: an inferred signal can restore a binding, never clear one.
    /// Requires <see cref="DesignBindingEnabled"/>.
    /// </summary>
    public bool DesignBindingFollowsAutomation { get; set; } = true;

    /// <summary>
    /// When true and no real glasses are worn, Proteus has Glamourer equip an (invisible-rendered) glasses
    /// item so the second-skin shell can ride the facewear slot instead of a ring/accessory. On by default;
    /// note it writes a (hidden) bonus item to the player's Glamourer state (see <c>CompositorService</c>
    /// reconcile). Applied with ApplyFlag.Once and re-asserted on design/reset; removed on disable, unload,
    /// real glasses, or turning this off.
    /// </summary>
    public bool AutoInvisibleGlasses { get; set; } = true;

    /// <summary>
    /// When true and nothing the player wears can host the shell in its own model space, Proteus has
    /// Glamourer equip the (invisible) Emperor's New Ring in the free RIGHT ring slot and hosts there.
    /// <para/>
    /// This is what makes non-Midlander races fit. The game race-deforms a model by the race code of the
    /// path it loaded it from: facewear and most accessories ship per-race, so a shell cut from c0201 body
    /// parts hosted on them gets no deform and renders a race-size wrong. We write the ring's EQDP entry
    /// ourselves, so we can leave the wearer's own race empty and let the lookup fall through to the c0201
    /// model we publish — inheriting the same deform the body already gets. Never steals an occupied right
    /// ring slot; same ApplyFlag.Once/removal contract as <see cref="AutoInvisibleGlasses"/>.
    /// </summary>
    public bool AutoEmperorRing { get; set; } = true;

    /// <summary>
    /// The Glamourer design whose Proteus binding was active when the plugin last ran, so a reload can
    /// pick it back up — Glamourer raises no apply signal for a design that is ALREADY applied, so
    /// without this the overrides stay null and the first composite paints metadata colours over a
    /// design the player is still visibly wearing. Null = nothing was active (a revert, or an explicit
    /// clear), which must stay cleared across the reload.
    /// <para/>
    /// Restored only after verifying it still matches live Glamourer state — see
    /// <c>DesignBindingService.TryBootRestore</c>. It lives here rather than in design_bindings.json
    /// because it is rewritten on every apply and every revert, and that file reaches tens of MB and is
    /// serialized under the binding lock.
    /// </summary>
    public Guid? LastActiveDesignId { get; set; } = null;

    /// <summary>Optional explicit path to Glamourer's designs directory; null = derive from the config dir.</summary>
    public string? GlamourerDesignDirOverride { get; set; } = null;

    /// <summary>Per-mod sibling-synthesis mode, keyed by Penumbra mod directory.
    /// Absent = BiboGen3Only (default, = legacy behavior: gen3 bake, no vanilla).</summary>
    public Dictionary<string, SiblingSynthesisMode> SiblingSynthesis { get; set; } = new();

    /// <summary>
    /// Ceiling on decoded-texture memory, in MB. One 4K RGBA texture is 64 MB, so this is effectively a
    /// count of textures: 2048 ≈ 30 of them.
    /// <para/>
    /// It only pays off above the composite's whole working set. The compositor reads the same files in the
    /// same order every run, which is the worst case for LRU — at 1 GB against an 18-file (~1.2 GB) outfit
    /// every file missed on every composite, costing 1420 ms of a 3244 ms run. Raise it if the phases log
    /// still shows misses on a repeat composite; lower it if the game starts paging.
    /// </summary>
    public int DecodeCacheBudgetMb { get; set; } = 2048;

    /// <summary>LEGACY, read-only now: mods the user had explicitly switched AO off for under the old
    /// "on unless opted out" rule. Still consulted by <see cref="AmbientOcclusionEnabledFor"/> so an
    /// existing opt-out keeps working, but nothing writes to it any more — new choices go to
    /// <see cref="AmbientOcclusionOverrides"/>. OrdinalIgnoreCase to match how mod directories are compared
    /// everywhere else (Newtonsoft populates the existing instance in place on deserialize, so the comparer
    /// survives the config round-trip).</summary>
    public HashSet<string> AmbientOcclusionDisabledMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Explicit per-mod AO/Skindenting choices made by the USER, keyed by Penumbra mod directory.
    /// Absent means the user has no opinion and the mod pack's own declaration decides. Present wins over
    /// the pack either way — a user who turns it on for a pack that never asked gets it.</summary>
    public Dictionary<string, bool> AmbientOcclusionOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether AO / Skindenting applies to a mod. The user's explicit choice first, then the legacy opt-out,
    /// then what the pack itself declared — and OFF if nobody said anything.
    /// <para/>
    /// Off is the default because the effect treats a mod's coverage as a garment pressed into skin. That
    /// suits straps and trim and actively damages flat artwork (tattoos, skin details, makeup), which is the
    /// larger share of packs — so it is opt-in, per <see cref="ProteusMetadata.AmbientOcclusion"/>.
    /// </summary>
    public bool AmbientOcclusionEnabledFor(string modDir, bool? packDeclared)
        => AmbientOcclusionOverrides.TryGetValue(modDir, out var user) ? user
         : !AmbientOcclusionDisabledMods.Contains(modDir) && (packDeclared ?? false);

    /// <summary>Sibling-synthesis mode for a mod, applying the absent-default.</summary>
    public SiblingSynthesisMode SiblingModeFor(string modDir) =>
        SiblingSynthesis.TryGetValue(modDir, out var m) ? m : SiblingSynthesisMode.BiboGen3Only;

    /// <summary>Per-mod cache of whether it ships obj/body/ material redirects, keyed by mod
    /// directory. Invalidated by Fingerprint (file size + mtime summed over the mod's own
    /// default_mod.json/group_*.json manifests) so mod updates are picked up without a plugin
    /// restart. Lets the compositor avoid an expensive Penumbra resource-tree walk unless a mod
    /// that could actually change the active body-type materials was touched.</summary>
    public Dictionary<string, BodyModCacheEntry> KnownBodyMods { get; set; } = new();

    /// <summary>Last-known active player material paths, persisted so the compositor doesn't need
    /// an expensive Penumbra resource-tree walk immediately at plugin boot/login — it seeds from
    /// this and only re-fetches once something actually invalidates it (a body mod change, or a
    /// real redraw).</summary>
    public List<string>? CachedActiveMaterialPaths { get; set; } = null;

    /// <summary>Game model paths the second skin last APPENDED into — the player's own necklace/ring,
    /// whose model we read back as the base for the merge. Persisted because the managed mod's manifest
    /// survives a restart and masks these paths from the very first composite of a session, while the
    /// in-memory upstream cache starts empty: without this, PrimeUpstreamCache cannot tell which host
    /// paths are worth unmasking before a shell has been built, and the first composite of every session
    /// would rebuild a modded host from vanilla. Carrier hosts are deliberately absent — their model is
    /// replaced, never read, so unmasking one only blanks the shell.</summary>
    public List<string>? AppendHostModelPaths { get; set; } = null;

    /// <summary>User-chosen stacking order for overlays within one Penumbra multi-select group, keyed by
    /// <see cref="StackKey"/> → option names TOP-FIRST. Options in the same group otherwise share a
    /// <c>GroupOrder</c> and stack in arbitrary order; this breaks that tie. Options not listed keep their
    /// existing relative order (they fall after listed ones). A user preference, so it lives here rather
    /// than in the mod folder and survives mod updates.</summary>
    public Dictionary<string, List<string>> OverlayStackOrder { get; set; } = new();

    /// <summary>Composite key for <see cref="OverlayStackOrder"/> (tuple keys don't round-trip through the
    /// config JSON). NUL-separated (a control char) so neither part can collide.</summary>
    public static string StackKey(string modDir, string group) => modDir + "\u0000" + group;

    /// <summary>Position of <paramref name="option"/> in its group's user stack order (0 = top). Returns
    /// <see cref="int.MaxValue"/> when unset, so unlisted options sort to the bottom while an all-unset
    /// group stays a tie (preserving the existing stable order — no change until the user reorders).</summary>
    public int StackIndexOf(string modDir, string group, string option)
    {
        if (OverlayStackOrder.TryGetValue(StackKey(modDir, group), out var order))
        {
            int i = order.FindIndex(o => string.Equals(o, option, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return int.MaxValue;
    }

    /// <summary>Persist the full top-first order for a group and save.</summary>
    public void SetStackOrder(string modDir, string group, IEnumerable<string> optionsTopFirst)
    {
        OverlayStackOrder[StackKey(modDir, group)] = new List<string>(optionsTopFirst);
        Save();
    }

    /// <summary>
    /// One flat top-first stack per MOD, spanning every group. <see cref="OverlayStackOrder"/> above is
    /// per group, so two overlays in different groups (say Patterns and Fabric) could never be ordered
    /// against each other — the group's Penumbra number decided, and one group was always on top no
    /// matter how the tabs were arranged. This is what the tab strip actually writes now.
    ///
    /// Entries are <see cref="ModStackEntry"/> values, since an option name is only unique within a group.
    /// The old per-group orders are still honoured as a lower-priority tiebreak, so nothing a user already
    /// arranged is lost — see the sort in CompositorService.
    /// </summary>
    public Dictionary<string, List<string>> OverlayModStackOrder { get; set; } = new();

    /// <summary>Identifies one option inside a mod-wide stack. NUL-separated, same reasoning as StackKey.</summary>
    public static string ModStackEntry(string group, string option) => group + "\u0000" + option;

    /// <summary>Position of (group,option) in a top-first <see cref="ModStackEntry"/> list (0 = top), or
    /// <see cref="int.MaxValue"/> when not listed. Shared by the composite sort and the tab strip so a
    /// design-binding stack override resolves identically in both (see CompositorService.ModStackIndexFor
    /// and StatusWindow's ModStackIdx).</summary>
    public static int ModStackIndexIn(IReadOnlyList<string> order, string group, string option)
    {
        var key = ModStackEntry(group, option);
        for (int i = 0; i < order.Count; i++)
            if (string.Equals(order[i], key, StringComparison.OrdinalIgnoreCase))
                return i;
        return int.MaxValue;
    }

    /// <summary>Position in the mod-wide stack (0 = top), or <see cref="int.MaxValue"/> when unset.</summary>
    public int ModStackIndexOf(string modDir, string group, string option)
        => OverlayModStackOrder.TryGetValue(modDir, out var order)
            ? ModStackIndexIn(order, group, option)
            : int.MaxValue;

    /// <summary>Persist the mod's full top-first stack across all groups, and save.</summary>
    public void SetModStackOrder(string modDir, IEnumerable<(string Group, string Option)> topFirst)
    {
        OverlayModStackOrder[modDir] = topFirst.Select(x => ModStackEntry(x.Group, x.Option)).ToList();
        Save();
    }

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        Migrate();
        pluginInterface.SavePluginConfig(this);
    }

    /// <summary>
    /// Carry a config written by an older build forward. Runs once at load, before any service reads a
    /// setting, and <see cref="Initialize"/> saves the result — so each step applies exactly once.
    /// </summary>
    private void Migrate()
    {
        // v1 -> v2: block compression is no longer on by default. Changing the property default reaches
        // only NEW configs, so without this every user who had already run the plugin would have stayed
        // on BC7 permanently. Forced off rather than left to the default, because the point is to move
        // existing users off it. Their baked output is not stranded: the encoding is part of each
        // texture's content tag (CompositorService's encSalt), so the names change and the next
        // composite rebakes uncompressed rather than re-approving the old compressed files.
        if (Version < 2) EnableCompression = false;

        Version = CurrentVersion;
    }

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
