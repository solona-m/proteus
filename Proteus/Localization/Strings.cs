using CheapLoc;

namespace Proteus.Localization;

/// <summary>
/// Every user-facing string, resolved ONCE per language instead of once per frame.
/// <para/>
/// The fields inside each holder are <c>readonly</c> and captured at construction, and that is the whole
/// point. <see cref="ProteusLoc.Localize"/> is not the cheap dictionary hit it looks like: CheapLoc calls
/// <c>Assembly.GetName()</c>, which builds a fresh <c>AssemblyName</c> (display-name parse, culture,
/// version, public-key token) on every single call before it looks anything up. Asking it for the status
/// window's visible strings at frame rate would be tens of thousands of allocations a second, in a plugin
/// that elsewhere goes out of its way to avoid one per-frame substring (see <c>ProteusStyle.Ellipsize</c>).
/// Reading a field costs none of that.
/// <para/>
/// The price is that a language change cannot reach a holder that already exists, so <see cref="Reload"/>
/// replaces them wholesale — about a dozen allocations, once, when the user changes a setting.
/// <b>Nothing may cache a holder INSTANCE in a field or a local across frames;</b> always read through
/// these properties at the point of use, or you will pin the old language.
/// <para/>
/// Rule for what belongs here: if a string can be reached from a <c>Draw()</c> call chain it lives in a
/// holder; if it can only be reached from a user action or a background task (a validation result, a chat
/// notice) it may call <see cref="ProteusLoc.Localize"/> inline, because it runs once per click, not once per
/// frame. Never CheapLoc's own <c>Loc.Localize</c>: see <see cref="ProteusLoc"/> for why.
/// <para/>
/// Both arguments to <see cref="ProteusLoc.Localize"/> must be compile-time literals — the key and the English fallback.
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
    public static HairPortStrings   HairPort   { get; private set; } = new();
    public static AutoRefitStrings  AutoRefit  { get; private set; } = new();
    public static SizeSetStrings    SizeSet    { get; private set; } = new();
    public static WhatsNewStrings   WhatsNew   { get; private set; } = new();

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
        HairPort   = new HairPortStrings();
        AutoRefit  = new AutoRefitStrings();
        SizeSet    = new SizeSetStrings();
        WhatsNew   = new WhatsNewStrings();
    }
}

/// <summary>The Body size tool's "every size for other bodies", which adds a whole set of sizes to a mod.</summary>
public sealed class SizeSetStrings
{
    public readonly string Header = ProteusLoc.Localize("SizeSet.Header", "Every size for other bodies") + "###sizeSetHeader";

    public readonly string Intro = ProteusLoc.Localize("SizeSet.Intro",
        "Adds every size of the bodies ticked below to this whole mod. Each size is refitted from the author's size "
      + "nearest it, the outfit's other pieces in that option from the same size, and saved beside the author's sizes as "
      + "\"Rue S\", \"YAB Buff L\" and so on. Sizes the mod already has are left alone. A women's mod can get TBSE and "
      + "TBSE-X too: its chest and legs are refitted for men and saved in a group of their own.");

    public readonly string Ends = ProteusLoc.Localize("SizeSet.Ends", "Include shoes and gloves") + "###sizeSetEnds";

    public readonly string EndsTip = ProteusLoc.Localize("SizeSet.Ends.Tip",
        "Shoes and gloves keep their own feet and hands; only the leg or arm they reach is refitted. Shoes get the legs' "
      + "sizes, gloves one option per body, and both join the outfit's chest or legs sizes when they share a group.");

    public readonly string Yas = ProteusLoc.Localize("SizeSet.Yas", "Use a body's YAS version where it has one") + "###sizeSetYas";

    public readonly string YasTip = ProteusLoc.Localize("SizeSet.Yas.Tip",
        "Rue, YAB and Tre come in Yet Another Skeleton versions. Neolithe and Bibo don't, and keep their plain sizes. A "
      + "piece already weighted to YAS always gets YAS sizes.");

    public readonly string Piercings = ProteusLoc.Localize("SizeSet.Piercings", "Add the new bodies' piercings")
                                     + "###sizeSetPiercings";

    public readonly string PiercingsTip = ProteusLoc.Localize("SizeSet.Piercings.Tip",
        "On: each body's piercings come in with its skin where the piece shows that skin. Nails always come along.\n"
      + "Sizes already made change only when made again.");

    public readonly string Remake = ProteusLoc.Localize("SizeSet.Remake", "Make again the sizes Proteus added before")
                                  + "###sizeSetRemake";

    public readonly string RemakeTip = ProteusLoc.Localize("SizeSet.Remake.Tip",
        "Off: a size Proteus already added is left as it is. On: it is refitted again and replaced. The author's own "
      + "sizes are never touched. If you added sizes with an earlier version of Proteus, tick this once: those were cut "
      + "from the author's first size.");

    public readonly string Run = ProteusLoc.Localize("SizeSet.Run", "Add the sizes") + "###sizeSetRun";

    public readonly string RunTip = ProteusLoc.Localize("SizeSet.Run.Tip",
        "Runs in the background and can take a few minutes for a mod with many pieces. Penumbra reloads the mod when it "
      + "is done.");

    public readonly string PickBody = ProteusLoc.Localize("SizeSet.PickBody", "Tick at least one body.");

    /// <summary>{0} is the body.</summary>
    public readonly string NotInstalledTipFmt = ProteusLoc.Localize("SizeSet.NotInstalledTip.Fmt",
        "{0} isn't installed in Penumbra. Install it, then close and reopen this section.");

    public readonly string Cancel = ProteusLoc.Localize("SizeSet.Cancel", "Stop") + "###sizeSetCancel";

    public readonly string Cancelled = ProteusLoc.Localize("SizeSet.Cancelled",
        "Stopped. Sizes already saved stay in the mod.");

    public readonly string DoneFmt = ProteusLoc.Localize("SizeSet.Done.Fmt", "Added {0} sizes to the mod.");

    public readonly string NothingAdded = ProteusLoc.Localize("SizeSet.NothingAdded",
        "No sizes were added: the mod already has every size of the bodies ticked.");

    public readonly string NothingAddedSeeLeftOut = ProteusLoc.Localize("SizeSet.NothingAdded.SeeLeftOut",
        "No sizes were added. Why is listed under \"Left out\" below.");

    public readonly string NothingAddedSeeLog = ProteusLoc.Localize("SizeSet.NothingAdded.SeeLog",
        "No sizes were added. The Dalamud log (/xllog) says why for each size.");

    public readonly string MadeOn = ProteusLoc.Localize("SizeSet.MadeOn", "Made on") + "###sizeSetMadeOn";

    public readonly string MadeOnAuto = ProteusLoc.Localize("SizeSet.MadeOn.Auto", "Find it for me");

    public readonly string MadeOnTip = ProteusLoc.Localize("SizeSet.MadeOn.Tip",
        "The body mod this mod's chest and legs were made on. Used only for sizes Proteus can't read itself, such as a "
      + "mod made on an older release of a body (Bibo+ before Dawntrail, say). Leave it as Find it for me unless Proteus "
      + "says it couldn't tell.");

    /// <summary>{0} is the piece.</summary>
    public readonly string UnreadableMadeOnFmt = ProteusLoc.Localize("SizeSet.UnreadableMadeOn.Fmt",
        "{0}: couldn't tell which body it was made on, so no sizes were made of it. If you know, choose it under "
      + "\"Made on\" and add the sizes again.");

    /// <summary>{0} is the piece, {1} the body chosen under "Made on".</summary>
    public readonly string NotMadeOnFmt = ProteusLoc.Localize("SizeSet.NotMadeOn.Fmt",
        "{0}: none of its sizes sits on {1}, so no sizes were made of it. Check the body chosen under \"Made on\".");

    /// <summary>{0} is the piece, {1} the body chosen under "Made on".</summary>
    public readonly string MadeOnMissingFmt = ProteusLoc.Localize("SizeSet.MadeOnMissing.Fmt",
        "{0}: {1}, chosen under \"Made on\", isn't installed, so no sizes were made of it.");

    /// <summary>{0} is the piece, {1} the body chosen under "Made on".</summary>
    public readonly string MadeOnUnclearFmt = ProteusLoc.Localize("SizeSet.MadeOnUnclear.Fmt",
        "{0}: on {1}, its sizes can't be told apart, so no sizes were made of it.");

    /// <summary>{0} is the piece, {1} the author's size, {2} the body it was read on, {3} the body chosen under "Made on".</summary>
    public readonly string MadeOnOtherBodyFmt = ProteusLoc.Localize("SizeSet.MadeOnOtherBody.Fmt",
        "{0}: \"{1}\" was read as made on {2}, not on {3} chosen under \"Made on\", and its sizes were cut as made on {2}.");

    public readonly string LeftOut = ProteusLoc.Localize("SizeSet.LeftOut", "Left out:");

    public readonly string FailedFmt = ProteusLoc.Localize("SizeSet.Failed.Fmt", "Couldn't add the sizes: {0}");

    public readonly string NoPieces = ProteusLoc.Localize("SizeSet.NoPieces",
        "This mod has no chest, legs, shoe or glove models to make sizes of.");

    /// <summary>{0} is the piece.</summary>
    public readonly string EndsUnreadableFmt = ProteusLoc.Localize("SizeSet.EndsUnreadable.Fmt",
        "{0}: its rig can't be read, so its foot or hand can't be kept and no sizes were made of it.");

    /// <summary>{0} is the piece.</summary>
    public readonly string UnpairedFmt = ProteusLoc.Localize("SizeSet.Unpaired.Fmt",
        "{0}: it is sized in one group with another piece it can't follow, so no sizes were made of it — each size would wear the other piece unrefitted.");

    /// <summary>{0} is the piece, {1} the size.</summary>
    public readonly string EndNoLeadSourceFmt = ProteusLoc.Localize("SizeSet.EndNoLeadSource.Fmt",
        "{0}: left out of \"{1}\": the author's size the outfit's chest or legs there was cut from couldn't be found or "
      + "read for it. Tick \"Make again\" to redo the option.");

    /// <summary>{0} is the piece, {1} the size.</summary>
    public readonly string EndNoLeadSourceRemakeFmt = ProteusLoc.Localize("SizeSet.EndNoLeadSourceRemake.Fmt",
        "{0}: \"{1}\" left as it was: the author's size the outfit's chest or legs there was cut from couldn't be read "
      + "for it.");

    public readonly string NoBodies = ProteusLoc.Localize("SizeSet.NoBodies", "No body mods are installed to refit onto.");

    /// <summary>{0} is the piece, {1} what went wrong.</summary>
    public readonly string PieceFailedFmt = ProteusLoc.Localize("SizeSet.PieceFailed.Fmt", "{0}: failed: {1}");

    public readonly string ReadingFmt = ProteusLoc.Localize("SizeSet.Reading.Fmt", "Reading what {0} was made on...");

    public readonly string UnreadableFmt = ProteusLoc.Localize("SizeSet.Unreadable.Fmt",
        "{0}: couldn't tell which body it was made on, so no sizes were made of it.");

    /// <summary>{0} is the piece, {1} the size ("Rue S"), {2} the body mod.</summary>
    public readonly string NoSuchSizeFmt = ProteusLoc.Localize("SizeSet.NoSuchSize.Fmt",
        "{0}: {2} has no model for \"{1}\", so that size was left out.");

    /// <summary>{0} is the piece, {1} the size, {2} why.</summary>
    public readonly string RefusedFmt = ProteusLoc.Localize("SizeSet.Refused.Fmt", "{0}: \"{1}\" couldn't be made: {2}");

    /// <summary>{0} is the piece, {1} what the save said.</summary>
    public readonly string SaveFailedFmt = ProteusLoc.Localize("SizeSet.SaveFailed.Fmt", "{0}: {1}");

    /// <summary>{0} is the piece, {1} the body.</summary>
    public readonly string NotInstalledFmt = ProteusLoc.Localize("SizeSet.NotInstalled.Fmt",
        "{0}: {1} isn't installed, so it got none of its sizes.");

    /// <summary>{0} is the piece, {1} the size.</summary>
    public readonly string WorkingFmt = ProteusLoc.Localize("SizeSet.Working.Fmt", "Refitting {0} to {1}...");

    /// <summary>{0} is a men's body ("TBSE").</summary>
    public readonly string ForMenFmt = ProteusLoc.Localize("SizeSet.ForMen.Fmt", "{0} (for men)");

    /// <summary>{0} is a men's body ("TBSE"), {1} the group the men's versions go into.</summary>
    public readonly string ForMenTipFmt = ProteusLoc.Localize("SizeSet.ForMen.Tip.Fmt",
        "This mod's women's chest and legs pieces get a men's version on {0}: refitted for Midlander men through the body "
      + "maps, which download the first time they're needed, each from the author's size whose body lies nearest his "
      + "once bent to a man's shape. They're saved in a group of their own, \"{1}\", so the women's sizes stay as they "
      + "are. Shoes and gloves aren't refitted across the sexes, and a piece the author already made for men keeps theirs.");

    /// <summary>The group a women's mod's men's versions are saved into.</summary>
    public readonly string MensGroup = ProteusLoc.Localize("SizeSet.MensGroup", "Men's body");

    public readonly string CheckInGame = ProteusLoc.Localize("SizeSet.CheckInGame", "Check in game:");

    /// <summary>{0} is the piece, {1} the size, {2} what to look at.</summary>
    public readonly string WarningFmt = ProteusLoc.Localize("SizeSet.Warning.Fmt", "{0}, \"{1}\": {2}");

    public readonly string MenMapsMissing = ProteusLoc.Localize("SizeSet.MenMapsMissing",
        "No men's versions were made: the body maps they need aren't here yet. Proteus is downloading them; add the "
      + "sizes again once they've arrived.");

    /// <summary>{0} is the piece.</summary>
    public readonly string NoSourceForMenFmt = ProteusLoc.Localize("SizeSet.NoSourceForMen.Fmt",
        "{0}: none of the author's sizes was made on a body Proteus can match to a man's (Bibo+ or the game's layout), "
      + "so it got no men's version.");

    /// <summary>{0} is the group's name.</summary>
    public readonly string MensGroupTakenFmt = ProteusLoc.Localize("SizeSet.MensGroupTaken.Fmt",
        "The mod already has a group of the author's called \"{0}\", so no men's versions were made. Rename that group "
      + "in Penumbra and add the sizes again.");

    /// <summary>{0} is the piece, {1} the men's option.</summary>
    public readonly string EndFromGameBodyFmt = ProteusLoc.Localize("SizeSet.EndFromGameBody.Fmt",
        "{0}, \"{1}\": its leg or arm has no skin to read a body off, so it was refit as made on the game's own body.");

    /// <summary>{0} is the piece, {1} the men's option, {2} millimetres, {3} the body.</summary>
    public readonly string EndClothGapFmt = ProteusLoc.Localize("SizeSet.EndClothGap.Fmt",
        "{0}, \"{1}\": its cloth sits about {2:F0} mm off {3}'s leg or arm on average, so the body it was read on may "
      + "not be the one it was made on.");

    /// <summary>{0} is the piece, {1} the author's option the men's version was made from.</summary>
    public readonly string OneVariantFmt = ProteusLoc.Localize("SizeSet.OneVariant.Fmt",
        "{0}: men get one version, made from \"{1}\"; the mod's other variants of it stay women's only.");

    public readonly string NotMade = ProteusLoc.Localize("SizeSet.NotMade", "the refit made nothing to save");

    public readonly string RigUnreadable = ProteusLoc.Localize("SizeSet.RigUnreadable",
        "its rig can't be read, so it can't be bent to a man's shape");
}

/// <summary>The automatic body refit's section of Settings, and the words it puts in chat.</summary>
public sealed class AutoRefitStrings
{
    public readonly string Intro = ProteusLoc.Localize("AutoRefit.Intro",
        "When you put on chest, legs, hands or feet gear — or an accessory that covers the body, such as a jacket on a "
      + "necklace — Proteus works out which body it was made for and refits it to the body and sizes chosen here, then "
      + "switches the new size on. Each Penumbra collection has its own choice.");

    public readonly string Enable = ProteusLoc.Localize("AutoRefit.Enable.Label",
        "Refit gear to my body as I put it on") + "###autoRefitEnable";

    public readonly string EnableTip = ProteusLoc.Localize("AutoRefit.Enable.Tip",
        "The refit is saved into the gear's own mod as a new size option, exactly as the Studio's Body size tool "
      + "saves one, so it keeps working with Proteus turned off. Choosing the mod's \"Original\" or another size in "
      + "Penumbra is left alone.");

    public readonly string Vanilla = ProteusLoc.Localize("AutoRefit.Vanilla.Label",
        "Also refit the game's own gear") + "###autoRefitVanilla";

    public readonly string VanillaTip = ProteusLoc.Localize("AutoRefit.Vanilla.Tip",
        "Unmodded gear has no mod to save into, so each piece gets a new mod of its own, named after the item and "
      + "the body.");

    public readonly string Piercings = ProteusLoc.Localize("AutoRefit.Piercings.Label",
        "Add my body's piercings to refitted gear") + "###autoRefitPiercings";

    public readonly string PiercingsTip = ProteusLoc.Localize("AutoRefit.Piercings.Tip",
        "Off: gear is refitted without your body mod's piercings. Nails always come along.\n"
      + "Gear already refitted keeps what it was made with; undo and refit what you're wearing to change it.");

    public readonly string Collection = ProteusLoc.Localize("AutoRefit.Collection", "Collection");

    public readonly string CurrentFmt = ProteusLoc.Localize("AutoRefit.Collection.Current.Fmt", "{0} (current)");

    public readonly string Body = ProteusLoc.Localize("AutoRefit.Body", "Body");

    /// <summary>The parts table's first column: chest, legs, hands, feet.</summary>
    public readonly string Part = ProteusLoc.Localize("AutoRefit.Part", "Part");

    public readonly string Size = ProteusLoc.Localize("AutoRefit.Size", "Size");

    public readonly string PartsHint = ProteusLoc.Localize("AutoRefit.PartsHint",
        "Each part can use its own body. The first body you pick is used for every part it has.");

    /// <summary>{0} is the size hands or feet follow the chest or legs to.</summary>
    public readonly string MatchedFmt = ProteusLoc.Localize("AutoRefit.Matched.Fmt", "Matched ({0})");

    /// <summary>{0} is the size taken when the size worn cannot be told.</summary>
    public readonly string MatchedWornFmt = ProteusLoc.Localize("AutoRefit.MatchedWorn.Fmt",
        "Matched (the size you wear, else {0})");

    public readonly string Match = ProteusLoc.Localize("AutoRefit.Match", "Match the chest or legs size");

    public readonly string SizesPending = ProteusLoc.Localize("AutoRefit.SizesPending",
        "Reading the sizes you wear in this body...");

    /// <summary>{0} lists the parts given the body's first size.</summary>
    public readonly string PrefilledFmt = ProteusLoc.Localize("AutoRefit.Prefilled.Fmt",
        "The size you wear couldn't be read, so {0} got the body's first size. Check it above.");

    public readonly string Choose = ProteusLoc.Localize("AutoRefit.Choose", "Choose...");

    public readonly string NoBodyChosen = ProteusLoc.Localize("AutoRefit.NoBodyChosen",
        "No body chosen: gear put on in this collection is left as it is.");

    public readonly string FindingBodies = ProteusLoc.Localize("AutoRefit.FindingBodies", "Looking for body mods...");

    public readonly string NoBodies = ProteusLoc.Localize("AutoRefit.NoBodies",
        "No body mods are installed, so there is nothing to refit onto.");

    public readonly string BodyMissingFmt = ProteusLoc.Localize("AutoRefit.BodyMissing.Fmt",
        "The body chosen here, \"{0}\", isn't installed any more. Choose another.");

    public readonly string NoCollections = ProteusLoc.Localize("AutoRefit.NoCollections",
        "Penumbra isn't available, so its collections can't be listed.");

    public readonly string EndsKept = ProteusLoc.Localize("AutoRefit.EndsKept",
        "Gloves and shoes keep the hands and feet they were made with; only the arms and legs they reach are refitted.");

    public readonly string Clear = ProteusLoc.Localize("AutoRefit.Clear", "Forget this collection's body") + "###autoRefitClear";

    public readonly string RunNow = ProteusLoc.Localize("AutoRefit.RunNow", "Refit what I'm wearing now") + "###autoRefitRunNow";

    public readonly string RunNowTip = ProteusLoc.Localize("AutoRefit.RunNow.Tip",
        "Only gear put on from now on is refitted by itself. This does the pieces already on your character.");

    public readonly string Working = ProteusLoc.Localize("AutoRefit.Working", "Working...");

    public readonly string WorkingFmt = ProteusLoc.Localize("AutoRefit.Working.Fmt", "Refitting {0}...");

    /// <summary>A modded piece in chat: the mod's name, then the part — "This Old Thing (chest)".</summary>
    public readonly string ItemInModFmt = ProteusLoc.Localize("AutoRefit.ItemInMod.Fmt", "{0} ({1})");

    public readonly string UndoWorn = ProteusLoc.Localize("AutoRefit.UndoWorn", "Undo the refit of what I'm wearing")
                                    + "###autoRefitUndoWorn";

    public readonly string UndoWornTip = ProteusLoc.Localize("AutoRefit.UndoWorn.Tip",
        "For the gear you have on now: removes the size the automatic refit made, deletes the mod it made for the game's "
      + "own gear, and puts back the option you had before. Those pieces are then left as they are when you put them on "
      + "again. Sizes you made yourself in the Body size tool, and refits of anything you are not wearing, stay.\n\n"
      + "Hold Ctrl or Shift and click.");

    public readonly string UndoWorking = ProteusLoc.Localize("AutoRefit.UndoWorking", "Undoing the refit...");

    /// <summary>{0} sizes removed, {1} mods deleted.</summary>
    public readonly string UndoDoneFmt = ProteusLoc.Localize("AutoRefit.UndoDone.Fmt",
        "Undone: removed {0} refitted sizes and deleted {1} mods made for the game's own gear. These pieces won't be "
      + "refitted again unless you click \"Refit what I'm wearing now\".");

    /// <summary>{0} sizes removed, {1} sizes that could not be removed.</summary>
    public readonly string UndoPartialFmt = ProteusLoc.Localize("AutoRefit.UndoPartial.Fmt",
        "Removed {0} refitted sizes, but {1} could not be removed. The log says which and why.");

    public readonly string UndoNothing = ProteusLoc.Localize("AutoRefit.UndoNothing",
        "Nothing you're wearing was refitted automatically.");

    public readonly string UndoFailedFmt = ProteusLoc.Localize("AutoRefit.UndoFailed.Fmt", "Couldn't undo the refit: {0}");
}

/// <summary>The one-time release-notes window.</summary>
public sealed class WhatsNewStrings
{
    // The stable id is fused here, not at the call site, so the concatenation is paid once per language.
    public readonly string Title = ProteusLoc.Localize("WhatsNew.Title", "What's new in Proteus") + "###ProteusWhatsNew";

    /// <summary>For someone seeing the notes for the first time: every section is shown.</summary>
    public readonly string IntroAll = ProteusLoc.Localize("WhatsNew.IntroAll",
        "A few things worth knowing about Proteus. This window opens once, and not again.");

    /// <summary>For someone who closed the earlier notes: only what is new since then is shown.</summary>
    public readonly string IntroSince = ProteusLoc.Localize("WhatsNew.IntroSince",
        "Something new since you last saw this window. It opens once, and not again.");

    public readonly string HairPortHead = ProteusLoc.Localize("WhatsNew.HairPort.Head",
        "Hair mods made for another race now fit yours");

    public readonly string HairPortBody = ProteusLoc.Localize("WhatsNew.HairPort.Body",
        "Many hair mods are made for one race only, so on any other race the game shows its own hairstyle instead. "
      + "Proteus now bends the mod's hairstyle onto your race's head and adds it to the same option of the same mod. "
      + "It looks through the hair mods switched on in your collection once now, and again whenever one is "
      + "installed, switched on or changed. Chat names the hairstyle numbers it fitted, so you know which to "
      + "choose. Physics hair keeps its physics.\n\n"
      + "It keeps working with Proteus turned off and travels with the mod. Switch it off, check again, or undo a "
      + "fitted hairstyle under Settings, Hair.");

    public readonly string HairPortStudioHead = ProteusLoc.Localize("WhatsNew.HairPortStudio.Head",
        "Port a hairstyle to every race at once");

    public readonly string HairPortStudioBody = ProteusLoc.Localize("WhatsNew.HairPortStudio.Body",
        "To make a hair mod work for everyone, not only you, open one of its hair models in the Studio and use "
      + "\"Port to all races\". Proteus bends it onto every other race of the same sex in one go and adds the "
      + "results to the same option of the mod. Races the author made it for are left alone, and \"Remove ports\" "
      + "takes them all back out.");

    public readonly string AutoRefitHead = ProteusLoc.Localize("WhatsNew.AutoRefit.Head",
        "Gear refits itself to your body");

    public readonly string AutoRefitBody = ProteusLoc.Localize("WhatsNew.AutoRefit.Body",
        "Proteus can now refit chest, legs, hands and feet gear to your body by itself, the moment you put it on. "
      + "It works out which body mod and size the gear was made for, refits it and the parts of the body it "
      + "reaches, saves the new size into the gear's own mod and switches it on. Chat says when it starts and when "
      + "it is done.\n\n"
      + "To use it, open Settings, Body refit. Tick \"Refit gear to my body as I put it on\", then pick your body "
      + "mod and its chest and legs sizes for each Penumbra collection; hands and feet are matched for you. Gear "
      + "you are already wearing is refitted with \"Refit what I'm wearing now\", and the game's own gear has a "
      + "checkbox of its own. Choosing \"Original\" or another size in Penumbra is always left alone.");

    public readonly string UpscalesHead = ProteusLoc.Localize("WhatsNew.Upscales.Head", "One click upscales");

    public readonly string UpscalesBody = ProteusLoc.Localize("WhatsNew.Upscales.Body",
        "The Studio has a new Body size tool. Pick the body a garment was made for and the body you want it "
      + "on, and Proteus moves every point of the garment to match: between sizes of one body mod, or between "
      + "body mods entirely. It can take on the new body's skin and bone weights, upscale onto several sizes "
      + "in one run, and upscale the game's own gear too.\n\n"
      + "The result is saved into the mod as an ordinary Penumbra option, so it keeps working with Proteus "
      + "turned off and travels with the mod if you export it.");

    public readonly string HatsHead = ProteusLoc.Localize("WhatsNew.Hats.Head",
        "Hats now automatically fit over modded hair");

    public readonly string HatsBody = ProteusLoc.Localize("WhatsNew.Hats.Body",
        "Making hairstyles fit under hats is now on by default. Proteus waits until you actually put a hat on, "
      + "then presses the hair the hat would cover flat against your head, so the hat stops going straight "
      + "through it.\n\n"
      + "It edits the hair mod's own files and keeps the originals, so it can be undone — or switched off "
      + "altogether — under Settings, Hair.");

    public readonly string DesignsHead = ProteusLoc.Localize("WhatsNew.Designs.Head",
        "Designs restore the whole look");

    public readonly string DesignsBody = ProteusLoc.Localize("WhatsNew.Designs.Body",
        "A Glamourer design bound to Proteus can restore every mod that was on your character when you saved "
      + "it, and raises those mods above anything they conflict with, so a look no longer comes back "
      + "half-applied.\n\n"
      + "Mods that aren't Proteus mods are only held that way with Penumbra temporary settings, so your "
      + "collection itself is never changed, and Penumbra's own banner on a held mod lets you drop that one "
      + "hold. It is off by default: turn it on under Bindings, \"Restore every mod on the character\".");

    public readonly string Open = ProteusLoc.Localize("WhatsNew.Open", "Open Proteus") + "###whatsNewOpen";

    public readonly string Close = ProteusLoc.Localize("WhatsNew.Close", "Close") + "###whatsNewClose";
}

