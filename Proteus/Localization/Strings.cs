using CheapLoc;

namespace Proteus.Localization;

/// <summary>
/// Every user-facing string, resolved ONCE per language instead of once per frame.
/// <para/>
/// The fields inside each holder are <c>readonly</c> and captured at construction, and that is the whole
/// point. <c>Loc.Localize</c> is not the cheap dictionary hit it looks like: it calls
/// <c>Assembly.GetCallingAssembly()</c> — a stack walk, which also blocks the caller from being inlined —
/// and then <c>Assembly.GetName()</c>, which builds a fresh <c>AssemblyName</c> (display-name parse,
/// culture, version, public-key token) on every single call before it looks anything up. Asking it for the
/// status window's visible strings at frame rate would be thousands of stack walks and tens of thousands of
/// allocations a second, in a plugin that elsewhere goes out of its way to avoid one per-frame substring
/// (see <c>ProteusStyle.Ellipsize</c>). Reading a field costs none of that.
/// <para/>
/// The price is that a language change cannot reach a holder that already exists, so <see cref="Reload"/>
/// replaces them wholesale — about a dozen allocations, once, when the user changes a setting.
/// <b>Nothing may cache a holder INSTANCE in a field or a local across frames;</b> always read through
/// these properties at the point of use, or you will pin the old language.
/// <para/>
/// Rule for what belongs here: if a string can be reached from a <c>Draw()</c> call chain it lives in a
/// holder; if it can only be reached from a user action or a background task (a validation result, a chat
/// notice) it may call <c>Loc.Localize</c> inline, because it runs once per click, not once per frame.
/// <para/>
/// Both arguments to <c>Loc.Localize</c> must be compile-time literals — the key and the English fallback.
/// CheapLoc's exporter reads the two IL instructions immediately before the call and both must be
/// <c>ldstr</c>, so an interpolated <c>$"..."</c> is invisible to it and can never be translated. Adjacent
/// <c>"a" + "b"</c> literals are folded by the compiler into one <c>ldstr</c> and are fine; a trailing
/// <c>+ "###id"</c> after the call is also fine, because it happens to the result rather than the argument.
/// <c>LocalizationTests.CodeKeysMatchEnglishJson</c> enforces all of this.
/// </summary>
public static class Strings
{
    public static CommonStrings   Common   { get; private set; } = new();
    public static TabStrings      Tab      { get; private set; } = new();
    public static ModsStrings     Mods     { get; private set; } = new();
    public static BindingsStrings Bindings { get; private set; } = new();
    public static CreateStrings   Create   { get; private set; } = new();
    public static SettingsStrings Settings { get; private set; } = new();
    public static BandStrings     Band     { get; private set; } = new();
    public static FooterStrings   Footer   { get; private set; } = new();
    public static ImportStrings   Import   { get; private set; } = new();
    public static ContentStrings  Content  { get; private set; } = new();
    public static ExportStrings   Export   { get; private set; } = new();
    public static ModsListStrings ModsList { get; private set; } = new();
    public static ColorPanelStrings ColorPanel { get; private set; } = new();
    public static ColorsStrings     Colors     { get; private set; } = new();
    public static PartsStrings      Parts      { get; private set; } = new();

    /// <summary>
    /// Rebuilds every holder against the language CheapLoc was just set up with. Called from
    /// <see cref="LocSetup"/> after <c>SetupWithLangCode</c>, and only from there.
    /// </summary>
    public static void Reload()
    {
        Common   = new CommonStrings();
        Tab      = new TabStrings();
        Mods     = new ModsStrings();
        Bindings = new BindingsStrings();
        Create   = new CreateStrings();
        Settings = new SettingsStrings();
        Band     = new BandStrings();
        Footer   = new FooterStrings();
        Import   = new ImportStrings();
        Content  = new ContentStrings();
        Export   = new ExportStrings();
        ModsList = new ModsListStrings();
        ColorPanel = new ColorPanelStrings();
        Colors     = new ColorsStrings();
        Parts      = new PartsStrings();
    }
}

/// <summary>Strings shared by more than one screen.</summary>
public sealed class CommonStrings
{
    public readonly string Browse = Loc.Localize("Common.Browse.Btn", "Browse");
    public readonly string Clear  = Loc.Localize("Common.Clear.Btn", "Clear");
    public readonly string None   = Loc.Localize("Common.None", "(none)");
}

/// <summary>
/// The six tabs across the top of the status window. Plain labels — <c>ProteusStyle.HeaderTabItem</c>
/// fuses the stable <c>###id</c> on itself, so it cannot be forgotten at a call site.
/// </summary>
public sealed class TabStrings
{
    public readonly string Mods     = Loc.Localize("Tab.Mods", "Mods");
    public readonly string Bindings = Loc.Localize("Tab.Bindings", "Bindings");
    public readonly string Create   = Loc.Localize("Tab.Create", "Create");
    public readonly string Import   = Loc.Localize("Tab.Import", "Import");
    public readonly string Parts    = Loc.Localize("Tab.Parts", "Toggles");
    public readonly string Export   = Loc.Localize("Tab.Export", "Export");
    public readonly string Settings = Loc.Localize("Tab.Settings", "Settings");
}

public sealed class ModsStrings
{
    // Sort headers pass their id to SortableHeader separately, so these stay plain.
    public readonly string ColOn       = Loc.Localize("Mods.Col.On", "On");
    public readonly string ColMod      = Loc.Localize("Mods.Col.Mod", "Mod");
    public readonly string ColPriority = Loc.Localize("Mods.Col.Priority", "Pri");

    // These two are drawn with a bare ImGui.TableHeader, which takes the label as its id — hence the
    // fused "###". Done here rather than at the call site so the concatenation happens once per language
    // instead of once per frame.
    public readonly string ColColors   = Loc.Localize("Mods.Col.Colors", "Colors") + "###modColors";
    public readonly string ColSkindent = Loc.Localize("Mods.Col.Skindent", "Skindent") + "###modSkindent";

    public readonly string ColorsBtn = Loc.Localize("Mods.Colors.Btn", "Colors");

    public readonly string ColorsBindingDrivenTip = Loc.Localize("Mods.Colors.BindingDriven.Tip",
        "Colors are driven by the active design binding.\nEdits preview live; click \"Update\" on the " +
        "Bindings tab to save them. Base colors are unchanged.");

    public readonly string AoOn  = Loc.Localize("Mods.Ao.On", "On");
    public readonly string AoOff = Loc.Localize("Mods.Ao.Off", "Off");

    /// <summary>{0} is the pack's own choice, already localized to <see cref="AoOn"/>/<see cref="AoOff"/>.</summary>
    public readonly string AoPackFmt = Loc.Localize("Mods.Ao.Pack.Fmt", "Pack ({0})");

    /// <summary>{0} is the "Pack (on)" label, repeated so the tooltip names the option it is describing.</summary>
    public readonly string AoTipFmt = Loc.Localize("Mods.Ao.Tip.Fmt",
        "Ambient-occlusion shadow + Skindenting normal indent for this mod's\n" +
        "straps/garment edges. It treats coverage as cloth pressed into skin, which is\n" +
        "wrong for tattoos and skin details — so it is off unless asked for.\n\n" +
        "{0} = whatever the pack declares (\"AmbientOcclusion\" in its\n" +
        "metadata.json; absent means off).\n" +
        "On / Off = your own setting for this mod, overriding the pack.\n\n" +
        "(The global strength sliders are in Settings.)");
}

public sealed class BindingsStrings
{
    public readonly string ColDesign   = Loc.Localize("Bindings.Col.Design", "Design");
    public readonly string ColCaptured = Loc.Localize("Bindings.Col.Captured", "Captured");

