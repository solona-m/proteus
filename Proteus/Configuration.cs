using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace Proteus;

/// <summary>
/// Whether Proteus overlays a mod onto a vanilla (gen2) body, stored per mod; read only through
/// <see cref="Configuration.OverlaysVanillaFor"/>. Members kept with their values so older configs deserialize.
/// </summary>
public enum SiblingSynthesisMode
{
    /// <summary>No vanilla (gen2).</summary>
    Off = 0,
    /// <summary>LEGACY: now identical to <see cref="AllBodies"/>.</summary>
    BiboGen3Only = 1,
    /// <summary>Vanilla (gen2) is overlaid whenever the character is wearing it.</summary>
    AllBodies = 2,
}

/// <summary>
/// Legacy storage for <see cref="Configuration.HideConnectorMeshes"/>, kept with its member names only so older
/// configs still deserialize.
/// </summary>
public enum ConnectorMeshMode
{
    Off = 0,
    Neolithe = 1,
}

/// <summary>Cached classification of one mod directory for <see cref="Configuration.KnownBodyMods"/>.</summary>
[Serializable]
public class BodyModCacheEntry
{
    /// <summary>Ships files under a skin surface tree (body/face/hair/tail/zear); the wide verdict that drives cache invalidation.</summary>
    public bool IsBodyMod { get; set; }

    /// <summary>Provides at least one game path the composite reads as a base; the narrow verdict that gates a
    /// recomposite. Computed against <see cref="BaseKeysHash"/>.</summary>
    public bool AffectsComposite { get; set; }

    /// <summary>Content hash of the composite base set <see cref="AffectsComposite"/> was computed against; a
    /// mismatch retires that verdict, since our inputs moved while the mod did not.</summary>
    public int BaseKeysHash { get; set; }

