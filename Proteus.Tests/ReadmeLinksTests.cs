using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Proteus.Gui;
using Proteus.Localization;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Guards the links from the header band into the README, which break SILENTLY: a fragment that names no
/// heading opens the page at the top, which looks like it worked. Renaming a heading in any one translation
/// is enough to do it, and nobody clicks all four links in all eight languages to find out.
/// </summary>
public sealed class ReadmeLinksTests
{
    /// <summary>Walks up from the test binary to the checkout, the same way ReadmeTranslationTests does.</summary>
    private static readonly Lazy<string?> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    });

    /// <summary>Sections by NAME: ReadmeSection is internal, so it cannot appear in a public theory's signature.</summary>
    public static TheoryData<string, string> Links
    {
        get
        {
            var d = new TheoryData<string, string>();
            foreach (var lang in LocSetup.Shipped)
                foreach (var section in Enum.GetNames<ReadmeSection>())
                    d.Add(lang, section);
            return d;
        }
    }

    /// <summary>
    /// GitHub's heading id: lower-cased, everything but letters, marks, digits, connector punctuation, spaces
    /// and hyphens dropped, then spaces turned into hyphens. The same rule github-slugger implements, and
    /// githubSlug in worker/src/render.js gives the mirror's headings.
    /// </summary>
    private static string Slug(string heading)
        => Regex.Replace(heading.Trim().ToLowerInvariant(), @"[^\p{L}\p{M}\p{N}\p{Pc} -]", "").Replace(' ', '-');

    private static HashSet<string> HeadingSlugs(string lang)
    {
        Assert.True(Root.Value != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        var path = Path.Combine(Root.Value!, ReadmeLinks.DocPathFor(lang));
        Assert.True(File.Exists(path), $"{ReadmeLinks.DocPathFor(lang)} is missing.");

        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var inFence = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            if (raw.StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence) continue;
            var m = Regex.Match(raw, @"^#{1,6}\s+(.+?)\s*#*\s*$");
            if (m.Success) slugs.Add(Slug(m.Groups[1].Value));
        }
        return slugs;
    }

    [Theory]
    [MemberData(nameof(Links))]
    public void AnchorNamesARealHeading(string lang, string sectionName)
    {
        var section = Enum.Parse<ReadmeSection>(sectionName);
        Assert.True(ReadmeLinks.Anchors.TryGetValue(lang, out var anchors),
            $"ReadmeLinks.Anchors has no entry for {lang}, which Proteus ships. Its players would be sent to " +
            "the English README.");
        Assert.True(anchors!.Length == Enum.GetValues<ReadmeSection>().Length,
            $"ReadmeLinks.Anchors[\"{lang}\"] has {anchors.Length} anchors; it needs one per ReadmeSection.");

        var anchor = anchors[(int)section];
        var slugs = HeadingSlugs(lang);
        Assert.True(slugs.Contains(anchor),
            $"The {section} link for {lang} points at #{anchor}, but {ReadmeLinks.DocPathFor(lang)} has no " +
            "heading with that id, so the link opens the page at the top. A heading was probably renamed; " +
            "update ReadmeLinks.Anchors to its new id. Headings in that file: " + string.Join(", ", slugs));
    }

    [Fact]
    public void UnshippedLanguageFallsBackToEnglish()
    {
        Assert.Equal(ReadmeLinks.Url("en", ReadmeSection.Studio), ReadmeLinks.Url("it", ReadmeSection.Studio));
        Assert.Equal(ReadmeLinks.Url("en", ReadmeSection.Studio), ReadmeLinks.Url(null, ReadmeSection.Studio));
    }

    [Fact]
    public void TranslatedLinkPointsAtItsOwnDocument()
    {
        var url = ReadmeLinks.Url("ja", ReadmeSection.Studio);
        Assert.StartsWith("https://dl.solona.info/ja/README.md#", url);
        Assert.StartsWith("https://dl.solona.info/en/README.md#", ReadmeLinks.Url("en", ReadmeSection.Studio));
        Assert.Equal("スタジオ", Uri.UnescapeDataString(url[(url.IndexOf('#') + 1)..]));
    }
}
