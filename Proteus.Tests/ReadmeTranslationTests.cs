using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Guards the translated READMEs, which fail SILENTLY: nothing at build time reads them, the asset
/// mirror serves whatever it is handed, and a translation that has quietly lost half its sections
/// still renders as a perfectly good-looking page. A reader in that language is the only person who
/// would ever find out, and they have no way to know what they are missing.
/// <para/>
/// These tests deliberately do NOT read the prose — nobody can assert a translation is good. They
/// assert it is the SAME DOCUMENT: same sections in the same order, same tables with the same number
/// of rows, same language switcher, same untranslatable tokens intact. Those are exactly the things
/// that go wrong when a section is added to the English source and the translations are forgotten.
/// </summary>
public sealed class ReadmeTranslationTests
{
    /// <summary>The same eight codes as LocSetup.Shipped and LANGS in worker/src/render.js.</summary>
    private static readonly string[] Locales = ["en", "ja", "de", "fr", "zh", "ko", "es", "ru"];

    /// <summary>The name each language calls itself, as it appears in the switcher line.</summary>
    private static readonly Dictionary<string, string> Native = new()
    {
        ["en"] = "English",
        ["ja"] = "日本語",
        ["de"] = "Deutsch",
        ["fr"] = "Français",
        ["zh"] = "简体中文",
        ["ko"] = "한국어",
        ["es"] = "Español",
        ["ru"] = "Русский",
    };

    public static TheoryData<string> Translations
    {
        get
        {
            var d = new TheoryData<string>();
            foreach (var l in Locales.Where(l => l != "en")) d.Add(l);
            return d;
        }
    }

