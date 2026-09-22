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
    public static LuminisStrings  Luminis  { get; private set; } = new();
    public static EmissiveStrings Emissive { get; private set; } = new();
    public static EyeStrings      Eye      { get; private set; } = new();
    public static ExportStrings   Export   { get; private set; } = new();
    public static ModsListStrings ModsList { get; private set; } = new();
    public static ColorPanelStrings ColorPanel { get; private set; } = new();
    public static ColorsStrings     Colors     { get; private set; } = new();
    public static PartsStrings      Parts      { get; private set; } = new();
    public static PresetsStrings    Presets    { get; private set; } = new();
    public static HatCompatStrings  HatCompat  { get; private set; } = new();

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
        Luminis  = new LuminisStrings();
        Emissive = new EmissiveStrings();
        Eye      = new EyeStrings();
        Export   = new ExportStrings();
        ModsList = new ModsListStrings();
        ColorPanel = new ColorPanelStrings();
        Colors     = new ColorsStrings();
        Parts      = new PartsStrings();
        Presets    = new PresetsStrings();
        HatCompat  = new HatCompatStrings();
    }
}

/// <summary>The hat-compatibility panel.</summary>
public sealed class HatCompatStrings
{
    public readonly string AutoFit = Loc.Localize(
        "HatCompat.AutoFit.Label", "Make hairstyles fit under hats") + "###hatCompatAuto";

    public readonly string AutoFitTip = Loc.Localize("HatCompat.AutoFit.Tip",
        "Most modded hair has no hat support, so a hat worn over it goes straight\n" +
        "through. When on, Proteus waits until you wear a hat, then presses the hair\n" +
        "that the hat would cover flat against your head.\n\n" +
        "This edits the hair mod's own files, so it keeps working with Proteus turned\n" +
        "off and travels with the mod if you export it. Originals are kept and the\n" +
        "change can be undone here.");

    public readonly string HidePonytails = Loc.Localize(
        "HatCompat.HidePonytails.Label", "Hide ponytails") + "###hatCompatHideTails";

    public readonly string HidePonytailsTip = Loc.Localize("HatCompat.HidePonytails.Tip",
        "Also make the parts that no hat could cover — ponytails, side tails, a long\n" +
        "fall at the back — disappear while a hat is worn, instead of leaving them\n" +
        "hanging out from under it.\n\n" +
        "Off by default. Pressing hair is invisible when it overshoots, because the hat\n" +
        "covers it; hiding is all or nothing, so a part judged wrongly simply vanishes.");

    public readonly string Working = Loc.Localize("HatCompat.Working", "Looking at this hairstyle...");

    public readonly string NoModdedHair = Loc.Localize("HatCompat.NoModdedHair",
        "You are wearing hair that came with the game, which already works under hats. "
      + "This has something to do when you wear a hair mod.");

    public readonly string WearingFmt = Loc.Localize("HatCompat.Wearing.Fmt",
        "Hairstyle {0}, from {1}.");

    public readonly string AuthorDidIt = Loc.Localize("HatCompat.AuthorDidIt",
        "This hairstyle already supports hats — its author added it. Nothing to do.");

    public readonly string AlreadyPatched = Loc.Localize("HatCompat.AlreadyPatched",
        "Proteus has already made this hairstyle fit under hats.");

    public readonly string InheritedTag = Loc.Localize("HatCompat.InheritedTag",
        "This hairstyle came with hat support, but it hides far more hair than a hat covers — most "
      + "likely carried over from the hairstyle it was built from and never fitted to this one. Proteus "
      + "can measure it again and replace it.");

    public readonly string InheritedTagTip = Loc.Localize("HatCompat.InheritedTag.Tip",
        "Only the part that hides hair is replaced. The hairstyle keeps its own\n" +
        "flattening, so the worst this can do is leave a hat clipping through hair,\n" +
        "instead of hair disappearing that no hat would have covered.\n\n" +
        "Undo puts back exactly what the hairstyle came with.");

    public readonly string PressFmt = Loc.Localize("HatCompat.Press.Fmt",
        "{0} points of hair would be pressed against your head, by {1:F1} cm on average and {2:F1} cm at most.");

    public readonly string TooWeldedFmt = Loc.Localize("HatCompat.TooWelded.Fmt",
        "{0} points cannot be moved: this hairstyle is welded into pieces too large for the game's shape "
      + "format to address. They will keep their shape, so a hat may still clip there.");

    public readonly string WillHideFmt = Loc.Localize("HatCompat.WillHide.Fmt",
        "{0} strand(s) hang too far off your head to fit under a hat and will be hidden while one is worn.");

    public readonly string NothingToHide = Loc.Localize("HatCompat.NothingToHide",
        "Every part of this hairstyle can be pressed under a hat, so none of it needs hiding.");

    public readonly string WaitingForHat = Loc.Localize("HatCompat.WaitingForHat",
        "Proteus will fit this hairstyle when you put on a hat. Or fit it now:");

    public readonly string Apply = Loc.Localize("HatCompat.Apply.Btn", "Make it fit") + "###hatCompatApply";

    public readonly string ApplyTip = Loc.Localize("HatCompat.Apply.Tip",
        "Writes into the hair mod's own folder. The original files are copied to\n"
      + "Proteus/hatcompat-backup/ inside that mod first, and this can be undone.");

    public readonly string Undo = Loc.Localize("HatCompat.Undo.Btn", "Undo") + "###hatCompatUndo";

    public readonly string UndoAllFmt = Loc.Localize("HatCompat.UndoAll.Fmt",
        "Undo all {0} of this mod's hairstyles") + "###hatCompatUndoAll";

    public readonly string UndoAllTip = Loc.Localize("HatCompat.UndoAll.Tip",
        "Undo puts back the hairstyle you are wearing. A hair mod usually ships one\n" +
        "model per race, so a hairstyle you fitted as another race is not the one you\n" +
        "have on now, and Undo cannot reach it.\n\n" +
        "This puts back every hairstyle Proteus has changed anywhere in this mod.");

    public readonly string Undone = Loc.Localize("HatCompat.Undone",
        "The hairstyle has been put back exactly as its author made it.");

    public readonly string Unreadable = Loc.Localize("HatCompat.Unreadable",
        "This hairstyle's model could not be read, so Proteus cannot change it.");

    public readonly string NoHead = Loc.Localize("HatCompat.NoHead",
        "Proteus could not work out where your head is, so it has left this hairstyle alone. It reads "
      + "your character's face to know where a hat sits, and without that a hat line would be a guess.");
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
    public readonly string Parts    = Loc.Localize("Tab.Parts", "Studio");
    public readonly string Export   = Loc.Localize("Tab.Export", "Export");
    public readonly string Settings = Loc.Localize("Tab.Settings", "Settings");
}

public sealed class ModsStrings
{
    // Sort headers pass their id to SortableHeader separately, so these stay plain.
    public readonly string ColOn       = Loc.Localize("Mods.Col.On", "On");
    public readonly string ColMod      = Loc.Localize("Mods.Col.Mod", "Mod");
    public readonly string ColPriority = Loc.Localize("Mods.Col.Priority", "Pri");

    // Drawn with a bare ImGui.TableHeader, which takes the label as its id — hence the fused "###". Done
    // here rather than at the call site so the concatenation happens once per language instead of once
    // per frame.
    public readonly string ColColors   = Loc.Localize("Mods.Col.Colors", "Colors") + "###modColors";

    // The per-mod AO/Skindent combo's label in the colour panel. Keeps the key it had as a Mods column
    // header so the existing translations carry over.
    public readonly string Skindent = Loc.Localize("Mods.Col.Skindent", "Skindent");

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

    // ── "this mod is on and painting nothing" ────────────────────────────────────────────────────────
    // The faces of InertModDiagnosis.InertReason. The LOG says the same things in English literals, on
    // purpose — a log is evidence, and evidence that changes language cannot be searched or compared.
    // See InertModDiagnosis.EnglishInert, which must be kept saying the same facts as these.
    //
    // Unlike the rest of this class these are wrapped tooltip and panel text, not column labels: they are
    // allowed to be sentences.

    /// <summary>{0} is how many option groups the mod has; {1} is their names, comma-joined, in the mod's
    /// own order — Penumbra group names, which are the author's and are never translated.</summary>
    public readonly string InertNothingTickedFmt = Loc.Localize("Mods.Inert.NothingTicked.Fmt",
        "Nothing is ticked in Penumbra. This mod's {0} option group(s) — {1} — are all empty, so it " +
        "paints nothing. Open it in Penumbra and tick an option in each.");

    /// <summary>{0} is the comma-joined names of groups the mod's Proteus data expects but Penumbra has
    /// not got. Author-facing: the user cannot fix this one.</summary>
    public readonly string InertGroupsMissingFmt = Loc.Localize("Mods.Inert.GroupsMissing.Fmt",
        "This mod's Proteus data names the option group(s) {0}, which Penumbra's copy of the mod hasn't " +
        "got — renamed or dropped when it was re-exported. Only its author can fix that.");

    /// <summary>{0} is what the mod paints and {1} what the character is, both as "body · Race F" — e.g.
    /// "bibo · Midlander F, Viera F". Race names come from ModelRace and are not translated.</summary>
    public readonly string InertWrongRaceFmt = Loc.Localize("Mods.Inert.WrongRace.Fmt",
        "This mod paints {0}, and you are {1}. Nothing it ships fits the body you are wearing.");

    /// <summary>{0} is the Penumbra group name for masks — literally "Masks", the author's own group
    /// name, which arrives already untranslated.</summary>
    public readonly string InertMaskNeedsShellFmt = Loc.Localize("Mods.Inert.MaskNeedsShell.Fmt",
        "Its masks render as gear, which needs a mask to build the garment from — and nothing is ticked " +
        "in its \"{0}\" group in Penumbra.");

    public readonly string InertNothingReached = Loc.Localize("Mods.Inert.NothingReached",
        "Its ticked options resolved, but none of them reached a surface this character has loaded.");

    public readonly string InertSettingsUnreadable = Loc.Localize("Mods.Inert.SettingsUnreadable",
        "Penumbra didn't answer when Proteus asked which of this mod's options are on.");
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

    public readonly string RestoreCharacter = Loc.Localize(
        "Bindings.RestoreCharacter.Label", "Restore every mod on the character") + "###bindRestoreCharacter";

    public readonly string RestoreCharacterTip = Loc.Localize("Bindings.RestoreCharacter.Tip",
        "When on, applying a bound design also restores every mod that was on the character\n" +
        "when it was saved (enable / priority / options), switches off mods on the character\n" +
        "that weren't, and raises the design's mods above anything they conflict with.\n\n" +
        "Mods that aren't Proteus mods are only held that way with Penumbra temporary settings:\n" +
        "your collection isn't changed, and reverting or applying an unbound design puts them back.\n" +
        "Penumbra's banner on a held mod can drop that one hold, if you want just it back.\n\n" +
        "When off, only Proteus mods are restored. Designs are captured either way, so\n" +
        "turning this on works for designs saved while it was off.");

    public readonly string FollowAutomation = Loc.Localize(
        "Bindings.FollowAutomation.Label", "Follow Glamourer automation (gearset / job changes)") + "###bindAutomation";

    public readonly string FollowAutomationTip = Loc.Localize("Bindings.FollowAutomation.Tip",
        "Glamourer reports nothing when automation applies a design on a gearset\n" +
        "or job change, so Proteus infers it from the signals that do arrive.\n\n" +
        "Only ever restores a binding — it never clears one.\n" +
        "The one redraw Proteus itself causes is discounted, so its own work\n" +
        "can't be mistaken for an automation apply.\n\n" +
        "Needs \"Bind Proteus state to Glamourer designs\" above.");

    public readonly string UnequipUnset = Loc.Localize(
        "Bindings.UnequipUnset.Label", "Unequip slots the design leaves unset") + "###bindUnequipUnset";

    public readonly string UnequipUnsetTip = Loc.Localize("Bindings.UnequipUnset.Tip",
        "When you apply a bound design, every gear and glasses slot the design doesn't set\n" +
        "is emptied, and imported Proteus packs it didn't capture are switched off, so pieces\n" +
        "from the previous look don't carry over.\n\n" +
        "Not on automation applies, which stack several designs. Weapons and the slots\n" +
        "Proteus is using for a shell are left alone.");

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

    public readonly string ApplyCharacterTip = Loc.Localize("Bindings.ApplyCharacter.Tip",
        "Restore this design's look now, without going through Glamourer: enable / priority /\n" +
        "options for every mod it captured, plus Proteus colours.\n\n" +
        "Mods on the character that are NOT in the binding are switched off, and bound mods are\n" +
        "raised above anything they conflict with, so this replaces the current look.");