    public readonly string Enable =
        Loc.Localize("Bindings.Enable.Label", "Bind Proteus state to Glamourer designs") + "###bindEnable";

    public readonly string EnableTip = Loc.Localize("Bindings.Enable.Tip",
        "When on, saving a Glamourer design snapshots the current Proteus state.\n" +
        "Applying that design later restores it (best-effort gear match).");

    public readonly string FollowAutomation = Loc.Localize(
        "Bindings.FollowAutomation.Label", "Follow Glamourer automation (gearset / job changes)") + "###bindAutomation";

    public readonly string FollowAutomationTip = Loc.Localize("Bindings.FollowAutomation.Tip",
        "Glamourer reports nothing when automation applies a design on a gearset\n" +
        "or job change, so Proteus infers it from the signals that do arrive.\n\n" +
        "Only ever restores a binding — it never clears one.\n" +
        "The one redraw Proteus itself causes is discounted, so its own work\n" +
        "can't be mistaken for an automation apply.\n\n" +
        "Needs \"Bind Proteus state to Glamourer designs\" above.");

    public readonly string ActiveFmt = Loc.Localize("Bindings.Active.Fmt", "Active: {0}");
    public readonly string NoBindings = Loc.Localize("Bindings.None", "No bound designs yet.");
    public readonly string PillActive = Loc.Localize("Bindings.Pill.Active", "active");

    public readonly string SecondsAgoFmt = Loc.Localize("Bindings.SecondsAgo.Fmt", "{0}s ago");
    public readonly string MinutesAgoFmt = Loc.Localize("Bindings.MinutesAgo.Fmt", "{0}m ago");
    public readonly string HoursAgoFmt   = Loc.Localize("Bindings.HoursAgo.Fmt", "{0}h ago");

    public readonly string Apply  = Loc.Localize("Bindings.Apply.Btn", "Apply");
    public readonly string Update = Loc.Localize("Bindings.Update.Btn", "Update");
    public readonly string Unbind = Loc.Localize("Bindings.Unbind.Btn", "Unbind");

    public readonly string ApplyTip = Loc.Localize("Bindings.Apply.Tip",
        "Restore this design's Proteus state now, without going through Glamourer —\n" +
        "enable / priority / options / colours for every mod it captured.\n\n" +
        "Proteus mods NOT in the binding are switched off, so this replaces the current\n" +
        "look rather than adding to it.");

    public readonly string UpdateTip = Loc.Localize("Bindings.Update.Tip",
        "Snapshot the current Proteus state (enable / priority / options / colors)\n" +
        "into this binding. Manual edits only persist when you click this.");

    public readonly string UpdateInactiveTip = Loc.Localize("Bindings.Update.Inactive.Tip",
        "Only the active binding can be updated from the current state.\n" +
        "Apply this design first.");

    public readonly string UnbindTip = Loc.Localize("Bindings.Unbind.Tip",
        "Forget this binding. The Glamourer design itself is untouched.");
}

public sealed class CreateStrings
{
    // The four texture slots. These are DISPLAY text only — the slot identity that drives the file-picker
    // dispatch and the material-slot lookup is a separate, untranslated token at the call site. See
    // StatusWindow.DrawTextureRow, where the two used to be one parameter.
    public readonly string SlotDiffuse = Loc.Localize("Create.Slot.Diffuse", "Diffuse");
    public readonly string SlotMask    = Loc.Localize("Create.Slot.Mask", "Mask");
    public readonly string SlotNormal  = Loc.Localize("Create.Slot.Normal", "Normal");
    public readonly string SlotIndex   = Loc.Localize("Create.Slot.Index", "Index");

    public readonly string SlotUnused = Loc.Localize("Create.Slot.Unused", "(not used by this material)");

    public readonly string PickTextureTitleFmt =
        Loc.Localize("Create.PickTexture.Title.Fmt", "Select {0} texture");

    public readonly string NoDiffuse = Loc.Localize("Create.Slot.NoDiffuse", "This material has no diffuse texture.");
    public readonly string NoMask    = Loc.Localize("Create.Slot.NoMask", "This material has no mask texture.");
    public readonly string NoNormal  = Loc.Localize("Create.Slot.NoNormal", "This material has no normal texture.");

    public readonly string NoIndex = Loc.Localize("Create.Slot.NoIndex",
        "This material has no index texture, and it isn't skin or face — nothing here\n" +
        "would read a colour-table row selector.");

    public readonly string Intro = Loc.Localize("Create.Intro",
        "Make a basic Proteus overlay mod. Pick at least one texture; Proteus writes a " +
        "new Penumbra mod and opens it so you can enable and tweak it.");

    public readonly string ModName = Loc.Localize("Create.ModName.Label", "Mod name") + "###createName";
    public readonly string Author  = Loc.Localize("Create.Author.Label", "Author") + "###createAuthor";

    public readonly string MaterialTarget =
        Loc.Localize("Create.MaterialTarget.Label", "Material target") + "###createMaterial";

    public readonly string MaterialTargetTip = Loc.Localize("Create.MaterialTarget.Tip",
        "The material this overlay composites onto. Auto-filled from the body you're\n" +
        "currently wearing; pick one you have equipped from the list, or type a path by hand to\n" +
        "target a body/race you aren't wearing right now.");

    public readonly string PickerStale =
        Loc.Localize("Create.Picker.Stale", "Showing the last known list — character isn't drawn.");

    public readonly string PickerEmpty = Loc.Localize("Create.Picker.Empty",
        "No equipped materials known yet.\nZone in or redraw, then reopen this list.");

    public readonly string PickerSkin = Loc.Localize("Create.Picker.SkinGroup", "Skin");

    public readonly string Redetect    = Loc.Localize("Create.Redetect.Btn", "Re-detect") + "###createRedetect";
    public readonly string RedetectTip = Loc.Localize("Create.Redetect.Tip", "Re-run body detection and overwrite the field.");

    public readonly string SlotsUnreadable =
        Loc.Localize("Create.SlotsUnreadable", "Couldn't read this material — offering every slot.");

    public readonly string CreateBtn = Loc.Localize("Create.Create.Btn", "Create") + "###createGo";

    public readonly string CreateDisabledTip = Loc.Localize("Create.Create.DisabledTip",
        "Enter a mod name, a material target, and pick at least one texture.");
}

public sealed class SettingsStrings
{
    // ── section headings ────────────────────────────────────────────────────────────────────────────
    public readonly string SecGeneral     = Loc.Localize("Settings.Section.General", "General");
    public readonly string SecOutput      = Loc.Localize("Settings.Section.Output", "Output");
    public readonly string SecSkinEffects = Loc.Localize("Settings.Section.SkinEffects", "Skin effects");
    public readonly string SecHosting     = Loc.Localize("Settings.Section.Hosting", "Hosting");
    public readonly string SecDiagnostics = Loc.Localize("Settings.Section.Diagnostics", "Diagnostics");

    // ── general ─────────────────────────────────────────────────────────────────────────────────────
    public readonly string Enabled = Loc.Localize("Settings.General.Enabled.Label", "Enabled");

    public readonly string EnabledTip = Loc.Localize("Settings.General.Enabled.Tip",
        "Turning this off clears Proteus' output, redraws you without it,\n" +
        "and disables the managed \"Proteus\" mod in Penumbra.");

    public readonly string DisableAutoRedraw =
        Loc.Localize("Settings.General.DisableAutoRedraw.Label", "Disable auto redraw") + "###disableAutoRedraw";

    public readonly string SkipUnchanged =
        Loc.Localize("Settings.General.SkipUnchanged.Label", "Skip unchanged recomposites") + "###skipUnchanged";

