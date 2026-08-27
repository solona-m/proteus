using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Proteus.Services;

namespace Proteus;

/// <summary>
/// Root of Proteus/metadata.json inside a Penumbra mod sidecar.
/// A mod may use either the simple Overlays list (applied unconditionally)
/// or OptionGroups (one group per Penumbra option group, applied based on user selection).
/// </summary>
public class ProteusMetadata
{
    [JsonPropertyName("FormatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Author")]
    public string Author { get; set; } = string.Empty;

    /// <summary>Unconditional overlays — used when the mod has no option groups.</summary>
    [JsonPropertyName("Overlays")]
    public List<OverlayDescriptor>? Overlays { get; set; }

    /// <summary>Option-gated overlays — used for multi-variant packs.</summary>
    [JsonPropertyName("OptionGroups")]
    public List<OverlayOptionGroup>? OptionGroups { get; set; }

    /// <summary>
    /// Per-row color table overrides (rows 1–16, matching FFXIV colorset numbering).
    /// Written by both mod authors and the Proteus UI. Drives diffuse tint and emissive.
    /// </summary>
    [JsonPropertyName("ColorTableRows")]
    public List<ColorTableRowPreset>? ColorTableRows { get; set; }

    /// <summary>
    /// The single colorset shared by ALL active masks. Active masks are composited together (coverage,
    /// relief, colour-row index) into one top layer, and this table colours it — each mask's <c>_id</c>
    /// indexes THIS table instead of merging into the overlays beneath. Null = legacy behaviour (mask
    /// <c>_id</c> merges into each overlay's own colorset). Written by the editor's single "Masks" tab.
    /// </summary>
    [JsonPropertyName("MaskColorTableRows")]
    public List<ColorTableRowPreset>? MaskColorTableRows { get; set; }

    /// <summary>
    /// The mask layer's render mode, when the mod is all-skin (no other gear) and the user has given the
    /// Masks tab its own Skin/Cloth/Glow mode. Carries Layer/Shader/Scroll/ManualShaderLock like any overlay
    /// descriptor. Null = Skin (the mask bakes into the body diffuse, the default). When the mod already has
    /// gear the mask is forced to a Cloth shell regardless of this.
    /// </summary>
    [JsonPropertyName("MaskDescriptor")]
    public OverlayDescriptor? MaskDescriptor { get; set; }

    /// <summary>
    /// Whether this pack wants the ambient-occlusion shadow and Skindenting normal indent applied to its
    /// coverage. Absent (null) means NO.
    /// <para/>
    /// Off by default because the effect reads a mod's coverage as a physical garment pressed into skin:
    /// right for straps and trim, wrong for a tattoo, a skin detail, or a makeup overlay, where it prints a
    /// shadow and a crease around flat artwork. A pack that wants it has to ask. The user can still override
    /// either way per mod in the Mods tab, and that override wins.
    /// </summary>
    [JsonPropertyName("AmbientOcclusion")]
    public bool? AmbientOcclusion { get; set; }

    /// <summary>
    /// Geometry this pack contributes unconditionally — used when it declares no
    /// <see cref="ContentGroups"/>. See <see cref="ContentPiece"/>.
    /// </summary>
    [JsonPropertyName("Content")]
    public List<ContentPiece>? Content { get; set; }

    /// <summary>
    /// Option-gated geometry, one entry per Penumbra option group. Written by the .pmp content importer;
    /// the selected options' pieces are appended into the carrier accessory each composite.
    /// </summary>
    [JsonPropertyName("ContentGroups")]
    public List<ContentOptionGroup>? ContentGroups { get; set; }

    /// <summary>
    /// The Penumbra group whose selection ALSO gates this pack's pieces — a multi-select group the importer
    /// synthesizes so individual pieces of a pack can be turned on and off.
    /// <para/>
    /// It exists because Penumbra has no way to say "apply only the model out of this always-on set". A pack
    /// that ships a whole outfit with no options of its own leaves nothing for Proteus to mirror, so the
    /// importer writes a group for it and every piece names the option that switches it on. Null when the
    /// pack's own options already select one model each — there is nothing to add.
    /// </summary>
    [JsonPropertyName("PieceGroupName")]
    public string? PieceGroupName { get; set; }

    /// <summary>
    /// Animated glow for a content piece that belongs to no option — the mod-wide fallback, exactly where
    /// <see cref="ColorTableRows"/> already stands in for an unconditional piece's colours.
    /// <para/>
    /// Named apart from the overlay settings above because it governs the pack's OWN material rather than
    /// anything Proteus composites. See <see cref="ContentOption.Glow"/>.
    /// </summary>
    [JsonPropertyName("ContentGlow")]
    public GearSettingsPreset? ContentGlow { get; set; }

    /// <summary>
    /// Per-MATERIAL colours and glow for an imported pack, keyed by the material's path relative to the mod
    /// root — the same key <c>SecondSkinService.ContentUnitKey</c> forms a published material around.
    /// <para/>
    /// The colour panel draws one tab per material, so this is where a tab's edits belong. The older
    /// per-option storage (<see cref="ContentOption.ColorTableRows"/>, <see cref="ContentGlow"/>) cannot
    /// express it: a pack holding nine accessories in one always-on model has ONE option and nine materials,
    /// so every tab read and wrote the same settings — set a glow on the ear rings and the shin laces were
    /// already glowing.
    /// <para/>
    /// Read in preference to the per-option values, which stay as the fallback so packs edited before this
    /// keep their colours. A field present here always wins, INCLUDING when it is empty — an empty row list
    /// means "cleared", not "unset", and must shadow the older value rather than let it come back.
    /// </summary>
    [JsonPropertyName("ContentMaterials")]
    public Dictionary<string, ContentMaterialSettings>? ContentMaterials { get; set; }

    /// <summary>The pack's own IMC show/hide toggles, which the composite applies by dropping geometry —
    /// see <see cref="ContentAttributeGroup"/>. Null for a pack that has none, which is most of them.</summary>
    [JsonPropertyName("ContentAttributes")]
    public List<ContentAttributeGroup>? ContentAttributes { get; set; }

    /// <summary>The extra skeletons this pack's pieces need, and which body part must ask for each — see
    /// <see cref="ContentSkeleton"/>. Null for a pack with no "ex" bones, which is nearly all of them: of
    /// 967 packs surveyed, nine declared an EST entry at all.</summary>
    [JsonPropertyName("ContentSkeletons")]
    public List<ContentSkeleton>? ContentSkeletons { get; set; }

