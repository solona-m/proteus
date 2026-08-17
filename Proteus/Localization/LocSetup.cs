using System;
using Dalamud.Plugin;
using Proteus.Gui;

namespace Proteus.Localization;

/// <summary>
/// Binds the plugin's string table to the player's Dalamud UI language and keeps it bound.
/// <para/>
/// The translations ride inside the DLL as embedded resources (<c>Proteus.Localization.{lang}.json</c>),
/// so packaging is unchanged and there are no loose files to lose. Every lookup carries its English text
/// as the fallback at the call site, which means a missing, truncated or malformed locale file degrades
/// to English rather than to blanks — a bad translation can never break the UI.
/// </summary>
public sealed class LocSetup : IDisposable
{
    /// <summary>
    /// The languages Proteus ships. Every one is in <c>Dalamud.Localization.ApplicableLangCodes</c>, which
    /// is what <see cref="IDalamudPluginInterface.UiLanguage"/> is filtered against — so each of these is
    /// actually reachable. Deliberately NOT exposed as a plugin-level override: Dalamud only merges the
    /// CJK glyph ranges into the font atlas when ITS OWN language is zh/tw/ko, so letting someone pick
    /// Korean here while Dalamud stayed English would render every glyph as a box.
    /// </summary>
    public static readonly string[] Shipped = ["en", "ja", "de", "fr", "zh", "ko", "es", "ru"];

    /// <summary>
    /// The subset of <see cref="Shipped"/> written in Latin script. Jupiter — the game's display face used
    /// for the tab bar and section headings — carries Latin only, so anything outside this set has to fall
    /// back to the default font or the headings turn to boxes. See <see cref="ProteusStyle.DisplayFontUsable"/>.
    /// </summary>
    private static readonly string[] LatinScript = ["en", "de", "fr", "es"];

    private readonly IDalamudPluginInterface pi;
    private readonly global::Dalamud.Localization loc;

    public LocSetup(IDalamudPluginInterface pi)
    {
        this.pi = pi;

        // The first argument is a manifest-resource PREFIX in embedded mode, not a directory, and it must
        // end in a dot: it is concatenated straight onto "{lang}.json".
        loc = new global::Dalamud.Localization("Proteus.Localization.", string.Empty, useEmbedded: true);

        Apply(pi.UiLanguage);
        pi.LanguageChanged += Apply;
    }

    /// <summary>The language currently in effect, as a two-letter code.</summary>
    public string Current { get; private set; } = "en";

    private void Apply(string langCode)
    {
        // SetupWithLangCode swallows its own exceptions and falls back to English, so an unshipped code
        // (Italian, Norwegian — both reachable via UiLanguage) needs no guard here.
        loc.SetupWithLangCode(langCode);
        Current = langCode;
        ProteusStyle.DisplayFontUsable = Array.IndexOf(LatinScript, langCode) >= 0;

        // Last, and never before SetupWithLangCode: the holders read their values in their constructors,
        // so rebuilding them against the OLD table would cache the outgoing language for good.
        Strings.Reload();
    }

    // There is deliberately no ExportLocalizable() wrapper here. CheapLoc's exporter reads
    // Assembly.Location and hands the path to Mono.Cecil's ReadAssembly — and Dalamud loads plugins from
    // a STREAM, so Location is the empty string at runtime (the same fact BuildStamp exists to work
    // around, see Plugin.BuildStamp). Calling it in-game throws rather than exporting. en.json is instead
    // generated and kept honest by LocalizationTests.CodeKeysMatchEnglishJson, which walks the same IL
    // from disk where the path is real, and runs in CI on every push.

    public void Dispose() => pi.LanguageChanged -= Apply;
}