    public readonly string SkipUnchangedTip = Loc.Localize("Settings.General.SkipUnchanged.Tip",
        "Let a recomposite triggered by zoning or a redraw stop early when nothing that\n" +
        "affects the output has changed. Anything you change yourself always recomposites.\n" +
        "Turn off only to rule this out when an edit isn't taking effect.");

    public readonly string AutoRaise =
        Loc.Localize("Settings.General.AutoRaise.Label", "Auto-raise mod priority") + "###autoRaise";

    public readonly string AutoRaiseTip = Loc.Localize("Settings.General.AutoRaise.Tip",
        "When another mod is confirmed to be overriding a skin texture Proteus composites\n" +
        "into — a tattoo or skin pack shipping its own copy of the body texture — raise\n" +
        "Proteus' Penumbra priority above it automatically, and say so in chat.\n\n" +
        "That override is otherwise invisible: overlays half-apply (the bumps land, the\n" +
        "colour doesn't) and every log line still reads as a success.\n\n" +
        "Turn off only if you deliberately want another mod to win a path Proteus\n" +
        "composites. Proteus never acts on a guess — only on a confirmed override.");

    public readonly string InPlaceReload =
        Loc.Localize("Settings.General.InPlaceReload.Label", "In-place reload") + "###inPlaceReload";

    public readonly string InPlaceReloadTip = Loc.Localize("Settings.General.InPlaceReload.Tip",
        "Refresh textures via Glamourer's in-place equipment reload instead of a full\n" +
        "redraw, avoiding the despawn/respawn flicker. Falls back to a full redraw\n" +
        "automatically when Glamourer can't service it.");

    public readonly string GlowLibrary =
        Loc.Localize("Settings.General.GlowLibrary.Btn", "Glow Effect Textures") + "###glowLibrary";

    public readonly string GlowLibraryTipFmt = Loc.Localize("Settings.General.GlowLibrary.Tip.Fmt",
        "Open the folder Proteus reads animated-glow scroll maps from — the \"_o\"\n" +
        "textures that ARE the glow. Anything dropped in here appears in every\n" +
        "gear overlay's Effect dropdown.\n\n" +
        "{0}\n\n" +
        "Accepts .tex, .dds, .png, .jpg, .bmp, .tga, .psd and .gif.\n" +
        "A mod's own Proteus/Effects/ folder takes precedence over it.");

    // ── output ──────────────────────────────────────────────────────────────────────────────────────
    public readonly string Compression =
        Loc.Localize("Settings.Output.Compression.Label", "Enable Compression") + "###enableCompression";

    public readonly string CompressionTip = Loc.Localize("Settings.Output.Compression.Tip",
        "Block-compress the baked textures (BC7), cutting each to about a quarter of its\n" +
        "uncompressed size on disk and in VRAM. The index texture stays uncompressed to keep\n" +
        "its exact row values. Off = uncompressed (byte-identical to before).");

    public readonly string SharpAlpha =
        Loc.Localize("Settings.Output.SharpAlpha.Label", "Sharp alpha (gpose sphere/metal)") + "###sharpAlpha";

    public readonly string SharpAlphaTip = Loc.Localize("Settings.Output.SharpAlpha.Tip",
        "EXPERIMENTAL. Renders shell coverage as a hard alpha-test cutout instead of smooth\n" +
        "transparency, so sphere maps and metalness survive gpose (which drops them on\n" +
        "transparent surfaces). Trade-off: sheer edges become hard/aliased. Best for\n" +
        "mostly-opaque fabrics; a very sheer fabric will look coarse. Recomposite after toggling.");

    // ── hosting ─────────────────────────────────────────────────────────────────────────────────────
    public readonly string InvisibleGlasses = Loc.Localize(
        "Settings.Hosting.InvisibleGlasses.Label", "Host on invisible glasses (keep rings free)") + "###invisibleGlasses";

    public readonly string InvisibleGlassesTip = Loc.Localize("Settings.Hosting.InvisibleGlasses.Tip",
        "When on and you have no glasses equipped, Proteus has Glamourer equip an\n" +
        "invisible glasses item so the second skin rides the facewear slot instead of a\n" +
        "ring. This writes a (hidden) bonus item to your Glamourer state; it's removed\n" +
        "when you disable Proteus, equip real glasses, or turn this off.");

    public readonly string RestoreAccessory =
        Loc.Localize("Settings.Hosting.RestoreAccessory.Btn", "Restore changed accessory") + "###restoreAccessory";

    public readonly string RestoreAccessoryTip = Loc.Localize("Settings.Hosting.RestoreAccessory.Tip",
        "Force a full redraw to reload any ring/bracelet the second skin replaced,\n" +
        "restoring it to its original model. Use if a gear shell stays stuck on an\n" +
        "accessory after disabling or swapping.");

    // ── diagnostics ─────────────────────────────────────────────────────────────────────────────────
    public readonly string ClearCache =
        Loc.Localize("Settings.Diag.ClearCache.Btn", "Clear texture cache") + "###clearCache";

    public readonly string ClearCacheTip = Loc.Localize("Settings.Diag.ClearCache.Tip",
        "Drop all cached decoded textures and recomposite now. Use if a texture edit\n" +
        "isn't showing up — e.g. you re-exported an overlay at the same size and the\n" +
        "change won't appear without restarting the plugin.\n\n" +
        "Also re-derives which mod each base skin texture comes from — use this if the\n" +
        "Base skin below names the wrong mod.");

    public readonly string BaseSkinHeaderFmt = Loc.Localize("Settings.Diag.BaseSkin.Header.Fmt", "Base skin ({0})");

    public readonly string BaseSkinUnconfirmedFmt =
        Loc.Localize("Settings.Diag.BaseSkin.Unconfirmed.Fmt", "{0} (unconfirmed)");

    public readonly string BaseSkinNote = Loc.Localize("Settings.Diag.BaseSkin.Note",
        "The mod each base texture is read from. Hover for the game path.");

    public readonly string ReachHeaderFmt = Loc.Localize("Settings.Diag.Reach.Header.Fmt", "Overlay reach ({0})");

    public readonly string ReachFailedFmt = Loc.Localize("Settings.Diag.Reach.Failed.Fmt",
        "{0} — diffuse {1}, normal {2}, mask {3} (diffuse did not apply)");

    public readonly string ReachEffectsOnlyFmt =
        Loc.Localize("Settings.Diag.Reach.EffectsOnly.Fmt", "{0} — effects only (no overlay art)");

    public readonly string ReachOkFmt =
        Loc.Localize("Settings.Diag.Reach.Ok.Fmt", "{0} — diffuse {1}, normal {2}, mask {3}");

    public readonly string ReachNote = Loc.Localize("Settings.Diag.Reach.Note",
        "How many overlays actually reached each channel. Hover for the material.");

    // ── skin effects ────────────────────────────────────────────────────────────────────────────────
    public readonly string SkinTint = Loc.Localize("Settings.Skin.SkinTint.Label", "Skin-tint suppression");

    public readonly string SkinTintTip = Loc.Localize("Settings.Skin.SkinTint.Tip",
        "How strongly overlays resist skin-tone tinting (global multiplier).\n" +
        "Applied per pixel by color: white/bright dyes keep their authored color on any\n" +
        "skin tone (slightly shinier), dark dyes stay skin-tinted and matte automatically.\n" +
        "0.00 disables it entirely (original look).");

    public readonly string AmbientOcclusion = Loc.Localize("Settings.Skin.AmbientOcclusion.Label", "Ambient occlusion");