    public long Fingerprint { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>
    /// Current config schema version. Bump whenever a STORED value has to be reinterpreted on load, and add the step
    /// to <see cref="Migrate"/>. A new config is stamped current, so it never runs old migrations.
    /// </summary>
    public const int CurrentVersion = 8;

    public int Version { get; set; } = CurrentVersion;

    public bool PluginEnabled { get; set; } = true;

    /// <summary>
    /// How many times the "you are on the old plugin repo URL" notice has been shown; persisted so the cap
    /// (Plugin.MirrorNoticeLimit) holds across restarts.
    /// </summary>
    public int MirrorNoticeShown { get; set; }

    /// <summary>
    /// The highest <see cref="Plugin.CurrentWhatsNew"/> generation whose release notes have been read. Zero on a
    /// fresh config AND on one written before this field existed, so a new install and an upgrade both see the
    /// current notes exactly once. Written when the window is CLOSED, so a crash cannot burn the one showing.
    /// </summary>
    public int WhatsNewShown { get; set; }

    /// <summary>
    /// Whether release notes of the given generation are still owed to this install. The one place the gate is
    /// decided, so the window, the plugin and the tests cannot drift apart on what "already read" means.
    /// </summary>
    public bool WantsWhatsNew(int generation) => WhatsNewShown < generation;

    /// <summary>
    /// Let Proteus act on its own initiative: ambient events recomposite and reload the character. Off makes it
    /// reactive: editor actions still recomposite, but nothing reloads until something redraws the character. On by default.
    /// </summary>
    public bool AutoRedraw { get; set; } = true;

    /// <summary>
    /// Legacy inverted form of <see cref="AutoRedraw"/>, read ONCE by the v3 -> v4 migration; deleting it would reset
    /// everyone's old choice.
    /// </summary>
    public bool DisableAutoRedraw { get; set; } = false;

    /// <summary>
    /// Block-compress the baked output textures (about 4x smaller on disk and in VRAM). BC7 for skin channels (the normal
    /// carries B/A data); the index texture is never compressed. Off by default: uncompressed B8G8R8A8.
    /// </summary>
    public bool EnableCompression { get; set; } = false;

    /// <summary>
    /// Render shell coverage as a hard alpha-test cutout instead of smooth alpha blending, so sphere maps and metalness
    /// survive gpose's transparent pass. Off by default: sheer fabrics need smooth edges. Experimental.
    /// </summary>
    public bool GearCutoutAlpha { get; set; } = false;

    /// <summary>
    /// Prefer Glamourer's in-place reload over a full Penumbra redraw, avoiding the respawn flicker. On by default;
    /// falls back to a redraw when Glamourer cannot.
    /// </summary>
    public bool UseInPlaceReload { get; set; } = true;

    /// <summary>
    /// The folder the Import tab's file picker opens in, persisted because packs usually come from one download folder.
    /// </summary>
    public string LastImportDir { get; set; } = string.Empty;

    public int ManagedModPriority { get; set; } = 900;

    /// <summary>
    /// Let Proteus raise the managed mod's priority above any mod confirmed to win a path it publishes. On by default,
    /// because that loss is otherwise invisible (overlays half-apply); acts only on a confirmed loss (VerifyRedirectsLive).
    /// </summary>
    public bool AutoRaiseModPriority { get; set; } = true;

    /// <summary>
    /// How strongly to suppress skin-tone tinting on opaque overlay pixels (0–1), by fading the normal's
    /// skin-color-influence channel. 1 (default) keeps authored colour; 0 disables it.
    /// </summary>
    public float SkinColorSuppression { get; set; } = 1f;

    /// <summary>
    /// Strength of the ambient-occlusion contact shadow baked onto the skin just outside garment edges (0–2); 0 = off.
    /// </summary>
    public float AmbientOcclusionStrength { get; set; } = 1f;

    /// <summary>
    /// How far the AO shadow spreads, as a fraction of the skin texture width (UI range 0.001–0.005). Shared by the
    /// shadow and the normal indent.
    /// </summary>
    public float AmbientOcclusionSoftness { get; set; } = 0.003f;

    /// <summary>
    /// Depth of the normal-map indentation ("Skindenting") baked at garment edges (0–10); 0 = off. Uses the AO
    /// silhouette and softness.
    /// </summary>
    public float AmbientOcclusionNormalDepth { get; set; } = 7f;

    /// <summary>
    /// Skip skin the second-skin shell would otherwise draw twice, which doubles the alpha on a semi-transparent shell.
    /// On by default and body-agnostic; see <c>SecondSkinWriter.PlanConnectorDrops</c>.
    /// </summary>
    public bool HideRedundantMeshes { get; set; } = true;

    /// <summary>
    /// Legacy storage for the body-specific form of <see cref="HideRedundantMeshes"/>. Never read; kept so the name
    /// is not reused for something else.
    /// </summary>
    public ConnectorMeshMode HideConnectorMeshes { get; set; } = ConnectorMeshMode.Off;

    /// <summary>
    /// Master switch for light-sensitive glow. Off stops the light probe and every row glows at its authored brightness.
    /// </summary>
    public bool LightResponseEnabled { get; set; } = true;

    /// <summary>
    /// Pin the light level by hand instead of reading the scene: for testing dark-only glow and for gpose.
    /// </summary>
    public bool LightResponseManual { get; set; }

    /// <summary>The pinned level (0 = pitch dark, 1 = full daylight) used while
    /// <see cref="LightResponseManual"/> is on.</summary>
    public float LightResponseManualLevel { get; set; } = 0f;

    /// <summary>When true, saving a Glamourer design auto-captures the current Proteus state bound to it.</summary>
    public bool DesignBindingEnabled { get; set; } = true;

    /// <summary>
    /// Also treat Glamourer's automation applies (gearset / job change) as design applications, inferred as in
    /// <c>DesignBindingService.IsInferredAutomationApply</c>. Restore-only. Requires <see cref="DesignBindingEnabled"/>.
    /// </summary>
    public bool DesignBindingFollowsAutomation { get; set; } = true;

    /// <summary>
    /// When a bound design is applied, unequip slots it leaves unset and switch off imported packs its binding didn't
    /// capture. Explicit applies only. Off by default. Requires <see cref="DesignBindingEnabled"/>.
    /// </summary>
    public bool DesignBindingUnequipUnsetSlots { get; set; } = false;

    /// <summary>
    /// When a bound design is restored, also restore every mod on the character it captured (non-Proteus mods held
    /// with locked temporary settings). Off by default: Proteus mods only. Requires <see cref="DesignBindingEnabled"/>.
    /// </summary>
    public bool DesignBindingRestoresCharacterMods { get; set; } = false;

    /// <summary>
    /// With no real glasses worn, have Glamourer equip an invisible glasses item for the second-skin shell to ride.
    /// On by default; it writes a hidden bonus item to the player's Glamourer state, removed when no longer needed.
    /// </summary>
    public bool AutoInvisibleGlasses { get; set; } = true;

    /// <summary>
    /// Patch the worn hairstyle for hat compatibility (<c>atr_kam</c> above the hat line, a <c>shp_hib</c> press below).
    /// On by default (v8). It writes into another author's mod folder, so it acts only while a hat is worn, and the
    /// chat notice fires per hairstyle fitted.
    /// </summary>
    public bool AutoHatCompat { get; set; } = true;

    /// <summary>
    /// Render asymmetric FACE art by rewriting the face model into the doubled sheet layout. On by default: a shell
    /// cannot carry a face (it has no shape keys, so it cannot blink). Off falls back to the fold.
    /// </summary>
    public bool FaceUvInPlace { get; set; } = true;

    /// <summary>
    /// Stop the window's decorative motion: hovers land at once, the ambient glow and pulses hold still. For people
    /// who find motion distracting or uncomfortable.
    /// </summary>
    public bool ReduceMotion { get; set; } = false;

    /// <summary>Paint the slow ember glow behind the status window's contents.</summary>
    public bool AmbientBackground { get; set; } = true;

    // No "hat-compat notice shown" flag: the notice fires per hairstyle fitted, tracked by the patch record on disk.

    /// <summary>
    /// Which accessory slots hold an invisible "Emperor's New" piece that PROTEUS equipped: a comma-joined set of
    /// "rir", "ril", "wrs", "nek", or null. The name is stale but renaming would lose this ownership record, which is
    /// the only thing separating our piece from one the player wears by choice.
    /// </summary>
    public string? InjectedRingSlot { get; set; }

    /// <summary>
    /// Whether the invisible glasses on the player's face are a pair PROTEUS equipped. The carrier is a real item
    /// (<see cref="Services.InvisibleGlasses"/>) that players can also wear by choice, so this record, not the item, is what makes it ours to remove.
    /// </summary>
    public bool InjectedGlasses { get; set; }

    /// <summary>
    /// The body mod the Studio's Body size tool last refitted ONTO, by folder. Null until one is chosen.
    /// <para/>
    /// A player refits onto the body they are wearing, over and over, across every garment they own — so the last
    /// answer is very nearly always the next one. Only the destination is remembered: the "made for" side belongs to
    /// the garment, and the detector reads it off the model.
    /// </summary>
    public string? RetargetBodyDir { get; set; }

    /// <summary>
    /// Per body slot ("_top", "_dwn", …), the size last refitted onto in <see cref="RetargetBodyDir"/>, by the
    /// option's file relative to that mod — the same identity the panel pairs and de-duplicates by, and the only one
    /// that survives an author reusing a name (Neolithe has eight options called "SFW M").
    /// </summary>
    public Dictionary<string, string> RetargetTargets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The Glamourer design whose binding was active when the plugin last ran, so a reload can pick it back up
    /// (Glamourer signals nothing for an already-applied design). Null = nothing active. Restored only after
    /// <c>DesignBindingService.TryBootRestore</c> verifies it.
    /// </summary>
    public Guid? LastActiveDesignId { get; set; } = null;

    /// <summary>Optional explicit path to Glamourer's designs directory; null = derive from the config dir.</summary>
    public string? GlamourerDesignDirOverride { get; set; } = null;

    /// <summary>
    /// The user has dragged the status window to a size of their own. Until they do, it fits itself to each
    /// tab's controls when the tab changes; from then on it keeps <see cref="TogglesWindowWidth"/> ×
    /// <see cref="TogglesWindowHeight"/>.
    /// </summary>
    public bool WindowUserSized { get; set; } = false;

    /// <summary>
    /// Size the user dragged the status window to, used on every tab once <see cref="WindowUserSized"/> is set.
    /// UNSCALED: Dalamud's window host applies the UI scale, so a scaled value would compound on restore.
    /// </summary>
    public float TogglesWindowWidth { get; set; } = 900f;

    /// <inheritdoc cref="TogglesWindowWidth"/>
    public float TogglesWindowHeight { get; set; } = 700f;

    /// <summary>
    /// Directory the last <c>.pmp</c> export was saved to; null or missing falls back to the desktop.
    /// </summary>
    public string? LastExportDirectory { get; set; } = null;

    /// <summary>Per-mod sibling-synthesis mode, keyed by Penumbra mod directory. Absent = vanilla on; only
    /// <see cref="SiblingSynthesisMode.Off"/> is ever written now (see <see cref="OverlaysVanillaFor"/>).</summary>
    public Dictionary<string, SiblingSynthesisMode> SiblingSynthesis { get; set; } = new();

    /// <summary>
    /// Ceiling on decoded-texture memory, in MB (a 4K RGBA texture is 64 MB). Pays off only above the composite's whole
    /// working set; the default is sized to the machine (<see cref="DefaultDecodeCacheBudgetMb"/>).
    /// </summary>
    public int DecodeCacheBudgetMb { get; set; } = DefaultDecodeCacheBudgetMb();

    /// <summary>Hard bounds on the budget, shared by the settings slider and the migration.</summary>
    public const int MinDecodeCacheBudgetMb = 512;
    public const int MaxDecodeCacheBudgetMb = 32768;

    /// <summary>
    /// A budget sized to the machine: an eighth of physical RAM, clamped to [2 GB, 16 GB]; 2048 when RAM reads 0.
    /// </summary>
    public static int DefaultDecodeCacheBudgetMb()
    {
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (ram <= 0) return 2048;
        return (int)Math.Clamp(ram / 8 / (1024 * 1024), 2048, 16384);
    }

    /// <summary>LEGACY, read-only: mods the user switched AO off for under the old opt-out rule; still honoured by
    /// <see cref="AmbientOcclusionEnabledFor"/>. OrdinalIgnoreCase, which survives deserialization (populated in place).</summary>
    public HashSet<string> AmbientOcclusionDisabledMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Explicit per-mod AO/Skindenting choices made by the USER, keyed by mod directory. Absent means the
    /// pack's own declaration decides; present wins over the pack.</summary>
    public Dictionary<string, bool> AmbientOcclusionOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether AO / Skindenting applies to a mod: the user's choice, then the legacy opt-out, then the pack's
    /// declaration, else OFF (it damages flat artwork; see <see cref="ProteusMetadata.AmbientOcclusion"/>).
    /// </summary>
    public bool AmbientOcclusionEnabledFor(string modDir, bool? packDeclared)
        => AmbientOcclusionOverrides.TryGetValue(modDir, out var user) ? user
         : !AmbientOcclusionDisabledMods.Contains(modDir) && (packDeclared ?? false);

    /// <summary>
    /// Whether a mod's overlays are baked onto a vanilla (gen2) body the character is wearing: on unless switched off
    /// for this mod. Permission, not detection: a body is baked only when its material is loaded.
    /// </summary>
    public bool OverlaysVanillaFor(string modDir) =>
        !SiblingSynthesis.TryGetValue(modDir, out var m) || m != SiblingSynthesisMode.Off;

    /// <summary>Per-mod cache of whether it ships body material redirects, keyed by mod directory. Invalidated by
    /// Fingerprint (size + mtime of the mod's manifests); spares the compositor a resource-tree walk.</summary>
    public Dictionary<string, BodyModCacheEntry> KnownBodyMods { get; set; } = new();

    /// <summary>Last-known active player material paths, persisted so boot does not need a resource-tree walk.</summary>
    public List<string>? CachedActiveMaterialPaths { get; set; } = null;

    /// <summary>Game paths the last composite read as bases, persisted so the "does this mod feed our composite?"
    /// test works from the first mod-settings event of a session.</summary>
    public List<string>? CachedCompositeBaseKeys { get; set; } = null;

    /// <summary>Shape of the composite the set above describes (a hash of its material paths), persisted with it.</summary>
    public int CachedCompositeBaseSignature { get; set; }

    /// <summary>Game model paths the second skin last APPENDED into (the player's own necklace/ring). Persisted
    /// because the managed mod masks these paths from a session's first composite. Carrier hosts are deliberately
    /// absent: their model is replaced, never read.</summary>
    public List<string>? AppendHostModelPaths { get; set; } = null;

    /// <summary>User-chosen stacking order for overlays within one Penumbra multi-select group, keyed by
    /// <see cref="StackKey"/> → option names TOP-FIRST. Unlisted options fall after listed ones.</summary>
    public Dictionary<string, List<string>> OverlayStackOrder { get; set; } = new();

    /// <summary>Composite key for <see cref="OverlayStackOrder"/> (tuple keys don't round-trip through the config
    /// JSON). NUL-separated so neither part can collide.</summary>
    public static string StackKey(string modDir, string group) => modDir + "\u0000" + group;

    /// <summary>Position of <paramref name="option"/> in its group's user stack order (0 = top), or
    /// <see cref="int.MaxValue"/> when unset, so an all-unset group stays a tie.</summary>
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
    /// One flat top-first stack of <see cref="ModStackEntry"/> values per MOD, spanning every group; what the tab strip
    /// writes. <see cref="OverlayStackOrder"/> is still honoured as a lower-priority tiebreak.
    /// </summary>
    public Dictionary<string, List<string>> OverlayModStackOrder { get; set; } = new();

    /// <summary>Identifies one option inside a mod-wide stack. NUL-separated, same reasoning as StackKey.</summary>
    public static string ModStackEntry(string group, string option) => group + "\u0000" + option;

    /// <summary>Position of (group,option) in a top-first <see cref="ModStackEntry"/> list (0 = top), or
    /// <see cref="int.MaxValue"/> when not listed. Shared by the composite sort and the tab strip.</summary>
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
    /// Carry a config written by an older build forward. Runs once at load, and <see cref="Initialize"/> saves the result.
    /// </summary>
    internal void Migrate()
    {
        // v1 -> v2: block compression forced off for existing configs.
        if (Version < 2) EnableCompression = false;

        // v2 -> v3: machine-sized decode-cache budget, only where the value is still the old default.
        if (Version < 3 && DecodeCacheBudgetMb == 2048)
            DecodeCacheBudgetMb = DefaultDecodeCacheBudgetMb();

        // v3 -> v4: carry the inverted "Disable auto redraw" choice into AutoRedraw.
        if (Version < 4) AutoRedraw = !DisableAutoRedraw;

        // v5 -> v6: hat compatibility off for everyone (undoing v4 -> v5's unconditional on).
        if (Version < 6) AutoHatCompat = false;

        // v6 -> v7: redundant-mesh hiding on for everyone, ignoring the old connector-mesh enum.
        if (Version < 7) HideRedundantMeshes = true;

        // v7 -> v8: hat compatibility on for everyone, once; a later opt-out sticks.
        if (Version < 8) AutoHatCompat = true;

        Version = CurrentVersion;
    }

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