    public readonly string UpdateTip = Loc.Localize("Bindings.Update.Tip",
        "Snapshot every mod on the character (enable / priority / options) and the current\n" +
        "Proteus colours into this binding. Manual edits only persist when you click this.");

    public readonly string ModCountFmt = Loc.Localize("Bindings.ModCount.Fmt", "{0} mods");
    public readonly string ProteusOnly = Loc.Localize("Bindings.ProteusOnly", "Proteus mods only");

    public readonly string ProteusOnlyTip = Loc.Localize("Bindings.ProteusOnly.Tip",
        "Captured before bindings recorded the whole character, so applying it only\n" +
        "restores Proteus mods. Apply this design, then press Update.");

    public readonly string ModListHeader      = Loc.Localize("Bindings.ModList.Header", "Mods this design restores:");
    public readonly string ModListOff         = Loc.Localize("Bindings.ModList.Off", "off");
    public readonly string ModListMissing     = Loc.Localize("Bindings.ModList.Missing", "not installed");
    public readonly string ModListPriorityFmt = Loc.Localize("Bindings.ModList.Priority.Fmt", "priority {0}");
    public readonly string ModListMoreFmt     = Loc.Localize("Bindings.ModList.More.Fmt", "…and {0} more");

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

    public readonly string WholeSkin = Loc.Localize("Create.WholeSkin.Label",
        "These textures are the whole skin") + "###createWholeSkin";

    public readonly string WholeSkinTip = Loc.Localize("Create.WholeSkin.Tip",
        "Tick this when you're converting a full skin mod, not painting something onto skin.\n" +
        "Proteus ticks it for you when the textures look like one; your own answer always wins.\n" +
        "Two things follow. The normal REPLACES the one already on the material instead of\n" +
        "stacking onto it — otherwise it lands on top of the same map underneath, every pore\n" +
        "and crease is applied twice, and the body reads flat against the face with a hard line\n" +
        "at the neck seam. And skin-tint suppression goes off, so the wearer's skin tone comes\n" +
        "through: it is there to stop fabric being re-tinted, and this art IS the skin.");

    public readonly string FaceAsymmetric = Loc.Localize("Create.FaceAsymmetric.Label",
        "This face texture is asymmetric") + "###createFaceAsymmetric";

    public readonly string FaceAsymmetricTip = Loc.Localize("Create.FaceAsymmetric.Tip",
        "For a face texture painted as TWO HALVES: the character's right side in the right half " +
        "of the image, their left side in the left half.\n" +
        "An ordinary face texture gives both cheeks the same pixels, so a mark on one side alone " +
        "cannot exist in it. Proteus moves the face's own UVs onto a doubled sheet instead, which " +
        "keeps the skin tone and every expression.\n" +
        "Ticked for you when the picked image is twice as wide as this face's own texture. Leave it " +
        "off for any ordinary face texture.");

    public readonly string Glow = Loc.Localize("Create.Glow.Label",
        "Make this art glow") + "###createGlow";

    public readonly string GlowTip = Loc.Localize("Create.Glow.Tip",
        "Light the art up. Its colour comes from the picture itself, per pixel — there is\n" +
        "nothing else to set.\n" +
        "Skin can't emit, so a glowing overlay renders on a second skin instead of being\n" +
        "painted into yours. Everything else about it works the same, and the Glow dial in\n" +
        "Colors adjusts it afterwards.");

    public readonly string GlowNeedsDiffuse = Loc.Localize("Create.Glow.NeedsDiffuse",
        "Pick a diffuse texture first — the glow takes its colour from the art.");

    public readonly string GlowNeedsSkin = Loc.Localize("Create.Glow.NeedsSkin",
        "Only skin and face targets can glow. A glowing overlay is drawn on a second skin cut\n" +
        "from your body, and there's nothing to cut one from on gear, an accessory or a weapon.");

    public readonly string GlowAlways = Loc.Localize("Create.Glow.Always", "Always glow");

    public readonly string GlowAlwaysTip = Loc.Localize("Create.Glow.Always.Tip",
        "The tattoo is there in daylight and glows day and night.\n" +
        "Kept gentle on purpose: this shader adds one flat colour across the whole tattoo\n" +
        "rather than following the picture, so a strong value bleaches the art in daylight.\n" +
        "For a brighter glow at night without that, raise Glow in Colors and set \"Fades in light\".");

    public readonly string GlowDarkOnly = Loc.Localize("Create.Glow.DarkOnly", "Only in the dark");

    public readonly string GlowDarkOnlyTip = Loc.Localize("Create.Glow.DarkOnly.Tip",
        "Nothing in daylight, glowing in an unlit room — what Atramentum Luminis tattoos did.\n" +
        "The art fades out as the light on you rises, and takes its own surface with it, so\n" +
        "there's skin and nothing else in the bright.\n" +
        "Needs \"React to the scene's light\" in Settings, which is on by default.");

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
    public readonly string SecHatCompat   = Loc.Localize("Settings.Section.HatCompat", "Hats");

    public readonly string SecHosting     = Loc.Localize("Settings.Section.Hosting", "Hosting");

    public readonly string SecLightResponse = Loc.Localize("Settings.Section.LightResponse", "Light-sensitive glow");

    public readonly string ReduceMotion = Loc.Localize("Settings.ReduceMotion", "Reduce motion") + "###reduceMotion";

    public readonly string ReduceMotionTip = Loc.Localize("Settings.ReduceMotion.Tip",
        "Hold the window still: hover effects land at once, and the background glow\n"
      + "and the pulse on active mods stop moving.");

    public readonly string AmbientBackground = Loc.Localize("Settings.AmbientBackground", "Background glow") + "###ambientBackground";

    public readonly string AmbientBackgroundTip = Loc.Localize("Settings.AmbientBackground.Tip",
        "The soft ember glow drifting behind this window's contents.");

    public readonly string LightResponseEnabled = Loc.Localize("Settings.LightResponse.Enabled",
        "React to the scene's light");

    public readonly string LightResponseEnabledTip = Loc.Localize("Settings.LightResponse.Enabled.Tip",
        "Let rows marked \"Fades in light\" dim as the light on you rises.\n"
      + "Off, every glow burns at its authored brightness everywhere, the way it did before.\n"
      + "Nothing is rebuilt either way — this reaches the character through the live material.");

    public readonly string LightResponseManual = Loc.Localize("Settings.LightResponse.Manual",
        "Set the light level by hand");

    public readonly string LightResponseManualTip = Loc.Localize("Settings.LightResponse.Manual.Tip",
        "Ignore the scene and use the slider instead.\n"
      + "The quickest way to see what a dark-only glow does without waiting for dusk,\n"
      + "and the right setting for gpose, where your own lighting rig is the point.");

    public readonly string LightResponseLevel = Loc.Localize("Settings.LightResponse.Level", "Light level");

    public readonly string LightResponseLevelTip = Loc.Localize("Settings.LightResponse.Level.Tip",
        "0 is pitch dark (dark-only glows at full brightness), 1 is full daylight (they vanish).");

    public readonly string LightResponseReadoutFmt = Loc.Localize("Settings.LightResponse.Readout.Fmt",
        "Reading {0:0.00} — sky {1:0.00}, lamps {2:0.00} ({3} of {4} reached)");

    /// <summary>The raw layout flags behind the sky term. Deliberately untranslated jargon on one line:
    /// it exists to be screenshotted when the reading disagrees with the room.</summary>
    public readonly string LightResponseSignalsFmt = Loc.Localize("Settings.LightResponse.Signals.Fmt",
        "outdoor={0}  indoor={1}  envspace={2}  sky={3}");

    public readonly string SecDiagnostics = Loc.Localize("Settings.Section.Diagnostics", "Diagnostics");

    // ── general ─────────────────────────────────────────────────────────────────────────────────────
    public readonly string Enabled = Loc.Localize("Settings.General.Enabled.Label", "Enabled");

    public readonly string EnabledTip = Loc.Localize("Settings.General.Enabled.Tip",
        "Turning this off clears Proteus' output, redraws you without it,\n" +
        "and disables the managed \"Proteus\" mod in Penumbra.");

    public readonly string AutoRedraw =
        Loc.Localize("Settings.General.AutoRedraw.Label", "Auto redraw") + "###autoRedraw";

    public readonly string AutoRedrawTip = Loc.Localize("Settings.General.AutoRedraw.Tip",
        "Let Proteus keep up with the world on its own - recompositing after zoning, gear\n" +
        "changes and redraws, then reloading your character so you can see the result.\n\n" +
        "Turn it off to make Proteus mostly manual: it won't composite or reload you until\n" +
        "you change something yourself. Your current look stays on either way, and edits\n" +
        "you make still apply - you just won't see them until something redraws you:\n" +
        "zoning, changing gear, or Penumbra's Redraw button.\n\n" +
        "One exception while it's off: if you're wearing a gear layer, changing gear still\n" +
        "recomposites, so the item hosting that layer doesn't get stranded on something\n" +
        "you took off. You still won't be redrawn for it.\n\n" +
        "Only covers what Proteus starts. Glamourer's \"Auto-Reload Gear\" reloads you\n" +
        "whenever any mod's settings change, independently of this.");

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
        "its exact row values. Off = uncompressed (byte-identical to before).\n\n" +
        "Costs processor time on EVERY refresh, and it is the slowest thing Proteus does.\n" +
        "On an older or low-core PC it can add a minute or more per change, which is long\n" +
        "enough that a new change restarts the work before it finishes and your look stops\n" +
        "appearing at all. Leave this off unless you are short of video memory, and turn it\n" +
        "off first if nothing is being drawn.");

    public readonly string CompressionRefusedFmt = Loc.Localize("Settings.Output.CompressionRefused.Fmt",
        "Not in use: this PC compresses too slowly ({0:F0} ms per megapixel), and doing it would\n" +
        "take long enough that your look would stop being drawn. Textures are being baked\n" +
        "uncompressed instead. Untick the box to hide this.");

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

    public readonly string CopyLogs =
        Loc.Localize("Settings.Diag.CopyLogs.Btn", "Copy Logs") + "###copyLogs";

    public readonly string CopyLogsBusy =
        Loc.Localize("Settings.Diag.CopyLogs.Busy", "Refreshing and capturing…") + "###copyLogs";

    public readonly string CopyLogsTip = Loc.Localize("Settings.Diag.CopyLogs.Tip",
        "Run a full refresh and save everything Proteus logs while it runs to a text\n" +
        "file on your desktop. Attach that file when you report a problem.");

    public readonly string CopyLogsSaved = Loc.Localize("Settings.Diag.CopyLogs.Saved", "Saved:");

    public readonly string CopyLogsOpenTip = Loc.Localize("Settings.Diag.CopyLogs.OpenTip", "Click to open the file.");

    public readonly string CopyLogsShowInFolder =
        Loc.Localize("Settings.Diag.CopyLogs.ShowInFolder", "Show in folder") + "###copyLogsFolder";

    public readonly string CopyLogsFailedFmt =
        Loc.Localize("Settings.Diag.CopyLogs.Failed.Fmt", "Could not save the log: {0}");

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

    public readonly string RedundantMeshes =
        Loc.Localize("Settings.Output.RedundantMeshes.Label", "Hide redundant body meshes")
        + "###hideRedundantMeshes";

    public readonly string RedundantMeshesTip = Loc.Localize("Settings.Output.RedundantMeshes.Tip",
        "Skip skin the gear \"second skin\" would otherwise draw twice. Some bodies cover the\n" +
        "same patch of themselves with two pieces of geometry — a small reinforcing ring at a\n" +
        "joint (wrist/ankle/…) that the neighbouring part already draws, or a spare copy of a\n" +
        "region sitting inside the one beside it. On a sheer overlay the overlap doubles up and\n" +
        "shows as a more-opaque seam, or as a stocking drawn twice down the calf.\n\n" +
        "Safe on any body: geometry is skipped only where something else demonstrably draws it.\n" +
        "Turn it off if a patch of skin is missing from the shell.");
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
        "Import a regular mod (.pmp, or the meta.json of one already installed in Penumbra). Wear parts of it "
      + "without using a gear slot, and add advanced colour table features.");

    public readonly string ReadFailedFmt = Loc.Localize("Content.ReadFailed.Fmt",
        "Couldn't read that pack: {0}");

    /// <summary>A .json picked that isn't an installed mod's manifest: the filter can only match by extension.</summary>
    public readonly string NotAManifest = Loc.Localize("Content.NotAManifest",
        "To import a mod installed in Penumbra, pick the meta.json in its folder.");

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

    public readonly string FollowsHairColor = Loc.Localize("Content.FollowsHairColor",
        "This material uses the hair shader, so the piece takes your character's hair colour and highlights "
      + "and carries no colour table — nothing you change below will reach it. Change your hair colour to "
      + "recolour it.");

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