    public readonly string AmbientOcclusionTip = Loc.Localize("Settings.Skin.AmbientOcclusion.Tip",
        "Soft contact shadow on the skin just outside masked strap edges, giving straps depth.\n" +
        "0.00 disables it entirely (skin diffuse unchanged).");

    public readonly string ShadowSoftness = Loc.Localize("Settings.Skin.ShadowSoftness.Label", "Shadow softness");

    public readonly string ShadowSoftnessTip = Loc.Localize("Settings.Skin.ShadowSoftness.Tip",
        "How far the ambient-occlusion shadow spreads from a strap edge (fraction of texture width).\n" +
        "Larger = wider, softer shadow. Shared by the shadow and the strap indent.");

    public readonly string Skindenting = Loc.Localize("Settings.Skin.Skindenting.Label", "Skindenting");

    public readonly string SkindentingTip = Loc.Localize("Settings.Skin.Skindenting.Tip",
        "Indents the skin normal at strap/garment edges so straps look pressed into the skin.\n" +
        "0.00 disables it (skin normal unchanged). Uses the same edges/softness as the shadow.");

    // ── cache + meshes ──────────────────────────────────────────────────────────────────────────────
    public readonly string TextureCache = Loc.Localize("Settings.Output.TextureCache.Label", "Texture cache (MB)");

    public readonly string TextureCacheTip = Loc.Localize("Settings.Output.TextureCache.Tip",
        "How much decoded texture data Proteus keeps in memory between composites.\n\n" +
        "A 4K texture costs 64 MB decoded, so this is really a count: 2048 MB ≈ 30 of them.\n" +
        "It only helps if it covers a whole composite's worth — below that, every run evicts\n" +
        "what the next one needs and nothing is reused.\n\n" +
        "Check the \"cache N entries, M MB\" figure in the recomposite log: if a SECOND\n" +
        "composite with nothing changed still reports misses, raise this. Lower it if the\n" +
        "game starts paging. Released automatically after 60s idle.");

    public readonly string ConnectorMeshes =
        Loc.Localize("Settings.Output.ConnectorMeshes.Label", "Hide Connector Meshes");

    public readonly string ConnectorMeshesTip = Loc.Localize("Settings.Output.ConnectorMeshes.Tip",
        "Skip each body part's connector ring on the gear \"second skin\" — the small extra\n" +
        "submesh at a joint (wrist/ankle/…). Some bodies (Neolithe) reinforce joints with a ring\n" +
        "that overlaps an already-complete body; on a sheer overlay the overlap doubles up and\n" +
        "shows as a more-opaque seam. Leave Off for other bodies — there that submesh is real\n" +
        "skin, and hiding it would leave gaps.");
}

/// <summary>The Import tab's content-pack (.pmp) half — packs that ship their own meshes.</summary>
public sealed class ContentStrings
{
    /// <summary>
    /// The .pmp half of the Import tab's two lines, in the same voice as <see cref="ImportStrings.Intro"/>:
    /// what you get, not how it is done. The mechanism it used to describe — copying the pack in, stopping
    /// Penumbra publishing its models, appending option meshes onto a carrier accessory so options sharing
    /// a game path can coexist — is all true and none of it belongs on the button someone is deciding
    /// whether to press.
    /// </summary>
    public readonly string Intro = Loc.Localize("Content.Intro",
        "Import a regular mod (.pmp). Wear parts of it without using a gear slot, and add advanced colour "
      + "table features.");

    public readonly string ReadFailedFmt = Loc.Localize("Content.ReadFailed.Fmt",
        "Couldn't read that pack: {0}");

    public readonly string PieceCountFmt = Loc.Localize("Content.PieceCount.Fmt",
        "Pieces: {0} of {1}");

    public readonly string RacesFmt = Loc.Localize("Content.Races.Fmt", "{0} races");

    public readonly string AllOffFmt = Loc.Localize("Content.AllOff.Fmt",
        "Pieces arrive switched OFF. After importing, tick the ones you want under \"{0}\" in Penumbra — "
      + "nothing is worn until you do.");

    public readonly string GeometryFmt = Loc.Localize("Content.Geometry.Fmt", "{0} mesh, {1} verts");
    public readonly string MaterialsFmt = Loc.Localize("Content.Materials.Fmt", "{0} materials");
    public readonly string Skipped = Loc.Localize("Content.Skipped", "skipped");
    public readonly string Unbound = Loc.Localize("Content.Unbound", "unbound material");

    /// <summary>The deliberate drop, said plainly and NOT in the warning colour — an outfit pack ships the
    /// body it was fitted to, the wearer already has one, and leaving it out is the wanted outcome.</summary>
    public readonly string BodyOnly = Loc.Localize("Content.BodyOnly", "wearer's own body");

    /// <summary>Shown in place of the piece table for a pack that already carries a Proteus sidecar: it is
    /// copied in unchanged rather than converted. Not a warning — this is the right outcome.</summary>
    public readonly string AlreadyProteus = Loc.Localize("Content.AlreadyProteus",
        "This pack is already a Proteus mod. It will be installed exactly as its author built it — nothing "
      + "is converted, and its own options stay in Penumbra where they are.");

    public readonly string ProblemFmt = Loc.Localize("Content.Problem.Fmt", "{0}\nSkipped: {1}");

    public readonly string NothingUsable = Loc.Localize("Content.NothingUsable",
        "No option in this pack ships a mesh Proteus can append.");

    public readonly string SharedByFmt = Loc.Localize("Content.SharedBy.Fmt",
        "Shared by: {0}. Those pieces are drawn with one material, so these colours reach all of them — and "
      + "rows you don't touch stay exactly as the pack's author wrote them.");

    /// <summary>Stands in for an option name in <see cref="SharedByFmt"/> when a piece belongs to no
    /// option — a model the pack applies whenever it is enabled.</summary>
    public readonly string Unconditional = Loc.Localize("Content.Unconditional", "always on");

    public readonly string NotForYourRaceFmt = Loc.Localize("Content.NotForYourRace.Fmt",
        "This pack's models are built for {0}, and you are {1}. Gear made for one race is a different shape, "
      + "so wearing it as-is would put it in the wrong place — Proteus leaves it off rather than show that.");

    public readonly string NoRaceFitFmt = Loc.Localize("Content.NoRaceFit.Fmt",
        "The nearest model this pack has is built for {0}, which is neither your own race ({1}) nor the "
      + "shared shape the game resizes for everyone. Proteus leaves it off rather than show it at the wrong "
      + "size.");

    public readonly string SamplesFmt = Loc.Localize("Content.Samples.Fmt",
        "Its index texture reads row {0}, column {1} — the other rows are dimmed because nothing samples "
      + "them, and editing the other column of this row will do nothing either.");

    public readonly string NoIndexFmt = Loc.Localize("Content.NoIndex.Fmt",
        "This material ships no index texture, so it takes row {0} for everything.");

    public readonly string SamplesRowsFmt = Loc.Localize("Content.SamplesRows.Fmt",
        "Its index texture reads rows {0} — the others are dimmed because nothing samples them.");

    public readonly string IndexUnreadable = Loc.Localize("Content.IndexUnreadable",
        "This material names an index texture Proteus couldn't read, so it can't tell which rows are live. "
      + "Every row is editable below, but only the ones the index selects will show.");

    public readonly string GlowNeedsEmissiveFmt = Loc.Localize("Content.GlowNeedsEmissive.Fmt",
        "Glow is at zero on row {0}{1} — the one cell this material reads — so the effect stays off. Raise "
      + "Glow there to turn it on and set how strongly it shows. Glow on any other row does nothing.");