    /// <summary>
    /// Walks up from the test binary to the checkout, ONCE. The tests run from bin/, and the documents
    /// under test are repo content rather than embedded resources — there is nothing to load them from
    /// but disk. proteus.slnx is the marker because it exists only at the root.
    /// </summary>
    private static readonly Lazy<string?> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    });

    private static string RepoRoot
    {
        get
        {
            Assert.True(Root.Value != null,
                $"Could not find proteus.slnx above {AppContext.BaseDirectory}. These tests read the " +
                "README files from the checkout; they cannot run from a detached copy of the binaries.");
            return Root.Value!;
        }
    }

    /// <summary>English is the root README; every translation lives in docs/.</summary>
    private static string PathFor(string lang)
        => lang == "en" ? "README.md" : Path.Combine("docs", $"README.{lang}.md");

    /// <summary>
    /// Read once per language and shared. KeepsUntranslatableTokens alone is one theory case per
    /// (language, token) pair, and re-reading the file for each of them is a hundred-odd pointless
    /// disk reads of the same seven documents.
    /// </summary>
    private static readonly Dictionary<string, string> Cache = [];

    private static string Load(string lang)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(lang, out var cached)) return cached;

            var full = Path.Combine(RepoRoot, PathFor(lang));
            Assert.True(File.Exists(full),
                $"{PathFor(lang).Replace('\\', '/')} is missing. Proteus ships {lang} in its UI " +
                "(LocSetup.Shipped) and the asset mirror serves /" + lang + "/README.md from this file, " +
                "so a missing translation is a 404 behind a switcher entry that is still shown.");

            var text = File.ReadAllText(full);
            Cache[lang] = text;
            return text;
        }
    }

    /// <summary>The delimited switcher block. Matches NAV_RX in worker/src/render.js.</summary>
    private static readonly Regex NavRx =
        new(@"<!--\s*i18n\s*-->(?<body>[\s\S]*?)<!--\s*/i18n\s*-->", RegexOptions.Compiled);

    private static readonly Regex LinkRx = new(@"\[(?<text>[^\]]+)\]\((?<href>[^)]+)\)", RegexOptions.Compiled);

    private static readonly Regex FenceRx = new(@"^```", RegexOptions.Compiled);

    /// <summary>
    /// The document's shape: heading levels in order, and the line count of each table.
    /// <para/>
    /// Levels, not text — the text is supposed to differ. This catches the failure that actually
    /// happens: a section added to the English README and never carried across, or a table that grew
    /// a row somewhere and not everywhere.
    /// </summary>
    private static (string Headings, string Tables) Skeleton(string md)
    {
        var headings = new List<int>();
        var tables = new List<int>();
        var run = 0;
        var inFence = false;

        foreach (var raw in md.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (FenceRx.IsMatch(line)) { inFence = !inFence; continue; }
            if (inFence) continue;

            var h = Regex.Match(line, @"^(#{1,6})\s");
            if (h.Success) headings.Add(h.Groups[1].Value.Length);

            if (line.StartsWith('|')) run++;
            else if (run > 0) { tables.Add(run); run = 0; }
        }
        if (run > 0) tables.Add(run);

        return (string.Join(",", headings), string.Join(",", tables));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void SameSectionsAndTablesAsEnglish(string lang)
    {
        var en = Skeleton(Load("en"));
        var other = Skeleton(Load(lang));

        Assert.True(en.Headings == other.Headings,
            $"docs/README.{lang}.md has a different heading structure to README.md.\n" +
            $"  en: {en.Headings}\n  {lang}: {other.Headings}\n" +
            "Heading TEXT is expected to differ; the levels and their order are not. A section was " +
            "most likely added to or removed from the English README without the translations following.");

        Assert.True(en.Tables == other.Tables,
            $"docs/README.{lang}.md has different tables to README.md (line counts, in order).\n" +
            $"  en: {en.Tables}\n  {lang}: {other.Tables}\n" +
            "A row was added to a settings or column table in one language only.");
    }

    /// <summary>
    /// The switcher block: present, exactly once, listing all eight languages, with this document's
    /// own language as the unlinked one. A translation that links to itself and not to English is the
    /// classic copy-paste slip, and it strands the reader on the page they are already on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void SwitcherListsEveryLanguageAndMarksItsOwn(string lang) => CheckSwitcher(lang);

    [Fact]
    public void EnglishSwitcherListsEveryLanguageAndMarksItsOwn() => CheckSwitcher("en");

    private static void CheckSwitcher(string lang)
    {
        var md = Load(lang);
        var navs = NavRx.Matches(md);

        Assert.True(navs.Count == 1,
            $"{PathFor(lang).Replace('\\', '/')} has {navs.Count} <!--i18n--> blocks; it must have " +
            "exactly one. The renderer in worker/src/render.js replaces the first one it finds with " +
            "the generated switcher and leaves any others as a duplicate row of links.");

        var nav = navs[0].Groups["body"].Value;

        // Grouped rather than ToDictionary'd: a language listed twice — the obvious slip when adding a
        // ninth — would make ToDictionary throw about duplicate keys, replacing every careful message
        // below with a stack trace about dictionaries.
        var byText = LinkRx.Matches(nav)
            .GroupBy(m => m.Groups["text"].Value)
            .ToArray();

        var duplicated = byText.Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.True(duplicated.Length == 0,
            $"{PathFor(lang).Replace('\\', '/')} lists these languages more than once in its " +
            "switcher: " + string.Join(", ", duplicated) + ". Each language appears exactly once.");

        var links = byText.ToDictionary(g => g.Key, g => g.First().Groups["href"].Value);

        foreach (var other in Locales.Where(l => l != lang))
        {
            var name = Native[other];
            Assert.True(links.ContainsKey(name),
                $"{PathFor(lang).Replace('\\', '/')} does not link to {name} ({other}) in its switcher. " +
                "Every language links to all seven others, so a reader can leave from any page.");

            // Relative, and correct for BOTH renderings: GitHub resolves it against the file, and the
            // mirror resolves it the same way before mapping it onto /<lang>/README.md.
            var want = lang == "en"
                ? (other == "en" ? "README.md" : $"docs/README.{other}.md")
                : (other == "en" ? "../README.md" : $"README.{other}.md");

            Assert.True(links[name] == want,
                $"{PathFor(lang).Replace('\\', '/')} links {name} to \"{links[name]}\", expected " +
                $"\"{want}\". The link has to work in a checkout and on GitHub; the mirror resolves " +
                "the same relative path against this document's own location.");
        }

        Assert.False(links.ContainsKey(Native[lang]),
            $"{PathFor(lang).Replace('\\', '/')} links to its own language ({Native[lang]}) in the " +
            "switcher. The current language is plain bold text, not a link to the page you are on.");

        Assert.Contains($"**{Native[lang]}**", nav);
    }

    /// <summary>
    /// Tokens that must survive translation verbatim: the command, the repo URL, the pack extensions,
    /// the hex literal, and the two documents every README links out to. These are the parts a reader
    /// has to TYPE or CLICK, and a translated one is worse than useless — it looks authoritative and
    /// does not work.
    /// </summary>
    public static TheoryData<string, string> RequiredTokens
    {
        get
        {
            string[] tokens =
            [
                "/proteus",                       // the command that opens the window
                "/xlplugins",
                "https://dl.solona.info/repo.json",
                "https://discord.gg/solona",
                "`.pmp`", "`.omp`", "`.ttmp2`",
                "`#FFFFFF`", "`metadata.json`",
                "bibo", "gen3",
                "Penumbra", "Glamourer", "Proteus", "Skindent",
                "../TROUBLESHOOTING.md",          // resolved by the mirror; must stay relative
                "../For%20Creators.md",
            ];

            var d = new TheoryData<string, string>();
            foreach (var l in Locales.Where(l => l != "en"))
                foreach (var t in tokens)
                    d.Add(l, t);
            return d;
        }
    }

    [Theory]
    [MemberData(nameof(RequiredTokens))]
    public void KeepsUntranslatableTokens(string lang, string token)
        => Assert.True(Load(lang).Contains(token, StringComparison.Ordinal),
            $"docs/README.{lang}.md no longer contains \"{token}\". Commands, URLs, file extensions " +
            "and paths are typed or clicked verbatim — translating one produces a page that reads " +
            "correctly and does not work.");

    /// <summary>
    /// Nothing in docs/ pretending to be a translation of a language Proteus does not ship. The
    /// switcher is generated from LANGS, so such a file would never be linked and would rot unread.
    /// </summary>
    [Fact]
    public void NoTranslationsForUnshippedLanguages()
    {
        var dir = Path.Combine(RepoRoot, "docs");
        if (!Directory.Exists(dir)) return;

        var stray = Directory.GetFiles(dir, "README.*.md")
            .Select(Path.GetFileName)
            .Where(f => !Locales.Any(l => l != "en" && f == $"README.{l}.md"))
            .ToArray();

        Assert.True(stray.Length == 0,
            "docs/ contains README translations for languages Proteus does not ship: " +
            string.Join(", ", stray) + ". Add the code to LocSetup.Shipped, to Locales here and to " +
            "LANGS in worker/src/render.js, or remove the file — the switcher never links it as it is.");
    }
}