/// <summary>The Atramentum Luminis (<c>.ttmp2</c>) half of the Import tab.</summary>
public sealed class LuminisStrings
{
    /// <summary>
    /// The third line of the Import tab's intro, in the same voice as the other two: what you get, not how.
    /// Names the old mod outright, because that name is the only reason anyone has one of these files —
    /// the packs themselves say "requires Atramentum Luminis" and nothing else identifies them.
    /// </summary>
    public readonly string Intro = Loc.Localize("Luminis.Intro",
        "Import an Atramentum Luminis glow tattoo (.ttmp2). Its glow becomes a Proteus overlay you can "
      + "recolour and dim, with no shader mod needed.");

    public readonly string ReadFailedFmt = Loc.Localize("Luminis.ReadFailed.Fmt",
        "Couldn't read that modpack: {0}");

    public readonly string TextureCountFmt = Loc.Localize("Luminis.TextureCount.Fmt",
        "Textures: {0} of {1}");

    /// <summary>How much of the sheet carries a glow mask — the number that says whether this really is an
    /// Atramentum Luminis pack, so it goes in the table rather than in a tooltip.</summary>
    public readonly string GlowFmt = Loc.Localize("Luminis.Glow.Fmt", "{0:P1} glows");

    public readonly string SizeFmt = Loc.Localize("Luminis.Size.Fmt", "{0}×{1}");

    /// <summary>Several manifest paths over one picture, which is the normal shape of these packs.</summary>
    public readonly string AliasesFmt = Loc.Localize("Luminis.Aliases.Fmt", "{0} paths");

    public readonly string Skipped = Loc.Localize("Luminis.Skipped", "skipped");

    public readonly string SkippedReasonFmt = Loc.Localize("Luminis.SkippedReason.Fmt", "{0}\nSkipped: {1}");

    public readonly string PathsFmt = Loc.Localize("Luminis.Paths.Fmt", "Imported once, for:\n{0}");

    /// <summary>Said before the button. The author's body texture is the surprising half of this import —
    /// it is a whole body texture, not a tattoo — so what it is, and that it comes on, are stated up
    /// front. The key was renamed when the option stopped starting off, so that a lagging translation
    /// falls back to English rather than telling the user the opposite of what happens.</summary>
    public readonly string SkinIncludedFmt = Loc.Localize("Luminis.SkinIncluded.Fmt",
        "Both halves go on: the glow, and the author's own body texture beneath it, under \"{0}\" in "
      + "Penumbra. The body texture carries the parts of the tattoo that don't glow, and it keeps your own "
      + "skin tone rather than the author's — untick it there if you only want the glow.");

    public readonly string BodyTarget = Loc.Localize("Luminis.BodyTarget.Label", "Body") + "###luminisBody";

    public readonly string BodyTargetTip = Loc.Localize("Luminis.BodyTarget.Tip",
        "Which body material the art is painted onto. Proteus picks this from the pack when it recognises "
      + "the body, and from the one you're wearing when it doesn't — change it if you know the pack was "
      + "made for a different one.");

    public readonly string BodyFromPackFmt = Loc.Localize("Luminis.BodyFromPack.Fmt",
        "The pack says it is painted for {0}, so Proteus will resize it onto whichever body you wear.");

    /// <summary>
    /// Stated plainly rather than in the warning colour. Proteus has never had a race or sex filter — it
    /// is a standing property of every overlay, not something this pack did — and amber on every import is
    /// the cried-wolf problem <see cref="ContentStrings.BodyOnly"/> exists to avoid.
    /// </summary>
    public readonly string NoRaceFilter = Loc.Localize("Luminis.NoRaceFilter",
        "Proteus has no race or sex filter: this paints any character wearing a body with the same "
      + "material. Turn the mod off in Penumbra for characters it wasn't painted for.");

    public readonly string NothingUsable = Loc.Localize("Luminis.NothingUsable",
        "No texture in this modpack carries an Atramentum Luminis glow mask.");
}

/// <summary>
/// The emissive-skin <c>.pmp</c> half of the Import tab — a glowing tattoo built for one of the community
/// skin shaders.
/// <para/>
/// Its own holder rather than shared keys with <see cref="LuminisStrings"/>, even where a sentence is word
/// for word the same. The two panels describe different formats and their wording will drift as each is
/// worked on; a shared key would silently carry an edit made for one into the other, which is exactly the
/// class of change nobody reviews.
/// </summary>
public sealed class EmissiveStrings
{
    /// <summary>
    /// The fifth bullet of the Import tab's intro, in the same voice as the others: what you get, not how.
    /// Says "no shader mod" because needing one is the whole reason these packs are hard to wear — the art
    /// is inert without a replaced skin.shpk installed alongside it.
    /// </summary>
    public readonly string Intro = Loc.Localize("Emissive.Intro",
        "Import an emissive skin pack (.pmp of glow art with no models). Its glow becomes a Proteus overlay "
      + "you can recolour and dim, with no shader mod needed.");

    public readonly string ReadFailedFmt = Loc.Localize("Emissive.ReadFailed.Fmt",
        "Couldn't read that pack: {0}");

    /// <summary>
    /// Shown between the pick and the preview, which for this one format are different frames. These masks
    /// are full body sheets — the reference pack's is 8192² and takes a second to decode — so the read runs
    /// on the pool and the tab has to say what it is waiting for rather than fall through to "pick a pack".
    /// </summary>
    public readonly string Reading = Loc.Localize("Emissive.Reading",
        "Reading this pack's textures…");

    public readonly string TextureCountFmt = Loc.Localize("Emissive.TextureCount.Fmt",
        "Textures: {0} of {1}");

    /// <summary>How much of the sheet the mask marks out — the number that says whether this really is glow
    /// art, so it goes in the table rather than in a tooltip.</summary>
    public readonly string GlowFmt = Loc.Localize("Emissive.Glow.Fmt", "{0:P1} glows");

    public readonly string SizeFmt = Loc.Localize("Emissive.Size.Fmt", "{0}×{1}");

    /// <summary>Several manifest paths over one picture, which some of these packs do.</summary>
    public readonly string AliasesFmt = Loc.Localize("Emissive.Aliases.Fmt", "{0} paths");

    public readonly string Skipped = Loc.Localize("Emissive.Skipped", "skipped");

    public readonly string SkippedReasonFmt = Loc.Localize("Emissive.SkippedReason.Fmt", "{0}\nSkipped: {1}");

    public readonly string PathsFmt = Loc.Localize("Emissive.Paths.Fmt", "Imported once, for:\n{0}");

    public readonly string BodyTarget = Loc.Localize("Emissive.BodyTarget.Label", "Body") + "###emissiveBody";

    public readonly string BodyTargetTip = Loc.Localize("Emissive.BodyTarget.Tip",
        "Which body material the art is painted onto. Proteus picks this from the pack when it recognises "
      + "the body, and from the one you're wearing when it doesn't — change it if you know the pack was "
      + "made for a different one.");

    public readonly string BodyFromPackFmt = Loc.Localize("Emissive.BodyFromPack.Fmt",
        "The pack says it is painted for {0}, so Proteus will resize it onto whichever body you wear.");

    /// <summary>
    /// Said before the button, because it is the surprising half of this import: the pack's own redirects
    /// are dropped. They are body materials rewired to name an emissive sampler that only a replaced
    /// skin.shpk has, and republishing them would put the imported mod into a fight with Proteus over the
    /// very material it composites into. Plain text, not the warning colour — this is true of a CORRECT
    /// import.
    /// </summary>
    public readonly string MaterialsIgnored = Loc.Localize("Emissive.MaterialsIgnored",
        "Only the glow art is imported. The pack's own body materials are left behind: they only work with "
      + "a replaced skin shader, and Proteus paints the glow onto whatever skin you already wear.");

    /// <summary>Stated plainly rather than in the warning colour, for the reason
    /// <see cref="LuminisStrings.NoRaceFilter"/> gives.</summary>
    public readonly string NoRaceFilter = Loc.Localize("Emissive.NoRaceFilter",
        "Proteus has no race or sex filter: this paints any character wearing a body with the same "
      + "material. Turn the mod off in Penumbra for characters it wasn't painted for.");

    public readonly string NothingUsable = Loc.Localize("Emissive.NothingUsable",
        "No texture in this pack carries a glow mask.");
}

/// <summary>The loose eye-texture pack (<c>.zip</c>) half of the Import tab.</summary>
public sealed class EyeStrings
{
    /// <summary>
    /// The fourth bullet of the Import tab's intro, in the same voice as the other three: what you get.
    /// Says "animated" because that is the only part Penumbra cannot already do on its own.
    /// </summary>
    public readonly string Intro = Loc.Localize("Eye.Intro",
        "Import an eye texture pack (.zip of loose images). Its eyes go in as normal, and the shape its "
      + "mask marks out gets an animated glow you can recolour and dim.");

    public readonly string ReadFailedFmt = Loc.Localize("Eye.ReadFailed.Fmt",
        "Couldn't read that archive: {0}");

    public readonly string TextureCountFmt = Loc.Localize("Eye.TextureCount.Fmt",
        "Textures: {0} of {1}");

    public readonly string Skipped = Loc.Localize("Eye.Skipped", "skipped");

    public readonly string SkippedReasonFmt = Loc.Localize("Eye.SkippedReason.Fmt", "{0}\nSkipped: {1}");

    /// <summary>Said before the Import button: what the glow will cover, and where to switch it off.</summary>
    public readonly string GlowFmt = Loc.Localize("Eye.Glow.Fmt",
        "The mask marks {0:P1} of the sheet as glowing, and that shape gets the animation — on {1} iris "
      + "material(s), so it follows you across races and faces. Switch it off under \"{2}\" in Penumbra.");

    public readonly string NoGlow = Loc.Localize("Eye.NoGlow",
        "No animated glow will be added — see below. The eye textures still import and work normally.");

    public readonly string FallbackIrises = Loc.Localize("Eye.FallbackIrises",
        "Proteus couldn't read the game's face list, so the glow will target a known-good pair of faces "
      + "only. Reopen this pack once you're in game to pick up every race.");

    public readonly string NothingUsable = Loc.Localize("Eye.NothingUsable",
        "Nothing in this archive looks like an eye texture.");

    public readonly string CutoutLabel = Loc.Localize("Eye.Cutout.Label", "Glow shape") + "###eyeCutout";

    public readonly string CutoutFalloff = Loc.Localize("Eye.Cutout.Falloff", "Artwork and its falloff");
    public readonly string CutoutArtwork = Loc.Localize("Eye.Cutout.Artwork", "Artwork only");

    /// <summary>Says why this is chosen here rather than tuned later — it is baked into the written art,
    /// and the Glow dial can only scale what survived it.</summary>
    public readonly string CutoutTip = Loc.Localize("Eye.Cutout.Tip",
        "How much of the mask's glow channel is cut into the shape that glows. Most packs mark the artwork "
      + "brightly and fade out around it; keeping that falloff glows softly beyond the artwork, dropping it "
      + "confines the glow to the shape itself.\n"
      + "Baked in at import, so pick it now — afterwards the Glow dial can only scale what's left.");
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

    /// <summary>Heading for the mod-wide geometry features, in their own section above the tab strip.
    /// "Geometry" rather than "Body" because what these change is the shape of the mesh, not which body
    /// the art is baked onto — that is Bodies, which stays per-tab in Advanced.</summary>
    public readonly string GeometrySection = Loc.Localize("Colors.Geometry.Section", "Geometry");

    /// <summary>Heading for the glow effect and Skindent, the section above Geometry.</summary>
    public readonly string EffectsSection = Loc.Localize("Colors.Effects.Section", "Effects");

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

    /// <summary>
    /// Its own section since the glow colour, amount and light response were pulled out of "Colours".
    /// NOT the same string as <see cref="GlowAmount"/>, which happens to render the same word in English —
    /// that one is the slider's own label and can be renamed without touching the heading. The field cannot
    /// be called <c>Glow</c> either: that is already the highlight button beside the Copy/Paste pair.
    /// </summary>
    public readonly string GlowSection = Loc.Localize("Colors.Section.Glow", "Glow");