    public readonly string GlowEffectMissingFmt = Loc.Localize("Content.GlowEffectMissing.Fmt",
        "The effect \"{0}\" is no longer in this mod's Effects folder or your library, so the piece is "
      + "rendering without it. Pick another, or put the file back.");

    public readonly string GlowDropsDiffuse = Loc.Localize("Content.GlowDropsDiffuse",
        "This pack paints its surface with a texture. An animated glow runs the material on a shader that "
      + "has no slot for one, so the colours above take over while the glow is on. Clearing the effect puts "
      + "the texture back.");

    public readonly string NoColorTable = Loc.Localize("Content.NoColorTable",
        "This material carries no colour table, so it has no rows to edit — nothing you change below will "
      + "reach the piece. Its colours come from its textures alone.");

    public readonly string IndexCompressedFmt = Loc.Localize("Content.IndexCompressed.Fmt",
        "Its index texture reads row {0}, column {1} — but that texture is compressed, so it could be a row "
      + "or two out. The dimmed rows are still clickable: if a colour doesn't take, try one either side.");

    /// <summary>Stands in for the column letter when an index uses both — see <see cref="IndexCompressedFmt"/>.</summary>
    public readonly string EitherColumn = Loc.Localize("Content.EitherColumn", "A and B");

    public readonly string IndexEmpty = Loc.Localize("Content.IndexEmpty",
        "This material's index texture is fully transparent, so it selects no colour row at all. Every row "
      + "is editable below, but the piece will take its colours from the textures alone.");
}

public sealed class ImportStrings
{
    /// <summary>
    /// What an .omp gets you, in one line. Says the PAYOFF, not the machinery: it used to explain sidecars
    /// and that the original file is left alone, which answers a question nobody has asked yet at the point
    /// they are deciding whether to click.
    /// </summary>
    public readonly string Intro = Loc.Localize("Import.Intro",
        "Import an Onion overlay pack (.omp). Wear its layers as Proteus overlays you can recolour and "
      + "restack.");

    /// <summary>Shared by BOTH formats — see DrawImportTab, which has one browse button and picks the
    /// reader off the extension.</summary>
    public readonly string BrowseBtn    = Loc.Localize("Import.Browse.Btn", "Browse for a pack") + "###importBrowse";
    public readonly string DialogTitle  = Loc.Localize("Import.Dialog.Title", "Select a pack");
    public readonly string DialogFilter = Loc.Localize("Import.Dialog.Filter", "Mod pack");

    public readonly string NoPack = Loc.Localize("Import.NoPack", "Pick a pack to see what it contains.");

    public readonly string ModName = Loc.Localize("Import.ModName.Label", "Mod name") + "###importName";
    public readonly string Author  = Loc.Localize("Import.Author.Label", "Author") + "###importAuthor";

    public readonly string Description  = Loc.Localize("Import.Description", "Description");
    public readonly string WebsiteTip   = Loc.Localize("Import.Website.Tip", "Carried into the mod's Penumbra page.");

    public readonly string LayoutsFmt = Loc.Localize("Import.Layouts.Fmt",
        "The pack has {0} UV layouts, so the mod gets a single-select \"{1}\" group in Penumbra ({2}). " +
        "Only one composites at a time.");

    public readonly string DefaultLayoutMatchedFmt = Loc.Localize("Import.DefaultLayoutMatched.Fmt",
        "\"{0}\" will be selected — it matches the body you're wearing.");

    public readonly string AsTex = Loc.Localize("Import.AsTex.Label", "Convert layers to BC7 .tex") + "###importAsTex";

    public readonly string AsTexTip = Loc.Localize("Import.AsTex.Tip",
        "Roughly a quarter of the disk size, and no PNG decode at composite time.\n" +
        "Block compression is lossy — leave it off to keep the pack's images exactly as authored.");

    public readonly string AsTexUnavailableTip = Loc.Localize("Import.AsTex.Unavailable.Tip",
        "The native block compressor isn't loaded, so BC7 encoding would take minutes.");

    public readonly string ImportBtn  = Loc.Localize("Import.Import.Btn", "Import") + "###importGo";
    public readonly string ImportBusy = Loc.Localize("Import.Importing.Btn", "Importing…") + "###importGo";

    public readonly string NeedName    = Loc.Localize("Import.NeedName", "Enter a mod name.");
    public readonly string NothingUsable = Loc.Localize("Import.NothingUsable", "No layer in this pack can be imported.");

    public readonly string LayerCountFmt = Loc.Localize("Import.LayerCount.Fmt", "Layers: {0} of {1}");

    public readonly string NoLayout = Loc.Localize("Import.Layer.NoLayout", "(no layout)");
    public readonly string NoMap    = Loc.Localize("Import.Layer.NoMap", "(no map)");
    public readonly string Skipped  = Loc.Localize("Import.Layer.Skipped", "skipped");

    public readonly string LayerImportedFmt =
        Loc.Localize("Import.Layer.Imported.Fmt", "{0}\nImported as {1} in {2} UV space.");

    public readonly string LayerSkippedFmt = Loc.Localize("Import.Layer.SkippedReason.Fmt", "{0}\nSkipped: {1}");

    public readonly string NotDrawnFmt = Loc.Localize("Import.BodyFit.NotDrawn.Fmt",
        "Your character isn't drawn yet, so Proteus picked \"{0}\" by preference rather than by your " +
        "body. Check the result once you're in game.");

    public readonly string NeedsAllBodiesFmt = Loc.Localize("Import.BodyFit.NeedsAllBodies.Fmt",
        "This pack has nothing painted for your vanilla body — \"{0}\" will be used instead. Baking onto " +
        "a vanilla body is off by default, so Proteus will set this mod's \"Bodies\" to \"All bodies\" on " +
        "import (Colors → Advanced); without that it would paint nothing.");

    public readonly string RemappedFmt = Loc.Localize("Import.BodyFit.Remapped.Fmt",
        "This pack has nothing for your {0} body, so \"{1}\" will be remapped onto it automatically.");

    public readonly string FallbackBodies = Loc.Localize("Import.Materials.Fallback",
        "Proteus couldn't read the game's body list, so this import will target a known-good set of " +
        "female bodies only. Reopen this pack once you're in game to pick up every race.");

    public readonly string MaterialTargetsFmt =
        Loc.Localize("Import.Materials.Header.Fmt", "Material targets ({0})");

    public readonly string MaterialsFromGame = Loc.Localize("Import.Materials.FromGame",
        "Every body the game defines, so the overlay follows you across races. " +
        "Bodies you aren't wearing are ignored at composite time.");

    public readonly string MaterialsFallbackNote = Loc.Localize("Import.Materials.FallbackNote",
        "Fallback list — the game data couldn't be read when this pack was opened.");

    public readonly string LayoutGroupFmt = Loc.Localize("Import.Materials.Layout.Fmt", "{0}  ({1})");

    public readonly string ImportFailedFmt = Loc.Localize("Import.Failed.Fmt", "Import failed: {0}");
}

public sealed class ExportStrings
{
    public readonly string Intro = Loc.Localize("Export.Intro",
        "Save one of your Proteus mods as a Penumbra mod pack (.pmp) to share it. " +
        "Everything goes in — options, colours, masks and glow effects — and the file installs " +
        "straight into Penumbra.");

    public readonly string ModCombo = Loc.Localize("Export.Mod.Label", "Mod") + "###exportmod";
    public readonly string FilterHint = Loc.Localize("Export.Filter.Hint", "Filter…");

    public readonly string DisabledInPenumbraFmt =
        Loc.Localize("Export.DisabledInPenumbra.Fmt", "{0}\n(disabled in Penumbra)");