/// <summary>Hair porting: the Settings > Hair section and the Studio's "Port to all races".</summary>
public sealed class HairPortStrings
{
    public readonly string Auto = ProteusLoc.Localize(
        "HairPort.Auto.Label", "Fit hair mods made for another race") + "###hairPortAuto";

    public readonly string AutoTip = ProteusLoc.Localize("HairPort.Auto.Tip2",
        "Many hair mods are made for one race only, so on any other race the game\n" +
        "shows its own hairstyle instead. When on, Proteus bends the mod's hairstyle\n" +
        "onto your race's head and adds it to the same option of the same mod — for the\n" +
        "hairstyle you put on, and ahead of time for the hair mods switched on in your\n" +
        "collection: when this is switched on, when a mod is installed, switched on or\n" +
        "changed, and when you press Check now.\n\n" +
        "Only for your own race, from races of the same sex. It keeps working with\n" +
        "Proteus off, travels with the mod, and shows for everyone drawn with that mod\n" +
        "on. It can be undone here.");

    public readonly string None = ProteusLoc.Localize("HairPort.Settings.None",
        "The hairstyle you are wearing needs no fitting.");

    public readonly string PortedFmt = ProteusLoc.Localize("HairPort.Settings.Ported.Fmt",
        "Hairstyle {0} is fitted to your race in {1}.");

    public readonly string Undo = ProteusLoc.Localize("HairPort.Settings.Undo", "Undo") + "###hairPortUndo";

    public readonly string UndoTip = ProteusLoc.Localize("HairPort.Settings.Undo.Tip",
        "Take the fitted hairstyle out of the mod again. Proteus will not fit it again by\n" +
        "itself; you can still port it in the Studio.");

    public readonly string UndoAllFmt = ProteusLoc.Localize("HairPort.Settings.UndoAll.Fmt",
        "Undo all {0} fitted hairstyles in this mod") + "###hairPortUndoAll";

    public readonly string Working = ProteusLoc.Localize("HairPort.Working", "Working…");

    // ── Earless Miqo'te hair ─────────────────────────────────────────────────────────────────────────
    public readonly string Earless = ProteusLoc.Localize(
        "HairPort.Earless.Label", "Take the ears off Miqo'te hair mods") + "###hairEarless";

    public readonly string EarlessTip = ProteusLoc.Localize("HairPort.Earless.Tip",
        "For a Miqo'te: when you wear a hair mod whose hairstyle has cat ears, Proteus\n" +
        "makes an earless copy of it and points the mod's option at that copy. The\n" +
        "author's own files are never changed.\n\n" +
        "This changes the mod itself, so every character drawn with that mod on — in\n" +
        "every collection, and anyone you share it with — has no ears with it. Turning\n" +
        "this off puts the ears back in every mod Proteus changed. Works with or without\n" +
        "fitting hair mods made for another race.");

    public readonly string EarlessFmt = ProteusLoc.Localize("HairPort.Settings.Earless.Fmt",
        "Hairstyle {0} from {1} is drawn without its ears — for every character drawn with this mod on, in every " +
        "collection. Turning the setting off puts them back.");

    public readonly string EarlessOptionHintFmt = ProteusLoc.Localize("HairPort.Settings.EarlessOptionHint.Fmt",
        "This mod has its own earless option: {0}");

    // ── Check of every hair mod ──────────────────────────────────────────────────────────────────────
    public readonly string CheckingStart = ProteusLoc.Localize("HairPort.Scan.Starting", "Checking your hair mods…");
    public readonly string CheckingFmt = ProteusLoc.Localize("HairPort.Scan.Checking.Fmt", "Checking your hair mods… {0} of {1}");
    public readonly string NoCheck = ProteusLoc.Localize("HairPort.Scan.None",
        "Your hair mods have not been checked in this session yet.");
    public readonly string CheckedFmt = ProteusLoc.Localize("HairPort.Scan.Checked.Fmt",
        "{0} hairstyle(s) from your hair mods are fitted to {1}.");
    public readonly string CheckNow = ProteusLoc.Localize("HairPort.Scan.CheckNow", "Check now") + "###hairPortCheck";
    public readonly string CheckNowTip = ProteusLoc.Localize("HairPort.Scan.CheckNow.Tip",
        "Look through every hair mod switched on in your collection and fit the ones\nmade for another race to yours.");
    public readonly string WearHint = ProteusLoc.Localize("HairPort.Scan.WearHint",
        "To wear one, choose that hairstyle number for your character — Glamourer shows the number.");
    public readonly string ListFmt = ProteusLoc.Localize("HairPort.Scan.List.Fmt", "Hair mods for your race ({0})");
    public readonly string RowFmt = ProteusLoc.Localize("HairPort.Scan.Row.Fmt", "Hairstyle {0} — {1}: {2}");
    public readonly string StateFitted = ProteusLoc.Localize("HairPort.Scan.State.Fitted", "fitted");
    /// <summary>{0} is what to look at in game, such as "bent onto c1101, 40 of its triangles turn inside out…".</summary>
    public readonly string StateFittedWarnFmt = ProteusLoc.Localize("HairPort.Scan.State.FittedWarn.Fmt", "fitted — {0}");
    public readonly string StateAlready = ProteusLoc.Localize("HairPort.Scan.State.Already", "already fitted");
    public readonly string StateRemade = ProteusLoc.Localize("HairPort.Scan.State.Remade", "fitted again (the mod changed)");
    public readonly string StateUndone = ProteusLoc.Localize("HairPort.Scan.State.Undone", "undone");
    public readonly string StateRefused = ProteusLoc.Localize("HairPort.Scan.State.Refused", "not fitted");
    public readonly string StateRefusedFmt = ProteusLoc.Localize("HairPort.Scan.State.Refused.Fmt", "not fitted — {0}");
    public readonly string UndoChecked = ProteusLoc.Localize("HairPort.Scan.UndoChecked",
        "Undo everything this check fitted") + "###hairPortUndoChecked";
    public readonly string UndoCheckedConfirmFmt = ProteusLoc.Localize("HairPort.Scan.UndoChecked.Confirm.Fmt",
        "Click again to undo {0} hairstyle(s) for good");

    // ── Studio ──────────────────────────────────────────────────────────────────────────────────────
    public readonly string Header = ProteusLoc.Localize("HairPort.Studio.Header", "Port to all races") + "###hairPortStudio";

    public readonly string Intro = ProteusLoc.Localize("HairPort.Studio.Intro",
        "Bends this hairstyle onto every other race of the same sex and adds the results to the same option of this mod, " +
        "so it shows on everyone whatever their race. Races you made it for yourself are left alone.");

    public readonly string RaceCovered = ProteusLoc.Localize("HairPort.Studio.Covered", "made by you");
    public readonly string RacePorted  = ProteusLoc.Localize("HairPort.Studio.Ported", "ported");
    public readonly string RaceStale   = ProteusLoc.Localize("HairPort.Studio.Stale", "out of date");
    public readonly string RaceNoStyle = ProteusLoc.Localize("HairPort.Studio.NoStyle", "cannot choose this hairstyle");
    public readonly string RaceOpen    = ProteusLoc.Localize("HairPort.Studio.Open", "not covered");

    public readonly string Port = ProteusLoc.Localize("HairPort.Studio.Port", "Port") + "###hairPortRun";
    public readonly string PortingFmt = ProteusLoc.Localize("HairPort.Studio.Porting.Fmt", "Porting… {0} of {1} races");
    public readonly string Cancel = ProteusLoc.Localize("HairPort.Studio.Cancel", "Cancel") + "###hairPortCancel";
    public readonly string Cancelled = ProteusLoc.Localize("HairPort.Studio.Cancelled", "Cancelled — nothing was written.");
    public readonly string DoneFmt = ProteusLoc.Localize("HairPort.Studio.Done.Fmt", "{0} port(s) added to the mod.");
    public readonly string NothingToDo = ProteusLoc.Localize("HairPort.Studio.Nothing", "Nothing was ported.");
    public readonly string FailedFmt = ProteusLoc.Localize("HairPort.Studio.Failed.Fmt", "Porting failed: {0}");
    public readonly string Remove = ProteusLoc.Localize("HairPort.Studio.Remove", "Remove ports") + "###hairPortRemove";
    public readonly string RemovedFmt = ProteusLoc.Localize("HairPort.Studio.Removed.Fmt", "{0} port(s) removed.");
    public readonly string Reading = ProteusLoc.Localize("HairPort.Studio.Reading", "Reading the mod…");
}

/// <summary>The hat-compatibility panel.</summary>
public sealed class HatCompatStrings
{
    public readonly string AutoFit = ProteusLoc.Localize(
        "HatCompat.AutoFit.Label", "Make hairstyles fit under hats") + "###hatCompatAuto";

    public readonly string AutoFitTip = ProteusLoc.Localize("HatCompat.AutoFit.Tip",
        "Most modded hair has no hat support, so a hat worn over it goes straight\n" +
        "through. When on, Proteus waits until you wear a hat, then presses the hair\n" +
        "that the hat would cover flat against your head.\n\n" +
        "This edits the hair mod's own files, so it keeps working with Proteus turned\n" +
        "off and travels with the mod if you export it. Originals are kept and the\n" +
        "change can be undone here.");

    public readonly string ReplaceAuthored = ProteusLoc.Localize(
        "HatCompat.ReplaceAuthored.Label", "Replace hat support a hairstyle came with") + "###hatCompatReplaceAuthored";

    public readonly string ReplaceAuthoredTip = ProteusLoc.Localize("HatCompat.ReplaceAuthored.Tip",
        "Some hairstyles come with hat support their author made by hand. With this off,\n" +
        "Proteus leaves those alone. With it on, Proteus discards the author's version and\n" +
        "fits the hairstyle itself, the same as one that came with none.\n\n" +
        "Off by default: an author's hand-made fit is usually the better one. The author's\n" +
        "version is kept, and is put back when you turn this off again.");

    public readonly string Replacing = ProteusLoc.Localize("HatCompat.Replacing",
        "This hairstyle came with hat support of its own. As set above, Proteus will replace it with its own fit.");

    public readonly string HidePonytails = ProteusLoc.Localize(
        "HatCompat.HidePonytails.Label", "Hide ponytails") + "###hatCompatHideTails";

    public readonly string HidePonytailsTip = ProteusLoc.Localize("HatCompat.HidePonytails.Tip",
        "Also make the parts that no hat could cover — ponytails, side tails, a long\n" +
        "fall at the back — disappear while a hat is worn, instead of leaving them\n" +
        "hanging out from under it.\n\n" +
        "Off by default. Pressing hair is invisible when it overshoots, because the hat\n" +
        "covers it; hiding is all or nothing, so a part judged wrongly simply vanishes.");

    public readonly string Working = ProteusLoc.Localize("HatCompat.Working", "Looking at this hairstyle...");

    public readonly string NoModdedHair = ProteusLoc.Localize("HatCompat.NoModdedHair",
        "You are wearing hair that came with the game, which already works under hats. "
      + "This has something to do when you wear a hair mod.");

    public readonly string WearingFmt = ProteusLoc.Localize("HatCompat.Wearing.Fmt",
        "Hairstyle {0}, from {1}.");

    public readonly string AuthorDidIt = ProteusLoc.Localize("HatCompat.AuthorDidIt",
        "This hairstyle already supports hats — its author added it. Nothing to do.");

    public readonly string AlreadyPatched = ProteusLoc.Localize("HatCompat.AlreadyPatched",
        "Proteus has already made this hairstyle fit under hats.");

    public readonly string InheritedTag = ProteusLoc.Localize("HatCompat.InheritedTag",
        "This hairstyle came with hat support, but it hides far more hair than a hat covers — most "
      + "likely carried over from the hairstyle it was built from and never fitted to this one. Proteus "
      + "can measure it again and replace it.");

    public readonly string InheritedTagTip = ProteusLoc.Localize("HatCompat.InheritedTag.Tip",
        "Only the part that hides hair is replaced. The hairstyle keeps its own\n" +
        "flattening, so the worst this can do is leave a hat clipping through hair,\n" +
        "instead of hair disappearing that no hat would have covered.\n\n" +
        "Undo puts back exactly what the hairstyle came with.");

    public readonly string PressFmt = ProteusLoc.Localize("HatCompat.Press.Fmt",
        "{0} points of hair would be pressed against your head, by {1:F1} cm on average and {2:F1} cm at most.");

    public readonly string TooWeldedFmt = ProteusLoc.Localize("HatCompat.TooWelded.Fmt",
        "{0} points cannot be moved: this hairstyle is welded into pieces too large for the game's shape "
      + "format to address. They will keep their shape, so a hat may still clip there.");

    public readonly string WillHideFmt = ProteusLoc.Localize("HatCompat.WillHide.Fmt",
        "{0} strand(s) hang too far off your head to fit under a hat and will be hidden while one is worn.");

    public readonly string NothingToHide = ProteusLoc.Localize("HatCompat.NothingToHide",
        "Every part of this hairstyle can be pressed under a hat, so none of it needs hiding.");

    public readonly string WaitingForHat = ProteusLoc.Localize("HatCompat.WaitingForHat",
        "Proteus will fit this hairstyle when you put on a hat. Or fit it now:");

    public readonly string Apply = ProteusLoc.Localize("HatCompat.Apply.Btn", "Make it fit") + "###hatCompatApply";

    public readonly string ApplyTip = ProteusLoc.Localize("HatCompat.Apply.Tip",
        "Writes into the hair mod's own folder. The original files are copied to\n"
      + "Proteus/hatcompat-backup/ inside that mod first, and this can be undone.");

    public readonly string Undo = ProteusLoc.Localize("HatCompat.Undo.Btn", "Undo") + "###hatCompatUndo";

    public readonly string UndoAllFmt = ProteusLoc.Localize("HatCompat.UndoAll.Fmt",
        "Undo all {0} of this mod's hairstyles") + "###hatCompatUndoAll";

    public readonly string UndoAllTip = ProteusLoc.Localize("HatCompat.UndoAll.Tip",
        "Undo puts back the hairstyle you are wearing. A hair mod usually ships one\n" +
        "model per race, so a hairstyle you fitted as another race is not the one you\n" +
        "have on now, and Undo cannot reach it.\n\n" +
        "This puts back every hairstyle Proteus has changed anywhere in this mod.");

    public readonly string Undone = ProteusLoc.Localize("HatCompat.Undone",
        "The hairstyle has been put back exactly as its author made it.");

    public readonly string Unreadable = ProteusLoc.Localize("HatCompat.Unreadable",
        "This hairstyle's model could not be read, so Proteus cannot change it.");

    public readonly string NoHead = ProteusLoc.Localize("HatCompat.NoHead",
        "Proteus could not work out where your head is, so it has left this hairstyle alone. It reads "
      + "your character's face to know where a hat sits, and without that a hat line would be a guess.");
}

/// <summary>Strings shared by more than one screen.</summary>
public sealed class CommonStrings
{
    public readonly string Browse = ProteusLoc.Localize("Common.Browse.Btn", "Browse");
    public readonly string Clear  = ProteusLoc.Localize("Common.Clear.Btn", "Clear");
    public readonly string None   = ProteusLoc.Localize("Common.None", "(none)");

    /// <summary>Stands in for an error message when a background task was cancelled rather than failing.</summary>
    public readonly string Cancelled = ProteusLoc.Localize("Common.Cancelled", "cancelled");

    // Units in ImGui slider formats. These are printf formats, not string.Format: keep the %-token exactly.
    public readonly string Mm0 = ProteusLoc.Localize("Common.Units.Mm0", "%.0f mm");
    public readonly string Mm1 = ProteusLoc.Localize("Common.Units.Mm1", "%.1f mm");
    public readonly string Mm2 = ProteusLoc.Localize("Common.Units.Mm2", "%.2f mm");

    public readonly string MmPerQuarterSecond = ProteusLoc.Localize("Common.Units.MmPerQuarterSecond", "%.2f mm / 0.25 s");

    /// <summary>Playable races in the game's own order, as <see cref="ModelRace.Describe"/> shows them. A
    /// separate table from <see cref="ModelRace.Names"/>, which is also written into mods and stays English.</summary>
    public readonly string[] RaceNames =
    [
        ProteusLoc.Localize("Common.Race.Midlander", "Midlander"),
        ProteusLoc.Localize("Common.Race.Highlander", "Highlander"),
        ProteusLoc.Localize("Common.Race.Elezen", "Elezen"),
        ProteusLoc.Localize("Common.Race.Miqote", "Miqote"),
        ProteusLoc.Localize("Common.Race.Roegadyn", "Roegadyn"),
        ProteusLoc.Localize("Common.Race.Lalafell", "Lalafell"),
        ProteusLoc.Localize("Common.Race.AuRa", "AuRa"),
        ProteusLoc.Localize("Common.Race.Hrothgar", "Hrothgar"),
        ProteusLoc.Localize("Common.Race.Viera", "Viera"),
    ];

    public readonly string RaceMale   = ProteusLoc.Localize("Common.Race.Male", "M");
    public readonly string RaceFemale = ProteusLoc.Localize("Common.Race.Female", "F");

    /// <summary>{0} is a race from <see cref="RaceNames"/>, {1} <see cref="RaceMale"/> or <see cref="RaceFemale"/>.</summary>
    public readonly string RaceGenderFmt = ProteusLoc.Localize("Common.Race.Gender.Fmt", "{0} {1}");

    // Equipment slots and character parts, as Services.ContentSlot.DisplayLabel shows them.
    public readonly string SlotHead      = ProteusLoc.Localize("Common.Slot.Head", "Head");
    public readonly string SlotBody      = ProteusLoc.Localize("Common.Slot.Body", "Body");
    public readonly string SlotHands     = ProteusLoc.Localize("Common.Slot.Hands", "Hands");
    public readonly string SlotLegs      = ProteusLoc.Localize("Common.Slot.Legs", "Legs");
    public readonly string SlotFeet      = ProteusLoc.Localize("Common.Slot.Feet", "Feet");
    public readonly string SlotEarrings  = ProteusLoc.Localize("Common.Slot.Earrings", "Earrings");
    public readonly string SlotNecklace  = ProteusLoc.Localize("Common.Slot.Necklace", "Necklace");
    public readonly string SlotBracelets = ProteusLoc.Localize("Common.Slot.Bracelets", "Bracelets");
    public readonly string SlotRightRing = ProteusLoc.Localize("Common.Slot.RightRing", "Right ring");
    public readonly string SlotLeftRing  = ProteusLoc.Localize("Common.Slot.LeftRing", "Left ring");
    public readonly string SlotHair      = ProteusLoc.Localize("Common.Slot.Hair", "Hair");
    public readonly string SlotFace      = ProteusLoc.Localize("Common.Slot.Face", "Face");
    public readonly string SlotTail      = ProteusLoc.Localize("Common.Slot.Tail", "Tail");
    public readonly string SlotEars      = ProteusLoc.Localize("Common.Slot.Ears", "Ears");
}

/// <summary>
/// The six tabs across the top of the status window. Plain labels — <c>ProteusStyle.HeaderTabItem</c>
/// fuses the stable <c>###id</c> on itself, so it cannot be forgotten at a call site.
/// </summary>
public sealed class TabStrings
{
    public readonly string Mods     = ProteusLoc.Localize("Tab.Mods", "Mods");
    public readonly string Bindings = ProteusLoc.Localize("Tab.Bindings", "Bindings");
    public readonly string Create   = ProteusLoc.Localize("Tab.Create", "Create");
    public readonly string Import   = ProteusLoc.Localize("Tab.Import", "Import");
    public readonly string Parts    = ProteusLoc.Localize("Tab.Parts", "Studio");
    public readonly string Export   = ProteusLoc.Localize("Tab.Export", "Export");
    public readonly string Settings = ProteusLoc.Localize("Tab.Settings", "Settings");
}

public sealed class ModsStrings
{
    // Sort headers pass their id to SortableHeader separately, so these stay plain.
    public readonly string ColOn       = ProteusLoc.Localize("Mods.Col.On", "On");
    public readonly string ColMod      = ProteusLoc.Localize("Mods.Col.Mod", "Mod");
    public readonly string ColPriority = ProteusLoc.Localize("Mods.Col.Priority", "Pri");

    // Drawn with a bare ImGui.TableHeader, which takes the label as its id — hence the fused "###". Done
    // here rather than at the call site so the concatenation happens once per language instead of once
    // per frame.
    public readonly string ColColors   = ProteusLoc.Localize("Mods.Col.Colors", "Colors") + "###modColors";

    // The per-mod AO/Skindent combo's label in the colour panel. Keeps the key it had as a Mods column
    // header so the existing translations carry over.
    public readonly string Skindent = ProteusLoc.Localize("Mods.Col.Skindent", "Skindent");

    public readonly string ColorsBtn = ProteusLoc.Localize("Mods.Colors.Btn", "Colors");

    public readonly string ColorsBindingDrivenTip = ProteusLoc.Localize("Mods.Colors.BindingDriven.Tip",
        "Colors are driven by the active design binding.\nEdits preview live; click \"Update\" on the " +
        "Bindings tab to save them. Base colors are unchanged.");

    public readonly string AoOn  = ProteusLoc.Localize("Mods.Ao.On", "On");
    public readonly string AoOff = ProteusLoc.Localize("Mods.Ao.Off", "Off");

    /// <summary>{0} is the pack's own choice, already localized to <see cref="AoOn"/>/<see cref="AoOff"/>.</summary>
    public readonly string AoPackFmt = ProteusLoc.Localize("Mods.Ao.Pack.Fmt", "Pack ({0})");

    /// <summary>{0} is the "Pack (on)" label, repeated so the tooltip names the option it is describing.</summary>
    public readonly string AoTipFmt = ProteusLoc.Localize("Mods.Ao.Tip.Fmt",
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
    public readonly string InertNothingTickedFmt = ProteusLoc.Localize("Mods.Inert.NothingTicked.Fmt",
        "Nothing is ticked in Penumbra. This mod's {0} option group(s) — {1} — are all empty, so it " +
        "paints nothing. Open it in Penumbra and tick an option in each.");

    /// <summary>{0} is the comma-joined names of groups the mod's Proteus data expects but Penumbra has
    /// not got. Author-facing: the user cannot fix this one.</summary>
    public readonly string InertGroupsMissingFmt = ProteusLoc.Localize("Mods.Inert.GroupsMissing.Fmt",
        "This mod's Proteus data names the option group(s) {0}, which Penumbra's copy of the mod hasn't " +
        "got — renamed or dropped when it was re-exported. Only its author can fix that.");

    /// <summary>{0} is what the mod paints and {1} what the character is, both as "body · Race F" — e.g.
    /// "bibo · Midlander F, Viera F". Race names come from ModelRace and are not translated.</summary>
    public readonly string InertWrongRaceFmt = ProteusLoc.Localize("Mods.Inert.WrongRace.Fmt",
        "This mod paints {0}, and you are {1}. Nothing it ships fits the body or the race you are wearing.");

    /// <summary>{0} is the Penumbra group name for masks — literally "Masks", the author's own group
    /// name, which arrives already untranslated.</summary>
    public readonly string InertMaskNeedsShellFmt = ProteusLoc.Localize("Mods.Inert.MaskNeedsShell.Fmt",
        "Its masks render as gear, which needs a mask to build the garment from — and nothing is ticked " +
        "in its \"{0}\" group in Penumbra.");

    public readonly string InertAwaitingMenMaps = ProteusLoc.Localize("Mods.Inert.AwaitingMenMaps",
        "This mod's art is made for women's bodies. It will be painted on you, in your own layout, once the men's body " +
        "maps are downloaded (retry from the UV maps notice if the download failed).");

    public readonly string InertNothingReached = ProteusLoc.Localize("Mods.Inert.NothingReached",
        "Its ticked options resolved, but none of them reached a surface this character has loaded.");

    public readonly string InertSettingsUnreadable = ProteusLoc.Localize("Mods.Inert.SettingsUnreadable",
        "Penumbra didn't answer when Proteus asked which of this mod's options are on.");
}

public sealed class BindingsStrings
{
    public readonly string ColDesign   = ProteusLoc.Localize("Bindings.Col.Design", "Design");
    public readonly string ColCaptured = ProteusLoc.Localize("Bindings.Col.Captured", "Captured");

    public readonly string Enable =
        ProteusLoc.Localize("Bindings.Enable.Label", "Bind Proteus state to Glamourer designs") + "###bindEnable";

    public readonly string EnableTip = ProteusLoc.Localize("Bindings.Enable.Tip",
        "When on, saving a Glamourer design snapshots the current Proteus state.\n" +
        "Applying that design later restores it (best-effort gear match).");

    public readonly string RestoreCharacter = ProteusLoc.Localize(
        "Bindings.RestoreCharacter.Label", "Restore every mod on the character") + "###bindRestoreCharacter";

    public readonly string RestoreCharacterTip = ProteusLoc.Localize("Bindings.RestoreCharacter.Tip",
        "When on, applying a bound design also restores every mod that was on the character\n" +
        "when it was saved (enable / priority / options), switches off mods on the character\n" +
        "that weren't, and raises the design's mods above anything they conflict with.\n\n" +
        "Mods that aren't Proteus mods are only held that way with Penumbra temporary settings:\n" +
        "your collection isn't changed, and reverting or applying an unbound design puts them back.\n" +
        "Penumbra's banner on a held mod can drop that one hold, if you want just it back.\n\n" +
        "When off, only Proteus mods are restored. Designs are captured either way, so\n" +
        "turning this on works for designs saved while it was off.");

    public readonly string FollowAutomation = ProteusLoc.Localize(
        "Bindings.FollowAutomation.Label", "Follow Glamourer automation (gearset / job changes)") + "###bindAutomation";

    public readonly string FollowAutomationTip = ProteusLoc.Localize("Bindings.FollowAutomation.Tip",
        "Glamourer reports nothing when automation applies a design on a gearset\n" +
        "or job change, so Proteus infers it from the signals that do arrive.\n\n" +
        "Only ever restores a binding — it never clears one.\n" +
        "The one redraw Proteus itself causes is discounted, so its own work\n" +
        "can't be mistaken for an automation apply.\n\n" +
        "Needs \"Bind Proteus state to Glamourer designs\" above.");

    public readonly string UnequipUnset = ProteusLoc.Localize(
        "Bindings.UnequipUnset.Label", "Unequip slots the design leaves unset") + "###bindUnequipUnset";

    public readonly string UnequipUnsetTip = ProteusLoc.Localize("Bindings.UnequipUnset.Tip",
        "When you apply a bound design, every gear and glasses slot the design doesn't set\n" +
        "is emptied, and imported Proteus packs it didn't capture are switched off, so pieces\n" +
        "from the previous look don't carry over.\n\n" +
        "Not on automation applies, which stack several designs. Weapons and the slots\n" +
        "Proteus is using for a shell are left alone.");

    public readonly string ActiveFmt = ProteusLoc.Localize("Bindings.Active.Fmt", "Active: {0}");
    public readonly string NoBindings = ProteusLoc.Localize("Bindings.None", "No bound designs yet.");
    public readonly string PillActive = ProteusLoc.Localize("Bindings.Pill.Active", "active");

    public readonly string SecondsAgoFmt = ProteusLoc.Localize("Bindings.SecondsAgo.Fmt", "{0}s ago");
    public readonly string MinutesAgoFmt = ProteusLoc.Localize("Bindings.MinutesAgo.Fmt", "{0}m ago");
    public readonly string HoursAgoFmt   = ProteusLoc.Localize("Bindings.HoursAgo.Fmt", "{0}h ago");

    public readonly string Apply  = ProteusLoc.Localize("Bindings.Apply.Btn", "Apply");
    public readonly string Update = ProteusLoc.Localize("Bindings.Update.Btn", "Update");
    public readonly string Unbind = ProteusLoc.Localize("Bindings.Unbind.Btn", "Unbind");

