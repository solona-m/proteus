using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Proteus.Gui;
using Proteus.Localization;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Terms of Service: the owner's text, in English only, linked from every README and the creator's guide,
/// next to a code licence they leave alone.
/// </summary>
public sealed class ReadmeTermsTests
{
    private static readonly Lazy<string?> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    });

    private static string Repo(string relative)
    {
        Assert.True(Root.Value != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        return Path.Combine(Root.Value!, relative);
    }

    private static string Read(string relative)
    {
        var path = Repo(relative);
        Assert.True(File.Exists(path), $"{relative} is missing.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    public static TheoryData<string> Languages
    {
        get
        {
            var d = new TheoryData<string>();
            foreach (var lang in LocSetup.Shipped) d.Add(lang);
            return d;
        }
    }

    [Fact]
    [Trait("Plan", "AC-readme-tos-5")]
    [Trait("Plan", "EDGE-readme-tos-7")]
    public void Terms_file_is_the_owners_text()
    {
        var text = Read("TERMS.md");
        Assert.Equal("# Terms of Service — Solona's mods and plugins", text.Split('\n')[0].TrimEnd());

        // The owner wraps lines by hand; compare with runs of whitespace collapsed.
        var flat = Regex.Replace(text, @"\s+", " ");
        foreach (var phrase in new[]
                 {
                     "## No hateful or abusive content", "racist caricature, such as blackface",
                     "harasses or threatens a real, identifiable person", "is sexual content involving minors",
                     "sexualises characters who look like children, such as Lalafell",
                     "Solona decides, acting in good faith", "**Lalafell ports:**", "## Proteus's code",
                     "[LICENSE](LICENSE)", "These terms don't change what that licence lets you do with the code.",
                     "## Reporting", "https://discord.gg/solona", "the English text is the one that applies",
                     "State of North Carolina",
                 })
            Assert.True(flat.Contains(phrase, StringComparison.Ordinal), $"TERMS.md no longer says \"{phrase}\".");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    [Trait("Plan", "AC-readme-tos-6")]
    [Trait("Plan", "EDGE-readme-tos-5")]
    [Trait("Plan", "EDGE-readme-tos-13")]
    public void Every_readme_links_the_terms(string lang)
    {
        var doc = ReadmeLinks.DocPathFor(lang);
        var text = Read(doc);
        var link = lang == "en" ? "](TERMS.md)" : "](../TERMS.md)";
        var at = text.IndexOf(link, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{doc} doesn't link the Terms of Service as \"{link}\".");
        if (lang != "en")
            Assert.False(text.Contains("](TERMS.md)", StringComparison.Ordinal),
                $"{doc} links \"TERMS.md\", which resolves to docs/TERMS.md; use \"../TERMS.md\".");

        var rule = Regex.Match(text, @"^---\s*$", RegexOptions.Multiline);
        Assert.True(rule.Success && at < rule.Index, $"{doc} must link the Terms above its first --- line.");

        var nav = Regex.Match(text, @"<!--\s*i18n\s*-->[\s\S]*?<!--\s*/i18n\s*-->");
        Assert.True(nav.Success, $"{doc} has no language switcher block.");
        Assert.False(at >= nav.Index && at < nav.Index + nav.Length,
            $"{doc} links the Terms inside the language switcher, which the mirror replaces.");
    }

    [Fact]
    [Trait("Plan", "AC-readme-tos-6")]
    [Trait("Plan", "EDGE-readme-tos-15")]
    public void For_Creators_links_the_terms_under_Distributing()
    {
        var text = Read("For Creators.md");
        var start = text.IndexOf("### Distributing Your Mod", StringComparison.Ordinal);
        var end = text.IndexOf("### Sample Mods", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "For Creators.md lost its Distributing Your Mod / Sample Mods sections.");
        var section = text[start..end];
        Assert.Contains("](TERMS.md)", section);
        Assert.Contains("sexualise childlike characters", section);
    }

    [Fact]
    [Trait("Plan", "EDGE-readme-tos-14")]
    public void Terms_exist_only_in_English()
    {
        Assert.True(File.Exists(Repo("TERMS.md")), "TERMS.md is missing from the repository root.");
        var translated = Directory.GetFiles(Repo("docs"), "TERMS*.md").Select(Path.GetFileName).ToArray();
        Assert.True(translated.Length == 0,
            "docs/ holds translated Terms: " + string.Join(", ", translated) + ". The English text is the one " +
            "that applies; the READMEs carry a translated summary and link the English file.");
    }

    [Fact]
    [Trait("Plan", "AC-readme-tos-10")]
    [Trait("Plan", "EDGE-readme-tos-21")]
    [Trait("Plan", "EDGE-readme-tos-22")]
    [Trait("Plan", "AC-contrib-clean-room-11")]
    [Trait("Plan", "EDGE-contrib-clean-room-21")]
    [Trait("Plan", "EDGE-contrib-clean-room-22")]
    [Trait("Plan", "AC-proteus-licence-2-1")]
    [Trait("Plan", "EDGE-proteus-licence-2-1")]
    public void Licence_is_the_Proteus_Licence_2_0()   // [AC-contrib-clean-room-11] [EDGE-contrib-clean-room-21] [EDGE-contrib-clean-room-22] [AC-proteus-licence-2-1] [EDGE-proteus-licence-2-1]
    {
        // AC-contrib-clean-room-11 was the 1.0 criterion ("Version 1.0 — in force"); AC-proteus-licence-2-1 supersedes
        // it. 1.0 itself stays in git history only (61c8c7b6:LICENSE).
        var licence = Read("LICENSE").Replace("\r\n", "\n");
        var lines = licence.Split('\n');
        Assert.Equal("PROTEUS LICENCE", lines[0].Trim());
        Assert.Equal("Version 2.0, 18 November 2026", lines[1].Trim());
        Assert.DoesNotContain("DRAFT", licence);
        Assert.DoesNotContain("GNU AFFERO GENERAL PUBLIC LICENSE", licence);
        Assert.False(File.Exists(Repo(Path.Combine("docs", "LICENSE-DRAFT.md"))), "docs/LICENSE-DRAFT.md is still there.");
        // The whole text, pinned: any change to the licence has to be deliberate (update this hash with it).
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(licence)));
        Assert.Equal("B9A1CE90DB32E7DAB1BFA3F79C19203C02A21727B03EB2B0882530C266E57570", hash);
        // GitHub reads any root LICENSE-* file as a second licence.
        Assert.False(Directory.GetFiles(Root.Value!, "LICENSE*")
                         .Any(f => Path.GetFileName(f).StartsWith("LICENSE-", StringComparison.OrdinalIgnoreCase)),
            "A LICENSE-* file sits at the repository root; GitHub would read it as a second licence.");
        Assert.True(File.Exists(Repo(Path.Combine("docs", "pbd-clean-room.md"))), "docs/pbd-clean-room.md is missing.");

        foreach (var lang in LocSetup.Shipped)
        {
            var doc = ReadmeLinks.DocPathFor(lang);
            Assert.False(Read(doc).Contains("LICENSE-DRAFT", StringComparison.Ordinal), $"{doc} links the old draft licence.");
        }
    }
}