    /// <summary>
    /// The settings stored for one material path, creating the entry if this is its first edit.
    /// <para/>
    /// Grows the dictionary by COPY-AND-SWAP rather than in place. The composite reads this map from a
    /// thread-pool thread while the panel writes to it from the UI thread, and a plain <c>Dictionary</c>
    /// being inserted into is not safe to read concurrently — a resize can hand the reader a wrong value or
    /// throw out of the middle of a composite. Swapping a finished dictionary in is the same publish
    /// contract the compositor's own maps use, and a reader that catches the older reference simply misses
    /// an edit that is about to trigger a recomposite anyway.
    /// </summary>
    public ContentMaterialSettings MaterialSettings(string materialRel)
    {
        if (ContentMaterials is { } cur && cur.TryGetValue(materialRel, out var have)) return have;

        var next = ContentMaterials == null
            ? new Dictionary<string, ContentMaterialSettings>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ContentMaterialSettings>(ContentMaterials, StringComparer.OrdinalIgnoreCase);
        var made = new ContentMaterialSettings();
        next[materialRel] = made;
        ContentMaterials = next;
        return made;
    }

    /// <summary>The settings stored for one material path, or null — reads must not create entries, since
    /// merely opening a panel must not change the mod.</summary>
    public ContentMaterialSettings? PeekMaterialSettings(string? materialRel)
        => materialRel != null && ContentMaterials is { } m && m.TryGetValue(materialRel, out var s) ? s : null;

    /// <summary>Whether this pack contributes any geometry at all (before selection is resolved).</summary>
    [JsonIgnore]
    public bool HasContent
        => Content is { Count: > 0 } || ContentGroups is { Count: > 0 };
}

/// <summary>Which surface an overlay renders on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlayLayer
{
    /// <summary>The character's own skin, composited into the body material. Always skin.shpk.</summary>
    Skin,

    /// <summary>
    /// A "second skin": the body's skin meshes duplicated, pushed out along their normals, and drawn as
    /// gear so they can run a full gear shader (color table, sphere maps, metalness, scrolling emissive)
    /// — none of which skin.shpk offers. Shells stack, gated by their normal map's blue channel.
    /// </summary>
    Gear,
}

/// <summary>Describes one set of overlay textures targeting one or more materials.</summary>
public class OverlayDescriptor
{
    /// <summary>Surface this overlay renders on. Defaults to the skin itself.</summary>
    [JsonPropertyName("Layer")]
    public OverlayLayer Layer { get; set; } = OverlayLayer.Skin;

    /// <summary>
    /// Shader package for a <see cref="OverlayLayer.Gear"/> overlay — e.g. "character.shpk" (default)
    /// or "characterscroll.shpk" (adds a time-animated scrolling emissive driven by an _o texture).
    /// Ignored on the skin layer, which is always skin.shpk. Prefer <see cref="ShaderPackage"/>.
    /// </summary>
    [JsonPropertyName("Shader")]
    public string? Shader { get; set; }

    /// <summary>Skin overlays have no shader choice — the body material is skin.shpk.</summary>
    public const string SkinShader = "skin.shpk";

    /// <summary>Gear shells default to plain character.shpk unless the option names another.</summary>
    public const string DefaultGearShader = "character.shpk";

    /// <summary>The shader this overlay actually renders with, after applying the layer's rules.</summary>
    [JsonIgnore]
    public string ShaderPackage
        => Layer == OverlayLayer.Skin ? SkinShader : (Shader ?? DefaultGearShader);

    /// <summary>
    /// When true, the editor stops auto-inferring <see cref="Layer"/>/<see cref="Shader"/> from the
    /// features in use — the user pinned the render mode by hand (Advanced). Off = the mode follows the
    /// features (a sphere map or metal ⇒ Cloth gear, a scroll effect ⇒ animated-glow gear).
    /// </summary>
    [JsonPropertyName("ManualShaderLock")]
    public bool ManualShaderLock { get; set; }

    /// <summary>
    /// Penumbra game path(s) of the .mtrl file(s). Accepts a single string or a JSON array.
    /// The same overlay textures are composited onto every listed material.
    /// </summary>
    [JsonPropertyName("MaterialGamePath")]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public List<string> MaterialGamePaths { get; set; } = [];

    /// <summary>Relative path (from Proteus/ sidecar root) to the diffuse overlay PNG. Optional.</summary>
    [JsonPropertyName("Diffuse")]
    public string? Diffuse { get; set; }

    /// <summary>Relative path (from Proteus/ sidecar root) to the normal overlay PNG. Optional.</summary>
    [JsonPropertyName("Normal")]
    public string? Normal { get; set; }

    /// <summary>Relative path (from Proteus/ sidecar root) to the mask overlay PNG. Optional.</summary>
    [JsonPropertyName("Mask")]
    public string? Mask { get; set; }

    /// <summary>
    /// Relative path (from Proteus/ sidecar root) to the index PNG (_id.png).
    /// Red channel selects color table row pair (value/17 → 0–15).
    /// Green channel blends sub-row A (255) and sub-row B (0).
    /// </summary>
    [JsonPropertyName("Index")]
    public string? Index { get; set; }

    /// <summary>
    /// Gear layer only. Relative path to the scrolling emissive map (vanilla calls this texture "_catc";
    /// mods often name it "_o"). Its color and intensity become the glow, animated by the shader from
    /// global time. Requires Shader = "characterscroll.shpk"; ignored otherwise.
    /// </summary>
    [JsonPropertyName("Scroll")]
    public string? Scroll { get; set; }

    /// <summary>
    /// Gear + characterscroll only. How fast the scroll map flows, per axis. These live in material
    /// constants, and vanilla ships them at ZERO — so without a speed the pattern sits still. ~0.01 is a
    /// typical rate; negative reverses the direction. Null = the default.
    /// </summary>
    [JsonPropertyName("ScrollSpeedX")]
    public float? ScrollSpeedX { get; set; }

    [JsonPropertyName("ScrollSpeedY")]
    public float? ScrollSpeedY { get; set; }

    /// <summary>
    /// Gear + characterscroll only. How many times the scroll map repeats across the surface, per axis.
    /// 1 = once. Null = the default.
    /// </summary>
    [JsonPropertyName("ScrollTilingX")]
    public float? ScrollTilingX { get; set; }

    [JsonPropertyName("ScrollTilingY")]
    public float? ScrollTilingY { get; set; }

    /// <summary>
    /// For a normal-only overlay (no Diffuse), whether to synthesize a diffuse tint from the
    /// normal's coverage and Row 16's color. Default true (legacy behaviour). Set false when the
    /// overlay should only touch the normal/mask and leave the skin diffuse untouched
    /// (e.g. a wetness normal+mask). Ignored when a Diffuse overlay is present.
    /// </summary>
    [JsonPropertyName("GenerateDiffuse")]
    public bool GenerateDiffuse { get; set; } = true;