    public readonly string ApplyTip = ProteusLoc.Localize("Bindings.Apply.Tip",
        "Restore this design's Proteus state now, without going through Glamourer —\n" +
        "enable / priority / options / colours for every mod it captured.\n\n" +
        "Proteus mods NOT in the binding are switched off, so this replaces the current\n" +
        "look rather than adding to it.");

    public readonly string ApplyCharacterTip = ProteusLoc.Localize("Bindings.ApplyCharacter.Tip",
        "Restore this design's look now, without going through Glamourer: enable / priority /\n" +
        "options for every mod it captured, plus Proteus colours.\n\n" +
        "Mods on the character that are NOT in the binding are switched off, and bound mods are\n" +
        "raised above anything they conflict with, so this replaces the current look.");

    public readonly string UpdateTip = ProteusLoc.Localize("Bindings.Update.Tip",
        "Snapshot every mod on the character (enable / priority / options) and the current\n" +
        "Proteus colours into this binding. Manual edits only persist when you click this.");

    public readonly string ModCountFmt = ProteusLoc.Localize("Bindings.ModCount.Fmt", "{0} mods");
    public readonly string ProteusOnly = ProteusLoc.Localize("Bindings.ProteusOnly", "Proteus mods only");

    public readonly string ProteusOnlyTip = ProteusLoc.Localize("Bindings.ProteusOnly.Tip",
        "Captured before bindings recorded the whole character, so applying it only\n" +
        "restores Proteus mods. Apply this design, then press Update.");

    public readonly string ModListHeader      = ProteusLoc.Localize("Bindings.ModList.Header", "Mods this design restores:");
    public readonly string ModListOff         = ProteusLoc.Localize("Bindings.ModList.Off", "off");
    public readonly string ModListMissing     = ProteusLoc.Localize("Bindings.ModList.Missing", "not installed");
    public readonly string ModListPriorityFmt = ProteusLoc.Localize("Bindings.ModList.Priority.Fmt", "priority {0}");
    public readonly string ModListMoreFmt     = ProteusLoc.Localize("Bindings.ModList.More.Fmt", "…and {0} more");

    public readonly string UpdateInactiveTip = ProteusLoc.Localize("Bindings.Update.Inactive.Tip",
        "Only the active binding can be updated from the current state.\n" +
        "Apply this design first.");

    public readonly string UnbindTip = ProteusLoc.Localize("Bindings.Unbind.Tip",
        "Forget this binding. The Glamourer design itself is untouched.");
}

public sealed class CreateStrings
{
    // The four texture slots. These are DISPLAY text only — the slot identity that drives the file-picker
    // dispatch and the material-slot lookup is a separate, untranslated token at the call site. See
    // StatusWindow.DrawTextureRow, where the two used to be one parameter.
    public readonly string SlotDiffuse = ProteusLoc.Localize("Create.Slot.Diffuse", "Diffuse");
    public readonly string SlotMask    = ProteusLoc.Localize("Create.Slot.Mask", "Mask");
    public readonly string SlotNormal  = ProteusLoc.Localize("Create.Slot.Normal", "Normal");
    public readonly string SlotIndex   = ProteusLoc.Localize("Create.Slot.Index", "Index");

    public readonly string SlotUnused = ProteusLoc.Localize("Create.Slot.Unused", "(not used by this material)");

    /// <summary>The texture file dialog's filter name; the extension list is appended after it.</summary>
    public readonly string ImageFilter = ProteusLoc.Localize("Create.Dialog.ImageFilter", "Images");

    // The material picker's left-column tag: what a material belongs to. Skin surfaces first, then by path.
    public readonly string TagBody      = ProteusLoc.Localize("Create.Picker.Tag.Body", "Body");
    public readonly string TagFace      = ProteusLoc.Localize("Create.Picker.Tag.Face", "Face");
    public readonly string TagIris      = ProteusLoc.Localize("Create.Picker.Tag.Iris", "Iris");
    public readonly string TagHair      = ProteusLoc.Localize("Create.Picker.Tag.Hair", "Hair");
    public readonly string TagTail      = ProteusLoc.Localize("Create.Picker.Tag.Tail", "Tail");
    public readonly string TagEar       = ProteusLoc.Localize("Create.Picker.Tag.Ear", "Ear");
    public readonly string TagNative    = ProteusLoc.Localize("Create.Picker.Tag.Native", "Native");
    public readonly string TagWeapon    = ProteusLoc.Localize("Create.Picker.Tag.Weapon", "Weapon");
    public readonly string TagGear      = ProteusLoc.Localize("Create.Picker.Tag.Gear", "Gear");
    public readonly string TagAccessory = ProteusLoc.Localize("Create.Picker.Tag.Accessory", "Accessory");
    public readonly string TagOther     = ProteusLoc.Localize("Create.Picker.Tag.Other", "Other");

    public readonly string PickTextureTitleFmt =
        ProteusLoc.Localize("Create.PickTexture.Title.Fmt", "Select {0} texture");

    public readonly string NoDiffuse = ProteusLoc.Localize("Create.Slot.NoDiffuse", "This material has no diffuse texture.");
    public readonly string NoMask    = ProteusLoc.Localize("Create.Slot.NoMask", "This material has no mask texture.");
    public readonly string NoNormal  = ProteusLoc.Localize("Create.Slot.NoNormal", "This material has no normal texture.");

    public readonly string WholeSkin = ProteusLoc.Localize("Create.WholeSkin.Label",
        "These textures are the whole skin") + "###createWholeSkin";

    public readonly string WholeSkinTip = ProteusLoc.Localize("Create.WholeSkin.Tip",
        "Tick this when you're converting a full skin mod, not painting something onto skin.\n" +
        "Proteus ticks it for you when the textures look like one; your own answer always wins.\n" +
        "Two things follow. The normal REPLACES the one already on the material instead of\n" +
        "stacking onto it — otherwise it lands on top of the same map underneath, every pore\n" +
        "and crease is applied twice, and the body reads flat against the face with a hard line\n" +
        "at the neck seam. And skin-tint suppression goes off, so the wearer's skin tone comes\n" +
        "through: it is there to stop fabric being re-tinted, and this art IS the skin.");

    public readonly string FaceAsymmetric = ProteusLoc.Localize("Create.FaceAsymmetric.Label",
        "This face texture is asymmetric") + "###createFaceAsymmetric";

    public readonly string FaceAsymmetricTip = ProteusLoc.Localize("Create.FaceAsymmetric.Tip",
        "For a face texture painted as TWO HALVES: the character's right side in the right half " +
        "of the image, their left side in the left half.\n" +
        "An ordinary face texture gives both cheeks the same pixels, so a mark on one side alone " +
        "cannot exist in it. Proteus moves the face's own UVs onto a doubled sheet instead, which " +
        "keeps the skin tone and every expression.\n" +
        "Ticked for you when the picked image is twice as wide as this face's own texture. Leave it " +
        "off for any ordinary face texture.");

    public readonly string Glow = ProteusLoc.Localize("Create.Glow.Label",
        "Make this art glow") + "###createGlow";

    public readonly string GlowTip = ProteusLoc.Localize("Create.Glow.Tip",
        "Light the art up. Its colour comes from the picture itself, per pixel — there is\n" +
        "nothing else to set.\n" +
        "Skin can't emit, so a glowing overlay renders on a second skin instead of being\n" +
        "painted into yours. Everything else about it works the same, and the Glow dial in\n" +
        "Colors adjusts it afterwards.");

    public readonly string GlowNeedsDiffuse = ProteusLoc.Localize("Create.Glow.NeedsDiffuse",
        "Pick a diffuse texture first — the glow takes its colour from the art.");

    public readonly string GlowNeedsSkin = ProteusLoc.Localize("Create.Glow.NeedsSkin",
        "Only skin and face targets can glow. A glowing overlay is drawn on a second skin cut\n" +
        "from your body, and there's nothing to cut one from on gear, an accessory or a weapon.");

    public readonly string GlowAlways = ProteusLoc.Localize("Create.Glow.Always", "Always glow");

    public readonly string GlowAlwaysTip = ProteusLoc.Localize("Create.Glow.Always.Tip",
        "The tattoo is there in daylight and glows day and night.\n" +
        "Kept gentle on purpose: this shader adds one flat colour across the whole tattoo\n" +
        "rather than following the picture, so a strong value bleaches the art in daylight.\n" +
        "For a brighter glow at night without that, raise Glow in Colors and set \"Fades in light\".");

    public readonly string GlowDarkOnly = ProteusLoc.Localize("Create.Glow.DarkOnly", "Only in the dark");

    public readonly string GlowDarkOnlyTip = ProteusLoc.Localize("Create.Glow.DarkOnly.Tip",
        "Nothing in daylight, glowing in an unlit room — what Atramentum Luminis tattoos did.\n" +
        "The art fades out as the light on you rises, and takes its own surface with it, so\n" +
        "there's skin and nothing else in the bright.\n" +
        "Needs \"React to the scene's light\" in Settings, which is on by default.");

    public readonly string NoIndex = ProteusLoc.Localize("Create.Slot.NoIndex",
        "This material has no index texture, and it isn't skin or face — nothing here\n" +
        "would read a colour-table row selector.");

    public readonly string Intro = ProteusLoc.Localize("Create.Intro",
        "Make a basic Proteus overlay mod. Pick at least one texture; Proteus writes a " +
        "new Penumbra mod and opens it so you can enable and tweak it.");

    public readonly string ModName = ProteusLoc.Localize("Create.ModName.Label", "Mod name") + "###createName";
    public readonly string Author  = ProteusLoc.Localize("Create.Author.Label", "Author") + "###createAuthor";

    public readonly string MaterialTarget =
        ProteusLoc.Localize("Create.MaterialTarget.Label", "Material target") + "###createMaterial";

    public readonly string MaterialTargetTip = ProteusLoc.Localize("Create.MaterialTarget.Tip",
        "The material this overlay composites onto. Auto-filled from the body you're\n" +
        "currently wearing; pick one you have equipped from the list, or type a path by hand to\n" +
        "target a body/race you aren't wearing right now.");

    public readonly string AllRacesFmt = ProteusLoc.Localize("Create.AllRaces.Fmt",
        "Also applies to this body on every other race ({0} materials).");

    public readonly string PickerStale =
        ProteusLoc.Localize("Create.Picker.Stale", "Showing the last known list — character isn't drawn.");

    public readonly string PickerEmpty = ProteusLoc.Localize("Create.Picker.Empty",
        "No equipped materials known yet.\nZone in or redraw, then reopen this list.");

    public readonly string PickerSkin = ProteusLoc.Localize("Create.Picker.SkinGroup", "Skin");

    public readonly string Redetect    = ProteusLoc.Localize("Create.Redetect.Btn", "Re-detect") + "###createRedetect";
    public readonly string RedetectTip = ProteusLoc.Localize("Create.Redetect.Tip", "Re-run body detection and overwrite the field.");

    public readonly string SlotsUnreadable =
        ProteusLoc.Localize("Create.SlotsUnreadable", "Couldn't read this material — offering every slot.");

    public readonly string CreateBtn = ProteusLoc.Localize("Create.Create.Btn", "Create") + "###createGo";

    public readonly string CreateDisabledTip = ProteusLoc.Localize("Create.Create.DisabledTip",
        "Enter a mod name, a material target, and pick at least one texture.");
}

public sealed class SettingsStrings
{
    // ── section headings ────────────────────────────────────────────────────────────────────────────
    public readonly string SecGeneral     = ProteusLoc.Localize("Settings.Section.General", "General");
    public readonly string SecOutput      = ProteusLoc.Localize("Settings.Section.Output", "Output");
    public readonly string SecSkinEffects = ProteusLoc.Localize("Settings.Section.SkinEffects", "Skin effects");
    public readonly string SecHairPort    = ProteusLoc.Localize("Settings.Section.HairPort", "Hair");
    public readonly string SecAutoRefit   = ProteusLoc.Localize("Settings.Section.AutoRefit", "Body refit");

    public readonly string SecHosting     = ProteusLoc.Localize("Settings.Section.Hosting", "Hosting");

    public readonly string SecLightResponse = ProteusLoc.Localize("Settings.Section.LightResponse", "Light-sensitive glow");

    public readonly string ReduceMotion = ProteusLoc.Localize("Settings.ReduceMotion", "Reduce motion") + "###reduceMotion";

    public readonly string ReduceMotionTip = ProteusLoc.Localize("Settings.ReduceMotion.Tip",
        "Hold the window still: hover effects land at once, and the background glow\n"
      + "and the pulse on active mods stop moving.");

    public readonly string AmbientBackground = ProteusLoc.Localize("Settings.AmbientBackground", "Background glow") + "###ambientBackground";

    public readonly string AmbientBackgroundTip = ProteusLoc.Localize("Settings.AmbientBackground.Tip",
        "The soft ember glow drifting behind this window's contents.");

    public readonly string LightResponseEnabled = ProteusLoc.Localize("Settings.LightResponse.Enabled",
        "React to the scene's light");

    public readonly string LightResponseEnabledTip = ProteusLoc.Localize("Settings.LightResponse.Enabled.Tip",
        "Let rows marked \"Fades in light\" dim as the light on you rises.\n"
      + "Off, every glow burns at its authored brightness everywhere, the way it did before.\n"
      + "Nothing is rebuilt either way — this reaches the character through the live material.");

    public readonly string LightResponseManual = ProteusLoc.Localize("Settings.LightResponse.Manual",
        "Set the light level by hand");

    public readonly string LightResponseManualTip = ProteusLoc.Localize("Settings.LightResponse.Manual.Tip",
        "Ignore the scene and use the slider instead.\n"
      + "The quickest way to see what a dark-only glow does without waiting for dusk,\n"
      + "and the right setting for gpose, where your own lighting rig is the point.");

    public readonly string LightResponseLevel = ProteusLoc.Localize("Settings.LightResponse.Level", "Light level");

    public readonly string LightResponseLevelTip = ProteusLoc.Localize("Settings.LightResponse.Level.Tip",
        "0 is pitch dark (dark-only glows at full brightness), 1 is full daylight (they vanish).");

    public readonly string LightResponseReadoutFmt = ProteusLoc.Localize("Settings.LightResponse.Readout.Fmt",
        "Reading {0:0.00} — sky {1:0.00}, lamps {2:0.00} ({3} of {4} reached)");

    /// <summary>The raw layout flags behind the sky term. Deliberately untranslated jargon on one line:
    /// it exists to be screenshotted when the reading disagrees with the room.</summary>
    public readonly string LightResponseSignalsFmt = ProteusLoc.Localize("Settings.LightResponse.Signals.Fmt",
        "outdoor={0}  indoor={1}  envspace={2}  sky={3}");

    public readonly string SecDiagnostics = ProteusLoc.Localize("Settings.Section.Diagnostics", "Diagnostics");

    // ── general ─────────────────────────────────────────────────────────────────────────────────────
    public readonly string Enabled = ProteusLoc.Localize("Settings.General.Enabled.Label", "Enabled");

    public readonly string EnabledTip = ProteusLoc.Localize("Settings.General.Enabled.Tip",
        "Turning this off clears Proteus' output, redraws you without it,\n" +
        "and disables the managed \"Proteus\" mod in Penumbra.");

    public readonly string AutoRedraw =
        ProteusLoc.Localize("Settings.General.AutoRedraw.Label", "Auto redraw") + "###autoRedraw";