    public readonly string NoMatchFmt = Loc.Localize("Export.NoMatch.Fmt", "Nothing matches \"{0}\".");

    public readonly string ModDisabledNote = Loc.Localize("Export.ModDisabled.Note",
        "This mod is disabled in Penumbra — it exports the same either way.");

    // One id for all three captions: the button changes what it says as the export progresses, and
    // without a shared id it would be three different widgets in a row.
    public readonly string Choosing  = Loc.Localize("Export.Phase.Choosing", "Choose a location…") + "###exportGo";
    public readonly string Exporting = Loc.Localize("Export.Phase.Writing", "Exporting…") + "###exportGo";
    public readonly string ExportBtn = Loc.Localize("Export.Phase.Idle", "Export") + "###exportGo";

    public readonly string DialogTitle  = Loc.Localize("Export.Dialog.Title", "Export Proteus mod");
    public readonly string DialogFilter = Loc.Localize("Export.Dialog.Filter", "Penumbra mod pack");
}

/// <summary>
/// The colour-table editor itself. Every label here is drawn by a widget that also uses it as an ImGui
/// id — but every one of those call sites already carries an explicit <c>##scope</c> suffix, so the
/// visible half is free to change with the language without moving any identity.
/// </summary>
public sealed class ColorsStrings
{
    public readonly string SphereTip = Loc.Localize("Colors.Sphere.Tip",
        "Reflects a slice of the game's shared sphere map array.\n" +
        "Index AND intensity must both be non-zero, or nothing happens.\n" +
        "Does NOT work under characterscroll.shpk — use character.shpk.");

    public readonly string MetalTip = Loc.Localize("Colors.Metal.Tip",
        "Metal has no diffuse colour of its own — it shows what it reflects.\n" +
        "With no sphere map to reflect, a metallic surface just goes dark.");

    // ── render modes ────────────────────────────────────────────────────────────────────────────────
    public readonly string ModeSkin  = Loc.Localize("Colors.Mode.Skin", "Skin (painted)");
    public readonly string ModeCloth = Loc.Localize("Colors.Mode.Cloth", "Cloth");
    public readonly string ModeGlow  = Loc.Localize("Colors.Mode.AnimatedGlow", "Animated glow");

    public readonly string RenderingAs = Loc.Localize("Colors.RenderingAs", "Rendering as:");

    public readonly string Advanced = Loc.Localize("Colors.Advanced", "Advanced");

    public readonly string Pinned = Loc.Localize("Colors.Pinned", "(pinned)");
    public readonly string Auto   = Loc.Localize("Colors.Auto", "(auto)");

    public readonly string PinnedTip = Loc.Localize("Colors.Pinned.Tip",
        "You pinned this mode in Advanced — it no longer follows the features you set.\n" +
        "Click \"Back to auto\" to let it adapt again.");

    public readonly string AutoTip = Loc.Localize("Colors.Auto.Tip",
        "The mode follows what you use: a sphere map or metal ⇒ Cloth,\n" +
        "a glow effect ⇒ Animated glow, nothing special ⇒ Skin.");

    public readonly string BackToAuto = Loc.Localize("Colors.BackToAuto.Btn", "Back to auto");

    public readonly string ForceModeHint =
        Loc.Localize("Colors.ForceMode.Hint", "Force the render mode instead of letting the features pick it:");

    public readonly string ForceModeTip = Loc.Localize("Colors.ForceMode.Tip",
        "Skin (painted) — skin.shpk.  Cloth — character.shpk (sphere, metal).\n" +
        "Animated glow — characterscroll.shpk. Pinning stops the auto mode-switch.");

    public readonly string ResetBtn = Loc.Localize("Colors.Reset.Btn", "Reset to defaults");

    public readonly string ResetTip = Loc.Localize("Colors.Reset.Tip",
        "Hold Ctrl and click to restore this option's colours, glow and mode to the\n" +
        "settings Proteus first recorded for this mod. Cannot be undone.\n\n" +
        "If a design is applied, this also drops that design's saved override for this\n" +
        "option — otherwise the design would just re-impose it. Other designs keep theirs.\n\n" +
        "Note: those originals were captured the first time Proteus saved this mod —\n" +
        "if you had already edited it before then, that edited state is the \"default\".");

    // ── glow effect picker ──────────────────────────────────────────────────────────────────────────
    public readonly string GlowEffect = Loc.Localize("Colors.GlowEffect.Label", "Glow effect");

    public readonly string GlowEffectTip = Loc.Localize("Colors.GlowEffect.Tip",
        "Pick a scrolling map to make this overlay GLOW and animate — that switches it to\n" +
        "Animated glow. The map IS the glow (colour, pattern, motion); the row's Glow scales it.\n" +
        "Effects come from the mod's Proteus/Effects/ folder, then your Effects folder.");

    public readonly string NoEffects = Loc.Localize("Colors.NoEffects",
        "No effects found — drop images into the folder the Glow Effect Textures\n" +
        "button opens (Settings), or the mod's own Proteus/Effects/ folder.");

    public readonly string None = Loc.Localize("Colors.None", "None");

    /// <summary>{0} is an effect's file name; the suffix marks it as shipped by the mod itself.</summary>
    public readonly string EffectFromModFmt = Loc.Localize("Colors.Effect.FromMod.Fmt", "{0}  (mod)");

    public readonly string ScrollSpeed = Loc.Localize("Colors.ScrollSpeed.Label", "Scroll speed");

    public readonly string ScrollSpeedTip = Loc.Localize("Colors.ScrollSpeed.Tip",
        "How fast the effect flows, X and Y. Negative reverses; 0 holds it still. ~0.01 is normal.");

    public readonly string Tiling = Loc.Localize("Colors.Tiling.Label", "Tiling");

    public readonly string TilingTip =
        Loc.Localize("Colors.Tiling.Tip", "How many times the effect repeats across the surface. 1 = once.");

    // ── row picker + clipboard ──────────────────────────────────────────────────────────────────────
    public readonly string RowUnusedTip = Loc.Localize("Colors.Row.Unused.Tip",
        "This overlay's index texture never selects this row,\nso editing it would have no effect.");

    public readonly string CopyRow  = Loc.Localize("Colors.CopyRow.Btn", "Copy row");
    public readonly string PasteRow = Loc.Localize("Colors.PasteRow.Btn", "Paste row");

    public readonly string CopyRowTip = Loc.Localize("Colors.CopyRow.Tip", "Copy both sub-rows (A and B) of this row.");
    public readonly string NeedRowCopy = Loc.Localize("Colors.PasteRow.NeedCopy", "Copy a row first.");

    public readonly string PasteRowTipFmt = Loc.Localize("Colors.PasteRow.Tip.Fmt",
        "Overwrite row {0} (both sub-rows) with the copied row.");

    /// <summary>Column headers of the A/B panel. {0} is the row number; A and B are the two sub-rows.</summary>
    public readonly string SubRowAFmt = Loc.Localize("Colors.SubRowA.Fmt", "Row {0}A");
    public readonly string SubRowBFmt = Loc.Localize("Colors.SubRowB.Fmt", "Row {0}B");

    public readonly string CopySub  = Loc.Localize("Colors.CopySub.Btn", "Copy");
    public readonly string PasteSub = Loc.Localize("Colors.PasteSub.Btn", "Paste");

    public readonly string CopySubTip  = Loc.Localize("Colors.CopySub.Tip", "Copy this sub-row's values.");
    public readonly string NeedSubCopy = Loc.Localize("Colors.PasteSub.NeedCopy", "Copy a sub-row first.");
    public readonly string PasteSubTip = Loc.Localize("Colors.PasteSub.Tip", "Overwrite this sub-row with the copied values.");