    public readonly string GlowColour = Loc.Localize("Colors.GlowColour.Label", "Glow colour");

    public readonly string GlowColourTip = Loc.Localize("Colors.GlowColour.Tip",
        "Glow colour, independent of the diffuse — a glowing material usually\n" +
        "wants a DARK surface with a bright glow. Defaults to the diffuse.");

    public readonly string GlowAmount = Loc.Localize("Colors.GlowAmount.Label", "Glow");

    public readonly string GlowAmountTip = Loc.Localize("Colors.GlowAmount.Tip",
        "How brightly this row glows. 0 switches it off.\n" +
        "Under an animated glow this is the effect's brightness, and a high value\n" +
        "blows a colourful scroll map out to white. Around 25% is a good start.");

    /// <summary>
    /// Shown at the top of whichever of the two columns the art's index never lands in. Most overlays carry
    /// no index at all, and the shell then samples the fabricated (255, 255, 0) — row 16, column A — so
    /// column B is dead on every row and used to be drawn as though it were not.
    /// </summary>
    public readonly string SubRowUnused = Loc.Localize("Colors.SubRowUnused",
        "Nothing samples this column — the art's index picks the other one everywhere, "
      + "so edits here won't show.");

    public readonly string LightResponse = Loc.Localize("Colors.LightResponse.Label", "Fades in light");

    public readonly string LightResponseTip = Loc.Localize("Colors.LightResponse.Tip",
        "How much the scene's light takes this row's glow away.\n" +
        "0% glows the same everywhere. 100% is a dark-only glow: full brightness in\n" +
        "an unlit room, nothing at all under a midday sky or beside a lamp.\n" +
        "Set per row, so one half of a tattoo can be dark-only and the other always on.");

    public readonly string HideInLight = Loc.Localize("Colors.HideInLight.Label", "Hide in light");

    public readonly string HideInLightTip = Loc.Localize("Colors.HideInLight.Tip",
        "Let this row's opacity follow its glow, so where it has stopped glowing\n" +
        "there is nothing left but skin.\n" +
        "Without this a dark-only row still leaves its own colour behind — usually black —\n" +
        "so the art reads as a dark patch in daylight instead of vanishing into the skin.");

    public readonly string Opacity = Loc.Localize("Colors.Opacity.Label", "Opacity");

    public readonly string OpacityTip = Loc.Localize("Colors.Opacity.Tip",
        "Negative fades this row toward transparent; positive pushes it toward opaque.");

    public readonly string Blend = Loc.Localize("Colors.Blend.Label", "Blend");

    public readonly string BlendTip = Loc.Localize("Colors.Blend.Tip",
        "How this row's art combines with what is already on the skin.\n" +
        "\n" +
        "\"Paint\" lays it on top, which is what every layer has always done.\n" +
        "Anything else makes this row a PRINT: it colours the fabric this mod's\n" +
        "other layers painted, and is clipped to their shape — so a rainbow on a\n" +
        "fishnet colours the threads and leaves the holes as skin.\n" +
        "\n" +
        "A print only reaches its OWN mod's layers; it never touches another mod's\n" +
        "clothing. On bare skin it paints nothing at all, so a print selected on its\n" +
        "own shows nothing — there is nothing there to print on.\n" +
        "\n" +
        "Multiply can only darken, so it reads faintly on very dark fabric — use\n" +
        "Screen there instead. This row's colour still tints the art, and its\n" +
        "Opacity is the print's strength.");

    /// <summary>Display names for <see cref="RowBlend"/>, in declaration order.</summary>
    public readonly string[] BlendNames =
    [
        Loc.Localize("Colors.Blend.Paint",    "Paint"),
        Loc.Localize("Colors.Blend.Multiply", "Multiply"),
        Loc.Localize("Colors.Blend.Screen",   "Screen"),
        Loc.Localize("Colors.Blend.Overlay",  "Overlay"),
        Loc.Localize("Colors.Blend.Add",      "Add"),
        Loc.Localize("Colors.Blend.Replace",  "Replace"),
    ];

    public readonly string Physical    = Loc.Localize("Colors.Section.Physical", "Physical");
    public readonly string ClothSuffix = Loc.Localize("Colors.Section.Physical.ClothSuffix", "— Cloth");

    public readonly string Roughness = Loc.Localize("Colors.Roughness.Label", "Roughness");
    public readonly string Metalness = Loc.Localize("Colors.Metalness.Label", "Metalness");

    public readonly string SphereMap   = Loc.Localize("Colors.Section.SphereMap", "Sphere map");
    public readonly string SphereIndex = Loc.Localize("Colors.Sphere.Index.Label", "Index");
    public readonly string Intensity   = Loc.Localize("Colors.Sphere.Intensity.Label", "Intensity");

    public readonly string Tile         = Loc.Localize("Colors.Section.Tile", "Tile");
    public readonly string TilePattern  = Loc.Localize("Colors.Tile.Pattern.Label", "Pattern");
    public readonly string TileNone     = Loc.Localize("Colors.Tile.None", "None");
    public readonly string TileStrength = Loc.Localize("Colors.Tile.Strength.Label", "Strength");
    public readonly string TileScaleU   = Loc.Localize("Colors.Tile.ScaleU.Label", "Scale U");
    public readonly string TileScaleV   = Loc.Localize("Colors.Tile.ScaleV.Label", "Scale V");

    public readonly string TileTip = Loc.Localize("Colors.Tile.Tip",
        "A fabric weave tiled over this region — one of the game's own 64 patterns, so it costs no texture.\n"
      + "This is what makes a second skin read as cloth rather than skin; Proteus leaves it off by default\n"
      + "because a weave over bare skin looks like grain. Needs the gear shader, so picking one moves a skin\n"
      + "overlay onto a cloth shell.");

    public readonly string TileScaleTip = Loc.Localize("Colors.Tile.Scale.Tip",
        "How many times the weave repeats across the surface. Higher is finer; 16 is the game's own default.\n"
      + "U and V are the two directions of the texture, so setting them apart stretches the weave one way.");

    /// <summary>Deliberately the same wording as the global Settings slider: this is the same knob at a
    /// narrower scope, and calling it something else would read as a second, unrelated control.</summary>
    public readonly string SkinTint = Loc.Localize("Colors.SkinTint.Label", "Skin-tint suppression");

    public readonly string SkinTintTip = Loc.Localize("Colors.SkinTint.Tip",
        "How strongly this option resists the wearer's skin tone.\n"
      + "1.00 keeps the authored colour on any skin tone — right for fabric that covers the skin.\n"
      + "0.00 lets the skin tone through — right for a tattoo, a decal, or a body texture that IS skin.\n"
      + "The global slider in Settings multiplies this, so 0.00 here always wins.");

    /// <summary>Shown beside the "Rendering as" badge while Advanced is collapsed, so a tab that has been
    /// turned down doesn't look untouched. Only drawn when the value isn't the default.</summary>
    public readonly string SkinTintBadgeFmt = Loc.Localize("Colors.SkinTint.Badge.Fmt", "tint {0:F2}");

    public readonly string WholeSkin = Loc.Localize("Colors.WholeSkin.Label",
        "This overlay is the whole skin");

    public readonly string Asymmetric = Loc.Localize("Colors.Asymmetric.Label",
        "This art is asymmetric (left and right differ)");

    public readonly string AsymmetricTip = Loc.Localize("Colors.Asymmetric.Tip",
        "Turn this on when the two sides are meant to DIFFER — a tattoo on one arm, a scar on one\n"
      + "cheek, makeup that isn't mirrored. Leave it off for anything else, including ordinary skin.\n"
      + "A vanilla body and the vanilla face give both sides the same pixels, so art painted for\n"
      + "them is folded in half: one side is kept and mirrored across. That is invisible on a\n"
      + "symmetric design and ruins an asymmetric one. With this on, Proteus renders the overlay\n"
      + "through a layer of its own whose two sides read the two halves of your sheet, so both\n"
      + "sides survive.\n"
      + "It cannot be detected for you. Real skin is never symmetric — freckles and moles differ\n"
      + "left to right — so only you can say whether a difference is the point or just detail.");

    public readonly string ReinforcedToe = Loc.Localize("Colors.ReinforcedToe.Label",
        "Reinforced toe");

    public readonly string ReinforcedToeTip = Loc.Localize("Colors.ReinforcedToe.Tip",
        "Make the capped toe denser than the rest of the stocking, the way real hosiery\n"
      + "knits a heavier toe box — a darker panel with the toes still showing through it,\n"
      + "fading out at the edge rather than stopping at a line.\n"
      + "It can only take away transparency that is there. Where the stocking paints\n"
      + "nothing it does nothing, so it will never put fabric on a bare toe, and on an\n"
      + "already-opaque stocking you will see no change.\n"
      + "With \"Sharp alpha\" on, every pixel is either solid or gone, so the soft edge\n"
      + "becomes a hard one.");

    public readonly string ToeDensity = Loc.Localize("Colors.ToeDensity.Label",
        "Density");

    public readonly string ToeDensityTip = Loc.Localize("Colors.ToeDensity.Tip",
        "How much denser the toe is than the leg. 100% is completely solid; the usual\n"
      + "reinforced toe sits well below that.");

    public readonly string ReinforcedToeSavedNote = Loc.Localize("Colors.ReinforcedToe.SavedNote",
        "Saved to the mod — presets and designs don't capture this.");

    public readonly string BustBridge = Loc.Localize("Colors.BustBridge.Label",
        "Span the cleavage");

    public readonly string BustBridgeTip = Loc.Localize("Colors.BustBridge.Tip",
        "Turn this on for cloth over the chest.\n"
      + "Proteus builds a garment as a copy of the body, so by default it follows the body into\n"
      + "the cleavage — which is what makes it read as paint rather than fabric. Real cloth spans\n"
      + "the gap: a straight line between the furthest-forward point of each breast.\n"
      + "With this on, the fabric between the breasts is relaxed out to that line. Nothing is ever\n"
      + "pulled inward, so it cannot cut into the body, and the breasts themselves keep their shape.\n"
      + "Where a neckline has cut the cloth away between the cups there is nothing to span, and\n"
      + "this does nothing.");

    public readonly string BustBridgeStrength = Loc.Localize("Colors.BustBridge.Strength.Label",
        "Span amount");

    public readonly string BustBridgeStrengthTip = Loc.Localize("Colors.BustBridge.Strength.Tip",
        "How far the fabric relaxes toward the flat span. 1.00 spans it fully; lower values keep\n"
      + "more of the shape underneath.");

    public readonly string SmoothNipples = Loc.Localize("Colors.SmoothNipples.Label",
        "Smooth the nipple");

    public readonly string SmoothNipplesTip = Loc.Localize("Colors.SmoothNipples.Tip",
        "Turn this on for cloth over the chest.\n"
      + "Proteus builds a garment as a copy of the body, so it reproduces the nipple as a point —\n"
      + "which reads as body paint rather than fabric. With this on, the fabric over it is smoothed\n"
      + "into the surrounding curve.\n"
      + "NOT FINISHED YET: this smooths the garment, and the body underneath keeps its own shape, so\n"
      + "on a close fit it can still show through. Smoothing the skin to match is still to come.");

    public readonly string SmoothNipplesStrength = Loc.Localize("Colors.SmoothNipples.Strength.Label",
        "Smoothing amount");

    public readonly string SmoothNipplesStrengthTip = Loc.Localize("Colors.SmoothNipples.Strength.Tip",
        "How far the fabric is smoothed. 1.00 smooths it fully; lower values keep more of the\n"
      + "shape underneath.");

    public readonly string CleftBridge = Loc.Localize("Colors.CleftBridge.Label",
        "Span the buttocks");

    public readonly string CleftBridgeTip = Loc.Localize("Colors.CleftBridge.Tip",
        "Turn this on for cloth over the seat.\n"
      + "Proteus builds a garment as a copy of the body, so it follows the cleft all the way in —\n"
      + "which reads as painted on rather than worn. With this on, the fabric spans across it the\n"
      + "way real cloth does.\n"
      + "The same idea as spanning the cleavage, on a much deeper cleft, so start the amount low.");

    public readonly string CleftBridgeStrength = Loc.Localize("Colors.CleftBridge.Strength.Label",
        "Span amount");

    public readonly string CleftBridgeStrengthTip = Loc.Localize("Colors.CleftBridge.Strength.Tip",
        "How far the fabric relaxes toward the flat span. 1.00 lifts the cleft level with the\n"
      + "cheeks either side, which is usually too much — lower values keep more of it.");

