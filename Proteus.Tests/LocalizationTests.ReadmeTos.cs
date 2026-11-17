using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>The README text the readme-tos change added: each README quotes its own language's labels, and the
/// translations carry the new text in their own language.</summary>
public sealed partial class LocalizationTests
{
    /// <summary>From the line equal to <paramref name="heading"/> (which must occur once) to the next line starting
    /// with <paramref name="until"/>.</summary>
    private static string ReadmeSlice(string lang, string heading, Func<string, bool> until)
    {
        string[] lines = ReadmeText(lang).Split('\n');
        int[] at = Enumerable.Range(0, lines.Length).Where(i => lines[i].TrimEnd() == heading).ToArray();
        Assert.True(at.Length == 1, $"[{lang}] README has {at.Length} \"{heading}\" lines; expected exactly one");
        int end = Enumerable.Range(at[0] + 1, lines.Length - at[0] - 1).FirstOrDefault(i => until(lines[i]), lines.Length);
        return string.Join('\n', lines[at[0]..end]);
    }

    /// <summary>Everything above the first <c>---</c> line: the README's introduction.</summary>
    private static string ReadmeIntro(string lang)
    {
        string[] lines = ReadmeText(lang).Split('\n');
        int rule = Array.FindIndex(lines, l => l.Trim() == "---");
        Assert.True(rule > 0, $"[{lang}] README has no --- line under its introduction");
        return string.Join('\n', lines[..rule]);
    }

    private static string Bold(Dictionary<string, LocEntry> t, string key) => "**" + t[key].Message + "**";