    // ── live highlight ──────────────────────────────────────────────────────────────────────────────
    public readonly string Glow    = Loc.Localize("Colors.Highlight.Glow.Btn", "Glow");
    public readonly string Glowing = Loc.Localize("Colors.Highlight.Glowing.Btn", "Glowing");

    public readonly string GlowGearTip = Loc.Localize("Colors.Highlight.Gear.Tip",
        "Make this sub-row's mesh glow on your character so you can find it\nin-game. Click again to stop.");

    public readonly string GlowSkinTip = Loc.Localize("Colors.Highlight.Skin.Tip",
        "Light up this sub-row's region on your character's skin so you can find\n" +
        "it in-game. Click again to stop. (Takes a moment to build the first time.)");

    // ── the values themselves ───────────────────────────────────────────────────────────────────────
    public readonly string Colours = Loc.Localize("Colors.Section.Colours", "Colours");

    public readonly string Diffuse = Loc.Localize("Colors.Diffuse.Label", "Diffuse");

    public readonly string DiffuseGearTip = Loc.Localize("Colors.Diffuse.Gear.Tip",
        "The surface UNDER the glow — it multiplies the overlay's own diffuse art.\n\n" +
        "Keep it DARK for a glowing material, or the glow has nothing to stand out\n" +
        "against and looks faint however high you push it. The vanilla scrolling\n" +
        "materials pair a near-black diffuse with a bright emissive for exactly this.");

    public readonly string Specular = Loc.Localize("Colors.Specular.Label", "Specular");

    public readonly string GlowColour = Loc.Localize("Colors.GlowColour.Label", "Glow colour");

    public readonly string GlowColourTip = Loc.Localize("Colors.GlowColour.Tip",
        "Glow colour, independent of the diffuse — a glowing material usually\n" +
        "wants a DARK surface with a bright glow. Defaults to the diffuse.");

    public readonly string GlowAmount = Loc.Localize("Colors.GlowAmount.Label", "Glow");

    public readonly string GlowAmountTip = Loc.Localize("Colors.GlowAmount.Tip",
        "How brightly this row glows. 0 switches it off.\n" +
        "Under an animated glow this is the effect's brightness, and a high value\n" +
        "blows a colourful scroll map out to white. Around 25% is a good start.");

    public readonly string Opacity = Loc.Localize("Colors.Opacity.Label", "Opacity");

    public readonly string OpacityTip = Loc.Localize("Colors.Opacity.Tip",
        "Negative fades this row toward transparent; positive pushes it toward opaque.");

    public readonly string Physical    = Loc.Localize("Colors.Section.Physical", "Physical");
    public readonly string ClothSuffix = Loc.Localize("Colors.Section.Physical.ClothSuffix", "— Cloth");

    public readonly string Roughness = Loc.Localize("Colors.Roughness.Label", "Roughness");
    public readonly string Metalness = Loc.Localize("Colors.Metalness.Label", "Metalness");

    public readonly string SphereMap   = Loc.Localize("Colors.Section.SphereMap", "Sphere map");
    public readonly string SphereIndex = Loc.Localize("Colors.Sphere.Index.Label", "Index");
    public readonly string Intensity   = Loc.Localize("Colors.Sphere.Intensity.Label", "Intensity");
}

/// <summary>The colour window's own chrome — the panel StatusWindow draws around ColorTableEditor.</summary>
public sealed class ColorPanelStrings
{
    public readonly string PillBinding = Loc.Localize("Colors.Pill.Binding", "binding");

    public readonly string EditingBindingFmt = Loc.Localize("Colors.EditingBinding.Fmt",
        "Editing '{0}' — previewing live; click \"Update\" on the Bindings tab to save.");

    public readonly string BaseUnchanged = Loc.Localize("Colors.BaseUnchanged",
        "Base colors unchanged — except in Advanced, where \"Reset to defaults\" rewrites them and " +
        "\"Bodies\" is a global setting no binding captures.");

    public readonly string NoIndexTexture =
        Loc.Localize("Colors.NoIndexTexture", "No index texture — only Row 16 is applied.");

    public readonly string NoActiveOptions = Loc.Localize("Colors.NoActiveOptions",
        "No active options — select one in Penumbra to edit its colours.");

    // "Advanced" is not repeated here — the disclosure this panel draws and the one inside the editor are
    // the same control by another route, so both read Strings.Colors.Advanced and share one key.

    public readonly string MasksTab = Loc.Localize("Colors.MasksTab", "Masks");

    /// <summary>Tab caption when more than one option group is active: "Group: Option".</summary>
    public readonly string GroupOptionFmt = Loc.Localize("Colors.GroupOption.Fmt", "{0}: {1}");

    public readonly string StackHint = Loc.Localize("Colors.StackHint",
        "Editing overlay  (drag a tab to restack — leftmost = on top):");

    // The arrows are direction, not decoration, and are kept outside the translated text so a
    // right-to-left reading of the sentence cannot leave them pointing the wrong way.
    public readonly string TowardTop    = Loc.Localize("Colors.TowardTop.Btn", "Toward top");
    public readonly string TowardBottom = Loc.Localize("Colors.TowardBottom.Btn", "Toward bottom");

    public readonly string StackTip = Loc.Localize("Colors.Stack.Tip",
        "Reorder how this mod's overlays stack on your body, across groups.\n" +
        "Leftmost tab = top of the stack (composites last, on top).");

    // Bodies. "bibo", "gen3", "Eve" and "gen2" are body-mod names and are never translated; the option
    // labels are therefore mostly proper nouns with one English word each.
    public readonly string Bodies      = Loc.Localize("Colors.Bodies.Label", "Bodies");
    public readonly string BodiesOff   = Loc.Localize("Colors.Bodies.Off", "Off");
    public readonly string BodiesSibling = Loc.Localize("Colors.Bodies.BiboGen3", "bibo+gen3");
    public readonly string BodiesAll   = Loc.Localize("Colors.Bodies.All", "All bodies");

    public readonly string BodiesTip = Loc.Localize("Colors.Bodies.Tip",
        "Which body types to bake this mod onto:\n" +
        "All bodies = sibling body (bibo↔gen3/Eve) + vanilla (gen2)\n" +
        "bibo+gen3 = bake to the sibling body only (default)\n" +
        "Off = no synthesis\n\n" +
        "Applies to the whole mod, not just this option.");

    public readonly string BodiesGlobalSuffix = Loc.Localize("Colors.Bodies.GlobalSuffix",
        "\nGlobal — this one is NOT part of the binding you're editing.");

    public readonly string BodiesGlobalNote =
        Loc.Localize("Colors.Bodies.GlobalNote", "Saved globally — bindings don't capture this.");

    public readonly string Forced = Loc.Localize("Colors.Mask.Forced", "(forced)");

    public readonly string ForcedTip = Loc.Localize("Colors.Mask.Forced.Tip",
        "Another active option in this mod renders as gear, so the mask has to sit on that\n" +
        "shell — it can't be painted into the skin underneath it. Switch those options to\n" +
        "Skin (Advanced on their tabs) and the mask gets its own mode choice back.");
}

/// <summary>Shared by the Mods and Bindings tabs.</summary>
public sealed class ModsListStrings
{
    public readonly string Disabled = Loc.Localize("Mods.PluginDisabled", "Proteus is disabled — enable it in Settings.");
    public readonly string NoMods   = Loc.Localize("Mods.NoSidecarMods", "No Proteus sidecar mods detected.");
}

/// <summary>The header band across the top of the status window.</summary>
public sealed class BandStrings
{
    // The wordmark ("PROTEUS") and the Discord button are brand names and are deliberately absent —
    // they are never translated.

