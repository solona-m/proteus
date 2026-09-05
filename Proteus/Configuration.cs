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
    /// <summary>Ships files under a skin surface tree (body/face/hair/tail/zear) — the wide verdict
    /// that drives cache invalidation.</summary>
    public bool IsBodyMod { get; set; }

    /// <summary>Provides at least one game path the composite actually reads as a base — the narrow
    /// verdict that gates a recomposite. Computed against the base set named by
    /// <see cref="BaseKeysHash"/>.</summary>
    public bool AffectsComposite { get; set; }

    /// <summary>Content hash of the composite base set <see cref="AffectsComposite"/> was computed
    /// against; a mismatch retires that verdict (the fingerprint alone cannot catch it, because the
    /// mod is unchanged and it is OUR inputs that moved).</summary>
    public int BaseKeysHash { get; set; }

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
    public const int CurrentVersion = 4;

    public int Version { get; set; } = CurrentVersion;

    public bool PluginEnabled { get; set; } = true;

    /// <summary>
    /// How many times the "you are on the old plugin repo URL" notice has been shown. Capped at
    /// <see cref="MirrorNoticeLimit"/> — a hint worth giving is not worth nagging about, and someone
    /// who has ignored it three times has decided.
    /// <para/>
    /// Persisted rather than counted per-session so it survives restarts; a per-session counter would
    /// show it forever to anyone who never changes the URL, which is exactly who it would annoy most.
    /// </summary>
    public int MirrorNoticeShown { get; set; }

    /// <summary>
    /// Let Proteus act on its own initiative. On (default) is the normal plugin: an ambient event —
    /// zoning, a redraw, an equipment change, Glamourer re-asserting temporary settings — recomposites
    /// if it needs to, and Proteus reloads the character so the result is visible.
    /// <para/>
    /// Off makes Proteus almost entirely reactive: no composite and no reload happens unless the user asks
    /// for one. Every editor interaction still recomposites and republishes (those are forced — see
    /// CompositorService.TriggerRecomposite), you just won't SEE the change until something redraws you:
    /// zoning, changing gear, or Penumbra's Redraw button. Nothing is lost while it is off, because the
    /// previous composite's output stays published — the character keeps wearing it.
    /// <para/>
    /// "Almost": an equipment change while a gear shell is hosted still recomposites, because that one
    /// leaves an ACTION of ours wrong rather than just a look stale — see TriggerRecomposite's
    /// autoRedrawExempt. The reload stays suppressed even there.
    /// </summary>
    public bool AutoRedraw { get; set; } = true;

    /// <summary>
    /// Legacy storage for the inverted form of <see cref="AutoRedraw"/>. Read ONCE, by the v3 -> v4
    /// migration, and never again — every consumer uses <see cref="AutoRedraw"/>. It stays a serialized
    /// property so a config written by an older build still carries the user's choice into that
    /// migration; deleting it would silently reset everyone who had the old setting on.
    /// </summary>
    public bool DisableAutoRedraw { get; set; } = false;

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

    /// <summary>
    /// The folder the Import tab's file picker opens in — wherever a pack was last picked from.
    /// <para/>
    /// Persisted rather than kept for the session because packs come from one download folder and importing
    /// several is the normal case, so restarting the game should not send the picker back to the top of the
    /// drive. Empty, or a folder since deleted, simply opens the dialog's own default.
    /// </summary>
    public string LastImportDir { get; set; } = string.Empty;

    public int ManagedModPriority { get; set; } = 900;

    /// <summary>
    /// Let Proteus raise the managed mod's priority above any mod observed taking a path it publishes.
    ///
    /// On by default because the failure it fixes is otherwise invisible and near-undiagnosable: a skin or
    /// tattoo mod that ships its own copy of a body texture — chara/bibo_mid_base.tex and friends — silently
    /// wins that one path, so overlays half-apply (the normal lands, the diffuse doesn't) while every log line
    /// reads healthy. The number here isn't a preference anyone holds; it only has to be higher than whatever
    /// else claims the same file.
    ///
    /// Turn it off if you deliberately want another mod to win a path Proteus composites. It only ever acts on
    /// a loss that has been positively confirmed — see VerifyRedirectsLive — never on a guess.
    /// </summary>
    public bool AutoRaiseModPriority { get; set; } = true;

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

    /// <summary>
    /// Master switch for light-sensitive glow. Off stops the light probe running at all, and every row
    /// glows at its authored brightness the way it did before the feature existed — so a mod that ships a
    /// light response still renders, just unconditionally.
    /// </summary>
    public bool LightResponseEnabled { get; set; } = true;

    /// <summary>
    /// Pin the light level by hand instead of reading the scene. This is how a dark-only glow is tested
    /// without waiting for dusk, and the escape hatch for gpose, where the rig's lighting is the point and
    /// an estimate of it is not wanted.
    /// </summary>
    public bool LightResponseManual { get; set; }

    /// <summary>The pinned level (0 = pitch dark, 1 = full daylight) used while
    /// <see cref="LightResponseManual"/> is on.</summary>
    public float LightResponseManualLevel { get; set; } = 0f;

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

    // AutoEmperorRing was removed. It gated whether the reconcile would EQUIP an invisible carrier, but not
    // whether ChooseHosts would offer one as a host — so turning it off did not stop layers being assigned to
    // carriers, it only stopped those carriers from ever being worn. The layers then rendered nothing, and
    // the "no host can carry it" warning could not fire because they had been given a host. That was survivable
    // while one ring was the only accessory carrier and every other host was something already on the player;
    // it stopped being survivable when face/hair/tail surfaces arrived, since those can ONLY ride a carrier.
    // Rather than thread the flag into host selection, the option is gone: an invisible piece in a slot the
    // player left empty is the mechanism the feature is built on, and it is removed again the moment it stops
    // hosting.

    /// <summary>
    /// Which accessory slots hold an invisible "Emperor's New" piece that PROTEUS equipped — a COMMA-JOINED
    /// SET drawn from "rir", "ril", "wrs", "nek" (e.g. <c>"nek,wrs"</c>), or null for none.
    /// <para/>
    /// A set, not one slot: a look can need a carrier in more than one place, because a carrier is the only
    /// host whose EQDP entry is ours to rewrite and therefore the only one that can carry a face, hair or
    /// tail layer without the game deforming it. The pieces are separate items (Ring, Bracelets, Necklace)
    /// that merely share the a0053 accessory model set, so the slot is what identifies each one.
    /// <para/>
    /// The NAME is deliberately stale. Renaming it would silently discard the value in every existing
    /// config, and the value is an ownership record — losing it strands a piece on the player with nothing
    /// left that knows we put it there. The comma-joined encoding is backward compatible for the same
    /// reason: an older config holding <c>"rir"</c> reads back as exactly that one slot.
    /// <para/>
    /// Persisted because it is the only thing separating our piece from the player's. These are ordinary
    /// obtainable items that plenty of people wear by choice (invisible hands, invisible arms), so "an a0053
    /// model is in this slot" cannot answer ownership — and that was the test the removal path used, which
    /// meant Proteus unequipped jewellery the player had put on themselves the moment a shell went to some
    /// other host. In memory alone it was useless for the job: a plugin reload cleared it while our piece
    /// stayed equipped, so gating removal on it would have stranded the piece instead.
    /// </summary>
    public string? InjectedRingSlot { get; set; }

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

    /// <summary>
    /// Size the user dragged the window to on the Toggles tab, which is the only tab that is resizable —
    /// every other one is AlwaysAutoResize and has no size of its own to remember.
    /// <para/>
    /// UNSCALED, like <c>StatusWindow.SizeConstraints</c>: Dalamud's window host multiplies both by the
    /// global UI scale itself, so storing a scaled size would compound the scale on every restore. Clamped
    /// to the resizable constraints on read, since the scale (or the constraints) can change between runs.
    /// </summary>
    public float TogglesWindowWidth { get; set; } = 900f;

    /// <inheritdoc cref="TogglesWindowWidth"/>
    public float TogglesWindowHeight { get; set; } = 700f;

    /// <summary>
    /// Directory the last <c>.pmp</c> export was saved to, so the save dialog reopens where the user left
    /// it. Null (or a path that no longer exists) falls back to the desktop.
    /// </summary>
    public string? LastExportDirectory { get; set; } = null;

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
    /// <para/>
    /// The default is derived from the machine (see <see cref="DefaultDecodeCacheBudgetMb"/>) rather than
    /// fixed, because the old fixed 2048 was smaller than the compositor's own prefetch window on any
    /// serious outfit: a measured run evicted 54 entries while holding 32, so completed prefetch decodes
    /// were being thrown away and re-paid on the critical path.
    /// </summary>
    public int DecodeCacheBudgetMb { get; set; } = DefaultDecodeCacheBudgetMb();

    /// <summary>Hard bounds on the budget, shared by the settings slider and the migration.</summary>
    public const int MinDecodeCacheBudgetMb = 512;
    public const int MaxDecodeCacheBudgetMb = 32768;

    /// <summary>
    /// A budget sized to the machine: an eighth of physical RAM, clamped to [2 GB, 16 GB].
    /// <para/>
    /// An eighth leaves the game and everything else the overwhelming majority, while still clearing the
    /// working set of a heavy outfit on any modern box (16 GB → 2 GB, unchanged from the old fixed default;
    /// 96 GB → 12 GB, comfortably resident). <c>TotalAvailableMemoryBytes</c> rather than a P/Invoke because
    /// it needs no interop and respects a container or job-object limit if one is ever imposed; it can read
    /// 0 on an unexpected runtime, which falls back to the old default rather than to nothing.
    /// </summary>
    public static int DefaultDecodeCacheBudgetMb()
    {
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (ram <= 0) return 2048;
        return (int)Math.Clamp(ram / 8 / (1024 * 1024), 2048, 16384);
    }

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

    /// <summary>Game paths the last composite read as bases. Persisted so the "does this mod feed our
    /// composite?" test — the one that stops an unrelated hair/face/iris mod from forcing a full
    /// recomposite — is answerable from the first mod-settings event of a session, rather than failing
    /// open until the first composite of that session has run.</summary>
    public List<string>? CachedCompositeBaseKeys { get; set; } = null;

    /// <summary>Shape of the composite the set above describes — a hash of the material paths it
    /// targets. Persisted alongside it so a path retired when overlays change stays retired across a
    /// restart, instead of the restored set being treated as belonging to whatever shape runs next.</summary>
    public int CachedCompositeBaseSignature { get; set; }

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

        // v2 -> v3: the decode-cache budget is machine-sized now. The old fixed 2048 MB holds 32 4K
        // textures, which is smaller than the compositor's own prefetch window — a measured run evicted 54
        // entries while holding 32, so completed prefetch decodes were thrown away and re-paid on the
        // critical path. Changing the property default reaches only NEW configs, hence this step.
        //
        // Conditioned on the value still being the OLD DEFAULT, not on Math.Max: anything else is a number
        // the user chose with the slider, and both directions are meaningful. Someone who lowered it did so
        // because the game was paging; someone who raised it to the old 4096 ceiling still gets to keep
        // that. Only an untouched setting is ours to move.
        if (Version < 3 && DecodeCacheBudgetMb == 2048)
            DecodeCacheBudgetMb = DefaultDecodeCacheBudgetMb();

        // v3 -> v4: the setting is stated positively now ("Auto redraw" on by default) instead of as the
        // opt-out "Disable auto redraw". Carry the old choice across rather than letting everyone fall to
        // the new property's default: someone who had the opt-out ticked asked us not to touch their
        // character, and silently undoing that is the one outcome they would notice immediately.
        //
        // Unconditional, not guarded on the old value being non-default: BOTH states are meaningful here,
        // and false -> true is exactly what a config that never touched the setting should get anyway.
        if (Version < 4) AutoRedraw = !DisableAutoRedraw;

        Version = CurrentVersion;
    }

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