    /// <summary>
    /// How strongly to mask the character's skin tone out of this overlay's opaque pixels (0–1).
    /// Omitted (null) = full masking — the default; a bright opaque overlay renders at its authored
    /// color on any skin tone. 0 = no masking: skin tone shows through fully (use for tattoos,
    /// decals, or anything meant to sit on the skin and take its color). Multiplies the user's global
    /// "Skin-tint suppression" setting, so an author's 0 always wins. Only affects diffuse overlays.
    /// </summary>
    [JsonPropertyName("SkinToneMask")]
    public float? SkinToneMask { get; set; }

    /// <summary>
    /// UV space the overlay PNGs were painted for: "bibo", "gen3", or "gen2".
    /// When set and different from the target material's body type (inferred from the
    /// material path suffix), Proteus remaps overlay pixels before compositing.
    /// Omit (null) when the overlay is already in the target body's UV space.
    /// </summary>
    [JsonPropertyName("SourceBodyType")]
    public string? SourceBodyType { get; set; }

    /// <summary>
    /// Transient (never serialized): this is the synthesized top gear shell for a mod's active masks,
    /// coloured by <see cref="ProteusMetadata.MaskColorTableRows"/>. Its coverage/_id/relief come from the
    /// mod's masks (not from Diffuse/Normal/Index), and SecondSkinService skips the ordinary mask merge for
    /// it (it IS the mask). Set by the compositor when a mod has a mask colorset AND builds gear shells.
    /// </summary>
    [JsonIgnore]
    public bool IsMaskShell { get; set; }
}

/// <summary>Maps one Penumbra option group to per-option overlay sets.</summary>
public class OverlayOptionGroup
{
    /// <summary>Must match the group name exactly as it appears in Penumbra.</summary>
    [JsonPropertyName("PenumbraGroupName")]
    public string PenumbraGroupName { get; set; } = string.Empty;

    [JsonPropertyName("Options")]
    public List<OverlayOption> Options { get; set; } = new();
}

public class OverlayOption
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Overlays")]
    public List<OverlayDescriptor> Overlays { get; set; } = new();

    /// <summary>
    /// Per-row color overrides for this option's overlays.
    /// Overrides the top-level ColorTableRows when present; falls back to top-level when null.
    /// </summary>
    [JsonPropertyName("ColorTableRows")]
    public List<ColorTableRowPreset>? ColorTableRows { get; set; }
}

// ── Content packs (imported .pmp mods that ship their own geometry) ──────────