    public readonly string AutoRedrawTip = ProteusLoc.Localize("Settings.General.AutoRedraw.Tip",
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
        ProteusLoc.Localize("Settings.General.AutoRaise.Label", "Auto-raise mod priority") + "###autoRaise";

    public readonly string AutoRaiseTip = ProteusLoc.Localize("Settings.General.AutoRaise.Tip",
        "When another mod is confirmed to be overriding a skin texture Proteus composites\n" +
        "into — a tattoo or skin pack shipping its own copy of the body texture — raise\n" +
        "Proteus' Penumbra priority above it automatically, and say so in chat.\n\n" +
        "That override is otherwise invisible: overlays half-apply (the bumps land, the\n" +
        "colour doesn't) and every log line still reads as a success.\n\n" +
        "Turn off only if you deliberately want another mod to win a path Proteus\n" +
        "composites. Proteus never acts on a guess — only on a confirmed override.");

    public readonly string InPlaceReload =
        ProteusLoc.Localize("Settings.General.InPlaceReload.Label", "In-place reload") + "###inPlaceReload";

    public readonly string InPlaceReloadTip = ProteusLoc.Localize("Settings.General.InPlaceReload.Tip",
        "Refresh textures via Glamourer's in-place equipment reload instead of a full\n" +
        "redraw, avoiding the despawn/respawn flicker. Falls back to a full redraw\n" +
        "automatically when Glamourer can't service it.");

    public readonly string GlowLibrary =
        ProteusLoc.Localize("Settings.General.GlowLibrary.Btn", "Glow Effect Textures") + "###glowLibrary";

    public readonly string GlowLibraryTipFmt = ProteusLoc.Localize("Settings.General.GlowLibrary.Tip.Fmt",
        "Open the folder Proteus reads animated-glow scroll maps from — the \"_o\"\n" +
        "textures that ARE the glow. Anything dropped in here appears in every\n" +
        "gear overlay's Effect dropdown.\n\n" +
        "{0}\n\n" +
        "Accepts .tex, .dds, .png, .jpg, .bmp, .tga, .psd and .gif.\n" +
        "A mod's own Proteus/Effects/ folder takes precedence over it.");

    // ── output ──────────────────────────────────────────────────────────────────────────────────────
    public readonly string Compression =
        ProteusLoc.Localize("Settings.Output.Compression.Label", "Enable Compression") + "###enableCompression";

    public readonly string CompressionTip = ProteusLoc.Localize("Settings.Output.Compression.Tip",
        "Block-compress the baked textures (BC7), cutting each to about a quarter of its\n" +
        "uncompressed size on disk and in VRAM. The index texture stays uncompressed to keep\n" +
        "its exact row values. Off = uncompressed (byte-identical to before).\n\n" +
        "Costs processor time on EVERY refresh, and it is the slowest thing Proteus does.\n" +
        "On an older or low-core PC it can add a minute or more per change, which is long\n" +
        "enough that a new change restarts the work before it finishes and your look stops\n" +
        "appearing at all. Leave this off unless you are short of video memory, and turn it\n" +
        "off first if nothing is being drawn.");

    public readonly string CompressionRefusedFmt = ProteusLoc.Localize("Settings.Output.CompressionRefused.Fmt",
        "Not in use: this PC compresses too slowly ({0:F0} ms per megapixel), and doing it would\n" +
        "take long enough that your look would stop being drawn. Textures are being baked\n" +
        "uncompressed instead. Untick the box to hide this.");

    public readonly string SharpAlpha =
        ProteusLoc.Localize("Settings.Output.SharpAlpha.Label", "Sharp alpha (gpose sphere/metal)") + "###sharpAlpha";

    public readonly string SharpAlphaTip = ProteusLoc.Localize("Settings.Output.SharpAlpha.Tip",
        "EXPERIMENTAL. Renders shell coverage as a hard alpha-test cutout instead of smooth\n" +
        "transparency, so sphere maps and metalness survive gpose (which drops them on\n" +
        "transparent surfaces). Trade-off: sheer edges become hard/aliased. Best for\n" +
        "mostly-opaque fabrics; a very sheer fabric will look coarse. Recomposite after toggling.");

    // ── hosting ─────────────────────────────────────────────────────────────────────────────────────
    public readonly string InvisibleGlasses = ProteusLoc.Localize(
        "Settings.Hosting.InvisibleGlasses.Label", "Host on invisible glasses (keep rings free)") + "###invisibleGlasses";

    public readonly string InvisibleGlassesTip = ProteusLoc.Localize("Settings.Hosting.InvisibleGlasses.Tip",
        "When on and you have no glasses equipped, Proteus has Glamourer equip an\n" +
        "invisible glasses item so the second skin rides the facewear slot instead of a\n" +
        "ring. This writes a (hidden) bonus item to your Glamourer state; it's removed\n" +
        "when you disable Proteus, equip real glasses, or turn this off.");

    public readonly string RestoreAccessory =
        ProteusLoc.Localize("Settings.Hosting.RestoreAccessory.Btn", "Restore changed accessory") + "###restoreAccessory";

    public readonly string RestoreAccessoryTip = ProteusLoc.Localize("Settings.Hosting.RestoreAccessory.Tip",
        "Force a full redraw to reload any ring/bracelet the second skin replaced,\n" +
        "restoring it to its original model. Use if a gear shell stays stuck on an\n" +
        "accessory after disabling or swapping.");

    // ── diagnostics ─────────────────────────────────────────────────────────────────────────────────
    public readonly string ClearCache =
        ProteusLoc.Localize("Settings.Diag.ClearCache.Btn", "Clear texture cache") + "###clearCache";

    public readonly string ClearCacheTip = ProteusLoc.Localize("Settings.Diag.ClearCache.Tip",
        "Drop all cached decoded textures and recomposite now. Use if a texture edit\n" +
        "isn't showing up — e.g. you re-exported an overlay at the same size and the\n" +
        "change won't appear without restarting the plugin.\n\n" +
        "Also re-derives which mod each base skin texture comes from — use this if the\n" +
        "Base skin below names the wrong mod.");

    public readonly string CopyLogs =
        ProteusLoc.Localize("Settings.Diag.CopyLogs.Btn", "Copy Logs") + "###copyLogs";

    public readonly string CopyLogsBusy =
        ProteusLoc.Localize("Settings.Diag.CopyLogs.Busy", "Refreshing and capturing…") + "###copyLogs";

    public readonly string CopyLogsTip = ProteusLoc.Localize("Settings.Diag.CopyLogs.Tip",
        "Run a full refresh and save everything Proteus logs while it runs to a text\n" +
        "file on your desktop. Attach that file when you report a problem.");

    public readonly string CopyLogsSaved = ProteusLoc.Localize("Settings.Diag.CopyLogs.Saved", "Saved:");

    public readonly string CopyLogsOpenTip = ProteusLoc.Localize("Settings.Diag.CopyLogs.OpenTip", "Click to open the file.");

    public readonly string CopyLogsShowInFolder =
        ProteusLoc.Localize("Settings.Diag.CopyLogs.ShowInFolder", "Show in folder") + "###copyLogsFolder";

    public readonly string CopyLogsFailedFmt =
        ProteusLoc.Localize("Settings.Diag.CopyLogs.Failed.Fmt", "Could not save the log: {0}");

    public readonly string BaseSkinHeaderFmt = ProteusLoc.Localize("Settings.Diag.BaseSkin.Header.Fmt", "Base skin ({0})");

    public readonly string BaseSkinUnconfirmedFmt =
        ProteusLoc.Localize("Settings.Diag.BaseSkin.Unconfirmed.Fmt", "{0} (unconfirmed)");

    public readonly string BaseSkinNote = ProteusLoc.Localize("Settings.Diag.BaseSkin.Note",
        "The mod each base texture is read from. Hover for the game path.");

    public readonly string ReachHeaderFmt = ProteusLoc.Localize("Settings.Diag.Reach.Header.Fmt", "Overlay reach ({0})");

    public readonly string ReachFailedFmt = ProteusLoc.Localize("Settings.Diag.Reach.Failed.Fmt",
        "{0} — diffuse {1}, normal {2}, mask {3} (diffuse did not apply)");

    public readonly string ReachEffectsOnlyFmt =
        ProteusLoc.Localize("Settings.Diag.Reach.EffectsOnly.Fmt", "{0} — effects only (no overlay art)");

    public readonly string ReachOkFmt =
        ProteusLoc.Localize("Settings.Diag.Reach.Ok.Fmt", "{0} — diffuse {1}, normal {2}, mask {3}");

    public readonly string ReachNote = ProteusLoc.Localize("Settings.Diag.Reach.Note",
        "How many overlays actually reached each channel. Hover for the material.");

    // ── skin effects ────────────────────────────────────────────────────────────────────────────────
    public readonly string SkinTint = ProteusLoc.Localize("Settings.Skin.SkinTint.Label", "Skin-tint suppression");

    public readonly string SkinTintTip = ProteusLoc.Localize("Settings.Skin.SkinTint.Tip",
        "How strongly overlays resist skin-tone tinting (global multiplier).\n" +
        "Applied per pixel by color: white/bright dyes keep their authored color on any\n" +
        "skin tone (slightly shinier), dark dyes stay skin-tinted and matte automatically.\n" +
        "0.00 disables it entirely (original look).");

    public readonly string AmbientOcclusion = ProteusLoc.Localize("Settings.Skin.AmbientOcclusion.Label", "Ambient occlusion");

    public readonly string AmbientOcclusionTip = ProteusLoc.Localize("Settings.Skin.AmbientOcclusion.Tip",
        "Soft contact shadow on the skin just outside masked strap edges, giving straps depth.\n" +
        "0.00 disables it entirely (skin diffuse unchanged).");

    public readonly string ShadowSoftness = ProteusLoc.Localize("Settings.Skin.ShadowSoftness.Label", "Shadow softness");

    public readonly string ShadowSoftnessTip = ProteusLoc.Localize("Settings.Skin.ShadowSoftness.Tip",
        "How far the ambient-occlusion shadow spreads from a strap edge (fraction of texture width).\n" +
        "Larger = wider, softer shadow. Shared by the shadow and the strap indent.");

    public readonly string Skindenting = ProteusLoc.Localize("Settings.Skin.Skindenting.Label", "Skindenting");

    public readonly string SkindentingTip = ProteusLoc.Localize("Settings.Skin.Skindenting.Tip",
        "Indents the skin normal at strap/garment edges so straps look pressed into the skin.\n" +
        "0.00 disables it (skin normal unchanged). Uses the same edges/softness as the shadow.");

    // ── cache + meshes ──────────────────────────────────────────────────────────────────────────────
    public readonly string TextureCache = ProteusLoc.Localize("Settings.Output.TextureCache.Label", "Texture cache (MB)");

    /// <summary>The slider's own value text — a printf format, so the %d stays exactly as it is.</summary>
    public readonly string TextureCacheFormat = ProteusLoc.Localize("Settings.Output.TextureCache.Format", "%d MB");

    public readonly string TextureCacheTip = ProteusLoc.Localize("Settings.Output.TextureCache.Tip",
        "How much decoded texture data Proteus keeps in memory between composites.\n\n" +
        "A 4K texture costs 64 MB decoded, so this is really a count: 2048 MB ≈ 30 of them.\n" +
        "It only helps if it covers a whole composite's worth — below that, every run evicts\n" +
        "what the next one needs and nothing is reused.\n\n" +
        "Check the \"cache N entries, M MB\" figure in the recomposite log: if a SECOND\n" +
        "composite with nothing changed still reports misses, raise this. Lower it if the\n" +
        "game starts paging. Released automatically after 60s idle.");

    public readonly string RedundantMeshes =
        ProteusLoc.Localize("Settings.Output.RedundantMeshes.Label", "Hide redundant body meshes")
        + "###hideRedundantMeshes";

    public readonly string RedundantMeshesTip = ProteusLoc.Localize("Settings.Output.RedundantMeshes.Tip",
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
    public readonly string Intro = ProteusLoc.Localize("Content.Intro",
        "Import a regular mod (.pmp, or the meta.json of one already installed in Penumbra). Wear parts of it "
      + "without using a gear slot, and add advanced colour table features.");

    public readonly string ReadFailedFmt = ProteusLoc.Localize("Content.ReadFailed.Fmt",
        "Couldn't read that pack: {0}");

    /// <summary>A .json picked that isn't an installed mod's manifest: the filter can only match by extension.</summary>
    public readonly string NotAManifest = ProteusLoc.Localize("Content.NotAManifest",
        "To import a mod installed in Penumbra, pick the meta.json in its folder.");

    public readonly string PieceCountFmt = ProteusLoc.Localize("Content.PieceCount.Fmt",
        "Pieces: {0} of {1}");

    public readonly string RacesFmt = ProteusLoc.Localize("Content.Races.Fmt", "{0} races");

    public readonly string AllOffFmt = ProteusLoc.Localize("Content.AllOff.Fmt",
        "Pieces arrive switched OFF. After importing, tick the ones you want under \"{0}\" in Penumbra — "
      + "nothing is worn until you do.");

    public readonly string GeometryFmt = ProteusLoc.Localize("Content.Geometry.Fmt", "{0} mesh, {1} verts");
    public readonly string MaterialsFmt = ProteusLoc.Localize("Content.Materials.Fmt", "{0} materials");
    public readonly string Skipped = ProteusLoc.Localize("Content.Skipped", "skipped");
    public readonly string Unbound = ProteusLoc.Localize("Content.Unbound", "unbound material");

    /// <summary>In place of the mesh count for an invisible placeholder that only carries an animated effect.</summary>
    public readonly string EffectOnly = ProteusLoc.Localize("Content.EffectOnly", "animated effect");
    public readonly string WithEffectFmt = ProteusLoc.Localize("Content.WithEffect.Fmt", "{0} + effect");
    public readonly string EffectTip = ProteusLoc.Localize("Content.Effect.Tip",
        "This piece has an animated effect. Proteus plays it on an invisible accessory it puts in a free slot, so "
      + "you do not need to wear the item it replaced.");

    /// <summary>The deliberate drop, said plainly and NOT in the warning colour — an outfit pack ships the
    /// body it was fitted to, the wearer already has one, and leaving it out is the wanted outcome.</summary>
    public readonly string BodyOnly = ProteusLoc.Localize("Content.BodyOnly", "wearer's own body");

    /// <summary>Shown in place of the piece table for a pack that already carries a Proteus sidecar: it is
    /// copied in unchanged rather than converted. Not a warning — this is the right outcome.</summary>
    public readonly string AlreadyProteus = ProteusLoc.Localize("Content.AlreadyProteus",
        "This pack is already a Proteus mod. It will be installed exactly as its author built it — nothing "
      + "is converted, and its own options stay in Penumbra where they are.");

    public readonly string ProblemFmt = ProteusLoc.Localize("Content.Problem.Fmt", "{0}\nSkipped: {1}");

    public readonly string NothingUsable = ProteusLoc.Localize("Content.NothingUsable",
        "No option in this pack ships a mesh Proteus can append.");

    public readonly string SharedByFmt = ProteusLoc.Localize("Content.SharedBy.Fmt",
        "Shared by: {0}. Those pieces are drawn with one material, so these colours reach all of them — and "
      + "rows you don't touch stay exactly as the pack's author wrote them.");

    /// <summary>Stands in for an option name in <see cref="SharedByFmt"/> when a piece belongs to no
    /// option — a model the pack applies whenever it is enabled.</summary>
    public readonly string Unconditional = ProteusLoc.Localize("Content.Unconditional", "always on");

    public readonly string NotForYourRaceFmt = ProteusLoc.Localize("Content.NotForYourRace.Fmt",
        "This pack's models are built for {0}, and you are {1}. Gear made for one race is a different shape, "
      + "so wearing it as-is would put it in the wrong place — Proteus leaves it off rather than show that.");

    public readonly string NoRaceFitFmt = ProteusLoc.Localize("Content.NoRaceFit.Fmt",
        "The nearest model this pack has is built for {0}, which is neither your own race ({1}) nor the "
      + "shared shape the game resizes for everyone. Proteus leaves it off rather than show it at the wrong "
      + "size.");

    public readonly string SamplesFmt = ProteusLoc.Localize("Content.Samples.Fmt",
        "Its index texture reads row {0}, column {1} — the other rows are dimmed because nothing samples "
      + "them, and editing the other column of this row will do nothing either.");

    public readonly string NoIndexFmt = ProteusLoc.Localize("Content.NoIndex.Fmt",
        "This material ships no index texture, so it takes row {0} for everything.");

    public readonly string SamplesRowsFmt = ProteusLoc.Localize("Content.SamplesRows.Fmt",
        "Its index texture reads rows {0} — the others are dimmed because nothing samples them.");

    public readonly string IndexUnreadable = ProteusLoc.Localize("Content.IndexUnreadable",
        "This material names an index texture Proteus couldn't read, so it can't tell which rows are live. "
      + "Every row is editable below, but only the ones the index selects will show.");

    public readonly string GlowNeedsEmissiveFmt = ProteusLoc.Localize("Content.GlowNeedsEmissive.Fmt",
        "Glow is at zero on row {0}{1} — the one cell this material reads — so the effect stays off. Raise "
      + "Glow there to turn it on and set how strongly it shows. Glow on any other row does nothing.");

    public readonly string GlowEffectMissingFmt = ProteusLoc.Localize("Content.GlowEffectMissing.Fmt",
        "The effect \"{0}\" is no longer in this mod's Effects folder or your library, so the piece is "
      + "rendering without it. Pick another, or put the file back.");

    public readonly string GlowDropsDiffuse = ProteusLoc.Localize("Content.GlowDropsDiffuse",
        "This pack paints its surface with a texture. An animated glow runs the material on a shader that "
      + "has no slot for one, so the colours above take over while the glow is on. Clearing the effect puts "
      + "the texture back.");

    public readonly string NoColorTable = ProteusLoc.Localize("Content.NoColorTable",
        "This material carries no colour table, so it has no rows to edit — nothing you change below will "
      + "reach the piece. Its colours come from its textures alone.");

    public readonly string FollowsHairColor = ProteusLoc.Localize("Content.FollowsHairColor",
        "This material uses the hair shader, so the piece takes your character's hair colour and highlights "
      + "and carries no colour table — nothing you change below will reach it. Change your hair colour to "
      + "recolour it.");

    public readonly string IndexCompressedFmt = ProteusLoc.Localize("Content.IndexCompressed.Fmt",
        "Its index texture reads row {0}, column {1} — but that texture is compressed, so it could be a row "
      + "or two out. The dimmed rows are still clickable: if a colour doesn't take, try one either side.");

    /// <summary>Stands in for the column letter when an index uses both — see <see cref="IndexCompressedFmt"/>.</summary>
    public readonly string EitherColumn = ProteusLoc.Localize("Content.EitherColumn", "A and B");

    public readonly string IndexEmpty = ProteusLoc.Localize("Content.IndexEmpty",
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
    public readonly string Intro = ProteusLoc.Localize("Import.Intro",
        "Import an Onion overlay pack (.omp). Wear its layers as Proteus overlays you can recolour and "
      + "restack.");

    /// <summary>Shared by BOTH formats — see DrawImportTab, which has one browse button and picks the
    /// reader off the extension.</summary>
    public readonly string BrowseBtn    = ProteusLoc.Localize("Import.Browse.Btn", "Browse for a pack") + "###importBrowse";
    public readonly string DialogTitle  = ProteusLoc.Localize("Import.Dialog.Title", "Select a pack");
    public readonly string DialogFilter = ProteusLoc.Localize("Import.Dialog.Filter", "Mod pack");

    public readonly string NoPack = ProteusLoc.Localize("Import.NoPack", "Pick a pack to see what it contains.");

    public readonly string ModName = ProteusLoc.Localize("Import.ModName.Label", "Mod name") + "###importName";
    public readonly string Author  = ProteusLoc.Localize("Import.Author.Label", "Author") + "###importAuthor";

    public readonly string Description  = ProteusLoc.Localize("Import.Description", "Description");
    public readonly string WebsiteTip   = ProteusLoc.Localize("Import.Website.Tip", "Carried into the mod's Penumbra page.");

    public readonly string LayoutsFmt = ProteusLoc.Localize("Import.Layouts.Fmt",
        "The pack has {0} UV layouts, so the mod gets a single-select \"{1}\" group in Penumbra ({2}). " +
        "Only one composites at a time.");

    public readonly string DefaultLayoutMatchedFmt = ProteusLoc.Localize("Import.DefaultLayoutMatched.Fmt",
        "\"{0}\" will be selected — it matches the body you're wearing.");

    public readonly string AsTex = ProteusLoc.Localize("Import.AsTex.Label", "Convert layers to BC7 .tex") + "###importAsTex";

    public readonly string AsTexTip = ProteusLoc.Localize("Import.AsTex.Tip",
        "Roughly a quarter of the disk size, and no PNG decode at composite time.\n" +
        "Block compression is lossy — leave it off to keep the pack's images exactly as authored.");

    public readonly string AsTexUnavailableTip = ProteusLoc.Localize("Import.AsTex.Unavailable.Tip",
        "The native block compressor isn't loaded, so BC7 encoding would take minutes.");

    public readonly string ImportBtn  = ProteusLoc.Localize("Import.Import.Btn", "Import") + "###importGo";
    public readonly string ImportBusy = ProteusLoc.Localize("Import.Importing.Btn", "Importing…") + "###importGo";

    public readonly string NeedName    = ProteusLoc.Localize("Import.NeedName", "Enter a mod name.");
    public readonly string NothingUsable = ProteusLoc.Localize("Import.NothingUsable", "No layer in this pack can be imported.");

    public readonly string LayerCountFmt = ProteusLoc.Localize("Import.LayerCount.Fmt", "Layers: {0} of {1}");

    public readonly string NoLayout = ProteusLoc.Localize("Import.Layer.NoLayout", "(no layout)");
    public readonly string NoMap    = ProteusLoc.Localize("Import.Layer.NoMap", "(no map)");
    public readonly string Skipped  = ProteusLoc.Localize("Import.Layer.Skipped", "skipped");

    public readonly string LayerImportedFmt =
        ProteusLoc.Localize("Import.Layer.Imported.Fmt", "{0}\nImported as {1} in {2} UV space.");

    public readonly string LayerSkippedFmt = ProteusLoc.Localize("Import.Layer.SkippedReason.Fmt", "{0}\nSkipped: {1}");

    public readonly string NotDrawnFmt = ProteusLoc.Localize("Import.BodyFit.NotDrawn.Fmt",
        "Your character isn't drawn yet, so Proteus picked \"{0}\" by preference rather than by your " +
        "body. Check the result once you're in game.");

    public readonly string RemappedFmt = ProteusLoc.Localize("Import.BodyFit.Remapped.Fmt",
        "This pack has nothing for your {0} body, so \"{1}\" will be remapped onto it automatically.");

    public readonly string FallbackBodies = ProteusLoc.Localize("Import.Materials.Fallback",
        "Proteus couldn't read the game's body list, so this import will target a known-good set of " +
        "female bodies only. Reopen this pack once you're in game to pick up every race.");

    public readonly string MaterialTargetsFmt =
        ProteusLoc.Localize("Import.Materials.Header.Fmt", "Material targets ({0})");

    public readonly string MaterialsFromGame = ProteusLoc.Localize("Import.Materials.FromGame",
        "Every body the game defines, so the overlay follows you across races. " +
        "Bodies you aren't wearing are ignored at composite time.");

    public readonly string MaterialsFallbackNote = ProteusLoc.Localize("Import.Materials.FallbackNote",
        "Fallback list — the game data couldn't be read when this pack was opened.");

    public readonly string LayoutGroupFmt = ProteusLoc.Localize("Import.Materials.Layout.Fmt", "{0}  ({1})");

    public readonly string ImportFailedFmt = ProteusLoc.Localize("Import.Failed.Fmt", "Import failed: {0}");

    /// <summary>The Onion layer table's last column: {0} is the layer's size in megabytes.</summary>
    public readonly string LayerSizeFmt = ProteusLoc.Localize("Import.Layer.Size.Fmt", "{0:0.#} MB");
}

/// <summary>The Atramentum Luminis (<c>.ttmp2</c>) half of the Import tab.</summary>
public sealed class LuminisStrings
{
    /// <summary>
    /// The third line of the Import tab's intro, in the same voice as the other two: what you get, not how.
    /// Names the old mod outright, because that name is the only reason anyone has one of these files —
    /// the packs themselves say "requires Atramentum Luminis" and nothing else identifies them.
    /// </summary>
    public readonly string Intro = ProteusLoc.Localize("Luminis.Intro",
        "Import an Atramentum Luminis glow tattoo (.ttmp2). Its glow becomes a Proteus overlay you can "
      + "recolour and dim, with no shader mod needed.");

    public readonly string ReadFailedFmt = ProteusLoc.Localize("Luminis.ReadFailed.Fmt",
        "Couldn't read that modpack: {0}");

    public readonly string TextureCountFmt = ProteusLoc.Localize("Luminis.TextureCount.Fmt",
        "Textures: {0} of {1}");

    /// <summary>How much of the sheet carries a glow mask — the number that says whether this really is an
    /// Atramentum Luminis pack, so it goes in the table rather than in a tooltip.</summary>
    public readonly string GlowFmt = ProteusLoc.Localize("Luminis.Glow.Fmt", "{0:P1} glows");

    public readonly string SizeFmt = ProteusLoc.Localize("Luminis.Size.Fmt", "{0}×{1}");

    /// <summary>Several manifest paths over one picture, which is the normal shape of these packs.</summary>
    public readonly string AliasesFmt = ProteusLoc.Localize("Luminis.Aliases.Fmt", "{0} paths");

    public readonly string Skipped = ProteusLoc.Localize("Luminis.Skipped", "skipped");

    public readonly string SkippedReasonFmt = ProteusLoc.Localize("Luminis.SkippedReason.Fmt", "{0}\nSkipped: {1}");

    public readonly string PathsFmt = ProteusLoc.Localize("Luminis.Paths.Fmt", "Imported once, for:\n{0}");

    /// <summary>Said before the button. The author's body texture is the surprising half of this import —
    /// it is a whole body texture, not a tattoo — so what it is, and that it comes on, are stated up
    /// front. The key was renamed when the option stopped starting off, so that a lagging translation
    /// falls back to English rather than telling the user the opposite of what happens.</summary>
    public readonly string SkinIncludedFmt = ProteusLoc.Localize("Luminis.SkinIncluded.Fmt",
        "Both halves go on: the glow, and the author's own body texture beneath it, under \"{0}\" in "
      + "Penumbra. The body texture carries the parts of the tattoo that don't glow, and it keeps your own "
      + "skin tone rather than the author's — untick it there if you only want the glow.");

    public readonly string BodyTarget = ProteusLoc.Localize("Luminis.BodyTarget.Label", "Body") + "###luminisBody";

    public readonly string BodyTargetTip = ProteusLoc.Localize("Luminis.BodyTarget.Tip",
        "Which body material the art is painted onto. Proteus picks this from the pack when it recognises "
      + "the body, and from the one you're wearing when it doesn't — change it if you know the pack was "
      + "made for a different one.");

    public readonly string BodyFromPackFmt = ProteusLoc.Localize("Luminis.BodyFromPack.Fmt",
        "The pack says it is painted for {0}, so Proteus will resize it onto whichever body you wear.");

    /// <summary>
    /// Stated plainly rather than in the warning colour. Proteus has never had a race or sex filter — it
    /// is a standing property of every overlay, not something this pack did — and amber on every import is
    /// the cried-wolf problem <see cref="ContentStrings.BodyOnly"/> exists to avoid.
    /// </summary>
    public readonly string NoRaceFilter = ProteusLoc.Localize("Luminis.NoRaceFilter",
        "Proteus has no race or sex filter: this paints any character wearing a body with the same "
      + "material. Turn the mod off in Penumbra for characters it wasn't painted for.");

    public readonly string NothingUsable = ProteusLoc.Localize("Luminis.NothingUsable",
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
    public readonly string Intro = ProteusLoc.Localize("Emissive.Intro",
        "Import an emissive skin pack (.pmp of glow art with no models). Its glow becomes a Proteus overlay "
      + "you can recolour and dim, with no shader mod needed.");

    public readonly string ReadFailedFmt = ProteusLoc.Localize("Emissive.ReadFailed.Fmt",
        "Couldn't read that pack: {0}");

    /// <summary>
    /// Shown between the pick and the preview, which for this one format are different frames. These masks
    /// are full body sheets — the reference pack's is 8192² and takes a second to decode — so the read runs
    /// on the pool and the tab has to say what it is waiting for rather than fall through to "pick a pack".
    /// </summary>
    public readonly string Reading = ProteusLoc.Localize("Emissive.Reading",
        "Reading this pack's textures…");

    public readonly string TextureCountFmt = ProteusLoc.Localize("Emissive.TextureCount.Fmt",
        "Textures: {0} of {1}");

    /// <summary>How much of the sheet the mask marks out — the number that says whether this really is glow
    /// art, so it goes in the table rather than in a tooltip.</summary>
    public readonly string GlowFmt = ProteusLoc.Localize("Emissive.Glow.Fmt", "{0:P1} glows");

    public readonly string SizeFmt = ProteusLoc.Localize("Emissive.Size.Fmt", "{0}×{1}");

    /// <summary>Several manifest paths over one picture, which some of these packs do.</summary>
    public readonly string AliasesFmt = ProteusLoc.Localize("Emissive.Aliases.Fmt", "{0} paths");

    public readonly string Skipped = ProteusLoc.Localize("Emissive.Skipped", "skipped");

    public readonly string SkippedReasonFmt = ProteusLoc.Localize("Emissive.SkippedReason.Fmt", "{0}\nSkipped: {1}");

    public readonly string PathsFmt = ProteusLoc.Localize("Emissive.Paths.Fmt", "Imported once, for:\n{0}");

    public readonly string BodyTarget = ProteusLoc.Localize("Emissive.BodyTarget.Label", "Body") + "###emissiveBody";

    public readonly string BodyTargetTip = ProteusLoc.Localize("Emissive.BodyTarget.Tip",
        "Which body material the art is painted onto. Proteus picks this from the pack when it recognises "
      + "the body, and from the one you're wearing when it doesn't — change it if you know the pack was "
      + "made for a different one.");

    public readonly string BodyFromPackFmt = ProteusLoc.Localize("Emissive.BodyFromPack.Fmt",
        "The pack says it is painted for {0}, so Proteus will resize it onto whichever body you wear.");

    /// <summary>
    /// Said before the button, because it is the surprising half of this import: the pack's own redirects
    /// are dropped. They are body materials rewired to name an emissive sampler that only a replaced
    /// skin.shpk has, and republishing them would put the imported mod into a fight with Proteus over the
    /// very material it composites into. Plain text, not the warning colour — this is true of a CORRECT
    /// import.
    /// </summary>
    public readonly string MaterialsIgnored = ProteusLoc.Localize("Emissive.MaterialsIgnored",
        "Only the glow art is imported. The pack's own body materials are left behind: they only work with "
      + "a replaced skin shader, and Proteus paints the glow onto whatever skin you already wear.");

    /// <summary>Stated plainly rather than in the warning colour, for the reason
    /// <see cref="LuminisStrings.NoRaceFilter"/> gives.</summary>
    public readonly string NoRaceFilter = ProteusLoc.Localize("Emissive.NoRaceFilter",
        "Proteus has no race or sex filter: this paints any character wearing a body with the same "
      + "material. Turn the mod off in Penumbra for characters it wasn't painted for.");

    public readonly string NothingUsable = ProteusLoc.Localize("Emissive.NothingUsable",
        "No texture in this pack carries a glow mask.");
}

/// <summary>The loose eye-texture pack (<c>.zip</c>) half of the Import tab.</summary>
public sealed class EyeStrings
{
    /// <summary>
    /// The fourth bullet of the Import tab's intro, in the same voice as the other three: what you get.
    /// Says "animated" because that is the only part Penumbra cannot already do on its own.
    /// </summary>
    public readonly string Intro = ProteusLoc.Localize("Eye.Intro",
        "Import an eye texture pack (.zip of loose images). Its eyes go in as normal, and the shape its "
      + "mask marks out gets an animated glow you can recolour and dim.");

    public readonly string ReadFailedFmt = ProteusLoc.Localize("Eye.ReadFailed.Fmt",
        "Couldn't read that archive: {0}");

    public readonly string TextureCountFmt = ProteusLoc.Localize("Eye.TextureCount.Fmt",
        "Textures: {0} of {1}");

    public readonly string Skipped = ProteusLoc.Localize("Eye.Skipped", "skipped");

    public readonly string SkippedReasonFmt = ProteusLoc.Localize("Eye.SkippedReason.Fmt", "{0}\nSkipped: {1}");

    /// <summary>Said before the Import button: what the glow will cover, and where to switch it off.</summary>
    public readonly string GlowFmt = ProteusLoc.Localize("Eye.Glow.Fmt",
        "The mask marks {0:P1} of the sheet as glowing, and that shape gets the animation — on {1} iris "
      + "material(s), so it follows you across races and faces. Switch it off under \"{2}\" in Penumbra.");

    public readonly string NoGlow = ProteusLoc.Localize("Eye.NoGlow",
        "No animated glow will be added — see below. The eye textures still import and work normally.");

    public readonly string FallbackIrises = ProteusLoc.Localize("Eye.FallbackIrises",
        "Proteus couldn't read the game's face list, so the glow will target a known-good pair of faces "
      + "only. Reopen this pack once you're in game to pick up every race.");

    public readonly string NothingUsable = ProteusLoc.Localize("Eye.NothingUsable",
        "Nothing in this archive looks like an eye texture.");

    public readonly string CutoutLabel = ProteusLoc.Localize("Eye.Cutout.Label", "Glow shape") + "###eyeCutout";

    public readonly string CutoutFalloff = ProteusLoc.Localize("Eye.Cutout.Falloff", "Artwork and its falloff");
    public readonly string CutoutArtwork = ProteusLoc.Localize("Eye.Cutout.Artwork", "Artwork only");

    /// <summary>Says why this is chosen here rather than tuned later — it is baked into the written art,
    /// and the Glow dial can only scale what survived it.</summary>
    public readonly string CutoutTip = ProteusLoc.Localize("Eye.Cutout.Tip",
        "How much of the mask's glow channel is cut into the shape that glows. Most packs mark the artwork "
      + "brightly and fade out around it; keeping that falloff glows softly beyond the artwork, dropping it "
      + "confines the glow to the shape itself.\n"
      + "Baked in at import, so pick it now — afterwards the Glow dial can only scale what's left.");

    // Which of the game's three eye textures a file replaces: the archive table's second column, and the
    // {0} of Import.Eye.Skip.Duplicate.Fmt.
    public readonly string SlotBase   = ProteusLoc.Localize("Eye.Slot.Base", "Base");
    public readonly string SlotMask   = ProteusLoc.Localize("Eye.Slot.Mask", "Mask");
    public readonly string SlotNormal = ProteusLoc.Localize("Eye.Slot.Normal", "Normal");

    public string SlotName(Services.EyeSlot slot) => slot switch
    {
        Services.EyeSlot.Base => SlotBase,
        Services.EyeSlot.Mask => SlotMask,
        _                     => SlotNormal,
    };
}

public sealed class ExportStrings
{
    public readonly string Intro = ProteusLoc.Localize("Export.Intro",
        "Save one of your Proteus mods as a Penumbra mod pack (.pmp) to share it. " +
        "Everything goes in — options, colours, masks and glow effects — and the file installs " +
        "straight into Penumbra.");

    public readonly string ModCombo = ProteusLoc.Localize("Export.Mod.Label", "Mod") + "###exportmod";
    public readonly string FilterHint = ProteusLoc.Localize("Export.Filter.Hint", "Filter…");

    public readonly string DisabledInPenumbraFmt =
        ProteusLoc.Localize("Export.DisabledInPenumbra.Fmt", "{0}\n(disabled in Penumbra)");

    public readonly string NoMatchFmt = ProteusLoc.Localize("Export.NoMatch.Fmt", "Nothing matches \"{0}\".");

    public readonly string ModDisabledNote = ProteusLoc.Localize("Export.ModDisabled.Note",
        "This mod is disabled in Penumbra — it exports the same either way.");

    // One id for all three captions: the button changes what it says as the export progresses, and
    // without a shared id it would be three different widgets in a row.
    public readonly string Choosing  = ProteusLoc.Localize("Export.Phase.Choosing", "Choose a location…") + "###exportGo";
    public readonly string Exporting = ProteusLoc.Localize("Export.Phase.Writing", "Exporting…") + "###exportGo";
    public readonly string ExportBtn = ProteusLoc.Localize("Export.Phase.Idle", "Export") + "###exportGo";

    public readonly string DialogTitle  = ProteusLoc.Localize("Export.Dialog.Title", "Export Proteus mod");
    public readonly string DialogFilter = ProteusLoc.Localize("Export.Dialog.Filter", "Penumbra mod pack");

    public readonly string FailedFmt = ProteusLoc.Localize("Export.Failed.Fmt", "Export failed: {0}");
}

/// <summary>
/// The colour-table editor itself. Every label here is drawn by a widget that also uses it as an ImGui
/// id — but every one of those call sites already carries an explicit <c>##scope</c> suffix, so the
/// visible half is free to change with the language without moving any identity.
/// </summary>
public sealed class ColorsStrings
{
    public readonly string SphereTip = ProteusLoc.Localize("Colors.Sphere.Tip",
        "Reflects a slice of the game's shared sphere map array.\n" +
        "Index AND intensity must both be non-zero, or nothing happens.\n" +
        "Does NOT work under characterscroll.shpk — use character.shpk.");

    public readonly string MetalTip = ProteusLoc.Localize("Colors.Metal.Tip",
        "Metal has no diffuse colour of its own — it shows what it reflects.\n" +
        "With no sphere map to reflect, a metallic surface just goes dark.");

    // ── render modes ────────────────────────────────────────────────────────────────────────────────
    public readonly string ModeSkin  = ProteusLoc.Localize("Colors.Mode.Skin", "Skin (painted)");
    public readonly string ModeCloth = ProteusLoc.Localize("Colors.Mode.Cloth", "Cloth");
    public readonly string ModeGlow  = ProteusLoc.Localize("Colors.Mode.AnimatedGlow", "Animated glow");

    public readonly string RenderingAs = ProteusLoc.Localize("Colors.RenderingAs", "Rendering as:");

    public readonly string Advanced = ProteusLoc.Localize("Colors.Advanced", "Advanced");

    /// <summary>Heading for the mod-wide geometry features, in their own section above the tab strip.
    /// "Geometry" rather than "Body" because what these change is the shape of the mesh, not which body
    /// the art is baked onto — that is Bodies, which stays per-tab in Advanced.</summary>
    public readonly string GeometrySection = ProteusLoc.Localize("Colors.Geometry.Section", "Geometry");

    /// <summary>Heading for the glow effect and Skindent, the section above Geometry.</summary>
    public readonly string EffectsSection = ProteusLoc.Localize("Colors.Effects.Section", "Effects");

    public readonly string Pinned = ProteusLoc.Localize("Colors.Pinned", "(pinned)");
    public readonly string Auto   = ProteusLoc.Localize("Colors.Auto", "(auto)");

    public readonly string PinnedTip = ProteusLoc.Localize("Colors.Pinned.Tip",
        "You pinned this mode in Advanced — it no longer follows the features you set.\n" +
        "Click \"Back to auto\" to let it adapt again.");

    public readonly string AutoTip = ProteusLoc.Localize("Colors.Auto.Tip",
        "The mode follows what you use: a sphere map or metal ⇒ Cloth,\n" +
        "a glow effect ⇒ Animated glow, nothing special ⇒ Skin.");

    public readonly string BackToAuto = ProteusLoc.Localize("Colors.BackToAuto.Btn", "Back to auto");

    public readonly string ForceModeHint =
        ProteusLoc.Localize("Colors.ForceMode.Hint", "Force the render mode instead of letting the features pick it:");

    public readonly string ForceModeTip = ProteusLoc.Localize("Colors.ForceMode.Tip",
        "Skin (painted) — skin.shpk.  Cloth — character.shpk (sphere, metal).\n" +
        "Animated glow — characterscroll.shpk. Pinning stops the auto mode-switch.");

    public readonly string ResetBtn = ProteusLoc.Localize("Colors.Reset.Btn", "Reset to defaults");

    public readonly string ResetTip = ProteusLoc.Localize("Colors.Reset.Tip",
        "Hold Ctrl and click to restore this option's colours, glow and mode to the\n" +
        "settings Proteus first recorded for this mod. Cannot be undone.\n\n" +
        "If a design is applied, this also drops that design's saved override for this\n" +
        "option — otherwise the design would just re-impose it. Other designs keep theirs.\n\n" +
        "Note: those originals were captured the first time Proteus saved this mod —\n" +
        "if you had already edited it before then, that edited state is the \"default\".");

    // ── glow effect picker ──────────────────────────────────────────────────────────────────────────
    public readonly string GlowEffect = ProteusLoc.Localize("Colors.GlowEffect.Label", "Glow effect");

    public readonly string GlowEffectTip = ProteusLoc.Localize("Colors.GlowEffect.Tip",
        "Pick a scrolling map to make this overlay GLOW and animate — that switches it to\n" +
        "Animated glow. The map IS the glow (colour, pattern, motion); the row's Glow scales it.\n" +
        "Effects come from the mod's Proteus/Effects/ folder, then your Effects folder.");

    public readonly string NoEffects = ProteusLoc.Localize("Colors.NoEffects",
        "No effects found — drop images into the folder the Glow Effect Textures\n" +
        "button opens (Settings), or the mod's own Proteus/Effects/ folder.");

    public readonly string None = ProteusLoc.Localize("Colors.None", "None");

    /// <summary>{0} is an effect's file name; the suffix marks it as shipped by the mod itself.</summary>
    public readonly string EffectFromModFmt = ProteusLoc.Localize("Colors.Effect.FromMod.Fmt", "{0}  (mod)");

    public readonly string ScrollSpeed = ProteusLoc.Localize("Colors.ScrollSpeed.Label", "Scroll speed");

    public readonly string ScrollSpeedTip = ProteusLoc.Localize("Colors.ScrollSpeed.Tip",
        "How fast the effect flows, X and Y. Negative reverses; 0 holds it still. ~0.01 is normal.");

    public readonly string Tiling = ProteusLoc.Localize("Colors.Tiling.Label", "Tiling");

    public readonly string TilingTip =
        ProteusLoc.Localize("Colors.Tiling.Tip", "How many times the effect repeats across the surface. 1 = once.");

    // ── row picker + clipboard ──────────────────────────────────────────────────────────────────────
    public readonly string RowUnusedTip = ProteusLoc.Localize("Colors.Row.Unused.Tip",
        "This overlay's index texture never selects this row,\nso editing it would have no effect.");

    public readonly string CopyRow  = ProteusLoc.Localize("Colors.CopyRow.Btn", "Copy row");
    public readonly string PasteRow = ProteusLoc.Localize("Colors.PasteRow.Btn", "Paste row");

    public readonly string CopyRowTip = ProteusLoc.Localize("Colors.CopyRow.Tip", "Copy both sub-rows (A and B) of this row.");
    public readonly string NeedRowCopy = ProteusLoc.Localize("Colors.PasteRow.NeedCopy", "Copy a row first.");

    public readonly string PasteRowTipFmt = ProteusLoc.Localize("Colors.PasteRow.Tip.Fmt",
        "Overwrite row {0} (both sub-rows) with the copied row.");

    /// <summary>Column headers of the A/B panel. {0} is the row number; A and B are the two sub-rows.</summary>
    public readonly string SubRowAFmt = ProteusLoc.Localize("Colors.SubRowA.Fmt", "Row {0}A");
    public readonly string SubRowBFmt = ProteusLoc.Localize("Colors.SubRowB.Fmt", "Row {0}B");

    public readonly string CopySub  = ProteusLoc.Localize("Colors.CopySub.Btn", "Copy");
    public readonly string PasteSub = ProteusLoc.Localize("Colors.PasteSub.Btn", "Paste");

    public readonly string CopySubTip  = ProteusLoc.Localize("Colors.CopySub.Tip", "Copy this sub-row's values.");
    public readonly string NeedSubCopy = ProteusLoc.Localize("Colors.PasteSub.NeedCopy", "Copy a sub-row first.");
    public readonly string PasteSubTip = ProteusLoc.Localize("Colors.PasteSub.Tip", "Overwrite this sub-row with the copied values.");

    // ── live highlight ──────────────────────────────────────────────────────────────────────────────
    public readonly string Glow    = ProteusLoc.Localize("Colors.Highlight.Glow.Btn", "Glow");
    public readonly string Glowing = ProteusLoc.Localize("Colors.Highlight.Glowing.Btn", "Glowing");

    public readonly string GlowGearTip = ProteusLoc.Localize("Colors.Highlight.Gear.Tip",
        "Make this sub-row's mesh glow on your character so you can find it\nin-game. Click again to stop.");

    public readonly string GlowSkinTip = ProteusLoc.Localize("Colors.Highlight.Skin.Tip",
        "Light up this sub-row's region on your character's skin so you can find\n" +
        "it in-game. Click again to stop. (Takes a moment to build the first time.)");

    // ── the values themselves ───────────────────────────────────────────────────────────────────────
    public readonly string Colours = ProteusLoc.Localize("Colors.Section.Colours", "Colours");

    public readonly string Diffuse = ProteusLoc.Localize("Colors.Diffuse.Label", "Diffuse");

    public readonly string DiffuseGearTip = ProteusLoc.Localize("Colors.Diffuse.Gear.Tip",
        "The surface UNDER the glow — it multiplies the overlay's own diffuse art.\n\n" +
        "Keep it DARK for a glowing material, or the glow has nothing to stand out\n" +
        "against and looks faint however high you push it. The vanilla scrolling\n" +
        "materials pair a near-black diffuse with a bright emissive for exactly this.");

    public readonly string Specular = ProteusLoc.Localize("Colors.Specular.Label", "Specular");

    /// <summary>
    /// Its own section since the glow colour, amount and light response were pulled out of "Colours".
    /// NOT the same string as <see cref="GlowAmount"/>, which happens to render the same word in English —
    /// that one is the slider's own label and can be renamed without touching the heading. The field cannot
    /// be called <c>Glow</c> either: that is already the highlight button beside the Copy/Paste pair.
    /// </summary>
    public readonly string GlowSection = ProteusLoc.Localize("Colors.Section.Glow", "Glow");

    public readonly string GlowColour = ProteusLoc.Localize("Colors.GlowColour.Label", "Glow colour");

    public readonly string GlowColourTip = ProteusLoc.Localize("Colors.GlowColour.Tip",
        "Glow colour, independent of the diffuse — a glowing material usually\n" +
        "wants a DARK surface with a bright glow. Defaults to the diffuse.");

    public readonly string GlowAmount = ProteusLoc.Localize("Colors.GlowAmount.Label", "Glow");

    public readonly string GlowAmountTip = ProteusLoc.Localize("Colors.GlowAmount.Tip",
        "How brightly this row glows. 0 switches it off.\n" +
        "Under an animated glow this is the effect's brightness, and a high value\n" +
        "blows a colourful scroll map out to white. Around 25% is a good start.");

    /// <summary>
    /// Shown at the top of whichever of the two columns the art's index never lands in. Most overlays carry
    /// no index at all, and the shell then samples the fabricated (255, 255, 0) — row 16, column A — so
    /// column B is dead on every row and used to be drawn as though it were not.
    /// </summary>
    public readonly string SubRowUnused = ProteusLoc.Localize("Colors.SubRowUnused",
        "Nothing samples this column — the art's index picks the other one everywhere, "
      + "so edits here won't show.");

    public readonly string LightResponse = ProteusLoc.Localize("Colors.LightResponse.Label", "Fades in light");

    public readonly string LightResponseTip = ProteusLoc.Localize("Colors.LightResponse.Tip",
        "How much the scene's light takes this row's glow away.\n" +
        "0% glows the same everywhere. 100% is a dark-only glow: full brightness in\n" +
        "an unlit room, nothing at all under a midday sky or beside a lamp.\n" +
        "Set per row, so one half of a tattoo can be dark-only and the other always on.");

    public readonly string HideInLight = ProteusLoc.Localize("Colors.HideInLight.Label", "Hide in light");

    public readonly string HideInLightTip = ProteusLoc.Localize("Colors.HideInLight.Tip",
        "Let this row's opacity follow its glow, so where it has stopped glowing\n" +
        "there is nothing left but skin.\n" +
        "Without this a dark-only row still leaves its own colour behind — usually black —\n" +
        "so the art reads as a dark patch in daylight instead of vanishing into the skin.");

    public readonly string Opacity = ProteusLoc.Localize("Colors.Opacity.Label", "Opacity");

    public readonly string OpacityTip = ProteusLoc.Localize("Colors.Opacity.Tip",
        "Negative fades this row toward transparent; positive pushes it toward opaque.");

    public readonly string Blend = ProteusLoc.Localize("Colors.Blend.Label", "Blend");

    public readonly string BlendTip = ProteusLoc.Localize("Colors.Blend.Tip",
        "How this row's art combines with what is already on the skin.\n" +
        "\n" +
        "\"Paint\" lays it on top, which is what every layer has always done.\n" +
        "Anything else makes this row a PRINT, blended like a Photoshop layer\n" +
        "mode: Multiply drops white out, Screen drops black out.\n" +
        "\n" +
        "\"Onto\" chooses what a print blends with: everything beneath it, skin\n" +
        "included, or only the fabric this mod's other layers painted.\n" +
        "\n" +
        "Multiply can only darken, so it reads faintly on very dark fabric — use\n" +
        "Screen there instead. This row's colour still tints the art, and its\n" +
        "Opacity is the print's strength.");

    public readonly string PrintOnto = ProteusLoc.Localize("Colors.PrintOnto.Label", "Onto");

    public readonly string PrintOntoTip = ProteusLoc.Localize("Colors.PrintOnto.Tip",
        "What this print blends with.\n" +
        "\n" +
        "\"Everything beneath\" works like a Photoshop blend layer: it lands on\n" +
        "the skin and on every layer under it, shaped only by its own alpha.\n" +
        "\n" +
        "\"This mod's paint only\" clips it to what this mod's other layers\n" +
        "painted, so a rainbow on a fishnet colours the threads and leaves the\n" +
        "holes as skin. On bare skin it shows nothing.");

    /// <summary>Display names for <see cref="PrintTarget"/>, in declaration order.</summary>
    public readonly string[] PrintOntoNames =
    [
        ProteusLoc.Localize("Colors.PrintOnto.OwnPaint", "This mod's paint only"),
        ProteusLoc.Localize("Colors.PrintOnto.Beneath",  "Everything beneath"),
    ];

    /// <summary>Display names for <see cref="RowBlend"/>, in declaration order.</summary>
    public readonly string[] BlendNames =
    [
        ProteusLoc.Localize("Colors.Blend.Paint",    "Paint"),
        ProteusLoc.Localize("Colors.Blend.Multiply", "Multiply"),
        ProteusLoc.Localize("Colors.Blend.Screen",   "Screen"),
        ProteusLoc.Localize("Colors.Blend.Overlay",  "Overlay"),
        ProteusLoc.Localize("Colors.Blend.Add",      "Add"),
        ProteusLoc.Localize("Colors.Blend.Replace",  "Replace"),
    ];

    public readonly string Physical    = ProteusLoc.Localize("Colors.Section.Physical", "Physical");
    public readonly string ClothSuffix = ProteusLoc.Localize("Colors.Section.Physical.ClothSuffix", "— Cloth");

    public readonly string Roughness = ProteusLoc.Localize("Colors.Roughness.Label", "Roughness");
    public readonly string Metalness = ProteusLoc.Localize("Colors.Metalness.Label", "Metalness");

    public readonly string SphereMap   = ProteusLoc.Localize("Colors.Section.SphereMap", "Sphere map");
    public readonly string SphereIndex = ProteusLoc.Localize("Colors.Sphere.Index.Label", "Index");
    public readonly string Intensity   = ProteusLoc.Localize("Colors.Sphere.Intensity.Label", "Intensity");

    public readonly string Tile         = ProteusLoc.Localize("Colors.Section.Tile", "Tile");
    public readonly string TilePattern  = ProteusLoc.Localize("Colors.Tile.Pattern.Label", "Pattern");
    public readonly string TileNone     = ProteusLoc.Localize("Colors.Tile.None", "None");
    public readonly string TileStrength = ProteusLoc.Localize("Colors.Tile.Strength.Label", "Strength");
    public readonly string TileScaleU   = ProteusLoc.Localize("Colors.Tile.ScaleU.Label", "Scale U");
    public readonly string TileScaleV   = ProteusLoc.Localize("Colors.Tile.ScaleV.Label", "Scale V");

    public readonly string TileTip = ProteusLoc.Localize("Colors.Tile.Tip",
        "A fabric weave tiled over this region — one of the game's own 64 patterns, so it costs no texture.\n"
      + "This is what makes a second skin read as cloth rather than skin; Proteus leaves it off by default\n"
      + "because a weave over bare skin looks like grain. Needs the gear shader, so picking one moves a skin\n"
      + "overlay onto a cloth shell.");

    public readonly string TileScaleTip = ProteusLoc.Localize("Colors.Tile.Scale.Tip",
        "How many times the weave repeats across the surface. Higher is finer; 16 is the game's own default.\n"
      + "U and V are the two directions of the texture, so setting them apart stretches the weave one way.");

    /// <summary>Deliberately the same wording as the global Settings slider: this is the same knob at a
    /// narrower scope, and calling it something else would read as a second, unrelated control.</summary>
    public readonly string SkinTint = ProteusLoc.Localize("Colors.SkinTint.Label", "Skin-tint suppression");

    public readonly string SkinTintTip = ProteusLoc.Localize("Colors.SkinTint.Tip",
        "How strongly this option resists the wearer's skin tone.\n"
      + "1.00 keeps the authored colour on any skin tone — right for fabric that covers the skin.\n"
      + "0.00 lets the skin tone through — right for a tattoo, a decal, or a body texture that IS skin.\n"
      + "The global slider in Settings multiplies this, so 0.00 here always wins.");

    /// <summary>Shown beside the "Rendering as" badge while Advanced is collapsed, so a tab that has been
    /// turned down doesn't look untouched. Only drawn when the value isn't the default.</summary>
    public readonly string SkinTintBadgeFmt = ProteusLoc.Localize("Colors.SkinTint.Badge.Fmt", "tint {0:F2}");

    public readonly string WholeSkin = ProteusLoc.Localize("Colors.WholeSkin.Label",
        "This overlay is the whole skin");

    public readonly string Asymmetric = ProteusLoc.Localize("Colors.Asymmetric.Label",
        "This art is asymmetric (left and right differ)");

    public readonly string AsymmetricTip = ProteusLoc.Localize("Colors.Asymmetric.Tip",
        "Turn this on when the two sides are meant to DIFFER — a tattoo on one arm, a scar on one\n"
      + "cheek, makeup that isn't mirrored. Leave it off for anything else, including ordinary skin.\n"
      + "A vanilla body and the vanilla face give both sides the same pixels, so art painted for\n"
      + "them is folded in half: one side is kept and mirrored across. That is invisible on a\n"
      + "symmetric design and ruins an asymmetric one. With this on, Proteus renders the overlay\n"
      + "through a layer of its own whose two sides read the two halves of your sheet, so both\n"
      + "sides survive.\n"
      + "It cannot be detected for you. Real skin is never symmetric — freckles and moles differ\n"
      + "left to right — so only you can say whether a difference is the point or just detail.");

    public readonly string ReinforcedToe = ProteusLoc.Localize("Colors.ReinforcedToe.Label",
        "Reinforced toe");

    public readonly string ReinforcedToeTip = ProteusLoc.Localize("Colors.ReinforcedToe.Tip",
        "Make the capped toe denser than the rest of the stocking, the way real hosiery\n"
      + "knits a heavier toe box — a darker panel with the toes still showing through it,\n"
      + "fading out at the edge rather than stopping at a line.\n"
      + "It can only take away transparency that is there. Where the stocking paints\n"
      + "nothing it does nothing, so it will never put fabric on a bare toe, and on an\n"
      + "already-opaque stocking you will see no change.\n"
      + "With \"Sharp alpha\" on, every pixel is either solid or gone, so the soft edge\n"
      + "becomes a hard one.");

    public readonly string ToeDensity = ProteusLoc.Localize("Colors.ToeDensity.Label",
        "Density");

    public readonly string ToeDensityTip = ProteusLoc.Localize("Colors.ToeDensity.Tip",
        "How much denser the toe is than the leg. 100% is completely solid; the usual\n"
      + "reinforced toe sits well below that.");

    public readonly string ReinforcedToeSavedNote = ProteusLoc.Localize("Colors.ReinforcedToe.SavedNote",
        "Saved to the mod — presets and designs don't capture this.");

    public readonly string BustBridge = ProteusLoc.Localize("Colors.BustBridge.Label",
        "Span the cleavage");

    public readonly string BustBridgeTip = ProteusLoc.Localize("Colors.BustBridge.Tip",
        "Turn this on for cloth over the chest.\n"
      + "Proteus builds a garment as a copy of the body, so by default it follows the body into\n"
      + "the cleavage — which is what makes it read as paint rather than fabric. Real cloth spans\n"
      + "the gap: a straight line between the furthest-forward point of each breast.\n"
      + "With this on, the fabric between the breasts is relaxed out to that line. Nothing is ever\n"
      + "pulled inward, so it cannot cut into the body, and the breasts themselves keep their shape.\n"
      + "Where a neckline has cut the cloth away between the cups there is nothing to span, and\n"
      + "this does nothing.");

    public readonly string BustBridgeStrength = ProteusLoc.Localize("Colors.BustBridge.Strength.Label",
        "Span amount");

    public readonly string BustBridgeStrengthTip = ProteusLoc.Localize("Colors.BustBridge.Strength.Tip",
        "How far the fabric relaxes toward the flat span. 1.00 spans it fully; lower values keep\n"
      + "more of the shape underneath.");

    public readonly string SmoothNipples = ProteusLoc.Localize("Colors.SmoothNipples.Label",
        "Smooth the nipple");

    public readonly string SmoothNipplesTip = ProteusLoc.Localize("Colors.SmoothNipples.Tip",
        "Turn this on for cloth over the chest.\n"
      + "Proteus builds a garment as a copy of the body, so it reproduces the nipple as a point —\n"
      + "which reads as body paint rather than fabric. With this on, the fabric over it is smoothed\n"
      + "into the surrounding curve.\n"
      + "NOT FINISHED YET: this smooths the garment, and the body underneath keeps its own shape, so\n"
      + "on a close fit it can still show through. Smoothing the skin to match is still to come.");

    public readonly string SmoothNipplesStrength = ProteusLoc.Localize("Colors.SmoothNipples.Strength.Label",
        "Smoothing amount");

    public readonly string SmoothNipplesStrengthTip = ProteusLoc.Localize("Colors.SmoothNipples.Strength.Tip",
        "How far the fabric is smoothed. 1.00 smooths it fully; lower values keep more of the\n"
      + "shape underneath.");

    public readonly string CleftBridge = ProteusLoc.Localize("Colors.CleftBridge.Label",
        "Span the buttocks");

    public readonly string CleftBridgeTip = ProteusLoc.Localize("Colors.CleftBridge.Tip",
        "Turn this on for cloth over the seat.\n"
      + "Proteus builds a garment as a copy of the body, so it follows the cleft all the way in —\n"
      + "which reads as painted on rather than worn. With this on, the fabric spans across it the\n"
      + "way real cloth does.\n"
      + "The same idea as spanning the cleavage, on a much deeper cleft, so start the amount low.");

    public readonly string CleftBridgeStrength = ProteusLoc.Localize("Colors.CleftBridge.Strength.Label",
        "Span amount");

    public readonly string CleftBridgeStrengthTip = ProteusLoc.Localize("Colors.CleftBridge.Strength.Tip",
        "How far the fabric relaxes toward the flat span. 1.00 lifts the cleft level with the\n"
      + "cheeks either side, which is usually too much — lower values keep more of it.");

    public readonly string NavelBridge = ProteusLoc.Localize("Colors.NavelBridge.Label",
        "Span the belly button");

    public readonly string NavelBridgeTip = ProteusLoc.Localize("Colors.NavelBridge.Tip",
        "Turn this on for cloth over the stomach.\n"
      + "Proteus builds a garment as a copy of the body, so it dips into the belly button —\n"
      + "which reads as painted on rather than worn. With this on, the fabric is stretched flat\n"
      + "across it the way real cloth is. Nothing is pulled inward, so it cannot cut into the body.\n"
      + "Where the garment is cut away at the belly button, or the body has none, this does nothing.");

    public readonly string SmoothFold = ProteusLoc.Localize("Colors.SmoothFold.Label",
        "Smooth between the legs");

    public readonly string SmoothFoldTip = ProteusLoc.Localize("Colors.SmoothFold.Tip",
        "Turn this on for cloth over the crotch.\n"
      + "Proteus builds a garment as a copy of the body, so it reproduces the fold between the legs —\n"
      + "which reads as body paint rather than fabric. With this on, the skin there is smoothed out to\n"
      + "the surrounding curve first, so the garment cut from it comes out smooth too.\n"
      + "This changes the body, so it applies to every garment worn at once.");

    public readonly string SmoothFoldStrength = ProteusLoc.Localize("Colors.SmoothFold.Strength.Label",
        "Smoothing amount");

    public readonly string SmoothFoldStrengthTip = ProteusLoc.Localize("Colors.SmoothFold.Strength.Tip",
        "How far the fold is smoothed. 1.00 flattens it into the surrounding curve; lower values keep\n"
      + "more of it.");

    public readonly string WholeSkinTip = ProteusLoc.Localize("Colors.WholeSkin.Tip",
        "Turn this on for a converted skin mod — art that IS the skin, not something laid on it.\n"
      + "It moves two settings together, and they only work as a pair.\n"
      + "The normal REPLACES the one already on the material instead of being added to it. Off is\n"
      + "right for a strap or a tattoo, which should keep the skin's pores underneath; for a skin\n"
      + "the map underneath is that same skin, so adding lands every pore twice and the body goes\n"
      + "flat against an untouched face, with a hard line at the neck seam.\n"
      + "And skin-tint suppression drops to 0.00, so the wearer's tone comes through — it exists\n"
      + "to stop fabric being re-tinted, which is backwards for skin. Turning this off restores\n"
      + "both. You can still set the slider by hand afterwards.");

    /// <summary>{0} is the mod's name. The window's ImGui id is appended after, so it is never translated.</summary>
    public readonly string WindowTitleFmt = ProteusLoc.Localize("Colors.Window.Title.Fmt", "Colors — {0}");

    /// <summary>Under the glow-effect picker, for an option that paints gear rather than skin.</summary>
    public readonly string NoShell = ProteusLoc.Localize("Colors.NoShell",
        "This overlay paints something Proteus can't build a layer over — gear, an accessory or a\n"
      + "weapon. Glow and Cloth need a layer over your own skin: body, face, hair, tail or ears.");

    /// <summary>Stands in for the "Reset to defaults" tooltip while there is nothing to reset to.</summary>
    public readonly string ResetBlockedTip = ProteusLoc.Localize("Colors.Reset.Blocked.Tip",
        "No original settings recorded for this mod yet — Proteus captures them the first time\n"
      + "it saves a change here.");
}

/// <summary>The colour window's own chrome — the panel StatusWindow draws around ColorTableEditor.</summary>
public sealed class ColorPanelStrings
{
    public readonly string PillBinding = ProteusLoc.Localize("Colors.Pill.Binding", "binding");

    public readonly string EditingBindingFmt = ProteusLoc.Localize("Colors.EditingBinding.Fmt",
        "Editing '{0}' — previewing live; click \"Update\" on the Bindings tab to save.");

    public readonly string BaseUnchanged = ProteusLoc.Localize("Colors.BaseUnchanged",
        "Base colors unchanged — except in Advanced, where \"Reset to defaults\" rewrites them and " +
        "\"Bodies\" is a global setting no binding captures.");

    public readonly string NoIndexTexture =
        ProteusLoc.Localize("Colors.NoIndexTexture", "No index texture — only Row 16 is applied.");

    public readonly string NoActiveOptions = ProteusLoc.Localize("Colors.NoActiveOptions",
        "No active options — select one in Penumbra to edit its colours.");

    // "Advanced" is not repeated here — the disclosure this panel draws and the one inside the editor are
    // the same control by another route, so both read Strings.Colors.Advanced and share one key.

    public readonly string MasksTab = ProteusLoc.Localize("Colors.MasksTab", "Masks");

    /// <summary>Tab caption when more than one option group is active: "Group: Option".</summary>
    public readonly string GroupOptionFmt = ProteusLoc.Localize("Colors.GroupOption.Fmt", "{0}: {1}");

    public readonly string StackHint = ProteusLoc.Localize("Colors.StackHint",
        "Editing overlay  (drag a tab to restack — leftmost = on top):");

    // The arrows are direction, not decoration, and are kept outside the translated text so a
    // right-to-left reading of the sentence cannot leave them pointing the wrong way.
    public readonly string TowardTop    = ProteusLoc.Localize("Colors.TowardTop.Btn", "Toward top");
    public readonly string TowardBottom = ProteusLoc.Localize("Colors.TowardBottom.Btn", "Toward bottom");

    public readonly string StackTip = ProteusLoc.Localize("Colors.Stack.Tip",
        "Reorder how this mod's overlays stack on your body, across groups.\n" +
        "Leftmost tab = top of the stack (composites last, on top).");

    // Vanilla overlay. "gen2", "bibo", "gen3" and "Eve" are body-mod names and are never translated.
    public readonly string OverlayVanilla = ProteusLoc.Localize("Colors.Bodies.Vanilla.Label", "Overlay gen2/vanilla");

    public readonly string BodiesTip = ProteusLoc.Localize("Colors.Bodies.Vanilla.Tip",
        "Also paint this mod onto vanilla (gen2) skin your character is wearing —\n" +
        "usually skin that comes with a piece of gear.\n" +
        "It is only painted while vanilla skin is actually on your character,\n" +
        "so this costs nothing when there is none.\n\n" +
        "bibo and gen3/Eve are always painted; this only affects vanilla.\n" +
        "Applies to the whole mod, not just this option.");

    // "TBSE" is a body-mod name and is never translated.
    public readonly string OverlayMen = ProteusLoc.Localize("Colors.Bodies.Men.Label", "Overlay on men");

    public readonly string MenTip = ProteusLoc.Localize("Colors.Bodies.Men.Tip",
        "Also paint this mod's women's art on men (TBSE or the men's vanilla body),\n" +
        "moved into his layout through the men's body maps.\n" +
        "Art made for men is never changed.\n" +
        "Applies to the whole mod, not just this option.");

    public readonly string BodiesGlobalSuffix = ProteusLoc.Localize("Colors.Bodies.GlobalSuffix",
        "\nGlobal — this one is NOT part of the binding you're editing.");

    public readonly string BodiesGlobalNote =
        ProteusLoc.Localize("Colors.Bodies.GlobalNote", "Saved globally — bindings don't capture this.");

    public readonly string Forced = ProteusLoc.Localize("Colors.Mask.Forced", "(forced)");

    public readonly string ForcedTip = ProteusLoc.Localize("Colors.Mask.Forced.Tip",
        "Another active option in this mod renders as gear, so the mask has to sit on that\n" +
        "shell — it can't be painted into the skin underneath it. Switch those options to\n" +
        "Skin (Advanced on their tabs) and the mask gets its own mode choice back.");

    public readonly string ToeCapOn = ProteusLoc.Localize("Colors.Mask.ToeCap", "Toe Cap is on");

    public readonly string ToeCapOnTip = ProteusLoc.Localize("Colors.Mask.ToeCap.Tip",
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
    public readonly string Disabled = ProteusLoc.Localize("Mods.PluginDisabled", "Proteus is disabled — enable it in Settings.");
    public readonly string NoMods   = ProteusLoc.Localize("Mods.NoSidecarMods", "No Proteus sidecar mods detected.");
}

/// <summary>The header band across the top of the status window.</summary>
public sealed class BandStrings
{
    // The wordmark ("PROTEUS") and the Discord button are brand names and are deliberately absent —
    // they are never translated.

    // Shown only when the capability row below has had to drop its labels and nothing is hovered, so this
    // is the narrow-window fallback rather than the usual second line.
    public readonly string Caption = ProteusLoc.Localize("Band.Caption", "overlay · accessorize · reshape · bind");

    // The four capabilities named across the band's second line. Length is not cosmetic here: the row is
    // measured every frame and hides ALL FOUR labels the moment they stop fitting, so a translation much
    // longer than the English costs the whole row at window sizes where the English still shows.
    public readonly string CapOverlay = ProteusLoc.Localize("Band.Cap.Overlay", "Overlay & recolour anything");
    public readonly string CapWear    = ProteusLoc.Localize("Band.Cap.Wear",    "Wear anything, no slot");
    public readonly string CapReshape = ProteusLoc.Localize("Band.Cap.Reshape", "Reshape any model");
    public readonly string CapBind    = ProteusLoc.Localize("Band.Cap.Bind",    "Bind it all to a design");

    // Tooltip on any of the four capabilities, each of which opens the README at the section about it.
    public readonly string CapLinkTip = ProteusLoc.Localize("Band.Cap.Link.Tip", "Read about this in the guide");

    public readonly string SettingsTip    = ProteusLoc.Localize("Band.Settings.Tip", "Settings");
    public readonly string RecompositeTip = ProteusLoc.Localize("Band.Recomposite.Tip", "Recomposite now");
    public readonly string RecompositeFullTip = ProteusLoc.Localize("Band.Recomposite.Full.Tip",
        "Shift-click to rebuild everything from scratch");

    public readonly string PillDisabled   = ProteusLoc.Localize("Band.Pill.Disabled", "disabled");
    public readonly string PillNoPenumbra = ProteusLoc.Localize("Band.Pill.NoPenumbra", "no Penumbra");

    public readonly string Retry = ProteusLoc.Localize("Band.Retry.Btn", "Retry");

    public readonly string Refresh = ProteusLoc.Localize("Band.Refresh.Btn", "Refresh");
}

/// <summary>The one-line composite result under the tab content.</summary>
public sealed class FooterStrings
{
    public readonly string NoResult     = ProteusLoc.Localize("Footer.NoResult", "No composite result yet.");
    public readonly string PillFailed   = ProteusLoc.Localize("Footer.Pill.Failed", "failed");
    public readonly string PillOk       = ProteusLoc.Localize("Footer.Pill.Ok", "ok");
    public readonly string UnknownError = ProteusLoc.Localize("Footer.UnknownError", "unknown error");

    public readonly string SecondsAgoFmt = ProteusLoc.Localize("Footer.SecondsAgo.Fmt", "{0}s ago");
    public readonly string MinutesAgoFmt = ProteusLoc.Localize("Footer.MinutesAgo.Fmt", "{0}m ago");

    /// <summary>
    /// Phrased so no count is ever grammatically attached to a noun: English used to inflect this inline
    /// ("1 texture" / "2 textures"), which has no correct translation into languages with three plural
    /// forms (Russian) or none at all (Japanese, Korean, Chinese). Labelling the counts instead is
    /// correct everywhere and costs nothing in English.
    /// </summary>
    public readonly string LastCompositeFmt = ProteusLoc.Localize("Footer.LastComposite.Fmt",
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
    public readonly string Intro = ProteusLoc.Localize("Parts.Intro",
        "Pick geometry out of a mod's model and give it an on/off switch. The switch is written into the " +
        "mod itself as an ordinary Penumbra option, so it keeps working with Proteus turned off.");

    public readonly string Mod   = ProteusLoc.Localize("Parts.Mod", "Mod");
    public readonly string Model = ProteusLoc.Localize("Parts.Model", "Model");

    public readonly string PickMod   = ProteusLoc.Localize("Parts.PickMod", "Choose a mod");
    public readonly string PickModel = ProteusLoc.Localize("Parts.PickModel", "Choose a model");

    public readonly string NoModels = ProteusLoc.Localize("Parts.NoModels",
        "This mod publishes no models, so there is no geometry to split up.");

    public readonly string Unreadable = ProteusLoc.Localize("Parts.Unreadable",
        "This model could not be read. Nothing has been changed.");

    public readonly string ClickTip = ProteusLoc.Localize("Parts.Click.Tip",
        "Click a piece of the model to switch it on or off. Drag to turn it, shift-drag to move it, scroll " +
        "to zoom.");

    /// <summary>Material and size beside a part's checkbox. {0} is a file name, {1} a triangle count.</summary>
    public readonly string RowFmt = ProteusLoc.Localize("Parts.Row.Fmt", "{0} · {1:N0} tris");

    // ── tools ───────────────────────────────────────────────────────────────

    public readonly string ToolNavigate = ProteusLoc.Localize("Parts.Tool.Navigate", "Toggle Parts")
                                        + "###partsToolNavigate";

    public readonly string ToolNavigateTip = ProteusLoc.Localize("Parts.Tool.Navigate.Tip",
        "Click pieces of the model to choose them, then give them an on/off switch.");

    public readonly string ToolInflate = ProteusLoc.Localize("Parts.Tool.Inflate", "Pull out")
                                       + "###partsToolInflate";

    public readonly string ToolInflateTip = ProteusLoc.Localize("Parts.Tool.Inflate.Tip",
        "Paint on the model to push the surface outwards, so a body that pokes\n" +
        "through a piece of clothing is covered again.");

    public readonly string ToolDeflate = ProteusLoc.Localize("Parts.Tool.Deflate", "Push in")
                                       + "###partsToolDeflate";

    public readonly string ToolRelax = ProteusLoc.Localize("Parts.Tool.Relax", "Relax")
                                     + "###partsToolRelax";

    public readonly string ToolRelaxTip = ProteusLoc.Localize("Parts.Tool.Relax.Tip",
        "Paint on the model to smooth the surface — lumps the clothing came with, or a\n" +
        "pull that came out rough. Like 3ds Max's relax it shrinks: curves flatten and\n" +
        "cloth can sink toward the body.");

    public readonly string ToolSmooth = ProteusLoc.Localize("Parts.Tool.Smooth", "Smooth")
                                      + "###partsToolSmooth";

    public readonly string ToolSmoothTip = ProteusLoc.Localize("Parts.Tool.Smooth.Tip",
        "Paint on the model to iron out lumps and ripples without shrinking it:\n" +
        "unlike Relax, curves keep their shape and cloth stays off the body.");

    public readonly string ToolGrab = ProteusLoc.Localize("Parts.Tool.Grab", "Grab")
                                    + "###partsToolGrab";

    public readonly string ToolGrabTip = ProteusLoc.Localize("Parts.Tool.Grab.Tip",
        "Press on the model and drag: the cloth around the brush follows the mouse\n" +
        "like rubber, strongest in the middle, with no hard edge.");

    public readonly string ToolBridge = ProteusLoc.Localize("Parts.Tool.Bridge", "Bridge")
                                      + "###partsToolBridge";

    public readonly string ToolBridgeTip = ProteusLoc.Localize("Parts.Tool.Bridge.Tip",
        "Paint across a hollow — the cleft between cheeks, a crease — to stretch the\n" +
        "cloth straight over it instead of following the body down into it. It only\n" +
        "ever lifts, and leaves rounded areas their own shape.");

    public readonly string ToolWind = ProteusLoc.Localize("Parts.Tool.Wind", "Wind")
                                    + "###partsToolWind";

    public readonly string ToolWindTip = ProteusLoc.Localize("Parts.Tool.Wind.Tip",
        "Paint where the wind moves this garment; red shows how much.\n" +
        "It is written into the model's second vertex colour.");

    public readonly string ToolMove = ProteusLoc.Localize("Parts.Tool.Move", "Move")
                                    + "###partsToolMove";

    public readonly string ToolMoveTip = ProteusLoc.Localize("Parts.Tool.Move.Tip",
        "Pick a part and drag it with the arrows: one arrow moves it along\n" +
        "that axis, a square moves it across that plane.");

    public readonly string ToolRotate = ProteusLoc.Localize("Parts.Tool.Rotate", "Rotate")
                                      + "###partsToolRotate";

    public readonly string ToolRotateTip = ProteusLoc.Localize("Parts.Tool.Rotate.Tip",
        "Pick a part, then drag one of the rings to turn it about its centre:\n" +
        "a coloured ring turns it about that axis, the outer one about your view.");

    public readonly string ToolScale = ProteusLoc.Localize("Parts.Tool.Scale", "Scale")
                                     + "###partsToolScale";

    public readonly string ToolScaleTip = ProteusLoc.Localize("Parts.Tool.ScaleAxes.Tip",
        "Pick a part, then drag one of the handles at its centre to stretch\n" +
        "it along that axis, or press on the part and drag right or left to\n" +
        "grow or shrink it evenly.");

    public readonly string RotateHelp = ProteusLoc.Localize("Parts.Rotate.Help",
        "Click a part to choose it, then drag one of the rings at its centre. Drag anywhere else to turn the model, "
      + "shift-drag to move the view, scroll to zoom.");

    public readonly string RotateLiveHint = ProteusLoc.Localize("Parts.Rotate.LiveHint",
        "Click a part on your character, then drag one of the rings at its centre. Hold Alt to move the camera. "
      + "Each turn is saved when you let go.");

    public readonly string ScaleHelp = ProteusLoc.Localize("Parts.ScaleAxes.Help",
        "Click a part to choose it, then drag a handle to stretch it along that axis, or press on the part and drag "
      + "right to grow it or left to shrink it. Drag anywhere else to turn the model, shift-drag to move the view, "
      + "scroll to zoom.");

    public readonly string ScaleLiveHint = ProteusLoc.Localize("Parts.ScaleAxes.LiveHint",
        "Click a part on your character, then drag a handle to stretch it along that axis, or press on the part and "
      + "drag right to grow it or left to shrink it. Hold Alt to move the camera. Each change is saved when you let go.");

    public readonly string MoveHelp = ProteusLoc.Localize("Parts.Move.Help",
        "Click a part to choose it, then drag an arrow or a square. Drag anywhere else to turn the model, "
      + "shift-drag to move the view, scroll to zoom.");

    public readonly string MoveLiveHint = ProteusLoc.Localize("Parts.Move.LiveHint",
        "Click a part on your character, then drag an arrow or a square. Hold Alt to move the camera. "
      + "Each move is saved when you let go.");

    /// <summary>{0} is the part's label.</summary>
    public readonly string MovePartFmt = ProteusLoc.Localize("Parts.Move.Part.Fmt", "Moving part {0}");

    public readonly string MoveNoPart = ProteusLoc.Localize("Parts.Move.NoPart", "Click a part to choose what to move.");

    public readonly string MoveListTip = ProteusLoc.Localize("Parts.Move.ListTip",
        "Choose the part to move here, or click it on the model or on your character.\n"
      + "Untick a part's box to lock it out of the way and reach what is underneath.");

    public readonly string MoveAdjacent = ProteusLoc.Localize("Parts.Move.Adjacent", "Move adjacent parts");

    public readonly string MoveAdjacentTip = ProteusLoc.Localize("Parts.Move.Adjacent.Tip",
        "Cloth near the part follows it, joined to it or not, less the further away it is.\n" +
        "Off, only the part moves. Points it shares exactly with a neighbour still move, so the seam stays closed.");

    public readonly string MoveFalloff = ProteusLoc.Localize("Parts.Move.Falloff", "Falloff");

    public readonly string MoveFalloffTip = ProteusLoc.Localize("Parts.Move.Falloff.Tip",
        "How far from the part nearby cloth still follows it.");

    public readonly string MoveFalloffSurfaceTip = ProteusLoc.Localize("Parts.Move.Falloff.SurfaceTip",
        "How far from the selected polygons the cloth around them still follows, measured along the surface.\n"
      + "Only cloth joined to the selection follows: a separate piece that merely sits close does not.");

    public readonly string MoveSelectParts = ProteusLoc.Localize("Parts.Move.SelectParts", "Whole parts")
                                           + "###partsMoveSelectParts";

    public readonly string MoveSelectPolygons = ProteusLoc.Localize("Parts.Move.SelectPolygons", "Polygons")
                                              + "###partsMoveSelectPolygons";

    public readonly string MoveSelectPolygonsTip = ProteusLoc.Localize("Parts.Move.SelectPolygons.Tip",
        "Move, turn or scale single polygons instead of whole parts. Click a polygon to select it; Shift-click adds\n"
      + "or removes one. Choosing a part in the list selects all of its polygons.");

    public readonly string MoveNoPolygons = ProteusLoc.Localize("Parts.Move.NoPolygons",
        "Click a polygon on the model to select it. Shift-click adds or removes one.");

    /// <summary>{0} is how many polygons are selected.</summary>
    public readonly string MovePolygonsFmt = ProteusLoc.Localize("Parts.Move.Polygons.Fmt", "{0:N0} polygon(s) selected");

    public readonly string MoveGrow = ProteusLoc.Localize("Parts.Move.Grow", "Grow") + "###partsMoveGrow";

    public readonly string MoveShrink = ProteusLoc.Localize("Parts.Move.Shrink", "Shrink") + "###partsMoveShrink";

    public readonly string MoveClearPolygons = ProteusLoc.Localize("Parts.Move.ClearPolygons", "Clear")
                                             + "###partsMoveClearPolygons";

    public readonly string MoveBonesNote = ProteusLoc.Localize("Parts.Move.BonesNote",
        "A moved part still follows the bones it was made for, so a part moved far from them can bend oddly in poses.");

    public readonly string MoveSkinTip = ProteusLoc.Localize("Parts.Move.SkinOff.Tip", "Turn on “Move skin too” to move skin.");

    public readonly string MoveLockedTip = ProteusLoc.Localize("Parts.Move.LockedTip",
        "This part is locked. Tick the box beside it to move it.");

    public readonly string MoveLockTip = ProteusLoc.Localize("Parts.Move.LockTip",
        "Untick to lock this part out of the way: it is hidden on the model and on your character, and clicks pass\n"
      + "through to what is underneath — a skirt, say, to reach the underwear beneath it.\n"
      + "The brushes leave a locked part alone and hide it too.");

    public readonly string MoveNothingFree = ProteusLoc.Localize("Parts.Move.NothingFree",
        "Nothing in this part can move: it is skin or locked.");

    /// <summary>{0} is the furthest any point has moved, in millimetres.</summary>
    public readonly string MoveMovedFmt = ProteusLoc.Localize("Parts.Move.Moved.Fmt", "Furthest moved: {0:F1} mm.");

    public readonly string MoveUndo = ProteusLoc.Localize("Parts.Move.Undo", "Undo move")
                                    + "###partsBrushUndo";

    public readonly string BrushWindAmount = ProteusLoc.Localize("Parts.Brush.WindAmount", "Amount");

    public readonly string BrushWindAmountTip = ProteusLoc.Localize("Parts.Brush.WindAmount.Tip",
        "How much wind to paint. 0% erases it, and so does holding Ctrl while painting.");

    public readonly string BrushWindRate = ProteusLoc.Localize("Parts.Brush.WindRate", "Rate");

    public readonly string BrushWindRateTip = ProteusLoc.Localize("Parts.Brush.WindRate.Tip",
        "How quickly the painted wind reaches the amount while you paint.");

    public readonly string WindAddsChannel = ProteusLoc.Localize("Parts.Wind.AddsChannel",
        "This model has no wind channel yet. One is added when the first stroke saves.");

    public readonly string WindFirstColorNotWhite = ProteusLoc.Localize("Parts.Wind.FirstColorNotWhite",
        "This model's first vertex colour isn't white, which the wind effect expects. Proteus leaves it as the author made it.");

    /// <summary>{0} is how many material files were changed.</summary>
    public readonly string WindMaterialsSetFmt = ProteusLoc.Localize("Parts.Wind.Materials.Set.Fmt",
        "Set {0} material file(s) so the wind can move this garment.");

    /// <summary>{0} is how many material files moved from the legacy character shader to Dawntrail's.</summary>
    public readonly string WindMaterialsUpgradedFmt = ProteusLoc.Localize("Parts.Wind.Materials.Upgraded.Fmt",
        "Upgraded {0} of them from the old character shader, which has no wind, to Dawntrail's.");

    /// <summary>{0} is how many material files are still on the legacy character shader.</summary>
    public readonly string WindMaterialsLegacyFmt = ProteusLoc.Localize("Parts.Wind.Materials.Legacy.Fmt",
        "{0} material file(s) use the old character shader, which has no wind, and couldn't be upgraded, so they won't sway.");

    /// <summary>{0} is how many material files lack the vertex movement settings.</summary>
    public readonly string WindMaterialsMissingFmt = ProteusLoc.Localize("Parts.Wind.Materials.Missing.Fmt",
        "{0} material file(s) have no wind movement settings to change, so they may not sway.");

    /// <summary>{0} is how many materials the mod does not ship.</summary>
    public readonly string WindMaterialsNotInModFmt = ProteusLoc.Localize("Parts.Wind.Materials.NotInMod.Fmt",
        "{0} material(s) this garment uses come from the game, not this mod, so their wind movement can't be set.");

    /// <summary>{0} is the error.</summary>
    public readonly string WindMaterialsFailedFmt = ProteusLoc.Localize("Parts.Wind.Materials.Failed.Fmt",
        "The garment's materials couldn't be set for wind: {0}");

    public readonly string WindRefusedFmt = ProteusLoc.Localize("Parts.Wind.Refused.Fmt",
        "Wind couldn't be added to {0} mesh(es): there's no room there for another vertex attribute.");

    public readonly string BrushBridgeRateTip = ProteusLoc.Localize("Parts.Brush.BridgeRate.Tip",
        "How quickly the cloth rises to span the hollow while you paint. Low is gentle\n" +
        "and easy to control; high closes the gap almost at once.");

    public readonly string BrushRelaxRateTip = ProteusLoc.Localize("Parts.Brush.RelaxRate.Tip",
        "How quickly the surface smooths while you paint. Low is gentle and easy to\n" +
        "control; high smooths almost at once.\n\n" +
        "Near an open edge, like a hem, relaxing draws the edge slightly inward.");

    public readonly string BrushSmoothRateTip = ProteusLoc.Localize("Parts.Brush.SmoothRate.Tip",
        "How quickly the surface smooths while you paint. Low is gentle and easy to\n" +
        "control; high smooths almost at once.");

    public readonly string GrabHelp = ProteusLoc.Localize("Parts.Grab.Help",
        "Press on the cloth and drag. The brush size sets how much cloth comes along;\n" +
        "it moves across your view, and Ctrl+Z takes a grab back.");

    public readonly string ToolDeflateTip = ProteusLoc.Localize("Parts.Tool.Deflate.Tip",
        "The same brush in reverse, for clothing that stands too far off the body.");

    // ── the brush ───────────────────────────────────────────────────────────

    public readonly string BrushHelp = ProteusLoc.Localize("Parts.Brush.Help",
        "Drag on the model to paint. Drag from the background to turn it, shift-drag to move it, scroll to "
      + "zoom.");

    public readonly string ShowModelView = ProteusLoc.Localize("Parts.ShowModelView", "Show model view");

    public readonly string ShowModelViewTip = ProteusLoc.Localize("Parts.ShowModelView.Tip",
        "Paint on the model in this window instead of on your character.");

    public readonly string LiveHint = ProteusLoc.Localize("Parts.Live.Hint",
        "Paint on your character. Hold Alt to move the camera. Each stroke is saved when you let go.");

    public readonly string LiveNotWorn = ProteusLoc.Localize("Parts.Live.NotWorn",
        "This model is not on your character right now. Wear it, or turn on Show model view.");

    /// <summary>Under the brush controls for an imported piece: painting works, the result takes a moment.</summary>
    public readonly string ContentHint = ProteusLoc.Localize("Parts.Content.Hint",
        "This garment was imported into Proteus, which wears it for you. Paint it as usual — each stroke "
      + "appears on your character a few seconds after it saves, once the garment is rebuilt.");

    /// <summary>Clicking a Proteus shell on the character — see <c>OnLivePicked</c>.</summary>
    public readonly string LivePickedShell = ProteusLoc.Localize("Parts.Live.PickedShell",
        "That is the garment Proteus draws for you, which is rebuilt every time something changes. Pick the "
      + "mod it came from to edit it.");

    public readonly string LiveUnreadable = ProteusLoc.Localize("Parts.Live.Unreadable",
        "This model could not be matched to the one on your character. Turn on Show model view to paint it here.");

    public readonly string LiveNoCharacter = ProteusLoc.Localize("Parts.Live.NoCharacter",
        "Your character is not available to paint on right now.");

    public readonly string LivePickedNotListedFmt = ProteusLoc.Localize("Parts.Live.PickedNotListed.Fmt",
        "{0} is not one of the models this mod publishes.");

    /// <summary>{0} is the file that was clicked.</summary>
    public readonly string LivePickedNotGearFmt = ProteusLoc.Localize("Parts.Live.PickedNotGear.Fmt",
        "{0} isn't equipment, so it can't be refitted.");

    /// <summary>Shown while a click on the character opens what was clicked — a mod's garment or the game's own.</summary>
    public readonly string LivePickGameTip = ProteusLoc.Localize("Parts.Live.PickGame.Tip",
        "Click a garment on your character to open it, vanilla or modded.");

    // ── vanilla gear ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Marks a worn piece of vanilla gear in the mod picker, where everything else is a mod.</summary>
    public readonly string VanillaWornSuffix = ProteusLoc.Localize("Parts.Vanilla.WornSuffix", "(vanilla)");

    public readonly string VanillaWornTip = ProteusLoc.Localize("Parts.Vanilla.Worn.Tip",
        "Worn, not modded. It opens as it is, and nothing is saved until you change it: your first change copies it "
      + "into a mod of its own.");

    /// <summary>Under a piece of vanilla gear, before anything is saved. {0} is the mod's name, e.g.
    /// "Ala Mhigan Coat of Fending — Proteus edit"; "Proteus edit" stays untranslated, as the mod is named.</summary>
    public readonly string VanillaCopyNoteFmt = ProteusLoc.Localize("Parts.Vanilla.CopyNote.Fmt",
        "Nothing is saved yet. Your first change copies this into the mod \"{0}\".");

    /// <summary>{0} is the item, e.g. "Body — Ala Mhigan Coat of Fending"; {1} is the mod it was copied into.</summary>
    public readonly string VanillaCopiedFmt = ProteusLoc.Localize("Parts.Vanilla.Copied.Fmt",
        "{0} was copied into the mod \"{1}\" with your change.");

    /// <summary>{0} is the mod's name.</summary>
    public readonly string VanillaEditedCopyThereFmt = ProteusLoc.Localize("Parts.Vanilla.EditedCopyThere.Fmt",
        "\"{0}\" already has an edited copy of this piece, so your change was not saved. To keep editing that copy, "
      + "open it here (with the mod switched on in Penumbra); to start again from the game's own, delete the mod.");

    /// <summary>{0} is the mod's name.</summary>
    public readonly string VanillaModSwitchedOffFmt = ProteusLoc.Localize("Parts.Vanilla.ModSwitchedOff.Fmt",
        "\"{0}\" is switched off, so your change was not saved into it. Switch it on in Penumbra, or delete it to "
      + "start again.");

    /// <summary>{0} is the reason, which may arrive in English.</summary>
    public readonly string VanillaCopyRefusedFmt = ProteusLoc.Localize("Parts.Vanilla.CopyRefused.Fmt",
        "Your change could not be saved, so nothing was copied: {0}");

    /// <summary>Under the Write button, when the brush edit saved just before has only now copied the piece into its mod.</summary>
    public readonly string VanillaCopyRegistering = ProteusLoc.Localize("Parts.Vanilla.CopyRegistering",
        "Your edit was just copied into a new mod, and Penumbra is still setting it up. Try again in a moment.");

    /// <summary>Under Body size, for a piece of vanilla gear it has no body slot for (a hat, a ring).</summary>
    public readonly string VanillaNotRefittable = ProteusLoc.Localize("Parts.Vanilla.NotRefittable",
        "Body size can't refit this piece. Choose another tool to edit it.");

    /// <summary>The description of the mod a piece of vanilla gear is copied into. {0} is the item.</summary>
    public readonly string VanillaModDescriptionFmt = ProteusLoc.Localize("Parts.Vanilla.ModDescription.Fmt",
        "{0}, copied from the game by Proteus to be edited.");

    /// <summary>The header over a piece of vanilla gear, in place of the mod and model pickers.</summary>
    public readonly string VanillaHeader = ProteusLoc.Localize("Parts.Vanilla.Header", "Vanilla gear");

    /// <summary>{0} is the item, e.g. "Body — Ala Mhigan Coat of Fending".</summary>
    public readonly string VanillaOpenFmt = ProteusLoc.Localize("Parts.Vanilla.Open.Fmt", "{0} (vanilla)");

    public readonly string VanillaSaveNote = ProteusLoc.Localize("Parts.Vanilla.SaveNote",
        "Saving creates a new mod with the refit and an \"Original\" option.");

    public readonly string VanillaCaveats = ProteusLoc.Localize("Parts.Vanilla.Caveats",
        "Refits don't carry shape keys or lower detail levels: hems may shift where pieces meet, and the old "
      + "size shows at a distance.");


    /// <summary>The label over the choice of WHICH MOD a refit is written into.</summary>
    public readonly string RetargetSaveToMod = ProteusLoc.Localize("Parts.Retarget.SaveToMod", "Save into");

    public readonly string RetargetSaveToModTip = ProteusLoc.Localize("Parts.Retarget.SaveToMod.Tip",
        "This mod: joins the author's sizes, but their next update removes it.\n"
      + "New mod: kept separate and survives updates.");

    public readonly string RetargetSaveToThisMod = ProteusLoc.Localize("Parts.Retarget.SaveToThisMod", "This mod");

    public readonly string RetargetSaveToNewMod = ProteusLoc.Localize("Parts.Retarget.SaveToNewMod", "A new mod");

    /// <summary>The row above the body mods in the "made for" picker.</summary>
    public readonly string RetargetFromVanilla = ProteusLoc.Localize("Parts.Retarget.FromVanilla", "Vanilla body");

    /// <summary>Said when the refit is about to write into a mod that does not exist yet.</summary>
    public readonly string RetargetSaveNewModFmt = ProteusLoc.Localize("Parts.Retarget.SaveNewMod.Fmt",
        "Saved into new mod \"{0}\".");

    public readonly string BrushSize = ProteusLoc.Localize("Parts.Brush.Size", "Brush size")
                                     + "###partsBrushSize";

    public readonly string BrushSizeTip = ProteusLoc.Localize("Parts.Brush.Size.Tip",
        "How far the brush reaches. The effect is strongest in the middle and fades\n" +
        "to nothing at the edge, so a wide brush moves a broad swell and a narrow\n" +
        "one moves a small bump.");

    public readonly string BrushStrength = ProteusLoc.Localize("Parts.Brush.Strength", "Strength")
                                         + "###partsBrushStrength";

    public readonly string BrushStrengthTip = ProteusLoc.Localize("Parts.Brush.Strength.Tip",
        "How far the middle of the brush moves the surface for each quarter second you\n" +
        "hold it, the same on any frame rate. Small is usually right: clothing only has\n" +
        "to clear the body by a fraction of a millimetre, and you can always paint over\n" +
        "the same place again.");

    public readonly string BrushLimit = ProteusLoc.Localize("Parts.Brush.Limit", "Limit")
                                      + "###partsBrushLimit";

    public readonly string BrushLimitTip = ProteusLoc.Localize("Parts.Brush.Limit.Tip",
        "The furthest the brushes may move any point from where the model's author\n" +
        "put it. Raise it for a bigger change; painting stops at the limit, so a\n" +
        "held brush cannot balloon the garment.");

    /// <summary>The brush is finer than the mesh. {0} is the model's average edge length in millimetres.</summary>
    public readonly string BrushTooSmallFmt = ProteusLoc.Localize("Parts.Brush.TooSmall.Fmt",
        "The brush is smaller than this model's triangles (about {0:F1} mm across), so it will pull single "
      + "points into spikes instead of moving the surface. Make it larger.");

    /// <summary>{0} is the furthest anything has moved, {1} the limit, both in millimetres.</summary>
    public readonly string BrushMovedFmt = ProteusLoc.Localize("Parts.Brush.Moved.Fmt",
        "Furthest moved: {0:F2} mm of {1:F1} mm.");

    public readonly string BrushUntouched = ProteusLoc.Localize("Parts.Brush.Untouched",
        "Nothing has been moved yet.");

    public readonly string BrushUndo = ProteusLoc.Localize("Parts.Brush.Undo", "Undo stroke")
                                     + "###partsBrushUndo";

    public readonly string BrushReset = ProteusLoc.Localize("Parts.Brush.Reset", "Start over")
                                      + "###partsBrushReset";

    public readonly string BrushNotSavedYet = ProteusLoc.Localize("Parts.Brush.NotSavedYet",
        "not saved yet");

    public readonly string BrushSave = ProteusLoc.Localize("Parts.Brush.Save", "Save into the mod")
                                     + "###partsBrushSave";

    public readonly string BrushSaveTip = ProteusLoc.Localize("Parts.Brush.Save.Tip",
        "Writes the change into the mod's own model file, so it keeps working with\n"
      + "Proteus turned off and travels with the mod if you export it. The original\n"
      + "is copied to Proteus/meshvolume-backup/ inside the mod first, and this can\n"
      + "be undone.");

    public readonly string BrushSaved = ProteusLoc.Localize("Parts.Brush.Saved", "Saved.");

    /// <summary>{0} is a count of shape values that could not be carried.</summary>
    public readonly string BrushSparesFmt = ProteusLoc.Localize("Parts.Brush.Spares.Fmt",
        "{0} points belonging to this model's body sliders could not be moved with it, so turning one of "
      + "those sliders on may bring the clipping back in places.");

    public readonly string BrushRevert = ProteusLoc.Localize("Parts.Brush.Revert", "Undo saved changes")
                                       + "###partsBrushRevert";

    public readonly string BrushRevertTip = ProteusLoc.Localize("Parts.Brush.Revert.Tip",
        "Puts every model the brush has changed in this mod back exactly as its\n"
      + "author made it.");

    public readonly string BrushApplySizes = ProteusLoc.Localize("Parts.Brush.ApplySizes", "Apply to other sizes")
                                           + "###partsBrushApplySizes";

    public readonly string BrushApplySizesTip = ProteusLoc.Localize("Parts.Brush.ApplySizes.Tip",
        "Copies the changes brushed on this model onto the mod's other sizes of it,\n"
      + "matched by where they sit on the garment. Each size is backed up first,\n"
      + "and Undo saved changes puts it back.");

    public readonly string BrushApplySizesRunning = ProteusLoc.Localize("Parts.Brush.ApplySizes.Running",
        "Applying to other sizes…");

    /// <summary>{0} is how many other sizes were written.</summary>
    public readonly string BrushAppliedSizesFmt = ProteusLoc.Localize("Parts.Brush.ApplySizes.Done.Fmt",
        "Applied to {0} other size(s).");

    /// <summary>{0} is the size's label, {1} what went wrong.</summary>
    public readonly string BrushApplySizesProblemFmt = ProteusLoc.Localize("Parts.Brush.ApplySizes.Problem.Fmt",
        "{0}: {1}");

    /// <summary>The Save button with nothing waiting to save.</summary>
    public readonly string BrushSaveNothingTip = ProteusLoc.Localize("Parts.Brush.Save.Nothing.Tip",
        "Everything is already saved. Changes save on their own a moment after you stop painting.");

    /// <summary>Added under <see cref="BrushRevertTip"/>: the button is armed only while a modifier is held.</summary>
    public readonly string BrushRevertArmTip = ProteusLoc.Localize("Parts.Brush.Revert.Arm.Tip",
        "Hold Ctrl or Shift and click.");

    // ── body retarget ───────────────────────────────────────────────────────

    public readonly string ToolRetarget = ProteusLoc.Localize("Parts.Tool.Retarget", "Body size")
                                        + "###partsToolRetarget";

    public readonly string ToolRetargetTip = ProteusLoc.Localize("Parts.Tool.Retarget.Tip",
        "Refit this model onto another body or size, saved as a new option.");

    // The first-use tips. Each tip is its own string, so a translation can reword one without the others.
    // Changing any of them? Bump BodyRetargetPanel.GuideVersion so players see the popup again.
    public readonly string RetargetGuideTitle = ProteusLoc.Localize("Parts.Retarget.Guide.Title", "Body size tips")
                                              + "###partsRetargetGuide";

    public readonly string RetargetGuideTip1 = ProteusLoc.Localize("Parts.Retarget.Guide.Tip1",
        "Works best with the body the piece was made for installed.");

    public readonly string RetargetGuideTip2 = ProteusLoc.Localize("Parts.Retarget.Guide.Tip2",
        "For pieces that cover several parts, like a dress, set sizes for each part.");

    public readonly string RetargetGuideTip3 = ProteusLoc.Localize("Parts.Retarget.Guide.Tip3",
        "Untick \"Use the new body's skin\" to keep skindenting or push-up shaping, unless the two bodies have "
      + "different skeletons.");

    public readonly string RetargetGuideTip4 = ProteusLoc.Localize("Parts.Retarget.Guide.Tip4",
        "Previews aren't kept until you press \"Save as a new option\".");

    public readonly string RetargetGuideTip5 = ProteusLoc.Localize("Parts.Retarget.Guide.Tip5",
        "Touch up afterwards with the other brushes, like Pull out. Use Move for rigid pieces.");

    public readonly string RetargetGuideTip6 = ProteusLoc.Localize("Parts.Retarget.Guide.Tip6",
        "Refit from a smaller size to a larger one where you can.");

    public readonly string RetargetGuideOk = ProteusLoc.Localize("Parts.Retarget.Guide.Ok", "Got it")
                                           + "###partsRetargetGuideOk";

    public readonly string RetargetToBody =ProteusLoc.Localize("Parts.Retarget.ToBody", "Refit onto body");

    public readonly string RetargetFromBody = ProteusLoc.Localize("Parts.Retarget.FromBody", "Made for body");

    public readonly string RetargetFromBodyTip = ProteusLoc.Localize("Parts.Retarget.FromBody.Tip",
        "Same as the target: refit between its sizes.\n"
      + "Different: the cloth also takes the new body's bone weights (the outfit's own bones keep theirs).");

    public readonly string RetargetClearBody = ProteusLoc.Localize("Parts.Retarget.ClearBody", "Push cloth out of the body");

    public readonly string RetargetClearBodyTip = ProteusLoc.Localize("Parts.Retarget.ClearBody.Tip",
        "Push out all buried cloth, not just what the refit buried.\n"
      + "Use it if the body shows through, e.g. stockings with their own leg skin.\n"
      + "Off by default: authors usually hide cloth under skin on purpose.");

    public readonly string RetargetNoBody = ProteusLoc.Localize("Parts.Retarget.NoBody", "Pick one…");

    /// <summary>{0} is a body mod's name; {1} is <see cref="RetargetMale"/> or <see cref="RetargetFemale"/>.</summary>
    public readonly string RetargetNoBodiesForSexFmt = ProteusLoc.Localize("Parts.Retarget.NoBodiesForSex.Fmt",
        "{0} has no {1} bodies.");

    public readonly string RetargetMale = ProteusLoc.Localize("Parts.Retarget.Male", "male");

    public readonly string RetargetFemale = ProteusLoc.Localize("Parts.Retarget.Female", "female");

    public readonly string RetargetPickBody = ProteusLoc.Localize("Parts.Retarget.PickBody",
        "Pick a body mod. Only mods with a choice of body models are listed.");

    /// <summary>{0} is the slot's name, {1} how many options it has.</summary>
    public readonly string RetargetSlotFmt = ProteusLoc.Localize("Parts.Retarget.Slot.Fmt", "{0} — {1} options");

    /// <summary>{0} is the slot's name.</summary>
    public readonly string RetargetSlotOptionalFmt = ProteusLoc.Localize("Parts.Retarget.Slot.Optional.Fmt",
        "{0} — optional");

    public readonly string RetargetSlotOptionalTip = ProteusLoc.Localize("Parts.Retarget.Slot.Optional.Tip",
        "Only if the garment reaches here, like a long dress over the legs.");

    public readonly string RetargetFrom = ProteusLoc.Localize("Parts.Retarget.From", "Made for");

    public readonly string RetargetTo = ProteusLoc.Localize("Parts.Retarget.To", "Refit onto");

    public readonly string RetargetOtherParts = ProteusLoc.Localize("Parts.Retarget.OtherParts",
        "Other body parts (optional)");

    /// <summary>{0} is how many of the other parts have a size chosen to refit onto.</summary>
    public readonly string RetargetOtherPartsInUseFmt = ProteusLoc.Localize("Parts.Retarget.OtherPartsInUse.Fmt",
        "Other body parts ({0} in use)");

    /// <summary>
    /// A part with a size to refit onto but no size it was made for. It is not being refitted, and says so where the
    /// size is chosen rather than holding the whole refit up: with the last sizes filled in for every part, this is
    /// the ordinary state of every part the garment does not reach.
    /// </summary>
    public readonly string RetargetSlotSittingOut = ProteusLoc.Localize("Parts.Retarget.SlotSittingOut",
        "Not refitted: pick a \"Made for\" size to include it.");

    /// <summary>
    /// {0} is the slot's name ("Chest"). The garment's own part only, which is the one part that is required — a new
    /// key, because the text it replaces told the user to untick their way out of a hold that no longer exists.
    /// </summary>
    public readonly string RetargetNeedFromFmt = ProteusLoc.Localize("Parts.Retarget.NeedFromRequired.Fmt",
        "{0}: pick the size it was made for.");

    /// <summary>{0} is the slot's name ("Chest").</summary>
    public readonly string RetargetNeedToFmt = ProteusLoc.Localize("Parts.Retarget.NeedTo.Fmt",
        "{0}: pick a size to refit onto.");

    public readonly string RetargetRefusedHold = ProteusLoc.Localize("Parts.Retarget.RefusedHold",
        "A chosen pair can't be refitted (see above).");

    public readonly string RetargetToMany = ProteusLoc.Localize("Parts.Retarget.ToMany", "Refit onto (pick any number)");

    /// <summary>{0} is which size is being refitted, {1} how many there are.</summary>
    public readonly string RetargetWorkingFmt = ProteusLoc.Localize("Parts.Retarget.Working.Fmt", "Refitting {0} of {1}…");

    public readonly string RetargetShowing = ProteusLoc.Localize("Parts.Retarget.Showing", "Showing on the character");

    /// <summary>{0} is how many sizes will be saved.</summary>
    public readonly string RetargetSaveManyFmt = ProteusLoc.Localize("Parts.Retarget.SaveMany.Fmt",
                                                              "Save {0} sizes as new options")
                                               + "###partsRetargetSave";

    public readonly string RetargetChoose = ProteusLoc.Localize("Parts.Retarget.Choose", "Choose…");

    /// <summary>
    /// The first entry in an optional part's dropdowns. Picking it, in either dropdown, takes the part out of the
    /// refit: both of its sizes are cleared and the remembered one is forgotten, so it is not filled back in.
    /// </summary>
    public readonly string RetargetLeaveOut = ProteusLoc.Localize("Parts.Retarget.LeaveOut", "None (leave this part out)");

    public readonly string RetargetChest = ProteusLoc.Localize("Parts.Retarget.Chest", "Chest");
    public readonly string RetargetLegs  = ProteusLoc.Localize("Parts.Retarget.Legs",  "Legs");
    public readonly string RetargetHands = ProteusLoc.Localize("Parts.Retarget.Hands", "Hands");
    public readonly string RetargetFeet  = ProteusLoc.Localize("Parts.Retarget.Feet",  "Feet");

    public readonly string RetargetChecking = ProteusLoc.Localize("Parts.Retarget.Checking", "Detecting the original size…");

    public readonly string RetargetWorking = ProteusLoc.Localize("Parts.Retarget.Working", "Refitting…");

    /// <summary>{0} is the option the garment's own body mesh matched exactly.</summary>
    public readonly string RetargetExactFmt = ProteusLoc.Localize("Parts.Retarget.Exact.Fmt", "Exact match: {0}.");

    public readonly string RetargetLikely = ProteusLoc.Localize("Parts.Retarget.Likely", "Very likely match.");

    public readonly string RetargetGuess = ProteusLoc.Localize("Parts.Retarget.Guess", "Best guess. Worth checking.");

    /// <summary>{0} is a comma-separated list of the sizes that fit equally well.</summary>
    public readonly string RetargetAmbiguousFmt = ProteusLoc.Localize("Parts.Retarget.Ambiguous.Fmt",
        "Several sizes fit equally ({0}). Pick the right one.");

    /// <summary>{0} is the option the garment's cloth fits most snugly without passing through.</summary>
    public readonly string RetargetClothLikelyFmt = ProteusLoc.Localize("Parts.Retarget.Cloth.Likely.Fmt",
        "From the cloth: fits {0} most snugly.");

    /// <summary>{0} is the option the garment's cloth fits most snugly without passing through.</summary>
    public readonly string RetargetClothGuessFmt = ProteusLoc.Localize("Parts.Retarget.Cloth.Guess.Fmt",
        "From the cloth: best guess {0}, but it may just be loose. Worth checking.");

    /// <summary>{0} is the slot's name, lower case ("legs").</summary>
    public readonly string RetargetTooLittleFmt = ProteusLoc.Localize("Parts.Retarget.TooLittle.Fmt",
        "Too little body mesh on the {0} to detect a size. Pick both sizes if the garment reaches here.");

    public readonly string RetargetNoBodyMesh = ProteusLoc.Localize("Parts.Retarget.NoBodyMesh",
        "No body mesh to detect a size from. Pick it yourself.");

    public readonly string RetargetPreview = ProteusLoc.Localize("Parts.Retarget.Preview", "Refit and preview")
                                           + "###partsRetargetPreview";

    public readonly string RetargetClearPreview = ProteusLoc.Localize("Parts.Retarget.ClearPreview", "Clear the preview")
                                                + "###partsRetargetClearPreview";

    public readonly string RetargetOtherLods = ProteusLoc.Localize("Parts.Retarget.OtherLods",
        "Lower detail levels aren't refitted; the old size shows at a distance.");

    public readonly string RetargetLowSnap = ProteusLoc.Localize("Parts.Retarget.LowSnap",
        "Body mesh isn't an exact match; the skin seam may be imperfect.");

    public readonly string RetargetSaveTo = ProteusLoc.Localize("Parts.Retarget.SaveTo", "Save to group");

    public readonly string RetargetNewGroup = ProteusLoc.Localize("Parts.Retarget.NewGroup", "A new group…");

    /// <summary>Marks a group in the save list that an earlier refit made, rather than the author.</summary>
    public readonly string RetargetMadeHere = ProteusLoc.Localize("Parts.Retarget.MadeHere", "(from a refit)");

    /// <summary>{0} is the model's label.</summary>
    public readonly string RetargetGroupFmt = ProteusLoc.Localize("Parts.Retarget.Group.Fmt", "Body — {0}");

    /// <summary>{0} is the name of the author's group that also replaces this model.</summary>
    public readonly string RetargetClashFmt = ProteusLoc.Localize("Parts.Retarget.Clash.Fmt",
        "Group \"{0}\" also replaces this model; the new group wins over it.");

    /// <summary>{0} is another group of the mod that replaces the same model; {1} is the group the refit is to be saved into.</summary>
    public readonly string RetargetClashOutrankedFmt = ProteusLoc.Localize("Parts.Retarget.Clash.Outranked.Fmt",
        "Group \"{0}\" also replaces this model and outranks \"{1}\" in Penumbra, so a refit saved there won't show while \"{0}\" is on. Save it to a new group instead.");

    /// <summary>{0} is another option of the multi-choice group {1} that replaces the same model.</summary>
    public readonly string RetargetClashOptionOutrankedFmt = ProteusLoc.Localize("Parts.Retarget.Clash.OptionOutranked.Fmt",
        "Option \"{0}\" of \"{1}\" also replaces this model and comes before the refit in that group, so the refit won't show while \"{0}\" is ticked. Untick it in Penumbra, or save to a new group instead.");

    public readonly string RetargetSave = ProteusLoc.Localize("Parts.Retarget.Save", "Save as a new option")
                                        + "###partsRetargetSave";

    /// <summary>{0} is how many options have been saved, {1} the group they are in.</summary>
    public readonly string RetargetSavedFmt = ProteusLoc.Localize("Parts.Retarget.Saved.Fmt",
        "{0} refit(s) saved in \"{1}\".");

    public readonly string RetargetOpenInPenumbra = ProteusLoc.Localize("Parts.Retarget.OpenInPenumbra", "Open in Penumbra")
                                                  + "###partsRetargetOpen";

    public readonly string RetargetUndo = ProteusLoc.Localize("Parts.Retarget.Undo", "Remove last refit")
                                        + "###partsRetargetUndo";

    /// <summary>{0} is the slot's name.</summary>
    public readonly string RetargetUnreadableFmt = ProteusLoc.Localize("Parts.Retarget.Unreadable.Fmt",
        "The {0} body model could not be read.");

    public readonly string RetargetLockListTip = ProteusLoc.Localize("Parts.Retarget.Lock.ListTip",
        "Untick a part to keep it where the author put it, like a buckle or jewellery. Clicking it on the "
      + "model does the same.");

    /// <summary>The part drawer's header button. {0} is how many are ticked, {1} how many there are.</summary>
    public readonly string PartsTickedFmt = ProteusLoc.Localize("Parts.Dropdown.Ticked.Fmt", "{0} of {1} parts ticked");

    /// <summary>The part drawer's header button under Move, Rotate or Scale, before a part is chosen.</summary>
    public readonly string PartsNoneChosen = ProteusLoc.Localize("Parts.Dropdown.NoneChosen", "Choose a part to move");

    /// <summary>The last line of the part drawer header's tooltip.</summary>
    public readonly string DrawerToggleTip = ProteusLoc.Localize("Parts.Drawer.Toggle.Tip",
        "Click to open the part list in a column of its own, so the model stays in view while you tick. Click again to close it.");

    public readonly string RetargetLockSkinTip = ProteusLoc.Localize("Parts.Retarget.Lock.SkinTip",
        "The garment's own skin. Holding it keeps the old size, so the body may show through or gap.");

    public readonly string RetargetKeepShape = ProteusLoc.Localize("Parts.Retarget.KeepShape", "Keep shape");

    public readonly string RetargetKeepShapeTip = ProteusLoc.Localize("Parts.Retarget.KeepShape.Tip",
        "For rigid pieces (rings, buckles, chains): each piece moves whole instead of bending.\n"
      + "Small closed pieces start ticked.");

    /// <summary>The Keep shape tooltip's last line, under its own key so a translation of the older tooltip cannot
    /// stand in for it.</summary>
    public readonly string RetargetKeepShapeSinkTip = ProteusLoc.Localize("Parts.Retarget.KeepShape.SinkTip",
        "A piece that would sink into the new body bends with it instead.");

    /// <summary>{0} is how many kept pieces the refit let go, {1} the deepest any would have sunk (mm), {2} their
    /// labels.</summary>
    public readonly string RetargetShapeReleasedFmt = ProteusLoc.Localize("Parts.Retarget.ShapeReleased.Fmt",
        "{0:N0} kept piece(s) would sink up to {1:F1} mm into the new body if moved whole, so they bend with it instead: {2}");

    public readonly string RetargetReplaceSkin = ProteusLoc.Localize("Parts.Retarget.ReplaceSkin", "Use the new body's skin")
                                               + "###partsRetargetReplaceSkin";

    public readonly string RetargetReplaceSkinTip = ProteusLoc.Localize("Parts.Retarget.SwapSkin.Tip",
        "On: replace the garment's skin with the new body's.\n"
      + "Off: resize the garment's own skin, keeping its shaping (push-up, skindenting).");

    public readonly string RetargetCutHidden = ProteusLoc.Localize("Parts.Retarget.CutHidden", "Remove hidden skin")
                                             + "###partsRetargetCutHidden";

    public readonly string RetargetCutHiddenTip = ProteusLoc.Localize("Parts.Retarget.CutHidden.Tip",
        "Leave out the new skin where the author deleted theirs, usually under cloth.\n"
      + "Turn off if the refit leaves holes.");

    public readonly string RetargetPiercings = ProteusLoc.Localize("Parts.Retarget.Piercings", "Add the new body's piercings")
                                             + "###partsRetargetPiercings";

    public readonly string RetargetPiercingsTip = ProteusLoc.Localize("Parts.Retarget.Piercings.Tip",
        "On: the new body's piercings come in with its skin where the garment shows that skin.\n"
      + "Nails always come along.");

    public readonly string RetargetCutForcedTip = ProteusLoc.Localize("Parts.Retarget.CutForcedTip",
        "Always on while the shoe keeps its own foot or the glove its own hand: otherwise the whole new legs or arms "
      + "would go in with it.");

    public readonly string RetargetClearForcedTip = ProteusLoc.Localize("Parts.Retarget.ClearForcedTip",
        "Always on for the game's own tops, as the automatic refit does it: the game buries their cloth in its own "
      + "body, so it is always pushed out of the new one.");

    public readonly string RetargetKeepFeet = ProteusLoc.Localize("Parts.Retarget.KeepFeet", "Keep its own feet")
                                            + "###partsRetargetKeepEnds";

    public readonly string RetargetKeepFeetTip = ProteusLoc.Localize("Parts.Retarget.KeepFeet.Tip",
        "The foot stays exactly as its author made it, heel and all; only the leg the shoe reaches is refitted, sized by "
      + "the legs. Untick to refit the whole shoe, feet included, as before.");

    public readonly string RetargetKeepHands = ProteusLoc.Localize("Parts.Retarget.KeepHands", "Keep its own hands")
                                             + "###partsRetargetKeepEnds";

    public readonly string RetargetKeepHandsTip = ProteusLoc.Localize("Parts.Retarget.KeepHands.Tip",
        "The hand stays exactly as its author made it; only the arm the glove reaches is refitted, sized by the chest. "
      + "Untick to refit the whole glove, hands included, as before.");

    public readonly string RetargetNoLimbFeet = ProteusLoc.Localize("Parts.Retarget.NoLimbFeet",
        "This shoe reaches no leg: it keeps its shape, and only its skin is drawn like the new body's. Pick a legs size "
      + "to refit it by anyway.");

    public readonly string RetargetNoLimbHands = ProteusLoc.Localize("Parts.Retarget.NoLimbHands",
        "These gloves reach no arm: they keep their shape, and only their skin is drawn like the new body's. Pick a "
      + "chest size to refit them by anyway.");

    public readonly string RetargetSkinAlreadyMatches = ProteusLoc.Localize("Parts.Retarget.SkinAlreadyMatches",
        "Nothing to change: its skin is already drawn the way the new body draws its own.");

    /// <summary>The preview draws a material the save publishes; until then the game has no such file.</summary>
    public readonly string RetargetSmallclothesAfterSave = ProteusLoc.Localize("Parts.Retarget.Smallclothes.AfterSave",
        "The new body draws underwear where it has no skin, and the refit brings it in: it shows in game only once you save.");

    /// <summary>The new body's underwear could not be brought in, so the garment keeps its own skin there.</summary>
    public readonly string RetargetSmallclothesFallback = ProteusLoc.Localize("Parts.Retarget.Smallclothes.Fallback",
        "The new body draws underwear where it has no skin, which could not be brought into this garment, so it keeps its own skin there.");

    /// <summary>{0}: the size chosen to refit onto.</summary>
    public readonly string RetargetAlreadyFitsFmt = ProteusLoc.Localize("Parts.Retarget.AlreadyFits.Fmt",
        "Nothing to change: where it reaches, {0} is the same shape as the size it was made for.");

    public readonly string RetargetEndsUnreadable = ProteusLoc.Localize("Parts.Retarget.EndsUnreadable",
        "Its rig can't be read, so its foot or hand can't be told from the rest: the whole piece is refitted.");

    /// <summary>{0}: a body mod's name. A shoe's.</summary>
    public readonly string RetargetNoLegsInBodyFmt = ProteusLoc.Localize("Parts.Retarget.NoLegsInBody.Fmt",
        "{0} has no legs to refit by, so the whole piece is refitted.");

    /// <summary>{0}: a body mod's name. Gloves'.</summary>
    public readonly string RetargetNoChestInBodyFmt = ProteusLoc.Localize("Parts.Retarget.NoChestInBody.Fmt",
        "{0} has no chest to refit by, so the whole piece is refitted.");

    public readonly string RetargetSwapShapesFmt = ProteusLoc.Localize("Parts.Retarget.SwapShapes.Fmt",
        "{0} shape key(s) lost to the skin swap. Untick \"Use the new body's skin\" to keep them.");

    /// <summary>{0} is how many triangles the refit left facing the wrong way.</summary>
    public readonly string RetargetFoldedFmt = ProteusLoc.Localize("Parts.Retarget.Folded.Fmt",
        "{0:N0} triangle(s) still flipped (black specks) where the bodies differ too much.");

    /// <summary>Shown in a dropdown when its search box matches nothing.</summary>
    public readonly string NoMatches = ProteusLoc.Localize("Parts.NoMatches", "Nothing matches.");

    public readonly string RetargetNoModel = ProteusLoc.Localize("Parts.Retarget.NoModel", "Open a model above to refit it.");

    public readonly string RetargetFindingBodies = ProteusLoc.Localize("Parts.Retarget.FindingBodies", "Finding body mods…");

    public readonly string RetargetBaking = ProteusLoc.Localize("Parts.Retarget.Baking", "Making a model for your race…");

    public readonly string RetargetBakedFmt = ProteusLoc.Localize("Parts.Retarget.Baked.Fmt",
        "No {1} model exists, so one was made from the {0} model. Saving adds the metadata the game needs.");

    public readonly string RetargetBakeFailedFmt = ProteusLoc.Localize("Parts.Retarget.BakeFailed.Fmt",
        "Couldn't make a {0} model ({1}); refitting the {2} model as is.");

    public readonly string RetargetShaping = ProteusLoc.Localize("Parts.Retarget.Shaping", "Bending the model into the shape the bodies are made for…");

    /// <summary>{0} is the race of the model open, {1} the race picked below it.</summary>
    public readonly string RetargetLoweredFmt = ProteusLoc.Localize("Parts.Retarget.Lowered.Fmt",
        "A {1} model is made from the {0} model, bent into the {1} shape the way the game bends between them. Saving adds the metadata the game needs.");

    public readonly string RetargetShapedDownFmt = ProteusLoc.Localize("Parts.Retarget.ShapedDown.Fmt",
        "There are no {0} bodies to refit between, so the {0} model is refitted in the {1} shape, the way the game bends it, and saved back as {0}.");

    public readonly string RetargetBendBackFmt = ProteusLoc.Localize("Parts.Retarget.Bake.BendBack.Fmt",
        "the refit could not be bent back into the {0} shape ({1})");

    /// <summary>A reason like <see cref="RetargetBendBackFmt"/>; {0} is the skin material names, as the model writes them.</summary>
    public readonly string RetargetForeignSkinFmt = ProteusLoc.Localize("Parts.Retarget.Bake.ForeignSkin.Fmt",
        "it would still draw skin with {0}, a skin material the new body does not use, and the game can crash loading a skin material it cannot find");

    /// <summary>A reason in the {1} of <see cref="RetargetBendBackFmt"/>.</summary>
    public readonly string RetargetBakeNoWearer = ProteusLoc.Localize("Parts.Retarget.Bake.NoWearer",
        "your character could not be read");

    /// <summary>Shown alone in the Body size tool; {0} is a lower-case reason such as <see cref="RetargetBendBackFmt"/>.</summary>
    public readonly string RetargetRefusedFmt = ProteusLoc.Localize("Parts.Retarget.Refused.Fmt", "Couldn't refit this model: {0}.");

    /// <summary>A reason in the {1} of <see cref="RetargetBakeFailedFmt"/>.</summary>
    public readonly string RetargetBakeReadBack = ProteusLoc.Localize("Parts.Retarget.Bake.ReadBack",
        "the new model could not be read back");

    /// <summary>Heads the "Refit for" dropdown: which race's model the refit is made and saved as.</summary>
    public readonly string RetargetRaceLabel = ProteusLoc.Localize("Parts.Retarget.Race.Label", "Refit for");

    public readonly string RetargetRaceTip = ProteusLoc.Localize("Parts.Retarget.Race.Tip",
        "Which race the refit is for. Another race gets its own model, made from this one the way the game bends it, refitted onto that race's body and saved where that race loads it. A Midlander chest or legs garment can also be refitted for the other sex. This model stays as it is.");

    /// <summary>{0} is a race of the other sex such as "Midlander M"; {1} the race of the model open.</summary>
    public readonly string RetargetRaceOtherSexFmt = ProteusLoc.Localize("Parts.Retarget.Race.OtherSex.Fmt",
        "{0} (refitted from this {1} model)");

    /// <summary>{0} is the race of the model open, {1} the race picked (the other sex).</summary>
    public readonly string RetargetRaceCrossSexNoteFmt = ProteusLoc.Localize("Parts.Retarget.Race.CrossSexNote.Fmt",
        "Refitted across the sexes: the {0} bodies it was made for are bent to {1}'s shape, then matched to the new body point by point through the body maps. Check it in game before you share it.");

    /// <summary>{0} is the race picked; {1} the races that also wear it, comma-separated; {2} the race of the model open.</summary>
    public readonly string RetargetRaceCrossSexWearsFmt = ProteusLoc.Localize("Parts.Retarget.Race.CrossSexWears.Fmt",
        "{0} will wear the saved refit, and so will {1} wherever they have no model of their own for this item; {2} keeps this model.");

    /// <summary>{0} is a count of folded faces; {1} a share such as "97%".</summary>
    public readonly string RetargetRaceCrossSexQualityFmt = ProteusLoc.Localize("Parts.Retarget.Race.CrossSexQuality.Fmt",
        "{0} folded faces; {1} of the body matched.");

    /// <summary>{0} is a race of the other sex such as "Midlander M".</summary>
    public readonly string RetargetRaceCrossSexHintFmt = ProteusLoc.Localize("Parts.Retarget.Race.CrossSexHint.Fmt",
        "To make a version for {0}, pick it under “Refit for”.");

    /// <summary>{0} is the race picked under "Refit for", such as "Midlander M", for the game's own gear.</summary>
    public readonly string RetargetRaceReplacesGameFmt = ProteusLoc.Localize("Parts.Retarget.Race.ReplacesGame.Fmt",
        "The game has its own {0} model of this item. Once saved, this refit replaces it for everyone you see wearing the game's {0} model while the new mod is on. Choose “Original” in that mod, or turn it off, to bring the game's back.");

    /// <summary>{0} is the race of the character playing, such as "Highlander M".</summary>
    public readonly string RetargetRaceCharacterKeepsGameFmt = ProteusLoc.Localize("Parts.Retarget.Race.CharacterKeepsGame.Fmt",
        "Your character is {0}, and the game has a {0} model of this item of its own: this refit won't show on your character.");

    /// <summary>{0} is the race of the model open, such as "Midlander F".</summary>
    public readonly string RetargetRefuseCrossSexMadeForFmt = ProteusLoc.Localize("Parts.Retarget.Refuse.CrossSexMadeFor.Fmt",
        "Across the sexes, the bodies this garment was made on have to be {0} bodies. Pick the body mod it was made for under “Made for body”.");

    /// <summary>{0} the share of the body matched, as found; {1} the least that is saved.</summary>
    public readonly string RetargetRefuseCrossSexCoverageFmt = ProteusLoc.Localize("Parts.Retarget.Refuse.CrossSexCoverage.Fmt",
        "This refit across the sexes matched only {0} of the body through the body maps. Proteus only saves one that matches at least {1}.");

    /// <summary>{0} is a count of cloth faces turned over.</summary>
    public readonly string RetargetRaceCrossSexFoldedFmt = ProteusLoc.Localize("Parts.Retarget.Race.CrossSexFolded.Fmt",
        "{0:N0} cloth face(s) turned over in this refit across the sexes. A turned-over face shows as a hole in game, so check those places before you share it.");

    /// <summary>{0} is the race picked; {1} a count of bones; {2} the moves, such as "ya_shiri_phys_l → iv_shiri_l".</summary>
    public readonly string RetargetRaceBonesMovedFmt = ProteusLoc.Localize("Parts.Retarget.Race.BonesMoved.Fmt",
        "{0}'s skeleton lacks {1} bone(s) this garment leans on. Their weight now follows the nearest bone it has, so those parts move with it rather than on their own: {2}");

    /// <summary>{0} is the race picked; {1} the mod its skeleton comes from; {2} a count of bones; {3} their names.</summary>
    public readonly string RetargetRaceNeedsSkeletonFmt = ProteusLoc.Localize("Parts.Retarget.Race.NeedsSkeleton.Fmt",
        "{0}'s skeleton from {1} gives this model {2} bone(s) the game's own skeleton lacks ({3}). Anyone without that skeleton sees those parts pulled out of place.");

    /// <summary>{0} is a race such as "Midlander M".</summary>
    public readonly string RetargetRaceThisModelFmt = ProteusLoc.Localize("Parts.Retarget.Race.ThisModel.Fmt", "{0} (this model)");

    /// <summary>{0} is a race such as "Midlander F": the race the logged-in character would get it as.</summary>
    public readonly string RetargetRaceAsWornFmt = ProteusLoc.Localize("Parts.Retarget.Race.AsWorn.Fmt",
        "{0} (as your character wears it)");

    /// <summary>{0} is a race such as "Midlander M" the mod already ships a model for (or one that race falls through to).</summary>
    public readonly string RetargetRaceOwnModelReplacedFmt = ProteusLoc.Localize("Parts.Retarget.Race.OwnModelReplaced.Fmt",
        "This mod already has a model of its own that {0} wears. A refit for {0} saved into this mod takes its place while it is on. If that model is the author's own work, open it and refit it instead.");

    /// <summary>A button; {0} is a race such as "Highlander M".</summary>
    public readonly string RetargetRaceOpenModelFmt = ProteusLoc.Localize("Parts.Retarget.Race.OpenModel.Fmt",
        "Open the {0} model");

    /// <summary>{0} is a race such as "Highlander M".</summary>
    public readonly string RetargetRaceBakingFmt = ProteusLoc.Localize("Parts.Retarget.Race.Baking.Fmt",
        "Making the {0} model…");

    /// <summary>{0} is the race picked, {1} the race of the model open.</summary>
    public readonly string RetargetRaceOnlyFmt = ProteusLoc.Localize("Parts.Retarget.Race.Only.Fmt",
        "Only {0} will wear the saved refit; {1} keeps this model.");

    /// <summary>{0} is the race picked, {1} the race of the model open; said for a race the open one falls back to.</summary>
    public readonly string RetargetRaceBelowFmt = ProteusLoc.Localize("Parts.Retarget.Race.Below.Fmt",
        "{0} will wear the saved refit, and so will every race of that sex with no model of its own for this item; {1} keeps this model.");

    /// <summary>{0} is the race picked.</summary>
    public readonly string RetargetRaceBelowExtraBonesFmt = ProteusLoc.Localize("Parts.Retarget.Race.BelowExtraBones.Fmt",
        "This garment uses extra bones. Only {0} is given the extra skeleton for them, so on the other races that wear this refit those bones won't move.");

    /// <summary>{0} is the race picked.</summary>
    public readonly string RetargetRaceSourceBakedFmt = ProteusLoc.Localize("Parts.Retarget.Race.SourceBaked.Fmt",
        "The bodies it was made for are bent to {0} the same way, so it sits on them as it did before.");

    /// <summary>{0} is the race picked.</summary>
    public readonly string RetargetRacePreviewOtherFmt = ProteusLoc.Localize("Parts.Retarget.Race.PreviewOther.Fmt",
        "Your character isn't {0}, so the live preview is bent onto your character, not what {0} will see.");

    /// <summary>{0} is the race picked.</summary>
    public readonly string RetargetRacePreviewNotDrawnFmt = ProteusLoc.Localize("Parts.Retarget.Race.PreviewNotDrawn.Fmt",
        "Your character isn't wearing this model, so there is no live preview. The saved refit is what {0} will wear.");

    /// <summary>{0} is the race picked.</summary>
    public readonly string RetargetRaceSkeletonFallbackFmt = ProteusLoc.Localize("Parts.Retarget.Race.SkeletonFallback.Fmt",
        "{0}'s skeleton from your mods couldn't be read, so the game's own was used.");

    /// <summary>{0} is the race picked. A save refused because where it goes changes what that race's model is made with.</summary>
    public readonly string RetargetRaceRemakeFmt = ProteusLoc.Localize("Parts.Retarget.Race.Remake.Fmt",
        "Where you're saving changes the {0} model, so it's being made again. Refit and preview again, then save.");

    /// <summary>{0} lists "body: reason" pairs.</summary>
    public readonly string RetargetRaceLeftOutFmt = ProteusLoc.Localize("Parts.Retarget.Race.LeftOut.Fmt",
        "Left out of the bodies it was made for, as they would not bend the same way: {0}");

    /// <summary>{0} is the race picked, {1} the race of the model open, {2} the extra skeleton's number.</summary>
    public readonly string RetargetRaceEstCarriedFmt = ProteusLoc.Localize("Parts.Retarget.Race.EstCarried.Fmt",
        "Saving also gives {0} the extra skeleton {1} loads for this item ({2}), so its extra bones move.");

    /// <summary>Starts a saved option's name when the target body mod's own name is not known.</summary>
    public readonly string RetargetBodyFallback = ProteusLoc.Localize("Parts.Retarget.BodyFallback", "Body");

    public readonly string RetargetCheckingPair = ProteusLoc.Localize("Parts.Retarget.CheckingPair",
        "Checking the sizes match…");

    /// <summary>Added under <see cref="BrushSizeTip"/>.</summary>
    public readonly string BrushSizeKeysTip = ProteusLoc.Localize("Parts.Brush.Size.Keys.Tip",
        "[ and ] change it; hold Shift for fine steps.");

    public readonly string BrushMirror = ProteusLoc.Localize("Parts.Brush.Mirror", "Mirror left/right")
                                       + "###partsBrushMirror";

    /// <summary>On <see cref="BrushMirror"/>, shown under the brushes and under Move, Rotate and Scale alike.</summary>
    public readonly string MirrorTip = ProteusLoc.Localize("Parts.Mirror.Tip",
        "Work on both sides at once, mirrored across the body's centre. The brushes paint the mirror image too; Move, Rotate and Scale move the matching cloth on the other side the mirrored way — and, with Move skin too, the matching skin. A piece that is its own mirror stays centred.");

    /// <summary>Under Move, Rotate and Scale with Mirror on: {0} selected points matched, of {1}.</summary>
    public readonly string MoveMirrorPairedFmt = ProteusLoc.Localize("Parts.Move.Mirror.Paired.Fmt",
        "Mirror: {0:N0} of {1:N0} selected points have a match on the other side.");

    /// <summary>Added under <see cref="MoveMirrorPairedFmt"/>: {0} of the matches are locked.</summary>
    public readonly string MoveMirrorHeldFmt = ProteusLoc.Localize("Parts.Move.Mirror.Held.Fmt",
        "{0:N0} of the matching points are locked and stay put.");

    public readonly string MoveMirrorNone = ProteusLoc.Localize("Parts.Move.Mirror.None",
        "Mirror: nothing on the other side matches this selection, so only this side moves.");

    public readonly string MoveMirrorCentred = ProteusLoc.Localize("Parts.Move.Mirror.Centred",
        "Mirror: this selection is its own mirror, so it moves as one piece and stays centred. Sideways moves and sideways turns are left out.");

    public readonly string SkinMoves = ProteusLoc.Localize("Parts.SkinMoves", "Move skin too") + "###partsSkinMoves";

    public readonly string SkinMovesTip = ProteusLoc.Localize("Parts.SkinMoves.Tip",
        "Lets the brushes and Move, Rotate and Scale move the body's skin as well as the clothes. Clothes lying on the "
        + "skin (within about 3 cm) move with it so the body doesn't poke through; clothes further out stay put. Off, "
        + "the skin stays exactly where the author put it.\n\n"
        + "The brushes leave the skin's edges in place where this piece meets the next. Moving skin there with Move, "
        + "Rotate or Scale can open a gap at the waist, wrists or neck.\n\n"
        + "Body size and the automatic refit replace a garment's skin with the new body's, so a sculpted skin does not "
        + "survive a refit.");

    public readonly string SkinMovesNotThisBrushTip = ProteusLoc.Localize("Parts.SkinMoves.NotThisBrush.Tip",
        "The bridge and wind brushes work on clothes only; the skin stays still under them.");

    public readonly string SkinMovesPreparing = ProteusLoc.Localize("Parts.SkinMoves.Preparing", "Getting the skin ready…");

    public readonly string SkinMovesBodyModelWarn = ProteusLoc.Localize("Parts.SkinMoves.BodyModel.Warn",
        "This is a body. Garments made for it are recognised by its exact shape, so after sculpting it Body size and "
        + "the automatic refit may no longer know which body they were made for.");

    public readonly string SkinMovesFailed = ProteusLoc.Localize("Parts.SkinMoves.Failed",
        "The skin could not be got ready, so it stays still. The log says why.");

    /// <summary>Added under every brush tool's tooltip.</summary>
    public readonly string BrushLockHint = ProteusLoc.Localize("Parts.Brush.Lock.Hint",
        "Shift-click a part to lock it.");

    public readonly string BrushLockListTip = ProteusLoc.Localize("Parts.Brush.Lock.ListTip",
        "Untick a part to lock it: no brush moves it, and it is hidden so you can reach what is beneath it. "
      + "Shift-clicking a part on the model or on your character locks it too; tick it here to bring it back.");

    /// <summary>{0} is how many parts are locked.</summary>
    public readonly string BrushLockCountFmt = ProteusLoc.Localize("Parts.Brush.Lock.Count.Fmt",
        "{0} part(s) locked");

    public readonly string BrushUnlockAll = ProteusLoc.Localize("Parts.Brush.Lock.UnlockAll", "Unlock all")
                                          + "###partsBrushUnlockAll";

    public readonly string BrushLockSkinTip = ProteusLoc.Localize("Parts.Brush.Lock.SkinOff.Tip",
        "Skin stays still while “Move skin too” is off.");

    /// <summary>{0} is how many model files were put back.</summary>
    public readonly string BrushRevertedFmt = ProteusLoc.Localize("Parts.Brush.Reverted.Fmt",
        "{0} model(s) put back as the author made them.");

    public readonly string ShatteredFmt = ProteusLoc.Localize("Parts.Shattered.Fmt",
        "Part {0} falls into {1} separate pieces, which is more than can be listed. Click the model to pick " +
        "one, or switch the whole part.");

    /// <summary>
    /// Hover on a part the author already switches. It can still take one of ours — the game draws a piece
    /// only when ALL its attributes are on — so this states the stacking rather than refusing it.
    /// </summary>
    public readonly string StacksWithAuthorTip = ProteusLoc.Localize("Parts.StacksWithAuthor.Tip",
        "The mod's author already has a switch on this part. A switch of yours stacks on top: the part " +
        "shows only when both are on.");

    /// <summary>The one case still refused — see <c>ModelPart.Toggleable</c>.</summary>
    public readonly string UnreadableTagTip = ProteusLoc.Localize("Parts.UnreadableTag.Tip",
        "This part is tagged with something the model does not name, so Proteus cannot tell what already " +
        "controls it and will not risk a switch that collides with one.");

    /// <summary>Dimmed marker on a row, so the stacking is visible without hovering.</summary>
    public readonly string AuthorSwitchedTag = ProteusLoc.Localize("Parts.AuthorSwitched.Tag", "author switch");

    public readonly string LegacyMod = ProteusLoc.Localize("Parts.LegacyMod",
        "This mod is still in Penumbra's old layout, which Proteus will not write to. Enable it in " +
        "Penumbra once so it updates itself, then come back.");

    /// <summary>Expander on a submesh row. {0} is how many separate pieces it holds.</summary>
    public readonly string ShowPiecesFmt = ProteusLoc.Localize("Parts.ShowPieces.Fmt", "{0} pieces ▾");

    public readonly string HidePiecesFmt = ProteusLoc.Localize("Parts.HidePieces.Fmt", "{0} pieces ▴");

    public readonly string SelectedFmt = ProteusLoc.Localize("Parts.Selected.Fmt", "{0} part(s) ticked");

    public readonly string BudgetFmt = ProteusLoc.Localize("Parts.Budget.Fmt", "{0} of 10 switches left on this model");

    public readonly string NoBudget = ProteusLoc.Localize("Parts.NoBudget",
        "This model has no switch slots left. The game gives each item ten, and this one's author has " +
        "used them all.");

    public readonly string ToggleName = ProteusLoc.Localize("Parts.ToggleName", "Name") + "###partsToggleName";

    public readonly string AddBtn = ProteusLoc.Localize("Parts.Add.Btn", "Make a switch from the ticked parts");

    public readonly string NeedName  = ProteusLoc.Localize("Parts.NeedName", "Give the switch a name first.");
    public readonly string NeedParts = ProteusLoc.Localize("Parts.NeedParts", "Tick the parts this switch should hide.");
    public readonly string NameTaken = ProteusLoc.Localize("Parts.NameTaken",
        "This item already has a switch with this name, written or waiting to be written. Give this one a different name.");

    public readonly string RemoveBtn = ProteusLoc.Localize("Parts.Remove.Btn", "Remove");

    public readonly string PendingHeader = ProteusLoc.Localize("Parts.Pending.Header", "Switches to write");

    public readonly string PendingFmt = ProteusLoc.Localize("Parts.Pending.Fmt", "{0} — {1}");

    public readonly string NotWrittenYet = ProteusLoc.Localize("Parts.NotWrittenYet",
        "Nothing has been written to the mod yet.");

    public readonly string WriteBtn = ProteusLoc.Localize("Parts.Write.Btn", "Write the switches into the mod");

    public readonly string WriteTip = ProteusLoc.Localize("Parts.Write.Tip",
        "Edits the mod's model and adds a Penumbra option group to it. The original model is kept, so this " +
        "can be undone.");

    public readonly string WrittenFmt = ProteusLoc.Localize("Parts.Written.Fmt",
        "Done. {0} switch(es) are now in this mod's own Penumbra settings, under \"{1}\".");

    public readonly string SkippedFmt = ProteusLoc.Localize("Parts.Skipped.Fmt",
        "{0} other model file(s) for this item were left alone, because their parts are arranged " +
        "differently and the same edit would land on the wrong geometry.");

    public readonly string ExistingHeader = ProteusLoc.Localize("Parts.Existing.Header", "Already added by Proteus");

    public readonly string RevertBtn = ProteusLoc.Localize("Parts.Revert.Btn", "Undo — restore the original models");

    /// <summary>
    /// Deliberately does not promise the option GROUP was removed. When Proteus merged its switches into a
    /// group the mod's author wrote, undoing takes the switches back out and leaves their group standing.
    /// </summary>
    public readonly string RevertedFmt = ProteusLoc.Localize("Parts.Reverted.Fmt",
        "Undone. {0} model file(s) restored, and the switches removed from this mod's settings.");
}

/// <summary>The named-looks strip at the top of a mod's colour editor. See <see cref="Gui.PresetBar"/>.</summary>
public sealed class PresetsStrings
{
    public readonly string Header = ProteusLoc.Localize("Presets.Header", "Presets");

    /// <summary>The collapsing header while a preset is worn, so a closed section still says which.</summary>
    public readonly string HeaderAppliedFmt = ProteusLoc.Localize("Presets.HeaderApplied.Fmt", "Presets — {0}");

    /// <summary>Bare text, with no "###id" suffix: this is a combo's preview value as well as a
    /// selectable's label, and a preview value is rendered verbatim — the id would show on screen.</summary>
    public readonly string NoPreset = ProteusLoc.Localize("Presets.None", "No preset");

    public readonly string NoPresetTip = ProteusLoc.Localize("Presets.None.Tip",
        "Wear the mod's own colours again. Your option ticks are left exactly as they are.");

    /// <summary>Prefix on a chip the mod author shipped. A glyph rather than a word so it costs no
    /// width in any language.</summary>
    public readonly string PackMarker = ProteusLoc.Localize("Presets.PackMarker", "* ");

    /// <summary>Suffix on the worn chip once the look has drifted from what was saved.</summary>
    public readonly string ModifiedMarker = ProteusLoc.Localize("Presets.ModifiedMarker", "●");

    public readonly string SaveNew = ProteusLoc.Localize("Presets.SaveNew", "+ Save…") + "###presetSaveNew";

    public readonly string SaveNewTip = ProteusLoc.Localize("Presets.SaveNew.Tip",
        "Save how this mod looks right now — its ticked options, colours and layer settings — under a name.");

    public readonly string Save = ProteusLoc.Localize("Presets.Save", "Save") + "###presetSaveGo";
    public readonly string RenameConfirm = ProteusLoc.Localize("Presets.RenameConfirm", "Rename") + "###presetSaveGo";
    public readonly string Cancel = ProteusLoc.Localize("Presets.Cancel", "Cancel");

    public readonly string NeedsAName = ProteusLoc.Localize("Presets.NeedsAName", "Give it a name first.");

    public readonly string NoCollection = ProteusLoc.Localize("Presets.NoCollection",
        "Penumbra hasn't told Proteus which collection you're wearing yet.");

    public readonly string FirstPresetName = ProteusLoc.Localize("Presets.FirstName", "My look");
    public readonly string NthPresetNameFmt = ProteusLoc.Localize("Presets.NthName.Fmt", "My look {0}");

    public readonly string SelectedFmt = ProteusLoc.Localize("Presets.Selected.Fmt", "{0} · {1}");

    public readonly string FromPack = ProteusLoc.Localize("Presets.FromPack", "from the mod");

    public readonly string PackReadOnly = ProteusLoc.Localize("Presets.PackReadOnly",
        "This one came with the mod, so it can't be changed. Duplicate it to make it yours.");

    public readonly string Update = ProteusLoc.Localize("Presets.Update", "Update");

    public readonly string UpdateTip = ProteusLoc.Localize("Presets.Update.Tip",
        "Fold everything on screen back into this preset.");

    public readonly string NothingChanged = ProteusLoc.Localize("Presets.NothingChanged",
        "Nothing has changed since this preset was saved.");

    public readonly string Rename = ProteusLoc.Localize("Presets.Rename", "Rename");
    public readonly string Duplicate = ProteusLoc.Localize("Presets.Duplicate", "Duplicate");

    public readonly string ForkTip = ProteusLoc.Localize("Presets.Fork.Tip",
        "Make an editable copy of the mod's preset and wear it.");

    public readonly string CopyCodeTip = ProteusLoc.Localize("Presets.CopyCode.Tip",
        "Copy this preset as a share code to paste to someone.");

    public readonly string CodeCopied = ProteusLoc.Localize("Presets.CodeCopied", "Share code copied to the clipboard.");

    public readonly string ExportTip = ProteusLoc.Localize("Presets.Export.Tip", "Save this preset as a file.");

    public readonly string DeleteTip = ProteusLoc.Localize("Presets.Delete.Tip", "Hold Ctrl and click to delete this preset.");

    public readonly string PasteCode = ProteusLoc.Localize("Presets.PasteCode", "Paste code");

    public readonly string PasteCodeTip = ProteusLoc.Localize("Presets.PasteCode.Tip",
        "Read a preset share code from the clipboard.");

    public readonly string Import = ProteusLoc.Localize("Presets.Import", "Import…");
    public readonly string ImportTip = ProteusLoc.Localize("Presets.Import.Tip", "Load a preset from a file.");

    public readonly string StagedFmt = ProteusLoc.Localize("Presets.Staged.Fmt", "\"{0}\" is ready to add.");

    public readonly string StagedOtherModFmt = ProteusLoc.Localize("Presets.StagedOtherMod.Fmt",
        "\"{0}\" was made for \"{1}\" by {2}, not for \"{3}\". Adding it anyway will apply whichever of its " +
        "options and colours this mod happens to share.");

    public readonly string AddStaged = ProteusLoc.Localize("Presets.AddStaged", "Add");
    public readonly string Discard = ProteusLoc.Localize("Presets.Discard", "Discard");
    public readonly string AddedFmt = ProteusLoc.Localize("Presets.Added.Fmt", "Added \"{0}\".");

    public readonly string ExportDialogTitle = ProteusLoc.Localize("Presets.ExportDialog.Title", "Save preset");
    public readonly string ImportDialogTitle = ProteusLoc.Localize("Presets.ImportDialog.Title", "Open preset");
    public readonly string DialogFilter = ProteusLoc.Localize("Presets.Dialog.Filter", "Proteus preset");

    // The Import tab's .ptp branch — a preset picked where mods are normally installed.
    public readonly string ImportFailedFmt = ProteusLoc.Localize("Presets.ImportFailed.Fmt",
        "That isn't a preset Proteus can read: {0}");

    public readonly string ImportedFromFmt = ProteusLoc.Localize("Presets.ImportedFrom.Fmt",
        "Preset \"{0}\" — made for \"{1}\" by {2}.");

    public readonly string NoMatchingMod = ProteusLoc.Localize("Presets.NoMatchingMod",
        "You don't have that mod installed under that name. Pick the mod it should go to, or install it first.");

    public readonly string AddTo = ProteusLoc.Localize("Presets.AddTo", "Add to");
    public readonly string PickAMod = ProteusLoc.Localize("Presets.PickAMod", "Pick a mod…");

    public readonly string AddToTip = ProteusLoc.Localize("Presets.AddTo.Tip",
        "Saves it against that mod. Nothing changes on screen until you wear it from the mod's Presets section.");

    public readonly string AddedToFmt = ProteusLoc.Localize("Presets.AddedTo.Fmt",
        "Added \"{0}\" to {1}. Wear it from that mod's Presets section in Colors.");

    public readonly string ExportedFmt = ProteusLoc.Localize("Presets.Exported.Fmt", "Saved to {0}.");
    public readonly string ExportFailedFmt = ProteusLoc.Localize("Presets.ExportFailed.Fmt", "Couldn't save it: {0}");

    public readonly string PartialApplyFmt = ProteusLoc.Localize("Presets.PartialApply.Fmt",
        "\"{0}\" was applied as far as it goes. {1}");

    public readonly string MissingGroupsFmt = ProteusLoc.Localize("Presets.MissingGroups.Fmt",
        "The mod no longer has these option groups: {0}.");

    public readonly string MissingOptionsFmt = ProteusLoc.Localize("Presets.MissingOptions.Fmt",
        "These options are gone: {0}.");

    public readonly string JustNow = ProteusLoc.Localize("Presets.JustNow", "just now");
    public readonly string MinutesAgoFmt = ProteusLoc.Localize("Presets.MinutesAgo.Fmt", "{0} m ago");
    public readonly string HoursAgoFmt = ProteusLoc.Localize("Presets.HoursAgo.Fmt", "{0} h ago");
    public readonly string DaysAgoFmt = ProteusLoc.Localize("Presets.DaysAgo.Fmt", "{0} d ago");

    /// <summary>The Mods-tab column and its combo entry for "nothing pinned".</summary>
    public readonly string ColumnHeader = ProteusLoc.Localize("Presets.Column.Header", "Preset");
    public readonly string ColumnNone = ProteusLoc.Localize("Presets.Column.None", "—");

    public readonly string ColumnTip = ProteusLoc.Localize("Presets.Column.Tip",
        "A saved look for this mod: its ticked options, colours and layer settings. Open Colors to save one.");
}