    public readonly string SmoothFold = Loc.Localize("Colors.SmoothFold.Label",
        "Smooth between the legs");

    public readonly string SmoothFoldTip = Loc.Localize("Colors.SmoothFold.Tip",
        "Turn this on for cloth over the crotch.\n"
      + "Proteus builds a garment as a copy of the body, so it reproduces the fold between the legs —\n"
      + "which reads as body paint rather than fabric. With this on, the skin there is smoothed out to\n"
      + "the surrounding curve first, so the garment cut from it comes out smooth too.\n"
      + "This changes the body, so it applies to every garment worn at once.");

    public readonly string SmoothFoldStrength = Loc.Localize("Colors.SmoothFold.Strength.Label",
        "Smoothing amount");

    public readonly string SmoothFoldStrengthTip = Loc.Localize("Colors.SmoothFold.Strength.Tip",
        "How far the fold is smoothed. 1.00 flattens it into the surrounding curve; lower values keep\n"
      + "more of it.");

    public readonly string WholeSkinTip = Loc.Localize("Colors.WholeSkin.Tip",
        "Turn this on for a converted skin mod — art that IS the skin, not something laid on it.\n"
      + "It moves two settings together, and they only work as a pair.\n"
      + "The normal REPLACES the one already on the material instead of being added to it. Off is\n"
      + "right for a strap or a tattoo, which should keep the skin's pores underneath; for a skin\n"
      + "the map underneath is that same skin, so adding lands every pore twice and the body goes\n"
      + "flat against an untouched face, with a hard line at the neck seam.\n"
      + "And skin-tint suppression drops to 0.00, so the wearer's tone comes through — it exists\n"
      + "to stop fabric being re-tinted, which is backwards for skin. Turning this off restores\n"
      + "both. You can still set the slider by hand afterwards.");
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

    // Vanilla overlay. "gen2", "bibo", "gen3" and "Eve" are body-mod names and are never translated.
    public readonly string OverlayVanilla = Loc.Localize("Colors.Bodies.Vanilla.Label", "Overlay gen2/vanilla");

