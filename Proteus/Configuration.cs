using System;
using System.Collections.Generic;
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
    public int Version { get; set; } = 1;

    public bool PluginEnabled { get; set; } = true;

    public bool DisableAutoRedraw { get; set; } = false;

    /// <summary>
    /// Prefer Glamourer's in-place equipment reload (ReapplyState) over a full Penumbra redraw when
    /// refreshing composited textures. Avoids the despawn/respawn flicker. Falls back to a full
    /// redraw automatically when Glamourer is unavailable or has no state for the player.
    /// </summary>
    public bool UseInPlaceReload { get; set; } = true;

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
    /// Skip a body's redundant connector rings when building the second-skin shell. Some bodies
    /// (Neolithe) reinforce each joint (wrist/ankle/…) with a small extra submesh that overlaps an
    /// already-complete main body; on a semi-transparent gear shell that overlap doubles the alpha and
    /// shows as a more-opaque seam. The connector is the mesh's last submesh, so we drop that one only.
    /// Off by default — on most bodies the last submesh is real skin.
    /// </summary>
    public ConnectorMeshMode HideConnectorMeshes { get; set; } = ConnectorMeshMode.Off;

    /// <summary>When true, saving a Glamourer design auto-captures the current Proteus state bound to it.</summary>
    public bool DesignBindingEnabled { get; set; } = true;

    /// <summary>Optional explicit path to Glamourer's designs directory; null = derive from the config dir.</summary>
    public string? GlamourerDesignDirOverride { get; set; } = null;

    /// <summary>Per-mod sibling-synthesis mode, keyed by Penumbra mod directory.
    /// Absent = BiboGen3Only (default, = legacy behavior: gen3 bake, no vanilla).</summary>
    public Dictionary<string, SiblingSynthesisMode> SiblingSynthesis { get; set; } = new();

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

    public void Initialize(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