    /// <summary>A header format's label without its count: "Base skin ({0})" → "Base skin".</summary>
    private static string NoCount(string fmt) => Regex.Replace(fmt, @"\s*[(（]\{0\}[)）]", "");

    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-readme-tos-1")]
    [Trait("Plan", "EDGE-readme-tos-8")]
    public void Readme_intro_names_bindings_and_mens_group(string lang)
    {
        var t = Load(lang);
        string intro = ReadmeIntro(lang);
        foreach (string key in new[] { "Bindings.RestoreCharacter.Label", "SizeSet.MensGroup" })
            Assert.True(intro.Contains(Bold(t, key), StringComparison.Ordinal),
                $"[{lang}] README introduction doesn't name {Bold(t, key)}");
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-readme-tos-2")]
    [Trait("Plan", "EDGE-readme-tos-8")]
    [Trait("Plan", "EDGE-readme-tos-11")]
    [Trait("Plan", "EDGE-readme-tos-12")]
    public void Readme_Import_section_documents_every_pack_kind(string lang)
    {
        var t = Load(lang);
        string import = ReadmeSlice(lang, "#### " + t["Tab.Import"].Message,
            l => l.StartsWith("#### ", StringComparison.Ordinal));

        foreach (string token in new[] { "`.omp`", "`.ttmp2`", "`.zip`", "`.ptp`" })
            Assert.True(import.Contains(token, StringComparison.Ordinal), $"[{lang}] README Import section lost {token}");

        // Regular mods and emissive skin packs are both .pmp; only the emissive pack's own lead line gives a second.
        int pmpLeads = import.Split('\n').Count(l => l.StartsWith("**", StringComparison.Ordinal) && l.Contains("`.pmp`"));
        Assert.True(pmpLeads >= 2, $"[{lang}] README Import section has {pmpLeads} bold .pmp lead lines; the emissive " +
                                   "skin pack paragraph needs its own");

        foreach (string key in new[] { "Import.AsTex.Label", "Eye.Cutout.Label", "Eye.Cutout.Falloff" })
            Assert.True(import.Contains(Bold(t, key), StringComparison.Ordinal),
                $"[{lang}] README Import section doesn't name {Bold(t, key)}");

        if (lang != Source) return;
        Assert.Contains("Five kinds are supported", import);
        string bc7 = import.Split("\n\n").Single(p => p.Contains("**Convert layers to BC7 .tex**"));
        foreach (string s in new[] { "Onion", "Atramentum Luminis", "emissive skin", "off by default" })
            Assert.Contains(s, bc7);
        Assert.False(Regex.IsMatch(bc7, @"\beyes?\b", RegexOptions.IgnoreCase),
            "[en] README says eye packs offer BC7; only Onion, Atramentum Luminis and emissive skin packs do");
    }

    /// <summary>Each README's old "eye glow isn't imported" sentence, in its own words.</summary>
    private static readonly Dictionary<string, string> ReadmeOldEyeGlow = new()
    {
        ["en"] = "Eye glow isn't imported today",
        ["ja"] = "現時点では瞳のグローは取り込まれません",
        ["de"] = "Augenleuchten wird derzeit nicht importiert",
        ["fr"] = "La lueur des yeux n'est pas importée pour l'instant",
        ["zh"] = "目前尚不支持导入眼部发光",
        ["ko"] = "눈 발광은 아직 가져오지 않지만",
        ["es"] = "El brillo de ojos no se importa hoy por hoy",
        ["ru"] = "Свечение глаз сейчас не импортируется",
    };

    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-readme-tos-2")]
    [Trait("Plan", "EDGE-readme-tos-10")]
    public void Readme_no_longer_says_eye_glow_isnt_imported(string lang)
        => Assert.False(ReadmeText(lang).Contains(ReadmeOldEyeGlow[lang], StringComparison.Ordinal),
            $"[{lang}] README still says eye glow isn't imported; eye texture packs import it now");

    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-readme-tos-3")]
    [Trait("Plan", "EDGE-readme-tos-8")]
    public void Readme_Hair_section_documents_earless_Miqote_hair(string lang)
    {
        var t = Load(lang);
        string hair = ReadmeSlice(lang, "##### " + t["Settings.Section.HairPort"].Message, l => l.StartsWith('#'));
        Assert.True(hair.Contains(Bold(t, "HairPort.Earless.Label"), StringComparison.Ordinal),
            $"[{lang}] README Hair section doesn't name {Bold(t, "HairPort.Earless.Label")}");
        Assert.Contains("`Proteus Earless`", hair);
    }

    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-readme-tos-4")]
    [Trait("Plan", "EDGE-readme-tos-8")]
    [Trait("Plan", "EDGE-readme-tos-12")]
    public void Readme_Settings_section_lists_Matcap_images_and_diagnostics(string lang)
    {
        var t = Load(lang);
        string settings = ReadmeSlice(lang, "#### " + t["Tab.Settings"].Message,
            l => l.StartsWith("##### ", StringComparison.Ordinal));

        foreach (string label in new[]
                 {
                     t["Settings.MatcapLibrary"].Message, t["Settings.Section.Diagnostics"].Message,
                     NoCount(t["Settings.Diag.BaseSkin.Header.Fmt"].Message),
                     NoCount(t["Settings.Diag.Reach.Header.Fmt"].Message),
                 })
            Assert.True(settings.Contains("**" + label + "**", StringComparison.Ordinal),
                $"[{lang}] README Settings section doesn't name **{label}**");

        if (lang != Source) return;
        Assert.Contains("Five buttons here", settings);
        Assert.DoesNotContain("Four buttons here", settings);
    }

    /// <summary>The introduction, Import and Hair sections have as many bullets in every translation as in English, so a
    /// translation can't quietly drop one (the emissive pack's unknown-body skip, the eye glow's one face). The heading
    /// skeleton test can't see that: it counts only headings and table rows.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    [Trait("Plan", "AC-readme-tos-7")]
    [Trait("Plan", "EDGE-readme-tos-19")]
    [Trait("Plan", "EDGE-readme-tos-20")]
    [Trait("Plan", "EDGE-readme-tos-23")]
    public void Readme_new_sections_keep_every_bullet(string lang)
    {
        static int Bullets(string s) => s.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        var en = Load(Source);
        var t = Load(lang);
        foreach (var (name, enText, text) in new[]
                 {
                     ("introduction", ReadmeIntro(Source), ReadmeIntro(lang)),
                     ("Import", ReadmeSlice(Source, "#### " + en["Tab.Import"].Message, l => l.StartsWith("#### ", StringComparison.Ordinal)),
                                ReadmeSlice(lang, "#### " + t["Tab.Import"].Message, l => l.StartsWith("#### ", StringComparison.Ordinal))),
                     ("Hair", ReadmeSlice(Source, "##### " + en["Settings.Section.HairPort"].Message, l => l.StartsWith('#')),
                              ReadmeSlice(lang, "##### " + t["Settings.Section.HairPort"].Message, l => l.StartsWith('#'))),
                 })
            Assert.True(Bullets(enText) == Bullets(text),
                $"[{lang}] README {name} has {Bullets(text)} bullets, English has {Bullets(enText)}");
    }

    /// <summary>English sentences the readme-tos change added. Each must be in the English README, and none in a
    /// translation, so English prose with only the labels translated can't pass.</summary>
    private static readonly string[] ReadmeTosEnglishPhrases =
    [
        "Give any part of a mod a matcap", "in one go", "Tie your look to Glamourer designs",
        "Recolour, glow and save looks without touching any files", "Turn other mods into Proteus mods",
        "Make and share overlay mods without leaving the game", "Bring old gear up to date",
        "Solona's mods, Discord, help and downloads come with", "it can take the cat ears off Miqo'te hair",
        "Many hair mods for Miqo'te model cat ears", "two lists show what the last composite did",
        "opens the folder your own matcap pictures live in", "glow art made for a replaced skin shader",
        "and your character isn't drawn yet, that texture is skipped", "eyes with a glow that moves",
        "it's cut for the game's first face", "stores the imported art block-compressed",
    ];

    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-readme-tos-7")]
    [Trait("Plan", "EDGE-readme-tos-9")]
    [Trait("Plan", "EDGE-readme-tos-19")]
    [Trait("Plan", "EDGE-readme-tos-20")]
    public void Readme_new_text_is_translated_not_English(string lang)
    {
        string text = ReadmeText(lang);
        foreach (string phrase in ReadmeTosEnglishPhrases)
            if (lang == Source)
                Assert.True(text.Contains(phrase, StringComparison.Ordinal),
                    $"[en] README no longer says \"{phrase}\"; update {nameof(ReadmeTosEnglishPhrases)}");
            else
                Assert.False(text.Contains(phrase, StringComparison.Ordinal),
                    $"[{lang}] README still has the English \"{phrase}\"; translate it");
    }
}