    public readonly string BodiesTip = Loc.Localize("Colors.Bodies.Vanilla.Tip",
        "Also paint this mod onto vanilla (gen2) skin your character is wearing —\n" +
        "usually skin that comes with a piece of gear.\n" +
        "It is only painted while vanilla skin is actually on your character,\n" +
        "so this costs nothing when there is none.\n\n" +
        "bibo and gen3/Eve are always painted; this only affects vanilla.\n" +
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

    public readonly string ToeCapOn = Loc.Localize("Colors.Mask.ToeCap", "Toe Cap is on");

    public readonly string ToeCapOnTip = Loc.Localize("Colors.Mask.ToeCap.Tip",
        "The \"Toe Cap\" option is ticked in this mod's Masks group in Penumbra. It rebuilds\n" +
        "the toes as one rounded shape, so this mod's skin overlays are promoted to cloth —\n" +
        "they need geometry the skin layer hasn't got. That costs a shell rebuild.\n" +
        "\n" +
        "If you didn't tick it: Penumbra stores this group's selection by option INDEX, so a\n" +
        "mod re-exported with its options in a different order silently re-points every saved\n" +
        "selection. Re-tick the group in Penumbra to re-sync it.");
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
    public readonly string Caption = Loc.Localize("Band.Caption", "overlay · accessorize · reshape · bind");

    // The four capabilities named across the band's second line. Length is not cosmetic here: the row is
    // measured every frame and hides ALL FOUR labels the moment they stop fitting, so a translation much
    // longer than the English costs the whole row at window sizes where the English still shows.
    public readonly string CapOverlay = Loc.Localize("Band.Cap.Overlay", "Overlay & recolour anything");
    public readonly string CapWear    = Loc.Localize("Band.Cap.Wear",    "Wear anything, no slot");
    public readonly string CapReshape = Loc.Localize("Band.Cap.Reshape", "Reshape any model");
    public readonly string CapBind    = Loc.Localize("Band.Cap.Bind",    "Bind it all to a design");

    // Tooltip on any of the four capabilities, each of which opens the README at the section about it.
    public readonly string CapLinkTip = Loc.Localize("Band.Cap.Link.Tip", "Read about this in the guide");

    public readonly string SettingsTip    = Loc.Localize("Band.Settings.Tip", "Settings");
    public readonly string RecompositeTip = Loc.Localize("Band.Recomposite.Tip", "Recomposite now");
    public readonly string RecompositeFullTip = Loc.Localize("Band.Recomposite.Full.Tip",
        "Shift-click to rebuild everything from scratch");

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

    // ── tools ───────────────────────────────────────────────────────────────

    public readonly string ToolNavigate = Loc.Localize("Parts.Tool.Navigate", "Toggle Parts")
                                        + "###partsToolNavigate";

    public readonly string ToolNavigateTip = Loc.Localize("Parts.Tool.Navigate.Tip",
        "Click pieces of the model to choose them, then give them an on/off switch.");

    public readonly string ToolInflate = Loc.Localize("Parts.Tool.Inflate", "Pull out")
                                       + "###partsToolInflate";

    public readonly string ToolInflateTip = Loc.Localize("Parts.Tool.Inflate.Tip",
        "Paint on the model to push the surface outwards, so a body that pokes\n" +
        "through a piece of clothing is covered again.");

    public readonly string ToolDeflate = Loc.Localize("Parts.Tool.Deflate", "Push in")
                                       + "###partsToolDeflate";

    public readonly string ToolRelax = Loc.Localize("Parts.Tool.Relax", "Relax")
                                     + "###partsToolRelax";

    public readonly string ToolRelaxTip = Loc.Localize("Parts.Tool.Relax.Tip",
        "Paint on the model to smooth the surface — lumps the clothing came with, or a\n" +
        "pull that came out rough. Like 3ds Max's relax it shrinks: curves flatten and\n" +
        "cloth can sink toward the body.");

    public readonly string ToolBridge = Loc.Localize("Parts.Tool.Bridge", "Bridge")
                                      + "###partsToolBridge";

    public readonly string ToolBridgeTip = Loc.Localize("Parts.Tool.Bridge.Tip",
        "Paint across a hollow — the cleft between cheeks, a crease — to stretch the\n" +
        "cloth straight over it instead of following the body down into it. It only\n" +
        "ever lifts, and leaves rounded areas their own shape.");

    public readonly string ToolWind = Loc.Localize("Parts.Tool.Wind", "Wind")
                                    + "###partsToolWind";

    public readonly string ToolWindTip = Loc.Localize("Parts.Tool.Wind.Tip",
        "Paint where the wind moves this garment; red shows how much.\n" +
        "It is written into the model's second vertex colour.");

    public readonly string ToolMove = Loc.Localize("Parts.Tool.Move", "Move")
                                    + "###partsToolMove";

    public readonly string ToolMoveTip = Loc.Localize("Parts.Tool.Move.Tip",
        "Pick a part and drag it with the arrows: one arrow moves it along\n" +
        "that axis, a square moves it across that plane.");

    public readonly string ToolRotate = Loc.Localize("Parts.Tool.Rotate", "Rotate")
                                      + "###partsToolRotate";

    public readonly string ToolRotateTip = Loc.Localize("Parts.Tool.Rotate.Tip",
        "Pick a part, then drag one of the rings to turn it about its centre:\n" +
        "a coloured ring turns it about that axis, the outer one about your view.");

    public readonly string ToolScale = Loc.Localize("Parts.Tool.Scale", "Scale")
                                     + "###partsToolScale";

    public readonly string ToolScaleTip = Loc.Localize("Parts.Tool.Scale.Tip",
        "Pick a part, then press on it and drag right to grow it or left\n" +
        "to shrink it about its centre.");

    public readonly string RotateHelp = Loc.Localize("Parts.Rotate.Help",
        "Click a part to choose it, then drag one of the rings at its centre. Drag anywhere else to turn the model, "
      + "shift-drag to move the view, scroll to zoom.");

    public readonly string RotateLiveHint = Loc.Localize("Parts.Rotate.LiveHint",
        "Click a part on your character, then drag one of the rings at its centre. Hold Alt to move the camera. "
      + "Each turn is saved when you let go.");

    public readonly string ScaleHelp = Loc.Localize("Parts.Scale.Help",
        "Click a part to choose it, then press on it and drag right to grow it or left to shrink it. Drag anywhere "
      + "else to turn the model, shift-drag to move the view, scroll to zoom.");

    public readonly string ScaleLiveHint = Loc.Localize("Parts.Scale.LiveHint",
        "Click a part on your character, then press on it and drag right to grow it or left to shrink it. Hold Alt "
      + "to move the camera. Each change is saved when you let go.");

    public readonly string MoveHelp = Loc.Localize("Parts.Move.Help",
        "Click a part to choose it, then drag an arrow or a square. Drag anywhere else to turn the model, "
      + "shift-drag to move the view, scroll to zoom.");

    public readonly string MoveLiveHint = Loc.Localize("Parts.Move.LiveHint",
        "Click a part on your character, then drag an arrow or a square. Hold Alt to move the camera. "
      + "Each move is saved when you let go.");

    /// <summary>{0} is the part's label.</summary>
    public readonly string MovePartFmt = Loc.Localize("Parts.Move.Part.Fmt", "Moving part {0}");

    public readonly string MoveNoPart = Loc.Localize("Parts.Move.NoPart", "Click a part to choose what to move.");

    public readonly string MoveListTip = Loc.Localize("Parts.Move.ListTip",
        "Choose the part to move here, or click it on the model or on your character.");

    public readonly string MoveAdjacent = Loc.Localize("Parts.Move.Adjacent", "Move adjacent parts");

    public readonly string MoveAdjacentTip = Loc.Localize("Parts.Move.Adjacent.Tip",
        "Cloth near the part follows it, joined to it or not, less the further away it is.\n" +
        "Off, only the part moves. Points it shares exactly with a neighbour still move, so the seam stays closed.");

    public readonly string MoveFalloff = Loc.Localize("Parts.Move.Falloff", "Falloff");

    public readonly string MoveFalloffTip = Loc.Localize("Parts.Move.Falloff.Tip",
        "How far from the part nearby cloth still follows it.");

    public readonly string MoveFalloffSurfaceTip = Loc.Localize("Parts.Move.Falloff.SurfaceTip",
        "How far from the selected polygons the cloth around them still follows, measured along the surface.\n"
      + "Only cloth joined to the selection follows: a separate piece that merely sits close does not.");

    public readonly string MoveSelectParts = Loc.Localize("Parts.Move.SelectParts", "Whole parts")
                                           + "###partsMoveSelectParts";

    public readonly string MoveSelectPolygons = Loc.Localize("Parts.Move.SelectPolygons", "Polygons")
                                              + "###partsMoveSelectPolygons";

    public readonly string MoveSelectPolygonsTip = Loc.Localize("Parts.Move.SelectPolygons.Tip",
        "Move, turn or scale single polygons instead of whole parts. Click a polygon to select it; Shift-click adds\n"
      + "or removes one. Choosing a part in the list selects all of its polygons.");

    public readonly string MoveNoPolygons = Loc.Localize("Parts.Move.NoPolygons",
        "Click a polygon on the model to select it. Shift-click adds or removes one.");

    /// <summary>{0} is how many polygons are selected.</summary>
    public readonly string MovePolygonsFmt = Loc.Localize("Parts.Move.Polygons.Fmt", "{0:N0} polygon(s) selected");

    public readonly string MoveGrow = Loc.Localize("Parts.Move.Grow", "Grow") + "###partsMoveGrow";

    public readonly string MoveShrink = Loc.Localize("Parts.Move.Shrink", "Shrink") + "###partsMoveShrink";

    public readonly string MoveClearPolygons = Loc.Localize("Parts.Move.ClearPolygons", "Clear")
                                             + "###partsMoveClearPolygons";

    public readonly string MoveBonesNote = Loc.Localize("Parts.Move.BonesNote",
        "A moved part still follows the bones it was made for, so a part moved far from them can bend oddly in poses.");

    public readonly string MoveSkinTip = Loc.Localize("Parts.Move.SkinTip", "Skin can't be moved.");

    public readonly string MoveLockedTip = Loc.Localize("Parts.Move.LockedTip",
        "This part is locked. Unlock it under a brush to move it.");

    public readonly string MoveNothingFree = Loc.Localize("Parts.Move.NothingFree",
        "Nothing in this part can move: it is skin or locked.");

    /// <summary>{0} is the furthest any point has moved, in millimetres.</summary>
    public readonly string MoveMovedFmt = Loc.Localize("Parts.Move.Moved.Fmt", "Furthest moved: {0:F1} mm.");

    public readonly string MoveUndo = Loc.Localize("Parts.Move.Undo", "Undo move")
                                    + "###partsBrushUndo";

    public readonly string BrushWindAmount = Loc.Localize("Parts.Brush.WindAmount", "Amount");

    public readonly string BrushWindAmountTip = Loc.Localize("Parts.Brush.WindAmount.Tip",
        "How much wind to paint. 0% erases it, and so does holding Ctrl while painting.");

    public readonly string BrushWindRate = Loc.Localize("Parts.Brush.WindRate", "Rate");

    public readonly string BrushWindRateTip = Loc.Localize("Parts.Brush.WindRate.Tip",
        "How quickly the painted wind reaches the amount while you paint.");

    public readonly string WindAddsChannel = Loc.Localize("Parts.Wind.AddsChannel",
        "This model has no wind channel yet. One is added when the first stroke saves.");

    public readonly string WindFirstColorNotWhite = Loc.Localize("Parts.Wind.FirstColorNotWhite",
        "This model's first vertex colour isn't white, which the wind effect expects. Proteus leaves it as the author made it.");

    /// <summary>{0} is how many material files were changed.</summary>
    public readonly string WindMaterialsSetFmt = Loc.Localize("Parts.Wind.Materials.Set.Fmt",
        "Set {0} material file(s) so the wind can move this garment.");

    /// <summary>{0} is how many material files lack the vertex movement settings.</summary>
    public readonly string WindMaterialsMissingFmt = Loc.Localize("Parts.Wind.Materials.Missing.Fmt",
        "{0} material file(s) have no wind movement settings to change, so they may not sway.");

    /// <summary>{0} is how many materials the mod does not ship.</summary>
    public readonly string WindMaterialsNotInModFmt = Loc.Localize("Parts.Wind.Materials.NotInMod.Fmt",
        "{0} material(s) this garment uses come from the game, not this mod, so their wind movement can't be set.");

    /// <summary>{0} is the error.</summary>
    public readonly string WindMaterialsFailedFmt = Loc.Localize("Parts.Wind.Materials.Failed.Fmt",
        "The garment's materials couldn't be set for wind: {0}");

    public readonly string WindRefusedFmt = Loc.Localize("Parts.Wind.Refused.Fmt",
        "Wind couldn't be added to {0} mesh(es): there's no room there for another vertex attribute.");

    public readonly string BrushBridgeRateTip = Loc.Localize("Parts.Brush.BridgeRate.Tip",
        "How quickly the cloth rises to span the hollow while you paint. Low is gentle\n" +
        "and easy to control; high closes the gap almost at once.");

    public readonly string BrushRelaxRateTip = Loc.Localize("Parts.Brush.RelaxRate.Tip",
        "How quickly the surface smooths while you paint. Low is gentle and easy to\n" +
        "control; high smooths almost at once.\n\n" +
        "Near an open edge, like a hem, relaxing draws the edge slightly inward.");

    public readonly string ToolDeflateTip = Loc.Localize("Parts.Tool.Deflate.Tip",
        "The same brush in reverse, for clothing that stands too far off the body.");

    // ── the brush ───────────────────────────────────────────────────────────

    public readonly string BrushHelp = Loc.Localize("Parts.Brush.Help",
        "Drag on the model to paint. Drag from the background to turn it, shift-drag to move it, scroll to "
      + "zoom.");

    public readonly string ShowModelView = Loc.Localize("Parts.ShowModelView", "Show model view");

    public readonly string ShowModelViewTip = Loc.Localize("Parts.ShowModelView.Tip",
        "Paint on the model in this window instead of on your character.");

    public readonly string LivePickTip = Loc.Localize("Parts.Live.Pick.Tip",
        "Click a garment on your character to open its mod and model here.");

    public readonly string LiveHint = Loc.Localize("Parts.Live.Hint",
        "Paint on your character. Hold Alt to move the camera. Each stroke is saved when you let go.");

    public readonly string LiveNotWorn = Loc.Localize("Parts.Live.NotWorn",
        "This model is not on your character right now. Wear it, or turn on Show model view.");

    /// <summary>Under the brush controls for an imported piece: painting works, the result takes a moment.</summary>
    public readonly string ContentHint = Loc.Localize("Parts.Content.Hint",
        "This garment was imported into Proteus, which wears it for you. Paint it as usual — each stroke "
      + "appears on your character a few seconds after it saves, once the garment is rebuilt.");

    /// <summary>Clicking a Proteus shell on the character — see <c>OnLivePicked</c>.</summary>
    public readonly string LivePickedShell = Loc.Localize("Parts.Live.PickedShell",
        "That is the garment Proteus draws for you, which is rebuilt every time something changes. Pick the "
      + "mod it came from to edit it.");

    public readonly string LiveUnreadable = Loc.Localize("Parts.Live.Unreadable",
        "This model could not be matched to the one on your character. Turn on Show model view to paint it here.");

    public readonly string LiveNoCharacter = Loc.Localize("Parts.Live.NoCharacter",
        "Your character is not available to paint on right now.");

    public readonly string LivePickedNotListedFmt = Loc.Localize("Parts.Live.PickedNotListed.Fmt",
        "{0} is not one of the models this mod publishes.");

    public readonly string BrushSize = Loc.Localize("Parts.Brush.Size", "Brush size")
                                     + "###partsBrushSize";

    public readonly string BrushSizeTip = Loc.Localize("Parts.Brush.Size.Tip",
        "How far the brush reaches. The effect is strongest in the middle and fades\n" +
        "to nothing at the edge, so a wide brush moves a broad swell and a narrow\n" +
        "one moves a small bump.");

    public readonly string BrushStrength = Loc.Localize("Parts.Brush.Strength", "Strength")
                                         + "###partsBrushStrength";

    public readonly string BrushStrengthTip = Loc.Localize("Parts.Brush.Strength.Tip",
        "How far the surface moves per moment of painting. Small is usually right:\n" +
        "clothing only has to clear the body by a fraction of a millimetre, and you\n" +
        "can always paint over the same place again.");

    /// <summary>The brush is finer than the mesh. {0} is the model's average edge length in millimetres.</summary>
    public readonly string BrushTooSmallFmt = Loc.Localize("Parts.Brush.TooSmall.Fmt",
        "The brush is smaller than this model's triangles (about {0:F1} mm across), so it will pull single "
      + "points into spikes instead of moving the surface. Make it larger.");

    /// <summary>{0} is the furthest anything has moved, {1} the limit, both in millimetres.</summary>
    public readonly string BrushMovedFmt = Loc.Localize("Parts.Brush.Moved.Fmt",
        "Furthest moved: {0:F2} mm of {1:F1} mm.");

    public readonly string BrushUntouched = Loc.Localize("Parts.Brush.Untouched",
        "Nothing has been moved yet.");

    public readonly string BrushUndo = Loc.Localize("Parts.Brush.Undo", "Undo stroke")
                                     + "###partsBrushUndo";

    public readonly string BrushReset = Loc.Localize("Parts.Brush.Reset", "Start over")
                                      + "###partsBrushReset";

    public readonly string BrushNotSavedYet = Loc.Localize("Parts.Brush.NotSavedYet",
        "not saved yet");

    public readonly string BrushSave = Loc.Localize("Parts.Brush.Save", "Save into the mod")
                                     + "###partsBrushSave";

    public readonly string BrushSaveTip = Loc.Localize("Parts.Brush.Save.Tip",
        "Writes the change into the mod's own model file, so it keeps working with\n"
      + "Proteus turned off and travels with the mod if you export it. The original\n"
      + "is copied to Proteus/meshvolume-backup/ inside the mod first, and this can\n"
      + "be undone.");

    /// <summary>{0} is the furthest anything moved, in millimetres.</summary>
    public readonly string BrushSavedFmt = Loc.Localize("Parts.Brush.Saved.Fmt",
        "Saved. The surface was moved by up to {0:F2} mm.");

    /// <summary>{0} is a count of shape values that could not be carried.</summary>
    public readonly string BrushSparesFmt = Loc.Localize("Parts.Brush.Spares.Fmt",
        "{0} points belonging to this model's body sliders could not be moved with it, so turning one of "
      + "those sliders on may bring the clipping back in places.");

    public readonly string BrushRevert = Loc.Localize("Parts.Brush.Revert", "Undo saved changes")
                                       + "###partsBrushRevert";

    public readonly string BrushRevertTip = Loc.Localize("Parts.Brush.Revert.Tip",
        "Puts every model the brush has changed in this mod back exactly as its\n"
      + "author made it.");

    public readonly string BrushApplySizes = Loc.Localize("Parts.Brush.ApplySizes", "Apply to other sizes")
                                           + "###partsBrushApplySizes";

    public readonly string BrushApplySizesTip = Loc.Localize("Parts.Brush.ApplySizes.Tip",
        "Copies the changes brushed on this model onto the mod's other sizes of it,\n"
      + "matched by where they sit on the garment. Each size is backed up first,\n"
      + "and Undo saved changes puts it back.");

    public readonly string BrushApplySizesRunning = Loc.Localize("Parts.Brush.ApplySizes.Running",
        "Applying to other sizes…");

    /// <summary>{0} is how many other sizes were written.</summary>
    public readonly string BrushAppliedSizesFmt = Loc.Localize("Parts.Brush.ApplySizes.Done.Fmt",
        "Applied to {0} other size(s).");

    /// <summary>{0} is the size's label, {1} what went wrong.</summary>
    public readonly string BrushApplySizesProblemFmt = Loc.Localize("Parts.Brush.ApplySizes.Problem.Fmt",
        "{0}: {1}");

    /// <summary>The Save button with nothing waiting to save.</summary>
    public readonly string BrushSaveNothingTip = Loc.Localize("Parts.Brush.Save.Nothing.Tip",
        "Everything is already saved. Changes save on their own a moment after you stop painting.");

    /// <summary>Added under <see cref="BrushRevertTip"/>: the button is armed only while a modifier is held.</summary>
    public readonly string BrushRevertArmTip = Loc.Localize("Parts.Brush.Revert.Arm.Tip",
        "Hold Ctrl or Shift and click.");

    // ── body retarget ───────────────────────────────────────────────────────

    public readonly string ToolRetarget = Loc.Localize("Parts.Tool.Retarget", "Body size")
                                        + "###partsToolRetarget";

    public readonly string ToolRetargetTip = Loc.Localize("Parts.Tool.Retarget.Tip",
        "Refit this model onto a different size of the body it was made for.\n"
      + "The body's own change in shape is carried onto the garment, so it keeps\n"
      + "the way it sits. Saved as a new option in this mod, leaving the author's\n"
      + "own files alone.");

    public readonly string RetargetToBody = Loc.Localize("Parts.Retarget.ToBody", "Refit onto body mod");

    public readonly string RetargetFromBody = Loc.Localize("Parts.Retarget.FromBody", "Made for body mod");

    public readonly string RetargetFromBodyTip = Loc.Localize("Parts.Retarget.FromBody.Tip",
        "The body mod this outfit was made for. Pick the same one it is being refitted onto to refit between its sizes.\n"
      + "Choose another to move the outfit from that body to this one: its cloth then also takes the new body's\n"
      + "bone weights, so it moves with the new body. Skirt chains and other bones of the outfit's own keep theirs.");

    public readonly string RetargetNoBody = Loc.Localize("Parts.Retarget.NoBody", "Pick one…");

    /// <summary>{0} is a body mod's name; {1} is <see cref="RetargetMale"/> or <see cref="RetargetFemale"/>.</summary>
    public readonly string RetargetNoBodiesForSexFmt = Loc.Localize("Parts.Retarget.NoBodiesForSex.Fmt",
        "{0} has no bodies for a {1} character, and this outfit is made for one. A refit cannot change a body's sex.");

    public readonly string RetargetMale = Loc.Localize("Parts.Retarget.Male", "male");

    public readonly string RetargetFemale = Loc.Localize("Parts.Retarget.Female", "female");

    public readonly string RetargetPickBody = Loc.Localize("Parts.Retarget.PickBody",
        "Pick the body mod this garment was made for. Only mods that offer a choice of body models are listed.");

    /// <summary>{0} is the slot's name, {1} how many options it has.</summary>
    public readonly string RetargetSlotFmt = Loc.Localize("Parts.Retarget.Slot.Fmt", "{0} — {1} options");

    /// <summary>{0} is the slot's name.</summary>
    public readonly string RetargetSlotOptionalFmt = Loc.Localize("Parts.Retarget.Slot.Optional.Fmt",
        "{0} — optional");

    public readonly string RetargetSlotOptionalTip = Loc.Localize("Parts.Retarget.Slot.Optional.Tip",
        "Fill this in only if the garment reaches here — a long dress worn in the chest slot\n"
      + "also hangs over the legs. Leave it alone for a garment that does not.");

    public readonly string RetargetFrom = Loc.Localize("Parts.Retarget.From", "Made for");

    public readonly string RetargetTo = Loc.Localize("Parts.Retarget.To", "Refit onto");

    public readonly string RetargetOtherParts = Loc.Localize("Parts.Retarget.OtherParts",
        "Other parts of the body (optional)");

    /// <summary>{0} is how many of the other parts have a size chosen to refit onto.</summary>
    public readonly string RetargetOtherPartsInUseFmt = Loc.Localize("Parts.Retarget.OtherPartsInUse.Fmt",
        "Other parts of the body — {0} being refitted");

    /// <summary>{0} is the slot's name ("Hands").</summary>
    public readonly string RetargetNeedFromFmt = Loc.Localize("Parts.Retarget.NeedFrom.Fmt",
        "{0}: a size to refit onto is chosen, but not the size it was made for. Choose that, or click the chosen "
      + "size again to untick it.");

    /// <summary>{0} is the slot's name ("Chest").</summary>
    public readonly string RetargetNeedToFmt = Loc.Localize("Parts.Retarget.NeedTo.Fmt",
        "{0}: choose a size to refit onto.");

    public readonly string RetargetRefusedHold = Loc.Localize("Parts.Retarget.RefusedHold",
        "One of the chosen pairs cannot be refitted — see the message under it.");

    public readonly string RetargetToMany = Loc.Localize("Parts.Retarget.ToMany",
        "Refit onto (tick as many sizes as you like)");

    /// <summary>{0} is which size is being refitted, {1} how many there are.</summary>
    public readonly string RetargetWorkingFmt = Loc.Localize("Parts.Retarget.Working.Fmt", "Refitting {0} of {1}…");

    public readonly string RetargetShowing = Loc.Localize("Parts.Retarget.Showing", "Showing on the character");

    /// <summary>{0} is how many sizes will be saved.</summary>
    public readonly string RetargetSaveManyFmt = Loc.Localize("Parts.Retarget.SaveMany.Fmt",
                                                              "Save {0} sizes as new options")
                                               + "###partsRetargetSave";

    public readonly string RetargetChoose = Loc.Localize("Parts.Retarget.Choose", "Choose…");

    public readonly string RetargetChest = Loc.Localize("Parts.Retarget.Chest", "Chest");
    public readonly string RetargetLegs  = Loc.Localize("Parts.Retarget.Legs",  "Legs");
    public readonly string RetargetHands = Loc.Localize("Parts.Retarget.Hands", "Hands");
    public readonly string RetargetFeet  = Loc.Localize("Parts.Retarget.Feet",  "Feet");

    public readonly string RetargetChecking = Loc.Localize("Parts.Retarget.Checking",
        "Working out which size this was made for…");

    public readonly string RetargetWorking = Loc.Localize("Parts.Retarget.Working", "Refitting…");

    /// <summary>{0} is the option the garment's own body mesh matched exactly.</summary>
    public readonly string RetargetExactFmt = Loc.Localize("Parts.Retarget.Exact.Fmt",
        "This model's body mesh is exactly {0}.");

    /// <summary>{0} is the average distance from the garment's body mesh, in millimetres.</summary>
    public readonly string RetargetLikelyFmt = Loc.Localize("Parts.Retarget.Likely.Fmt",
        "Very likely — the closest fit by a clear margin ({0:F2} mm average).");

    /// <summary>{0} is the average distance, in millimetres.</summary>
    public readonly string RetargetGuessFmt = Loc.Localize("Parts.Retarget.Guess.Fmt",
        "Best guess: nothing matched exactly, but this one fits closest ({0:F2} mm average). Worth checking.");

    /// <summary>{0} is a comma-separated list of the sizes that fit equally well.</summary>
    public readonly string RetargetAmbiguousFmt = Loc.Localize("Parts.Retarget.Ambiguous.Fmt",
        "Several sizes fit this model equally well ({0}). Pick the one you know it was made for.");

    /// <summary>{0} is the option the garment's cloth fits most snugly without passing through.</summary>
    public readonly string RetargetClothLikelyFmt = Loc.Localize("Parts.Retarget.Cloth.Likely.Fmt",
        "Read from how the cloth sits: it fits {0} most snugly without passing into it, and passes into anything "
      + "larger.");

    /// <summary>{0} is the option the garment's cloth fits most snugly without passing through.</summary>
    public readonly string RetargetClothGuessFmt = Loc.Localize("Parts.Retarget.Cloth.Guess.Fmt",
        "Best guess from how the cloth sits: it fits {0} most snugly, but clears every size here, so it may simply be "
      + "loose. Worth checking.");

    /// <summary>{0} is the slot's name, lower case ("legs").</summary>
    public readonly string RetargetTooLittleFmt = Loc.Localize("Parts.Retarget.TooLittle.Fmt",
        "Too little of this model's body mesh reaches the {0} to tell which size it was made for. If the garment "
      + "does reach here, choose both sizes yourself — a long top's hem over the hips needs them.");

    public readonly string RetargetNoBodyMesh = Loc.Localize("Parts.Retarget.NoBodyMesh",
        "This model carries no body mesh, so the size it was made for cannot be guessed. Pick it yourself.");

    public readonly string RetargetPreview = Loc.Localize("Parts.Retarget.Preview", "Refit and preview")
                                           + "###partsRetargetPreview";

    public readonly string RetargetClearPreview = Loc.Localize("Parts.Retarget.ClearPreview", "Clear the preview")
                                                + "###partsRetargetClearPreview";

    /// <summary>{0} is the furthest any vertex moved, in millimetres.</summary>
    public readonly string RetargetMovedFmt = Loc.Localize("Parts.Retarget.Moved.Fmt", "Moved up to {0:F1} mm.");

    /// <summary>{0} is how many landed on a body vertex exactly, {1} the share of those that moved.</summary>
    public readonly string RetargetSnappedFmt = Loc.Localize("Parts.Retarget.Snapped.Fmt",
        "{0:N0} landed on the new body exactly ({1:P0}).");

    /// <summary>{0} is how many were pushed out, {1} the furthest, in millimetres.</summary>
    public readonly string RetargetPushedFmt = Loc.Localize("Parts.Retarget.Pushed.Fmt",
        "{0:N0} pushed back out of the body, up to {1:F2} mm.");

    /// <summary>{0} is how many vertices found nothing on the body to follow.</summary>
    public readonly string RetargetMissedFmt = Loc.Localize("Parts.Retarget.Missed.Fmt",
        "{0:N0} were too far from the body to follow it, and stayed where they were.");

    public readonly string RetargetOtherLods = Loc.Localize("Parts.Retarget.OtherLods",
        "This model has lower detail levels, which are not refitted — it will look like the old size from a distance.");

    public readonly string RetargetLowSnap = Loc.Localize("Parts.Retarget.LowSnap",
        "This model's body mesh does not match the size it was made for exactly, so the seam where skin meets cloth "
      + "may not come out perfect.");

    public readonly string RetargetSaveTo = Loc.Localize("Parts.Retarget.SaveTo", "Save to group");

    public readonly string RetargetNewGroup = Loc.Localize("Parts.Retarget.NewGroup", "A new group…");

    /// <summary>Marks a group in the save list that an earlier refit made, rather than the author.</summary>
    public readonly string RetargetMadeHere = Loc.Localize("Parts.Retarget.MadeHere", "(made by a refit)");

    /// <summary>{0} is the model's label.</summary>
    public readonly string RetargetGroupFmt = Loc.Localize("Parts.Retarget.Group.Fmt", "Body — {0}");

    /// <summary>{0} is the name of the author's group that also replaces this model.</summary>
    public readonly string RetargetClashFmt = Loc.Localize("Parts.Retarget.Clash.Fmt",
        "This mod already has a group (\"{0}\") that replaces this model. The new group is set to win over it, so "
      + "changing sizes there will not change this refit.");

    public readonly string RetargetSave = Loc.Localize("Parts.Retarget.Save", "Save as a new option")
                                        + "###partsRetargetSave";

    /// <summary>{0} is how many options have been saved, {1} the group they are in.</summary>
    public readonly string RetargetSavedFmt = Loc.Localize("Parts.Retarget.Saved.Fmt",
        "{0} refit option(s) saved in \"{1}\". Penumbra only picks a default for a mod it is adding for the first "
      + "time, so choose the option there to wear it.");

    public readonly string RetargetOpenInPenumbra = Loc.Localize("Parts.Retarget.OpenInPenumbra", "Open in Penumbra")
                                                  + "###partsRetargetOpen";

    public readonly string RetargetUndo = Loc.Localize("Parts.Retarget.Undo", "Remove the last refit")
                                        + "###partsRetargetUndo";

    /// <summary>{0} is the slot's name.</summary>
    public readonly string RetargetUnreadableFmt = Loc.Localize("Parts.Retarget.Unreadable.Fmt",
        "The {0} body model could not be read.");

    public readonly string RetargetLockListTip = Loc.Localize("Parts.Retarget.Lock.ListTip",
        "Untick a part to hold it where its author put it: the refit leaves it exactly as it is. Useful for "
      + "things that should not stretch with the body, like a buckle or a piece of jewellery. Clicking a part on "
      + "the model does the same.");

    public readonly string RetargetLockSkinTip = Loc.Localize("Parts.Retarget.Lock.SkinTip",
        "This is the garment's own body skin. Holding it keeps it at the old size, so the new body can show "
      + "through or gap at the edges.");

    public readonly string RetargetReplaceSkin = Loc.Localize("Parts.Retarget.ReplaceSkin", "Use the new body's skin")
                                               + "###partsRetargetReplaceSkin";

    public readonly string RetargetReplaceSkinTip = Loc.Localize("Parts.Retarget.SwapSkin.Tip",
        "On: the garment's skin for each part of the body being resized is removed and that part's new body\n"
      + "skin put in its place, so the skin under and around the garment is exactly the body mod's.\n"
      + "Off: the skin the garment came with is resized instead, keeping any reshaping its author did — a top\n"
      + "that lifts or presses the chest keeps doing so at the new size.");

    /// <summary>{0} is how many triangles the garment's swapped skin meshes had, {1} how many the body's have.</summary>
    public readonly string RetargetSwappedFmt = Loc.Localize("Parts.Retarget.SwapSlots.Fmt",
        "Skin swapped for the new body's where it is being resized: {0:N0} of the garment's skin triangles out, "
      + "{1:N0} of the body's in.");

    /// <summary>{0} is how many skin meshes were left because they belong to no slot being resized.</summary>
    public readonly string RetargetSwapKeptFmt = Loc.Localize("Parts.Retarget.SwapKeptMeshes.Fmt",
        "{0} skin mesh(es) belong to a part of the body that is not being resized, and were left as they are.");

    /// <summary>{0} is how many shape keys the garment had.</summary>
    /// <summary>{0} is how many cloth vertices took the new body's bone weights.</summary>
    public readonly string RetargetReweightedFmt = Loc.Localize("Parts.Retarget.Reweighted.Fmt",
        "{0:N0} cloth points now move with the new body's bones.");

    /// <summary>{0} is how many vertices had body weights cut to fit eight influences.</summary>
    public readonly string RetargetTrimmedFmt = Loc.Localize("Parts.Retarget.Trimmed.Fmt",
        "{0:N0} of them had more bones than a point can follow; the weakest were left out.");

    /// <summary>{0} is how many triangles of the old body's piercings and pubic hair were removed.</summary>
    public readonly string RetargetExtrasDroppedFmt = Loc.Localize("Parts.Retarget.ExtrasDropped.Fmt",
        "The old body's piercings and pubic hair ({0:N0} triangles) were left out.");

    /// <summary>{0} is how many bone weights could not be written.</summary>
    public readonly string RetargetUnplacedFmt = Loc.Localize("Parts.Retarget.Unplaced.Fmt",
        "{0:N0} bone weights could not be written and were left out.");

    public readonly string RetargetSwapShapesFmt = Loc.Localize("Parts.Retarget.SwapShapes.Fmt",
        "This garment had {0} shape key(s), which a skin swap does not carry over. Untick \"Use the new body's skin\" "
      + "to keep them.");

    /// <summary>{0} is how many skin points were laid onto the new body.</summary>
    public readonly string RetargetLaidFmt = Loc.Localize("Parts.Retarget.Laid.Fmt",
        "{0:N0} skin points laid onto the new body.");

    /// <summary>{0} is how many welded points were held.</summary>
    public readonly string RetargetHeldFmt = Loc.Localize("Parts.Retarget.Held.Fmt",
        "{0:N0} held where the author put them.");

    /// <summary>Shown in a dropdown when its search box matches nothing.</summary>
    public readonly string NoMatches = Loc.Localize("Parts.NoMatches", "Nothing matches.");

    public readonly string RetargetNoModel = Loc.Localize("Parts.Retarget.NoModel",
        "Open a model above to refit it onto another body size.");

    public readonly string RetargetFindingBodies = Loc.Localize("Parts.Retarget.FindingBodies",
        "Looking through your mods for body mods…");

    public readonly string RetargetCheckingPair = Loc.Localize("Parts.Retarget.CheckingPair",
        "Checking these two are sizes of the same body…");

    /// <summary>Added under <see cref="BrushSizeTip"/>.</summary>
    public readonly string BrushSizeKeysTip = Loc.Localize("Parts.Brush.Size.Keys.Tip",
        "[ and ] change it; hold Shift for fine steps.");

    public readonly string BrushMirror = Loc.Localize("Parts.Brush.Mirror", "Mirror left/right")
                                       + "###partsBrushMirror";

    public readonly string BrushMirrorTip = Loc.Localize("Parts.Brush.Mirror.Tip",
        "Paint both sides at once, mirrored across the body's centre.");

    /// <summary>Added under every brush tool's tooltip.</summary>
    public readonly string BrushLockHint = Loc.Localize("Parts.Brush.Lock.Hint",
        "Shift-click a part to lock it.");

    public readonly string BrushLockListTip = Loc.Localize("Parts.Brush.Lock.ListTip",
        "Untick a part to lock it: no brush moves it. Shift-clicking a part on the model or on your character "
      + "does the same.");

    /// <summary>{0} is how many parts are locked.</summary>
    public readonly string BrushLockCountFmt = Loc.Localize("Parts.Brush.Lock.Count.Fmt",
        "{0} part(s) locked");

    public readonly string BrushUnlockAll = Loc.Localize("Parts.Brush.Lock.UnlockAll", "Unlock all")
                                          + "###partsBrushUnlockAll";

    public readonly string BrushLockSkinTip = Loc.Localize("Parts.Brush.Lock.Skin.Tip",
        "Skin never moves.");

    /// <summary>{0} is how many model files were put back.</summary>
    public readonly string BrushRevertedFmt = Loc.Localize("Parts.Brush.Reverted.Fmt",
        "{0} model(s) put back as the author made them.");

    public readonly string ShatteredFmt = Loc.Localize("Parts.Shattered.Fmt",
        "Part {0} falls into {1} separate pieces, which is more than can be listed. Click the model to pick " +
        "one, or switch the whole part.");

    /// <summary>
    /// Hover on a part the author already switches. It can still take one of ours — the game draws a piece
    /// only when ALL its attributes are on — so this states the stacking rather than refusing it.
    /// </summary>
    public readonly string StacksWithAuthorTip = Loc.Localize("Parts.StacksWithAuthor.Tip",
        "The mod's author already has a switch on this part. A switch of yours stacks on top: the part " +
        "shows only when both are on.");

    /// <summary>The one case still refused — see <c>ModelPart.Toggleable</c>.</summary>
    public readonly string UnreadableTagTip = Loc.Localize("Parts.UnreadableTag.Tip",
        "This part is tagged with something the model does not name, so Proteus cannot tell what already " +
        "controls it and will not risk a switch that collides with one.");

    /// <summary>Dimmed marker on a row, so the stacking is visible without hovering.</summary>
    public readonly string AuthorSwitchedTag = Loc.Localize("Parts.AuthorSwitched.Tag", "author switch");

    public readonly string LegacyMod = Loc.Localize("Parts.LegacyMod",
        "This mod is still in Penumbra's old layout, which Proteus will not write to. Enable it in " +
        "Penumbra once so it updates itself, then come back.");

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

    /// <summary>
    /// Deliberately does not promise the option GROUP was removed. When Proteus merged its switches into a
    /// group the mod's author wrote, undoing takes the switches back out and leaves their group standing.
    /// </summary>
    public readonly string RevertedFmt = Loc.Localize("Parts.Reverted.Fmt",
        "Undone. {0} model file(s) restored, and the switches removed from this mod's settings.");
}

/// <summary>The named-looks strip at the top of a mod's colour editor. See <see cref="Gui.PresetBar"/>.</summary>
public sealed class PresetsStrings
{
    public readonly string Header = Loc.Localize("Presets.Header", "Presets");