/// <summary>
/// One model an imported pack contributes, with the materials its meshes are bound to.
/// <para/>
/// Unlike an overlay — which is art painted onto geometry Proteus copies from the character — a piece IS
/// geometry. Its meshes are copied verbatim into the carrier accessory, so the pack keeps its own vertices,
/// UVs and skinning, and two options that would collide on one game path in Penumbra can be worn together.
/// </summary>
public class ContentPiece
{
    /// <summary>The .mdl, as a path relative to the MOD ROOT (not the Proteus/ sidecar).</summary>
    [JsonPropertyName("Model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Model material name (leaf, no leading slash) → the .mtrl backing it, relative to the mod root.
    /// <para/>
    /// Binding is by NAME and never guessed: a mesh renders with the material its model declares, and a
    /// mesh whose material has no entry here is dropped with a warning rather than bound to something
    /// plausible. The importer builds this map by matching each declared leaf against the .mtrl files the
    /// pack ships anywhere — a pack commonly puts its material in one always-on group and its meshes in
    /// another.
    /// </summary>
    [JsonPropertyName("Materials")]
    public Dictionary<string, string> Materials { get; set; } = new();

    /// <summary>
    /// The .mtrl backing <paramref name="materialName"/>, or null when the model names a material this pack
    /// does not ship. Compared leaf-to-leaf and case-insensitively because the model stores names with a
    /// leading slash while a manifest lists them without one.
    /// <para/>
    /// A method rather than an OrdinalIgnoreCase dictionary: System.Text.Json constructs its own Dictionary
    /// for a settable property and assigns it, so a comparer given in the initializer is silently discarded
    /// on every load from disk — which is every load that matters.
    /// </summary>
    public string? MaterialFor(string materialName)
    {
        var leaf = materialName.TrimStart('/');
        foreach (var (k, v) in Materials)
            if (string.Equals(k.TrimStart('/'), leaf, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    /// <summary>
    /// Which surface the piece belongs to — the race space it is authored in, and therefore which hosts can
    /// carry it. Body (the default) rides a carrier in cut space and is deformed onto the wearer exactly as
    /// a second-skin shell is; a face/hair/tail piece is already the right shape and needs a host that will
    /// not deform it. See <see cref="ShellSurfaceKind"/>.
    /// </summary>
    [JsonPropertyName("Surface")]
    public ShellSurfaceKind Surface { get; set; } = ShellSurfaceKind.Body;

    /// <summary>The part id for a non-body surface ("f0001", "h0133"); empty for the body.</summary>
    [JsonPropertyName("SurfaceId")]
    public string SurfaceId { get; set; } = string.Empty;

    /// <summary>
    /// The option in <see cref="ProteusMetadata.PieceGroupName"/> that must ALSO be selected for this piece
    /// to be worn. Null = ungated, which is what a hand-authored sidecar gets.
    /// </summary>
    [JsonPropertyName("GateOption")]
    public string? GateOption { get; set; }

    /// <summary>Where the piece rides, as a label ("Head", "Body"). Display only; stored so a hand-edited
    /// sidecar reads without anyone having to decode a path.</summary>
    [JsonPropertyName("Slot")]
    public string? Slot { get; set; }

    /// <summary>
    /// Equipment model code ("0201", "1201") → the model authored for it, when a pack ships the same garment
    /// per race. Null means <see cref="Model"/> applies whoever is wearing it.
    /// <para/>
    /// A map rather than one piece per race, because these are variants of ONE thing: the user picks the
    /// garment, and which file backs it is a question only the wearer's race can answer. Cerise ships five
    /// models of one shirt; as separate pieces that would be five entries in the picker, four of them
    /// wrong for any given character.
    /// </summary>
    [JsonPropertyName("Models")]
    public Dictionary<string, string>? Models { get; set; }

    /// <summary>
    /// The model authored for <paramref name="modelCode"/>, or null when the pack ships none. An EXACT
    /// lookup only: walking the race fall-through chain when there is no exact match belongs to
    /// <c>SecondSkinService</c>, which owns that chain. Falls back to <see cref="Model"/> for a pack that
    /// ships one model for everyone.
    /// </summary>
    public string? ModelFor(string? modelCode)
    {
        if (Models is not { Count: > 0 })
            return string.IsNullOrWhiteSpace(Model) ? null : Model;
        if (modelCode == null) return null;
        foreach (var (k, v) in Models)
            if (string.Equals(k, modelCode, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    /// <summary>The model codes this piece has a variant for, for a message that has to say what it does ship.</summary>
    [JsonIgnore]
    public IEnumerable<string> ModelCodes
        => Models is { Count: > 0 } ? Models.Keys : [];

    /// <summary>
    /// Which of the PACK'S own options reveal each of this piece's materials, when the pack switches its
    /// pieces on and off by model attribute rather than by shipping separate models.
    /// <para/>
    /// Recorded at import by walking submesh → attribute mask → attribute name → the option that turns that
    /// attribute on. Without it the colour panel has no way to tell a material the user is actually wearing
    /// from one belonging to a piece they left unticked, and shows a tab for all nine whatever is selected.
    /// <para/>
    /// A material with no gate here is drawn unconditionally. Null for a pack that does not work this way,
    /// which is most of them.
    /// </summary>
    [JsonPropertyName("MaterialGates")]
    public List<ContentMaterialGate>? MaterialGates { get; set; }

    /// <summary>Every option that reveals <paramref name="materialLeaf"/>, or an empty list when nothing
    /// gates it — compared leaf-to-leaf like <see cref="MaterialFor"/>, for the same reason.</summary>
    public IReadOnlyList<ContentMaterialGate> GatesFor(string materialLeaf)
    {
        if (MaterialGates is not { Count: > 0 }) return [];
        var leaf = materialLeaf.TrimStart('/');
        return [.. MaterialGates.Where(g =>
            string.Equals(g.Material.TrimStart('/'), leaf, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>The surface this piece is cut for, as the host chooser understands it.</summary>
    [JsonIgnore]
    public ShellSurfaceKey SurfaceKey
        => new(Surface, Surface == ShellSurfaceKind.Body ? string.Empty : SurfaceId);
}

/// <summary>
/// An extra skeleton one of a pack's pieces needs, and the body part that has to ask for it.
/// <para/>
/// A garment with "ex" bones does not carry them: <c>j_ex_*</c> live in an extra skeleton the game loads
/// only when the EST table points at it, keyed by race, gender, slot and SET. The Cerise kimono jacket
/// rides the <c>met</c> slot but its bones are top-space, so what loads them is the entry for the wearer's
/// CHEST piece — "t6085 on the chest piece", in the words of the report.
/// <para/>
/// Recorded because Proteus breaks the pack's own arrangement twice over: it moves the geometry onto a host
/// accessory, so the pack's entry names a set nobody is wearing, and accessories have no EST of their own.
/// The composite re-points the entry at whatever body part the character actually has on — see
/// <c>SecondSkinService.EstManipulation</c>.
/// </summary>
public class ContentSkeleton
{
    /// <summary>The Penumbra group and option that must be selected for this to apply, or null for a piece
    /// the pack applies unconditionally.</summary>
    [JsonPropertyName("Group")]
    public string? Group { get; set; }

    [JsonPropertyName("Option")]
    public string? Option { get; set; }

    /// <summary>The EST slot — "Body", "Head", "Hair" or "Face". Names the body part whose entry has to be
    /// written, NOT the slot the pack's own model rides.</summary>
    [JsonPropertyName("Slot")]
    public string Slot { get; set; } = string.Empty;

    /// <summary>The extra skeleton id. Never 0 here: an entry of 0 means "no extra skeleton", which enables
    /// nothing and is dropped at import rather than stored.</summary>
    [JsonPropertyName("Entry")]
    public int Entry { get; set; }
}

/// <summary>
/// A pack's own show/hide toggles for one of its items, as an IMC attribute mask.
/// <para/>
/// This is how a mod offers "Hide panty strap" without shipping a second model: the item's IMC entry
/// carries ten attribute bits, one per entry in the model's own attribute name table BY POSITION, and the
/// game culls a submesh whose attributes are all switched off. Penumbra exposes it as a group whose options
/// each clear some of those bits.
/// <para/>
/// It cannot work through Proteus unaided, and that is why this is recorded. The mask belongs to the
/// pack's own equipment set; Proteus moves the geometry onto a host accessory, so at draw time the game
/// reads the HOST's mask and the pack's edit governs an item nobody is wearing. The composite therefore
/// applies the toggle itself, by dropping the submeshes the mask switches off — see
/// <c>SecondSkinService.HiddenAttributes</c>.
/// </summary>
public class ContentAttributeGroup
{
    /// <summary>The Penumbra group whose selection drives this — read live, like every other gate.</summary>
    [JsonPropertyName("Group")]
    public string Group { get; set; } = string.Empty;

    /// <summary>The equipment set the mask belongs to, so a pack with several items applies each toggle to
    /// the right models. -1 matches every piece of the mod.</summary>
    [JsonPropertyName("SetId")]
    public int SetId { get; set; } = -1;

    /// <summary>The bits set when none of the options are selected.</summary>
    [JsonPropertyName("DefaultMask")]
    public int DefaultMask { get; set; }

    /// <summary>Option name → the bits it CLEARS while selected.</summary>
    [JsonPropertyName("Options")]
    public Dictionary<string, int> Options { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The mask left after <paramref name="selected"/> have had their bits cleared.
    /// <para/>
    /// Selected options SUBTRACT, which is what makes them read as "hide". Denim Shorts is the evidence:
    /// its default is 3 — both bits on — and its two options carry 1 and 2 under the names "Panty Strap
    /// Hide" and "Pockets Hide". Inverting this would hide exactly the half the user asked to keep, so it
    /// lives here, named, rather than inline at the call site.
    /// </summary>
    public int MaskFor(IEnumerable<string>? selected)
    {
        int mask = DefaultMask;
        foreach (var name in selected ?? [])
            if (Options.TryGetValue(name, out var off)) mask &= ~off;
        return mask & 0x3FF;
    }
}

/// <summary>
/// What the colour panel stores for ONE of an imported pack's materials — which is what one of its tabs
/// governs. See <see cref="ProteusMetadata.ContentMaterials"/>.
/// </summary>
public class ContentMaterialSettings
{
    /// <summary>Colour table overrides stamped into this material. Null keeps the author's own table.</summary>
    [JsonPropertyName("ColorTableRows")]
    public List<ColorTableRowPreset>? ColorTableRows { get; set; }

    /// <summary>
    /// Animated glow for this material. A preset naming no effect means the glow was CLEARED here, which is
    /// not the same as never having been set: it publishes the material as its author wrote it, and it
    /// shadows the older per-option glow instead of letting it come back.
    /// <para/>
    /// Null only for an entry created by a colour edit that never touched the glow at all — that one does
    /// fall through to the per-option value, because nothing here has an opinion about it yet.
    /// </summary>
    [JsonPropertyName("Glow")]
    public GearSettingsPreset? Glow { get; set; }
}

/// <summary>
/// One of the pack's own options that reveals one of its materials — the link between a checkbox in the
/// mod's own settings and the geometry it shows.
/// <para/>
/// Stored rather than recomputed because working it out means reading the model: submesh attribute masks,
/// the model's attribute name table, and the option whose <c>Atr</c> manipulation turns that name on. The
/// panel draws every frame and cannot open a .mdl to do it.
/// </summary>
public class ContentMaterialGate
{
    /// <summary>The model's own material name, leading slash and all.</summary>
    [JsonPropertyName("Material")]
    public string Material { get; set; } = string.Empty;

    [JsonPropertyName("Group")]
    public string Group { get; set; } = string.Empty;

    [JsonPropertyName("Option")]
    public string Option { get; set; } = string.Empty;
}

/// <summary>One Penumbra option's geometry contribution.</summary>
public class ContentOption
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Pieces")]
    public List<ContentPiece> Pieces { get; set; } = new();

    /// <summary>
    /// Colour table overrides applied to EVERY material this option's pieces bind. Null keeps the pack's
    /// own authored colorset. Two options binding the same .mtrl with different rows each get their own
    /// published material (and so each cost a material slot on the host); identical rows are deduped.
    /// </summary>
    [JsonPropertyName("ColorTableRows")]
    public List<ColorTableRowPreset>? ColorTableRows { get; set; }

    /// <summary>
    /// Animated glow for the materials this option's pieces bind. Null — the usual case — publishes the
    /// pack's own material as the author wrote it.
    /// <para/>
    /// Only <see cref="GearSettingsPreset.Scroll"/> and the four scroll numbers are read; Layer, Shader and
    /// ManualShaderLock belong to overlay descriptors and mean nothing here. The type is shared anyway so a
    /// design binding's gear override — which is already this shape — can drive a content glow without a
    /// second parallel record.
    /// <para/>
    /// Setting it does not touch the pack: the composite rebuilds the material onto characterscroll from a
    /// vanilla template every run, so clearing this republishes the author's original bytes.
    /// </summary>
    [JsonPropertyName("Glow")]
    public GearSettingsPreset? Glow { get; set; }
}

/// <summary>
/// How content pieces are named back to the user — the rule behind the caption over a shared colour grid.
/// <para/>
/// Kept out of the drawing code, and out of <c>Gui/</c>, for the same reason <see cref="RenderModeInference"/>
/// is: it is a decision, not a rendering detail, and the decision is worth a test. It has been got wrong
/// once already — captioning by the OPTION reads "always on" for every piece of an imported pack, because
/// those belong to no option of the pack's own and are gated through the synthesized piece group instead.
/// </summary>
public static class ContentLabels
{
    /// <summary>
    /// What to call each piece of one shared material: the switch the user actually ticked to turn it on.
    /// That is the piece's own gate where it has one, the owning option where it does not, and
    /// <paramref name="unconditional"/> only for a piece that is genuinely always applied — a hand-authored
    /// sidecar with no gate and no option.
    /// <para/>
    /// Distinct and in encounter order, so one option contributing several pieces under one gate is named
    /// once and the caption reads in the order the panel lists them.
    /// </summary>
    public static List<string> For(
        IEnumerable<(string? Option, IReadOnlyList<ContentPiece> Pieces)> owners, string unconditional)
    {
        var seen = new List<string>();
        foreach (var (option, pieces) in owners)
            foreach (var piece in pieces)
            {
                var label = piece.GateOption ?? option ?? unconditional;
                if (!seen.Contains(label, StringComparer.Ordinal)) seen.Add(label);
            }
        return seen;
    }
}

/// <summary>
/// Reads an <c>_id</c> (index) texture for the one thing the colour editor needs from it: which colour-table
/// cell the material actually samples.
/// <para/>
/// Pure, and out of the drawing code, because the two conventions it encodes are exactly the ones that made
/// a content pack look broken — a user colouring row 16 while the pack's index pointed at row 1 saw nothing
/// change and no glow, with no way to tell which of the two was at fault.
/// </summary>
public static class ContentIndexTexture
{
    /// <summary>
    /// Rows (1-based) the texture selects, and the sub-row column when every sampled texel agrees on one.
    /// <para/>
    /// Red picks the row pair — 16 pairs spread over 0–255, so <c>red / 17</c> — and green blends sub-row A
    /// at 255 against B at 0. Fully transparent texels select nothing and are skipped. A null
    /// <paramref name="SubRow"/> means the texture genuinely uses both columns (a gradient), which is not
    /// something to narrow away. An empty <paramref name="Rows"/> means the texture was read and selects
    /// nothing, which is NOT the same as failing to read it.
    /// </summary>
    public readonly record struct Scan(HashSet<int> Rows, string? SubRow);

    /// <summary>
    /// The row pair a red value names, 1–16.
    /// <para/>
    /// ROUNDED, not truncated. The encoding puts pair <c>n</c> at <c>n * 17</c>, so plain division is exact
    /// only on the multiples — 254 truncates to pair 14 when the author plainly meant 15, and anything that
    /// has been through a lossy step lands a row off. That matters more than it reads: the editor DISABLES
    /// the rows this doesn't name, so a one-off decode doesn't merely mislabel a row, it puts the working
    /// one out of reach.
    /// </summary>
    public static int RowOf(byte red) => Math.Clamp((red + 8) / 17 + 1, 1, 16);

    public static Scan Read(byte[] rgba)
    {
        var rows = new HashSet<int>();
        bool anyA = false, anyB = false;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] == 0) continue;
            rows.Add(RowOf(rgba[i]));
            if (rgba[i + 1] > 127) anyA = true; else anyB = true;
        }
        return new Scan(rows, anyA == anyB ? null : anyA ? "A" : "B");
    }
}

/// <summary>
/// Which colour-table cell an animated glow has to be set on, and whether it is.
/// <para/>
/// Pure, and out of the drawing code, because this exact decision has now been got wrong twice in the same
/// way. A material renders from ONE cell — the row its index texture selects, in the column that index's
/// green channel picks — and a value on any other row is invisible. First a user coloured row 16 while the
/// pack's index pointed at row 1 and saw nothing; then the glow was armed on row 16 for the same reason, and
/// a piercing with a correct shader, scroll map, second UV set and decal key rendered as plain metal.
/// </summary>
public static class ContentGlowRow
{
    /// <summary>
    /// The cell the material actually reads. <paramref name="rows"/> is the index scan's row set and
    /// <paramref name="subRow"/> its column, both null when the index could not be read — then this falls
    /// back to the row the grid is showing, which is the only guess available. Column A is the fallback
    /// because an index's green channel defaults high.
    /// </summary>
    public static (int Row, bool SubRowA) Sampled(IReadOnlyCollection<int>? rows, string? subRow, int selectedRow)
        => (rows is { Count: 1 } ? rows.First() : selectedRow,
            !string.Equals(subRow, "B", StringComparison.Ordinal));

    /// <summary>
    /// Whether that cell's Glow is above zero — the only value the shader will ever read.
    /// <para/>
    /// The intensity alone is the question: an unset glow colour means WHITE, in the editor's swatch and in
    /// the material writer alike.
    /// </summary>
    public static bool Emits(IEnumerable<ColorTableRowPreset> rows, int row, bool subRowA)
    {
        var preset = rows.FirstOrDefault(r => r.Row == row);
        return (subRowA ? preset?.SubRowA : preset?.SubRowB) is { Emissive: > 0f };
    }

    /// <summary>
    /// The Glow a newly switched-on effect starts at.
    /// <para/>
    /// Deliberately not full. On a scrolling material this dial is what the effect's brightness scales with,
    /// and a scroll map is usually a saturated colour — pushed to 1.0 it blows out and the piece reads as a
    /// white blob rather than as the pattern. A quarter shows the map's own colours clearly and leaves the
    /// dial room in both directions.
    /// </summary>
    public const float DefaultGlow = 0.25f;

    /// <summary>The glow colour a switched-on effect seeds. Explicit rather than left null: the writer
    /// resolves a glow's colour <c>EmissiveColor → Diffuse</c> and a row with neither stays dark, however
    /// high its intensity.</summary>
    public const string DefaultGlowColour = "#FFFFFF";

    /// <summary>
    /// Turn the cell on at <see cref="DefaultGlow"/> if it is off, adding the row when the list has none.
    /// Returns true when it wrote something.
    /// <para/>
    /// The diffuse is left alone, so a piece keeps whatever surface its author gave it — for the piercings
    /// pack, its silver.
    /// </summary>
    public static bool Arm(List<ColorTableRowPreset> rows, int row, bool subRowA)
    {
        if (Emits(rows, row, subRowA)) return false;

        var preset = rows.FirstOrDefault(r => r.Row == row);
        if (preset == null) rows.Add(preset = new ColorTableRowPreset { Row = row });

        var cell = subRowA
            ? preset.SubRowA ??= new ColorTableSubRowPreset()
            : preset.SubRowB ??= new ColorTableSubRowPreset();
        cell.Emissive = DefaultGlow;
        cell.EmissiveColor ??= DefaultGlowColour;
        return true;
    }

    /// <summary>
    /// Take back an <see cref="Arm"/> when the effect is switched off, leaving no trace. Returns true when
    /// it changed something.
    /// <para/>
    /// Necessary because switching the effect off only changes the SHADER. The Glow that arming wrote stays
    /// in the rows, and on the pack's own <c>character.shpk</c> that is an ordinary emissive — so a piece
    /// whose animated glow had been turned off went on glowing, plainly. Turning a feature off has to undo
    /// what turning it on did, or the promise that clearing an effect republishes the author's material
    /// exactly is not kept.
    /// <para/>
    /// Only an UNTOUCHED seed is taken back: a value still sitting at <see cref="DefaultGlow"/> is one
    /// nobody has moved, while any other number is the user's and stays. A cell left with nothing in it at
    /// all is dropped rather than written as an explicit zero, because the row writer writes every emissive
    /// it is given and a zero would overwrite whatever the author had there.
    /// </summary>
    public static bool Disarm(List<ColorTableRowPreset> rows, int row, bool subRowA)
    {
        var preset = rows.FirstOrDefault(r => r.Row == row);
        var cell = subRowA ? preset?.SubRowA : preset?.SubRowB;
        if (preset == null || cell == null || cell.Emissive != DefaultGlow) return false;

        cell.Emissive = 0f;
        // The colour arming seeded goes with it — left behind it is a field nobody chose, and it would stop
        // the cell reading as blank so the row could never be dropped.
        if (string.Equals(cell.EmissiveColor, DefaultGlowColour, StringComparison.OrdinalIgnoreCase))
            cell.EmissiveColor = null;
        if (IsBlank(cell))
        {
            if (subRowA) preset.SubRowA = null; else preset.SubRowB = null;
            if (preset.SubRowA == null && preset.SubRowB == null) rows.Remove(preset);
        }
        return true;
    }

    /// <summary>Whether a sub-row now says nothing at all, so it can be dropped instead of persisted.</summary>
    private static bool IsBlank(ColorTableSubRowPreset s)
        => s.Diffuse == null && s.EmissiveColor == null && s.Specular == null
        && s.Emissive == 0f && s.Opacity == 0
        && s.SphereMap == null && s.SphereIntensity == null
        && s.Roughness == null && s.Metalness == null;
}

/// <summary>Maps one Penumbra option group to per-option geometry.</summary>
public class ContentOptionGroup
{
    /// <summary>Must match the group name exactly as it appears in Penumbra.</summary>
    [JsonPropertyName("PenumbraGroupName")]
    public string PenumbraGroupName { get; set; } = string.Empty;

    [JsonPropertyName("Options")]
    public List<ContentOption> Options { get; set; } = new();
}

/// <summary>
/// Resolved texture game paths extracted from a parsed .mtrl file.
/// <para/>
/// <paramref name="Index"/> is the material's own colour-table row selector (the <c>_id</c> sampler).
/// Gear and accessories carry one almost universally; body and face skin materials NEVER do — a Proteus
/// index on skin is Proteus's own concept, not something the material declares. Defaulted so the older
/// three-argument construction sites keep working.
/// </summary>
/// <param name="Parsed">
/// Whether the walk actually reached the sampler array. The parser is fail-open by design — a truncated
/// file, a table running past the end, a shader block it cannot step to, all return "nothing found" rather
/// than throwing — so a null <paramref name="Index"/> alone cannot tell "this material declares no index
/// sampler" from "Proteus could not read this material". Callers that act on the ABSENCE of a texture must
/// check this first; callers that only use the paths they got can ignore it.
/// </param>
/// <param name="HasColorTable">
/// Whether the material carries a Dawntrail 32×64 colour table at all. A material without one has no rows
/// to select, so nothing may claim which row it samples — and any colour edit aimed at it is discarded by
/// <c>GearMaterialWriter.PatchColorTable</c>, which no-ops on exactly this shape.
/// </param>
public record MtrlTexturePaths(
    string? Diffuse,
    string? Normal,
    string? Mask,
    string? Index = null,
    bool Parsed = false,
    bool HasColorTable = false
);

// ── Color table types ────────────────────────────────────────────────────────

/// <summary>
/// Serialised form of a single color table row override stored in metadata.json.
/// Row is 1-based (1–16) matching what FFXIV modders know.
/// </summary>
public class ColorTableRowPreset
{
    [JsonPropertyName("Row")]
    public int Row { get; set; }

    [JsonPropertyName("SubRowA")]
    public ColorTableSubRowPreset? SubRowA { get; set; }

    [JsonPropertyName("SubRowB")]
    public ColorTableSubRowPreset? SubRowB { get; set; }

    /// <summary>Deep copy. Used by the editor to work on rows without writing through to the metadata or to
    /// a design binding until it decides where the edit belongs.</summary>
    public ColorTableRowPreset Clone() => new() { Row = Row, SubRowA = SubRowA?.Clone(), SubRowB = SubRowB?.Clone() };
}

public class ColorTableSubRowPreset
{
    /// <summary>Hex color string, e.g. "#FF0000" or "#F00". White if null.</summary>
    [JsonPropertyName("Diffuse")]
    public string? Diffuse { get; set; }

    /// <summary>Emissive intensity 0–1. Zero means no glow.</summary>
    [JsonPropertyName("Emissive")]
    public float Emissive { get; set; } = 0f;

    /// <summary>
    /// Gear layer only. Glow colour, independent of <see cref="Diffuse"/>. Defaults to the diffuse
    /// colour when omitted.
    ///
    /// These have to be separate: a scrolling-emissive material typically wants a nearly BLACK diffuse
    /// (so the glow reads against it) with a WHITE emissive — e.g. Luci's shirt is diffuse (0.08,0,0),
    /// emissive (1,1,1). Deriving the glow from the diffuse cannot express that.
    /// </summary>
    [JsonPropertyName("EmissiveColor")]
    public string? EmissiveColor { get; set; }

    /// <summary>
    /// Opacity adjustment −100…100. Negative fades the overlay toward transparent;
    /// positive pushes semi-transparent pixels toward fully opaque. Zero = no change.
    /// </summary>
    [JsonPropertyName("Opacity")]
    public int Opacity { get; set; } = 0;

    /// <summary>
    /// Gear layer only. Sphere map to reflect on this row — a slice of the game's shared
    /// chara/common/texture/sphere_d_array.tex. Needs no texture of our own.
    /// Has no effect unless <see cref="SphereIntensity"/> is also non-zero, and does not work under
    /// characterscroll.shpk (use the default character.shpk).
    /// </summary>
    [JsonPropertyName("SphereMap")]
    public int? SphereMap { get; set; }

    /// <summary>Gear layer only. How strongly the sphere map blends in (0–1).</summary>
    [JsonPropertyName("SphereIntensity")]
    public float? SphereIntensity { get; set; }

    /// <summary>Gear layer only. Specular colour. Null keeps the template's value.</summary>
    [JsonPropertyName("Specular")]
    public string? Specular { get; set; }

    /// <summary>Gear layer only. Surface roughness (0–1). Null keeps the shader default.</summary>
    [JsonPropertyName("Roughness")]
    public float? Roughness { get; set; }

    /// <summary>Gear layer only. Metalness (0–1). Null keeps the shader default.</summary>
    [JsonPropertyName("Metalness")]
    public float? Metalness { get; set; }

    /// <summary>Copy. Every member is a value type or an immutable string, so the shallow copy IS a deep
    /// one — and MemberwiseClone keeps that true automatically when a property is added later, which a
    /// hand-written field list would not.</summary>
    public ColorTableSubRowPreset Clone() => (ColorTableSubRowPreset)MemberwiseClone();
}

/// <summary>Runtime (0-based) representation of a single color table sub-row.</summary>
public class ColorTableSubRow
{
    public float DiffuseR { get; set; } = 1f;
    public float DiffuseG { get; set; } = 1f;
    public float DiffuseB { get; set; } = 1f;
    public float Emissive { get; set; } = 0f;
    public int   Opacity  { get; set; } = 0;
}

/// <summary>Runtime pair of sub-rows A and B for one color table row pair.</summary>
public class ColorTableRowOverride
{
    public ColorTableSubRow A { get; set; } = new();
    public ColorTableSubRow B { get; set; } = new();
}

/// <summary>
/// Non-persistent per-mod color override pushed by the design-binding system into the compositor.
/// Mirrors the metadata color structure (a top-level row list plus per-group/per-option lists), but
/// is applied only at composite time — metadata.json is never modified. Stored in design_bindings.json.
/// </summary>
public class OverlayColorOverride
{
    [JsonPropertyName("Top")]
    public List<ColorTableRowPreset>? Top { get; set; }

    /// <summary>group → option → rows.</summary>
    [JsonPropertyName("Options")]
    public Dictionary<string, Dictionary<string, List<ColorTableRowPreset>>>? Options { get; set; }

    /// <summary>
    /// The mod's shared "Masks" colorset (<see cref="ProteusMetadata.MaskColorTableRows"/>). Captured
    /// separately because the synthesized Masks tab isn't a real option group — it has no entry in
    /// <see cref="Options"/>. Null ⇒ fall back to the live metadata mask colours at composite time.
    /// </summary>
    [JsonPropertyName("Mask")]
    public List<ColorTableRowPreset>? Mask { get; set; }

    /// <summary>
    /// Resolve the rows for an overlay: the matching option's rows if present, else the top-level rows.
    /// Returns null when nothing is stored, so callers can fall back to the live metadata colors.
    /// </summary>
    public List<ColorTableRowPreset>? Resolve(string? group, string? option)
    {
        if (group != null && option != null && Options != null
            && Options.TryGetValue(group, out var opts) && opts.TryGetValue(option, out var rows))
            return rows;
        return Top;
    }
}

/// <summary>
/// The gear-layer settings of one overlay option: which layer and shader it renders with, and its
/// scrolling-effect setup. These live on <see cref="OverlayDescriptor"/> rather than the colour rows,
/// so a design binding has to capture them separately from the colours.
/// </summary>
public class GearSettingsPreset
{
    [JsonPropertyName("Layer")]
    public OverlayLayer? Layer { get; set; }

    [JsonPropertyName("Shader")]
    public string? Shader { get; set; }

    [JsonPropertyName("Scroll")]
    public string? Scroll { get; set; }

    [JsonPropertyName("ScrollSpeedX")]
    public float? ScrollSpeedX { get; set; }

    [JsonPropertyName("ScrollSpeedY")]
    public float? ScrollSpeedY { get; set; }

    [JsonPropertyName("ScrollTilingX")]
    public float? ScrollTilingX { get; set; }

    [JsonPropertyName("ScrollTilingY")]
    public float? ScrollTilingY { get; set; }

    [JsonPropertyName("ManualShaderLock")]
    public bool? ManualShaderLock { get; set; }

    /// <summary>Snapshot an overlay's gear settings.</summary>
    public static GearSettingsPreset From(OverlayDescriptor d) => new()
    {
        Layer = d.Layer,
        Shader = d.Shader,
        Scroll = d.Scroll,
        ScrollSpeedX = d.ScrollSpeedX,
        ScrollSpeedY = d.ScrollSpeedY,
        ScrollTilingX = d.ScrollTilingX,
        ScrollTilingY = d.ScrollTilingY,
        ManualShaderLock = d.ManualShaderLock,
    };

    /// <summary>An independent copy. A design-binding edit works on a copy of what the sidecar holds, so
    /// merely opening a panel can never move the mod's own settings.</summary>
    public GearSettingsPreset Clone() => new()
    {
        Layer = Layer,
        Shader = Shader,
        Scroll = Scroll,
        ScrollSpeedX = ScrollSpeedX,
        ScrollSpeedY = ScrollSpeedY,
        ScrollTilingX = ScrollTilingX,
        ScrollTilingY = ScrollTilingY,
        ManualShaderLock = ManualShaderLock,
    };

    /// <summary>
    /// The scroll settings as one comparable string, or null when there is no glow at all.
    /// <para/>
    /// This is identity, not display: it goes into a content material's merge key, so two options sharing a
    /// <c>.mtrl</c> publish one material only while they agree on the effect AND its numbers. Letting them
    /// merge on the effect alone would silently give one option the other's speed.
    /// <para/>
    /// Layer, Shader and ManualShaderLock are excluded deliberately — they describe an overlay descriptor,
    /// not a content material, and a stray value in one must not split a slot.
    /// </summary>
    public string? GlowKey()
        => string.IsNullOrEmpty(Scroll)
            ? null
            : string.Join(' ', Scroll,
                ScrollSpeedX?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
                ScrollSpeedY?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
                ScrollTilingX?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
                ScrollTilingY?.ToString("R", CultureInfo.InvariantCulture) ?? "-");

    /// <summary>The scroll settings the material writer wants, with the shared defaults filled in for
    /// anything the user never touched. Null when this preset names no effect.</summary>
    public ScrollSettings? ToScrollSettings()
        => string.IsNullOrEmpty(Scroll)
            ? null
            : new ScrollSettings(
                ScrollSpeedX ?? ScrollSettings.Default.SpeedX,
                ScrollSpeedY ?? ScrollSettings.Default.SpeedY,
                ScrollTilingX ?? ScrollSettings.Default.TilingX,
                ScrollTilingY ?? ScrollSettings.Default.TilingY);

    /// <summary>
    /// Copy just the scroll settings out of <paramref name="from"/>, leaving Layer, Shader and
    /// ManualShaderLock alone.
    /// <para/>
    /// For a content glow written into a design binding's gear override: that override may already be
    /// carrying an overlay's layer and shader for the same mod, and a content material has no business
    /// touching either.
    /// </summary>
    public void ApplyScrollFrom(GearSettingsPreset from)
    {
        Scroll = from.Scroll;
        ScrollSpeedX = from.ScrollSpeedX;
        ScrollSpeedY = from.ScrollSpeedY;
        ScrollTilingX = from.ScrollTilingX;
        ScrollTilingY = from.ScrollTilingY;
    }

    /// <summary>Apply onto a descriptor (used on a clone, so metadata.json is never mutated).</summary>
    public void ApplyTo(OverlayDescriptor d)
    {
        if (Layer is { } l) d.Layer = l;
        d.Shader = Shader;
        d.Scroll = Scroll;
        d.ScrollSpeedX = ScrollSpeedX;
        d.ScrollSpeedY = ScrollSpeedY;
        d.ScrollTilingX = ScrollTilingX;
        d.ScrollTilingY = ScrollTilingY;
        d.ManualShaderLock = ManualShaderLock ?? false;
    }
}

/// <summary>
/// Per-mod gear-settings override pushed by the design-binding system into the compositor — the same
/// shape as <see cref="OverlayColorOverride"/>, and applied the same way: only at composite time, onto a
/// copy of the descriptor. metadata.json is never modified.
/// </summary>
public class OverlayGearOverride
{
    [JsonPropertyName("Top")]
    public GearSettingsPreset? Top { get; set; }

    /// <summary>group → option → settings.</summary>
    [JsonPropertyName("Options")]
    public Dictionary<string, Dictionary<string, GearSettingsPreset>>? Options { get; set; }

    /// <summary>
    /// The mod's shared "Masks" tab gear settings (<see cref="ProteusMetadata.MaskDescriptor"/>). Captured
    /// separately because the synthesized Masks tab isn't a real option group — it has no entry in
    /// <see cref="Options"/>. Null ⇒ fall back to the live metadata mask descriptor at composite time.
    /// Mirrors <see cref="OverlayColorOverride.Mask"/>.
    /// </summary>
    [JsonPropertyName("Mask")]
    public GearSettingsPreset? Mask { get; set; }

    /// <summary>
    /// The animated glow of an imported content pack's pieces that belong to no option
    /// (<see cref="ProteusMetadata.ContentGlow"/>). Its own slot for the same reason
    /// <see cref="Mask"/> has one — there is no entry in <see cref="Options"/> to hold it — and for one
    /// more that matters here: <see cref="Top"/> is captured from the mod's first OVERLAY descriptor, so
    /// resolving content against it would hand a pack's meshes whatever scroll effect an overlay of the
    /// same mod happens to be using. See <see cref="ResolveContent"/>.
    /// </summary>
    [JsonPropertyName("Content")]
    public GearSettingsPreset? Content { get; set; }

    public GearSettingsPreset? Resolve(string? group, string? option)
        => ResolveOption(group, option) ?? Top;

    /// <summary>
    /// The same lookup for a content piece: its option's entry, else the content slot — never
    /// <see cref="Top"/>, which belongs to the mod's overlays.
    /// </summary>
    public GearSettingsPreset? ResolveContent(string? group, string? option)
        => ResolveOption(group, option) ?? Content;

    private GearSettingsPreset? ResolveOption(string? group, string? option)
        => group != null && option != null && Options != null
        && Options.TryGetValue(group, out var opts) && opts.TryGetValue(option, out var s)
            ? s : null;
}

/// <summary>
/// Deserialises MaterialGamePath as either a JSON string or a JSON array of strings.
/// Serialises a single-element list back as a plain string for compact output.
/// </summary>
public class StringOrStringArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString()!];

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                if (reader.TokenType == JsonTokenType.String)
                    list.Add(reader.GetString()!);
            return list;
        }

        throw new JsonException($"Expected string or array for MaterialGamePath, got {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
            writer.WriteStringValue(value[0]);
        else
        {
            writer.WriteStartArray();
            foreach (var s in value)
                writer.WriteStringValue(s);
            writer.WriteEndArray();
        }
    }
}