    // Shown only when the capability row below has had to drop its labels and nothing is hovered, so this
    // is the narrow-window fallback rather than the usual second line.
    public readonly string Caption = Loc.Localize("Band.Caption", "overlay · accessorize · toggle · bind");

    // The four capabilities named across the band's second line. Length is not cosmetic here: the row is
    // measured every frame and hides ALL FOUR labels the moment they stop fitting, so a translation much
    // longer than the English costs the whole row at window sizes where the English still shows.
    public readonly string CapOverlay = Loc.Localize("Band.Cap.Overlay", "Overlay & recolour anything");
    public readonly string CapWear    = Loc.Localize("Band.Cap.Wear",    "Wear anything, no slot");
    public readonly string CapToggle  = Loc.Localize("Band.Cap.Toggle",  "Toggle anything off");
    public readonly string CapBind    = Loc.Localize("Band.Cap.Bind",    "Bind it all to a design");

    public readonly string SettingsTip    = Loc.Localize("Band.Settings.Tip", "Settings");
    public readonly string RecompositeTip = Loc.Localize("Band.Recomposite.Tip", "Recomposite now");

    public readonly string PillDisabled   = Loc.Localize("Band.Pill.Disabled", "disabled");
    public readonly string PillNoPenumbra = Loc.Localize("Band.Pill.NoPenumbra", "no Penumbra");

    public readonly string Retry = Loc.Localize("Band.Retry.Btn", "Retry");
}

/// <summary>The one-line composite result under the tab content.</summary>
public sealed class FooterStrings
{
    public readonly string NoResult     = Loc.Localize("Footer.NoResult", "No composite result yet.");
    public readonly string PillFailed   = Loc.Localize("Footer.Pill.Failed", "failed");
    public readonly string PillOk       = Loc.Localize("Footer.Pill.Ok", "ok");
    public readonly string UnknownError = Loc.Localize("Footer.UnknownError", "unknown error");

    public readonly string SecondsAgoFmt = Loc.Localize("Footer.SecondsAgo.Fmt", "{0}s ago");
    public readonly string MinutesAgoFmt = Loc.Localize("Footer.MinutesAgo.Fmt", "{0}m ago");

    /// <summary>
    /// Phrased so no count is ever grammatically attached to a noun: English used to inflect this inline
    /// ("1 texture" / "2 textures"), which has no correct translation into languages with three plural
    /// forms (Russian) or none at all (Japanese, Korean, Chinese). Labelling the counts instead is
    /// correct everywhere and costs nothing in English.
    /// </summary>
    public readonly string LastCompositeFmt = Loc.Localize("Footer.LastComposite.Fmt",
        "Last composite: {0}   textures patched: {1}   mods: {2}");
}

/// <summary>
/// The Parts tab: picking geometry out of a mod's model and putting it behind a toggle.
/// <para/>
/// The vocabulary matters here and is deliberately not the format's. A user does not know what a submesh is
/// and should not have to; they know there is a bow on the dress and they want to take it off. So the panel
/// says "part" throughout, numbers them the way a modder writes them (1.1, 1.2) because that is the one
/// notation the surrounding community already shares, and shows a picture of each.
/// </summary>
public sealed class PartsStrings
{
    public readonly string Intro = Loc.Localize("Parts.Intro",
        "Pick geometry out of a mod's model and give it an on/off switch. The switch is written into the " +
        "mod itself as an ordinary Penumbra option, so it keeps working with Proteus turned off.");

    public readonly string Mod   = Loc.Localize("Parts.Mod", "Mod");
    public readonly string Model = Loc.Localize("Parts.Model", "Model");

    public readonly string PickMod   = Loc.Localize("Parts.PickMod", "Choose a mod");
    public readonly string PickModel = Loc.Localize("Parts.PickModel", "Choose a model");

    public readonly string NoModels = Loc.Localize("Parts.NoModels",
        "This mod publishes no models, so there is no geometry to split up.");

    public readonly string Unreadable = Loc.Localize("Parts.Unreadable",
        "This model could not be read. Nothing has been changed.");

    public readonly string ClickTip = Loc.Localize("Parts.Click.Tip",
        "Click a piece of the model to switch it on or off. Drag to turn it, shift-drag to move it, scroll " +
        "to zoom.");

    /// <summary>Material and size beside a part's checkbox. {0} is a file name, {1} a triangle count.</summary>
    public readonly string RowFmt = Loc.Localize("Parts.Row.Fmt", "{0} · {1:N0} tris");

    public readonly string ShatteredFmt = Loc.Localize("Parts.Shattered.Fmt",
        "Part {0} falls into {1} separate pieces, which is more than can be listed. Click the model to pick " +
        "one, or switch the whole part.");

    public readonly string AlreadyGatedTip = Loc.Localize("Parts.AlreadyGated.Tip",
        "The mod's author already put this part behind one of its own switches, so it cannot take another.");

    /// <summary>Expander on a submesh row. {0} is how many separate pieces it holds.</summary>
    public readonly string ShowPiecesFmt = Loc.Localize("Parts.ShowPieces.Fmt", "{0} pieces ▾");

    public readonly string HidePiecesFmt = Loc.Localize("Parts.HidePieces.Fmt", "{0} pieces ▴");

    public readonly string SelectedFmt = Loc.Localize("Parts.Selected.Fmt", "{0} part(s) ticked");

    public readonly string BudgetFmt = Loc.Localize("Parts.Budget.Fmt", "{0} of 10 switches left on this model");

    public readonly string NoBudget = Loc.Localize("Parts.NoBudget",
        "This model has no switch slots left. The game gives each item ten, and this one's author has " +
        "used them all.");

    public readonly string ToggleName = Loc.Localize("Parts.ToggleName", "Name") + "###partsToggleName";

    public readonly string AddBtn = Loc.Localize("Parts.Add.Btn", "Make a switch from the ticked parts");

    public readonly string NeedName  = Loc.Localize("Parts.NeedName", "Give the switch a name first.");
    public readonly string NeedParts = Loc.Localize("Parts.NeedParts", "Tick the parts this switch should hide.");

    public readonly string RemoveBtn = Loc.Localize("Parts.Remove.Btn", "Remove");

    public readonly string PendingHeader = Loc.Localize("Parts.Pending.Header", "Switches to write");

    public readonly string PendingFmt = Loc.Localize("Parts.Pending.Fmt", "{0} — {1}");

    public readonly string NotWrittenYet = Loc.Localize("Parts.NotWrittenYet",
        "Nothing has been written to the mod yet.");

    public readonly string WriteBtn = Loc.Localize("Parts.Write.Btn", "Write the switches into the mod");

    public readonly string WriteTip = Loc.Localize("Parts.Write.Tip",
        "Edits the mod's model and adds a Penumbra option group to it. The original model is kept, so this " +
        "can be undone.");

    public readonly string WrittenFmt = Loc.Localize("Parts.Written.Fmt",
        "Done. {0} switch(es) are now in this mod's own Penumbra settings, under \"{1}\".");

    public readonly string SkippedFmt = Loc.Localize("Parts.Skipped.Fmt",
        "{0} other model file(s) for this item were left alone, because their parts are arranged " +
        "differently and the same edit would land on the wrong geometry.");

    public readonly string ExistingHeader = Loc.Localize("Parts.Existing.Header", "Already added by Proteus");

    public readonly string RevertBtn = Loc.Localize("Parts.Revert.Btn", "Undo — restore the original models");

    public readonly string RevertedFmt = Loc.Localize("Parts.Reverted.Fmt",
        "Undone. {0} model file(s) restored, and the option group removed.");
}