    /// <summary>The collapsing header while a preset is worn, so a closed section still says which.</summary>
    public readonly string HeaderAppliedFmt = Loc.Localize("Presets.HeaderApplied.Fmt", "Presets — {0}");

    /// <summary>Bare text, with no "###id" suffix: this is a combo's preview value as well as a
    /// selectable's label, and a preview value is rendered verbatim — the id would show on screen.</summary>
    public readonly string NoPreset = Loc.Localize("Presets.None", "No preset");

    public readonly string NoPresetTip = Loc.Localize("Presets.None.Tip",
        "Wear the mod's own colours again. Your option ticks are left exactly as they are.");

    /// <summary>Prefix on a chip the mod author shipped. A glyph rather than a word so it costs no
    /// width in any language.</summary>
    public readonly string PackMarker = Loc.Localize("Presets.PackMarker", "* ");

    /// <summary>Suffix on the worn chip once the look has drifted from what was saved.</summary>
    public readonly string ModifiedMarker = Loc.Localize("Presets.ModifiedMarker", "●");

    public readonly string SaveNew = Loc.Localize("Presets.SaveNew", "+ Save…") + "###presetSaveNew";

    public readonly string SaveNewTip = Loc.Localize("Presets.SaveNew.Tip",
        "Save how this mod looks right now — its ticked options, colours and layer settings — under a name.");

    public readonly string Save = Loc.Localize("Presets.Save", "Save") + "###presetSaveGo";
    public readonly string RenameConfirm = Loc.Localize("Presets.RenameConfirm", "Rename") + "###presetSaveGo";
    public readonly string Cancel = Loc.Localize("Presets.Cancel", "Cancel");

    public readonly string NeedsAName = Loc.Localize("Presets.NeedsAName", "Give it a name first.");

    public readonly string NoCollection = Loc.Localize("Presets.NoCollection",
        "Penumbra hasn't told Proteus which collection you're wearing yet.");

    public readonly string FirstPresetName = Loc.Localize("Presets.FirstName", "My look");
    public readonly string NthPresetNameFmt = Loc.Localize("Presets.NthName.Fmt", "My look {0}");

    public readonly string SelectedFmt = Loc.Localize("Presets.Selected.Fmt", "{0} · {1}");

    public readonly string FromPack = Loc.Localize("Presets.FromPack", "from the mod");

    public readonly string PackReadOnly = Loc.Localize("Presets.PackReadOnly",
        "This one came with the mod, so it can't be changed. Duplicate it to make it yours.");

    public readonly string Update = Loc.Localize("Presets.Update", "Update");

    public readonly string UpdateTip = Loc.Localize("Presets.Update.Tip",
        "Fold everything on screen back into this preset.");

    public readonly string NothingChanged = Loc.Localize("Presets.NothingChanged",
        "Nothing has changed since this preset was saved.");

    public readonly string Rename = Loc.Localize("Presets.Rename", "Rename");
    public readonly string Duplicate = Loc.Localize("Presets.Duplicate", "Duplicate");

    public readonly string ForkTip = Loc.Localize("Presets.Fork.Tip",
        "Make an editable copy of the mod's preset and wear it.");

    public readonly string CopyCodeTip = Loc.Localize("Presets.CopyCode.Tip",
        "Copy this preset as a share code to paste to someone.");

    public readonly string CodeCopied = Loc.Localize("Presets.CodeCopied", "Share code copied to the clipboard.");

    public readonly string ExportTip = Loc.Localize("Presets.Export.Tip", "Save this preset as a file.");

    public readonly string DeleteTip = Loc.Localize("Presets.Delete.Tip", "Hold Ctrl and click to delete this preset.");

    public readonly string PasteCode = Loc.Localize("Presets.PasteCode", "Paste code");

    public readonly string PasteCodeTip = Loc.Localize("Presets.PasteCode.Tip",
        "Read a preset share code from the clipboard.");

    public readonly string Import = Loc.Localize("Presets.Import", "Import…");
    public readonly string ImportTip = Loc.Localize("Presets.Import.Tip", "Load a preset from a file.");

    public readonly string StagedFmt = Loc.Localize("Presets.Staged.Fmt", "\"{0}\" is ready to add.");

    public readonly string StagedOtherModFmt = Loc.Localize("Presets.StagedOtherMod.Fmt",
        "\"{0}\" was made for \"{1}\" by {2}, not for \"{3}\". Adding it anyway will apply whichever of its " +
        "options and colours this mod happens to share.");

    public readonly string AddStaged = Loc.Localize("Presets.AddStaged", "Add");
    public readonly string Discard = Loc.Localize("Presets.Discard", "Discard");
    public readonly string AddedFmt = Loc.Localize("Presets.Added.Fmt", "Added \"{0}\".");

    public readonly string ExportDialogTitle = Loc.Localize("Presets.ExportDialog.Title", "Save preset");
    public readonly string ImportDialogTitle = Loc.Localize("Presets.ImportDialog.Title", "Open preset");
    public readonly string DialogFilter = Loc.Localize("Presets.Dialog.Filter", "Proteus preset");

    // The Import tab's .ptp branch — a preset picked where mods are normally installed.
    public readonly string ImportFailedFmt = Loc.Localize("Presets.ImportFailed.Fmt",
        "That isn't a preset Proteus can read: {0}");

    public readonly string ImportedFromFmt = Loc.Localize("Presets.ImportedFrom.Fmt",
        "Preset \"{0}\" — made for \"{1}\" by {2}.");

    public readonly string NoMatchingMod = Loc.Localize("Presets.NoMatchingMod",
        "You don't have that mod installed under that name. Pick the mod it should go to, or install it first.");

    public readonly string AddTo = Loc.Localize("Presets.AddTo", "Add to");
    public readonly string PickAMod = Loc.Localize("Presets.PickAMod", "Pick a mod…");

    public readonly string AddToTip = Loc.Localize("Presets.AddTo.Tip",
        "Saves it against that mod. Nothing changes on screen until you wear it from the mod's Presets section.");

    public readonly string AddedToFmt = Loc.Localize("Presets.AddedTo.Fmt",
        "Added \"{0}\" to {1}. Wear it from that mod's Presets section in Colors.");

    public readonly string ExportedFmt = Loc.Localize("Presets.Exported.Fmt", "Saved to {0}.");
    public readonly string ExportFailedFmt = Loc.Localize("Presets.ExportFailed.Fmt", "Couldn't save it: {0}");

    public readonly string PartialApplyFmt = Loc.Localize("Presets.PartialApply.Fmt",
        "\"{0}\" was applied as far as it goes. {1}");

    public readonly string MissingGroupsFmt = Loc.Localize("Presets.MissingGroups.Fmt",
        "The mod no longer has these option groups: {0}.");

    public readonly string MissingOptionsFmt = Loc.Localize("Presets.MissingOptions.Fmt",
        "These options are gone: {0}.");

    public readonly string JustNow = Loc.Localize("Presets.JustNow", "just now");
    public readonly string MinutesAgoFmt = Loc.Localize("Presets.MinutesAgo.Fmt", "{0} m ago");
    public readonly string HoursAgoFmt = Loc.Localize("Presets.HoursAgo.Fmt", "{0} h ago");
    public readonly string DaysAgoFmt = Loc.Localize("Presets.DaysAgo.Fmt", "{0} d ago");

    /// <summary>The Mods-tab column and its combo entry for "nothing pinned".</summary>
    public readonly string ColumnHeader = Loc.Localize("Presets.Column.Header", "Preset");
    public readonly string ColumnNone = Loc.Localize("Presets.Column.None", "—");

    public readonly string ColumnTip = Loc.Localize("Presets.Column.Tip",
        "A saved look for this mod: its ticked options, colours and layer settings. Open Colors to save one.");
}
