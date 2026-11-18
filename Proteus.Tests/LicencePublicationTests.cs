using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Proteus Licence 2.0 (plan proteus-licence-2; first published as 1.0 under plan D28/D29): the AGPL-3.0 terms,
/// renamed, under an overriding Part 1 whose section B (1.0's section 4, word for word) travels with every copy, fork
/// and project. The package carries it, Proteus's own code says so, XivLiveMesh follows it, and the notices list
/// everything third-party the package ships. These tests read repository files and the test output folder only.
/// </summary>
public class LicencePublicationTests
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
        Assert.True(Root.Value != null, "could not find proteus.slnx");
        return Path.Combine(Root.Value!, relative);
    }

    private static string Read(string relative) => File.ReadAllText(Repo(relative)).Replace("\r\n", "\n");

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-11")]
    [Trait("Plan", "AC-proteus-licence-2-7")]
    public void Package_carries_the_licence()   // [AC-contrib-clean-room-11] [AC-proteus-licence-2-7]
    {
        var csproj = Read("Proteus/Proteus.csproj");
        Assert.Contains("<Content Include=\"..\\LICENSE\" Link=\"LICENSE.txt\">", csproj);
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-12")]
    [Trait("Plan", "AC-proteus-licence-2-8")]
    [Trait("Plan", "EDGE-proteus-licence-2-13")]
    public void Spdx_headers_follow_the_licence()   // [AC-contrib-clean-room-12] [AC-proteus-licence-2-8] [EDGE-proteus-licence-2-13]
    {
        var bad = new List<string>();
        foreach (var dir in new[] { "Proteus", "XivLiveMesh", "Proteus.Refit", "Proteus.Tests" })
            foreach (var file in Directory.EnumerateFiles(Repo(dir), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(Root.Value!, file).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
                var first = File.ReadLines(file).FirstOrDefault() ?? "";
                if (!first.Contains("SPDX-License-Identifier", StringComparison.Ordinal)) continue;
                string want = rel == "Proteus/Interop/Ipc/GlamourerApiTypes.cs" ? "Apache-2.0" : "LicenseRef-Proteus";
                if (first.TrimEnd() != "// SPDX-License-Identifier: " + want) bad.Add(rel);
            }
        Assert.True(bad.Count == 0, "wrong SPDX line: " + string.Join(", ", bad));
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-12")]
    [Trait("Plan", "EDGE-contrib-clean-room-48")]
    [Trait("Plan", "AC-proteus-licence-2-7")]
    [Trait("Plan", "EDGE-proteus-licence-2-12")]
    public void XivLiveMesh_licence_follows_the_usage_check()   // [AC-contrib-clean-room-12] [EDGE-contrib-clean-room-48] [AC-proteus-licence-2-7] [EDGE-proteus-licence-2-12]
    {
        // C1 found no other user of XivLiveMesh, so it moves to the Proteus Licence outright (no dual licence).
        Assert.Equal(File.ReadAllBytes(Repo("LICENSE")), File.ReadAllBytes(Repo("XivLiveMesh/LICENSE")));
        Assert.False(File.Exists(Repo("XivLiveMesh/LICENSE-PROTEUS.md")));
        Assert.Contains("Proteus Licence", Read("XivLiveMesh/NOTICE.md"));
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-12")]
    [Trait("Plan", "AC-proteus-licence-2-8")]
    [Trait("Plan", "EDGE-proteus-licence-2-19")]
    public void Projects_carry_no_AGPL_licence_expression()   // [AC-contrib-clean-room-12] [AC-proteus-licence-2-8] [EDGE-proteus-licence-2-19]
    {
        foreach (var p in new[] { "Proteus/Proteus.csproj", "XivLiveMesh/XivLiveMesh.csproj" })
        {
            var text = Read(p);
            Assert.DoesNotContain("PackageLicenseExpression", text);
            Assert.DoesNotContain("AGPL", text);
            // 1.0's section numbers mean nothing under 2.0.
            Assert.DoesNotContain("§2.5", text);
        }
    }

    // Files that may mention the AGPL: the historical records, the third-party listings, the licence itself (built on
    // the AGPL terms; earlier versions stay AGPL), the AGPL text kept as a fixture, and tests that assert its absence.
    private static readonly string[] MayMentionAgpl =
    [
        "LICENSE", "XivLiveMesh/LICENSE", "XivLiveMesh/NOTICE.md", "THIRD_PARTY_NOTICES.txt",
        "Proteus.Tests/Fixtures/gnu-agpl-3.0.txt",
        "docs/pbd-clean-room.md", "docs/pbd-clean-room-plan.md", "docs/pbd-clean-room-log.md",
        "docs/contrib-clean-room-plan.md", "docs/contrib-clean-room-log.md", "docs/contrib-clean-room-spec.md",
        "docs/ipc-contract.md",
        "Proteus.Tests/ReadmeTermsTests.cs", "Proteus.Tests/ShippedAssembliesTests.cs",
        "Proteus.Tests/LicencePublicationTests.cs",
    ];

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-12")]
    [Trait("Plan", "EDGE-contrib-clean-room-50")]
    [Trait("Plan", "AC-proteus-licence-2-8")]
    [Trait("Plan", "EDGE-proteus-licence-2-14")]
    [Trait("Plan", "EDGE-proteus-licence-2-16")]
    public void No_file_claims_AGPL_for_Proteus_code()   // [AC-contrib-clean-room-12] [EDGE-contrib-clean-room-50] [AC-proteus-licence-2-8] [EDGE-proteus-licence-2-14] [EDGE-proteus-licence-2-16]
    {
        // README.md and every docs/README.*.md translation are scanned too: none is on MayMentionAgpl.
        var skipDirs = SkipDirs;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".csproj", ".md", ".txt", ".json", ".yml", ".props", ".js", ".ts", ".toml" };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Root.Value!, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Root.Value!, file).Replace('\\', '/');
            if (skipDirs.Any(d => ("/" + rel).Contains(d, StringComparison.Ordinal))) continue;
            // Only this repository's own files: never another checkout nested inside it (a worktree or submodule
            // has its own .git), such as the agents' worktrees under .claude/worktrees in the owner's checkout.
            if (InNestedCheckout(file)) continue;
            if (!exts.Contains(Path.GetExtension(file)) && Path.GetFileName(file) != "LICENSE") continue;
            if (MayMentionAgpl.Contains(rel)) continue;
            var text = File.ReadAllText(file);
            if (Regex.IsMatch(text, "AGPL|Affero", RegexOptions.IgnoreCase)) offenders.Add(rel);
        }
        Assert.True(offenders.Count == 0, "claims the AGPL: " + string.Join(", ", offenders));
    }

    // Folders a repository-wide scan never reads: git data, other checkouts, build output and the submodules.
    private static readonly string[] SkipDirs =
        ["/.git/", "/.claude/worktrees/", "/bin/", "/obj/", "/node_modules/", "/Glamourer/", "/Luna/", "/Penumbra.Api/"];

    /// <summary>Whether a file sits under a folder (below the repository root) that holds its own .git.</summary>
    private static bool InNestedCheckout(string file)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(file)!);
        var root = Path.GetFullPath(Root.Value!).TrimEnd(Path.DirectorySeparatorChar);
        while (dir != null && dir.FullName.TrimEnd(Path.DirectorySeparatorChar).Length > root.Length)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (File.Exists(git) || Directory.Exists(git)) return true;
            dir = dir.Parent;
        }
        return false;
    }

    private static List<string[]> Inventory() =>
        Read("Proteus.Tests/Fixtures/package-inventory.tsv").Split('\n')
            .Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t')).ToList();

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-13")]
    [Trait("Plan", "EDGE-contrib-clean-room-52")]
    [Trait("Plan", "AC-proteus-licence-2-7")]
    [Trait("Plan", "AC-featureless-bulge-10")]
    public void Package_inventory_classifies_every_entry()   // [AC-contrib-clean-room-13] [EDGE-contrib-clean-room-52] [AC-proteus-licence-2-7]
    {
        var notices = Read("THIRD_PARTY_NOTICES.txt");
        var rows = Inventory();
        Assert.NotEmpty(rows);
        foreach (var r in rows)
        {
            Assert.True(r.Length == 3, "an inventory row has not three columns: " + string.Join("|", r));
            Assert.Contains(r[1], new[] { "own", "package", "native", "carved-out" });
            if (r[1] != "own")
                Assert.True(Regex.IsMatch(notices, "^" + Regex.Escape(r[2]) + "\\b", RegexOptions.Multiline),
                            $"{r[0]}: no notice block headed {r[2]}");
        }
        Assert.Contains(rows, r => r[0] == "LICENSE.txt" && r[1] == "own");
        Assert.All(rows.Where(r => r[0].StartsWith("Meshes\\", StringComparison.Ordinal)), r => Assert.Equal("own", r[1]));
        Assert.Contains(rows, r => r[0].EndsWith("Resources/sphere_d_array.png", StringComparison.Ordinal) && r[1] == "carved-out");
        Assert.Contains(rows, r => r[0].EndsWith("Resources/tile_norm_array.png", StringComparison.Ordinal) && r[1] == "carved-out");
        // [AC-featureless-bulge-10] the shipped bulge is carved out under its own block, never under the Proteus Licence.
        Assert.Contains(rows, r => r[0] == "(embedded) Resources/lava_bulge.mdl" && r[1] == "carved-out" && r[2] == "lava_bulge");
        Assert.Matches(new Regex("^lava_bulge — featureless bulge mesh \\(carved out; not covered by the Proteus Licence\\)", RegexOptions.Multiline), notices);
        Assert.Contains(rows, r => r[0] == "(embedded) Resources/tbse_bulge.json" && r[1] == "carved-out" && r[2] == "tbse_bulge");
        Assert.Matches(new Regex("^tbse_bulge — featureless bulge for The Body SE \\(carved out; not covered by the Proteus Licence\\)", RegexOptions.Multiline), notices);
        Assert.DoesNotContain(rows, r => r[0] is "Luna.dll" or "Penumbra.Api.dll" or "Glamourer.Api.dll");
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-13")]
    public void Every_shipped_package_has_a_notice()   // [AC-contrib-clean-room-13]
    {
        var buildOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DalamudPackager", "DotNet.ReproducibleBuilds" };
        var lockFile = Read("Proteus/packages.lock.json");
        var packages = Regex.Matches(lockFile, "\"([^\"]+)\": \\{\\s*\"type\": \"(Direct|Transitive)\"")
                            .Select(m => m.Groups[1].Value).Where(p => !buildOnly.Contains(p)).ToList();
        Assert.NotEmpty(packages);
        var notices = Read("THIRD_PARTY_NOTICES.txt");
        foreach (var p in packages)
            Assert.True(notices.Contains(p.Replace(".Net", ".NET"), StringComparison.OrdinalIgnoreCase),
                        $"{p} ships but has no notice block");
        var inventory = Inventory();
        foreach (var p in packages)
            Assert.Contains(inventory, r => r[1] == "package" && string.Equals(r[2].Replace(".NET", ".Net"), p.Replace(".NET", ".Net"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-13")]
    [Trait("Plan", "EDGE-proteus-licence-2-17")]
    public void Existing_notices_are_kept_and_the_new_ones_added()   // [AC-contrib-clean-room-13] [EDGE-proteus-licence-2-17]
    {
        var notices = Read("THIRD_PARTY_NOTICES.txt");
        foreach (var block in new[]
                 {
                     "FFXIVLooseTextureCompiler", "BCnEncoder.NET", "bc7enc", "BitMiracle.LibTiff.NET",
                     "CommunityToolkit.HighPerformance", "StbImageSharp", "StbImageWriteSharp", "Glamourer IPC types",
                     "Apache License", "Interoperability", "Game assets", "SQUARE ENIX",
                 })
            Assert.Contains(block, notices);
    }

    // ---- The Proteus Licence 2.0 text (plan proteus-licence-2) ----

    private const string AgplFixture = "Proteus.Tests/Fixtures/gnu-agpl-3.0.txt";

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string Sha(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    private static string Licence() => Read("LICENSE");

    /// <summary>The text from the first line equal (trimmed) to <paramref name="from"/> up to, not including, the
    /// first later line that starts (trimmed) with <paramref name="to"/>.</summary>
    private static string Block(string text, string from, string to)
    {
        var lines = text.Split('\n');
        int start = Array.FindIndex(lines, l => l.Trim() == from);
        Assert.True(start >= 0, $"no line '{from}'");
        int end = Array.FindIndex(lines, start + 1, l => l.Trim().StartsWith(to, StringComparison.Ordinal));
        Assert.True(end > start, $"no line starting '{to}' after '{from}'");
        return string.Join("\n", lines[start..end]);
    }

    private static string About() => Block(Licence(), "About this licence", "PART 1 — PROTEUS TERMS");

    private static readonly string[] Part1Headings =
    [
        "A. Words used here.", "B. What you may never use it for", "C. Section B travels with the code.",
        "D. Breaking section B.", "E. Other people's code.", "F. Contributions.",
    ];

    /// <summary>One lettered Part 1 section, from its heading to the next (section F ends at Part 2's heading).</summary>
    private static string Part1(char letter)
    {
        int i = letter - 'A';
        string next = i + 1 < Part1Headings.Length ? Part1Headings[i + 1] : "PART 2 — GENERAL TERMS";
        return Block(Licence(), Part1Headings[i], next);
    }

    /// <summary>Part 2's numbered sections (0–17) between the TERMS AND CONDITIONS markers, as collapsed paragraphs.
    /// Headings are lines indented exactly two spaces, so the AGPL's indented "    7.  This requirement" never counts.</summary>
    private static Dictionary<int, List<string>> Terms(string text)
    {
        var lines = text.Split('\n');
        int start = Array.FindIndex(lines, l => l.Trim() == "TERMS AND CONDITIONS");
        int end = Array.FindIndex(lines, l => l.Trim() == "END OF TERMS AND CONDITIONS");
        Assert.True(start >= 0 && end > start, "no TERMS AND CONDITIONS markers");
        var heading = new Regex(@"^  (\d{1,2})\. [A-Z]");
        var sections = new Dictionary<int, List<string>>();
        var order = new List<int>();
        int current = -1;
        var buffer = new List<string>();
        void Flush()
        {
            if (current < 0) return;
            sections[current] = Regex.Split(string.Join("\n", buffer), @"\n[ \t]*\n")
                                     .Select(Collapse).Where(p => p.Length > 0).ToList();
        }
        for (int i = start + 1; i < end; i++)
        {
            var m = heading.Match(lines[i]);
            if (m.Success)
            {
                Flush();
                current = int.Parse(m.Groups[1].Value);
                order.Add(current);
                buffer = [];
            }
            buffer.Add(lines[i]);
        }
        Flush();
        Assert.Equal(Enumerable.Range(0, 18), order);
        return sections;
    }

    [Fact]
    [Trait("Plan", "EDGE-proteus-licence-2-5")]
    public void Agpl_fixture_is_mains_licence()   // [EDGE-proteus-licence-2-5]
    {
        // The oracle for "word for word" is the GNU AGPL-3.0 exactly as main:LICENSE carried it.
        Assert.Equal("8486A10C4393CEE1C25392769DDD3B2D6C242D6EC7928E1414EFFF7DFB2F07EF", Sha(Read(AgplFixture)));
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-1")]
    [Trait("Plan", "EDGE-proteus-licence-2-6")]
    public void Licence_is_renamed_without_the_preamble()   // [AC-proteus-licence-2-1] [EDGE-proteus-licence-2-6]
    {
        var licence = Licence();
        Assert.DoesNotContain("GNU AFFERO GENERAL PUBLIC LICENSE", licence);
        Assert.DoesNotContain("Preamble", licence);
        Assert.DoesNotContain("changing it is not allowed", licence);
        var about = Collapse(About());
        Assert.Contains("built on the terms of the GNU Affero General Public License, version 3", about);
        Assert.Contains("is not the GNU Affero General Public License", about);
        Assert.Contains("has not published or endorsed it", about);
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-2")]
    [Trait("Plan", "EDGE-proteus-licence-2-3")]
    [Trait("Plan", "EDGE-proteus-licence-2-4")]
    public void Part2_keeps_the_AGPL_sections_word_for_word()   // [AC-proteus-licence-2-2] [EDGE-proteus-licence-2-3] [EDGE-proteus-licence-2-4]
    {
        var agpl = Terms(Read(AgplFixture));
        var ours = Terms(Licence());
        foreach (var s in new[] { 1, 3, 4, 5, 6, 11, 12, 15, 16, 17 })
            Assert.True(agpl[s].SequenceEqual(ours[s]), $"Part 2, section {s} is not the AGPL's word for word.");
    }

    // The only changes Part 2 makes to the AGPL terms (plan proteus-licence-2, the changes table).
    private abstract record Change(int Section);
    private sealed record Swap(int Section, string Old, string New) : Change(Section);
    private sealed record AddAfter(int Section, string AnchorEnd, string Paragraph) : Change(Section);
    private sealed record Drop(int Section, string StartsWith) : Change(Section);
    private sealed record ReplaceRun(int Section, string FirstStartsWith, int Count, string Paragraph) : Change(Section);

    private static readonly Change[] Changes =
    [
        new Swap(0, "\"This License\" refers to version 3 of the GNU Affero General Public License.",
                 "\"This License\" refers to the Proteus Licence, version 2.0 (Part 1 and Part 2 together)."),
        new Swap(2, "This License explicitly affirms your unlimited permission to run the unmodified Program.",
                 "This License explicitly affirms your permission to run the unmodified Program, subject to Part 1."),
        new Swap(2, "without conditions so long as your license otherwise remains in force.",
                 "without conditions other than those in Part 1, so long as your license otherwise remains in force."),
        new AddAfter(7, "the above requirements apply either way.",
                     "The terms in Part 1 are part of this License itself, not additional terms or further restrictions. " +
                     "Nothing in this section lets anyone remove them from a covered work or any part of it."),
        new AddAfter(8, "you do not qualify to receive new licenses for the same material under section 10.",
                     "For a breach of Part 1, section B, Part 1, section D applies instead of the second and third " +
                     "paragraphs of this section."),
        new Swap(9, "You are not required to accept this License in order to receive or run a copy of the Program.",
                 "You are not required to accept this License in order to receive a copy of the Program, but you may " +
                 "run or otherwise use it only on the conditions in Part 1."),
        new Swap(9, "Therefore, by modifying or propagating a covered work, you indicate your acceptance of this License to do so.",
                 "Therefore, by running, using, modifying or propagating a covered work, you indicate your acceptance " +
                 "of this License to do so."),
        new Swap(10, "sale, or importing the Program or any portion of it.",
                 "sale, or importing the Program or any portion of it. The terms in Part 1 are not further restrictions: " +
                 "every recipient receives the license subject to them, and must keep to them."),
        new Swap(13, "13. Remote Network Interaction; Use with the GNU General Public License.", "13. Remote Network Interaction."),
        new Swap(13, " This Corresponding Source shall include the Corresponding Source for any work covered by version 3 " +
                     "of the GNU General Public License that is incorporated pursuant to the following paragraph.", ""),
        new Drop(13, "Notwithstanding any other provision of this License, you have permission to link or combine"),
        new ReplaceRun(14, "The Free Software Foundation may publish revised and/or new versions", 3,
                       "Solona may publish revised and/or new versions of the Proteus Licence from time to time. Each " +
                       "version is given a distinguishing version number. Each release of the Program is covered by the " +
                       "version it comes with, unless Solona says otherwise for a later release."),
    ];

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-2")]
    [Trait("Plan", "EDGE-proteus-licence-2-3")]
    [Trait("Plan", "EDGE-proteus-licence-2-23")]
    public void Part2_changes_only_the_listed_sentences()   // [AC-proteus-licence-2-2] [EDGE-proteus-licence-2-3] [EDGE-proteus-licence-2-23]
    {
        var agpl = Terms(Read(AgplFixture));
        var ours = Terms(Licence());
        foreach (var group in Changes.GroupBy(c => c.Section))
        {
            var expected = new List<string>(agpl[group.Key]);
            foreach (var change in group)
            {
                switch (change)
                {
                    case Swap(_, var old, var @new):
                    {
                        int hit = expected.FindIndex(p => p.Contains(Collapse(old), StringComparison.Ordinal) ||
                                                          (old.StartsWith(' ') && p.Contains(old, StringComparison.Ordinal)));
                        Assert.True(hit >= 0, $"section {group.Key}: '{old}' is not in the AGPL text");
                        expected[hit] = Collapse(expected[hit].Replace(old.StartsWith(' ') ? old : Collapse(old), @new));
                        break;
                    }
                    case AddAfter(_, var anchor, var paragraph):
                    {
                        int hit = expected.FindIndex(p => p.EndsWith(Collapse(anchor), StringComparison.Ordinal));
                        Assert.True(hit >= 0, $"section {group.Key}: no paragraph ends '{anchor}'");
                        expected.Insert(hit + 1, Collapse(paragraph));
                        break;
                    }
                    case Drop(_, var startsWith):
                        Assert.Equal(1, expected.RemoveAll(p => p.StartsWith(startsWith, StringComparison.Ordinal)));
                        break;
                    case ReplaceRun(_, var first, var count, var paragraph):
                    {
                        int hit = expected.FindIndex(p => p.StartsWith(first, StringComparison.Ordinal));
                        Assert.True(hit >= 0, $"section {group.Key}: no paragraph starts '{first}'");
                        expected.RemoveRange(hit, count);
                        expected.Insert(hit, Collapse(paragraph));
                        break;
                    }
                }
            }
            Assert.Equal(expected, ours[group.Key]);
        }
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-2")]
    [Trait("Plan", "EDGE-proteus-licence-2-6")]
    [Trait("Plan", "EDGE-proteus-licence-2-24")]
    public void Closing_section_is_guidance_and_never_names_GNU()   // [AC-proteus-licence-2-2] [EDGE-proteus-licence-2-6] [EDGE-proteus-licence-2-24]
    {
        var licence = Licence();
        int endOfTerms = licence.IndexOf("END OF TERMS AND CONDITIONS", StringComparison.Ordinal);
        Assert.True(endOfTerms > 0);
        var closing = licence[endOfTerms..];
        Assert.Contains("How to Keep This Licence With Your Copy", closing);
        Assert.Contains("guidance only; not part of the terms above", Collapse(closing));
        Assert.DoesNotContain("How to Apply These Terms to Your New Programs", licence);
        var section14 = string.Join(" ", Terms(licence)[14]);
        foreach (var part in new[] { closing, section14 })
        {
            Assert.DoesNotContain("GNU", part);
            Assert.DoesNotContain("Free Software Foundation", part);
        }
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-3")]
    [Trait("Plan", "EDGE-proteus-licence-2-2")]
    [Trait("Plan", "EDGE-proteus-licence-2-18")]
    public void Section_B_is_section_4_of_licence_1_0()   // [AC-proteus-licence-2-3] [EDGE-proteus-licence-2-2] [EDGE-proteus-licence-2-18]
    {
        // The hash of 1.0's section 4 (61c8c7b6:LICENSE, "What you may never use it for" .. "what breaks this
        // section.", whitespace collapsed), fixed in the plan from that commit rather than from this file.
        var b = Collapse(Part1('B'));
        int from = b.IndexOf("What you may never use it for", StringComparison.Ordinal);
        const string last = "what breaks this section.";
        int to = b.IndexOf(last, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "section B lost its first or last words");
        Assert.Equal("6BA7B5F89C4399201B7C6DE53FDB548C92286E7054A5955B5453ECA21506D0A8", Sha(b[from..(to + last.Length)]));
        Assert.Equal(b.Length, to + last.Length);   // nothing after the clause inside section B

        var licence = Licence();
        Assert.DoesNotMatch(new Regex("^## ", RegexOptions.Multiline), licence);
        Assert.DoesNotContain("](", licence);
        Assert.DoesNotContain("**", licence);
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-3")]
    public void Part1_overrides_Part2()   // [AC-proteus-licence-2-3]
    {
        var licence = Licence();
        Assert.True(licence.IndexOf("PART 1 — PROTEUS TERMS", StringComparison.Ordinal) <
                    licence.IndexOf("PART 2 — GENERAL TERMS", StringComparison.Ordinal), "Part 1 must come before Part 2.");
        var about = Collapse(About());
        Assert.Contains("Where Part 1 and Part 2 differ, Part 1 applies.", about);
        Assert.Contains("except where Part 1 overrides them", about);
        var a = Collapse(Part1('A'));
        // 1.0's section 1 definitions, word for word without the Markdown.
        Assert.Contains("\"The software\" means Proteus, in source or built form, or any part of it.", a);
        Assert.Contains("\"Solona\" means the author of Proteus, reachable on Discord at https://discord.gg/solona " +
                        "or through the Proteus repository on GitHub.", a);
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-4")]
    [Trait("Plan", "EDGE-proteus-licence-2-20")]
    public void Fork_is_code_use_not_interaction()   // [AC-proteus-licence-2-4] [EDGE-proteus-licence-2-20]
    {
        var a = Collapse(Part1('A'));
        foreach (var phrase in new[]
                 {
                     "contains any part of the software's code", "built to run with the software as one program",
                     "\"A project\", in section C, means the same as a fork.",
                     "only loads the software as a plugin, such as Dalamud", "an aggregate, as Part 2, section 5 defines it",
                     "a mere hyperlink to the software",
                     "only interacts with a running copy of the software through inter-plugin calls, files or a network",
                 })
            Assert.Contains(phrase, a);
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-4")]
    [Trait("Plan", "EDGE-proteus-licence-2-7")]
    [Trait("Plan", "EDGE-proteus-licence-2-24")]
    public void Section_B_travels_and_cannot_be_stripped()   // [AC-proteus-licence-2-4] [EDGE-proteus-licence-2-7] [EDGE-proteus-licence-2-24]
    {
        var c = Collapse(Part1('C'));
        foreach (var phrase in new[]
                 {
                     "to every fork and to every project", "as a whole",
                     "include the full text of this licence with it, unchanged", "license it under this licence",
                     "say, where its users will see it",
                     "for all of it except third-party code that stays under its own licence (as section E describes for Proteus)",
                     "other than the parts section E describes",
                     "not \"further restrictions\" under Part 2, sections 7 and 10", "no one may remove them",
                 })
            Assert.Contains(phrase, c);
        Assert.DoesNotContain("covered by this licence", c);
        var terms = Terms(Licence());
        Assert.Contains("Nothing in this section lets anyone remove them from a covered work or any part of it.", terms[7].Last());
        Assert.Contains("The terms in Part 1 are not further restrictions", string.Join(" ", terms[10]));
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-5")]
    [Trait("Plan", "EDGE-proteus-licence-2-8")]
    [Trait("Plan", "EDGE-proteus-licence-2-22")]
    public void Breaking_section_B_ends_rights_at_once()   // [AC-proteus-licence-2-5] [EDGE-proteus-licence-2-8] [EDGE-proteus-licence-2-22]
    {
        var d = Collapse(Part1('D'));
        Assert.Contains("end straight away, without notice", d);
        Assert.Contains("do not apply to a breach of section B", d);
        Assert.Contains("do not qualify to receive new licences for the software under Part 2, section 10, for this version or any later one", d);
        Assert.StartsWith("For a breach of Part 1, section B, Part 1, section D applies instead", Terms(Licence())[8].Last());
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-6")]
    [Trait("Plan", "EDGE-proteus-licence-2-9")]
    public void Other_peoples_code_describes_this_tree()   // [AC-proteus-licence-2-6] [EDGE-proteus-licence-2-9]
    {
        var e = Collapse(Part1('E'));
        foreach (var phrase in new[]
                 {
                     "THIRD_PARTY_NOTICES.txt", "NOTICE.md",
                     // The Glamourer Apache exception, tied to its file.
                     "Proteus/Interop/Ipc/GlamourerApiTypes.cs, which stay under the Apache License, version 2.0",
                     "Glamourer (Apache License, version 2.0",
                     "Luna (GNU Affero General Public License, version 3",
                     "Penumbra.Api (which publishes no licence file, and stays under its author's terms)",
                     "never shipped with the plugin", "only the interface facts", "SQUARE ENIX",
                     "this licence does not apply to them",
                 })
            Assert.Contains(phrase, e);
        foreach (var wrong in new[] { "where the two differ", "Penumbra.Api (MIT", "Glamourer.Api (MIT", "contributors added" })
            Assert.DoesNotContain(wrong, e);
        // The facts section E relies on: the plugin references no submodule, and the notices agree on Penumbra.Api.
        var csproj = Read("Proteus/Proteus.csproj");
        foreach (var sub in new[] { "Luna", "Penumbra.Api", "Glamourer.Api" })
            Assert.DoesNotContain($"{sub}.csproj", csproj);
        Assert.Contains("Penumbra.Api has no licence file", Read("THIRD_PARTY_NOTICES.txt"));
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-6")]
    [Trait("Plan", "EDGE-proteus-licence-2-10")]
    public void Earlier_copies_keep_the_AGPL()   // [AC-proteus-licence-2-6] [EDGE-proteus-licence-2-10]
    {
        var about = Collapse(About());
        foreach (var phrase in new[]
                 {
                     "came with the GNU Affero General Public License", "obtained later",
                     "Section B does not bind a copy that came with the GNU Affero General Public License.",
                     "is under this licence in full, apart from the parts Part 1, section E describes.",
                     "Anyone holding a copy that came with version 1.0 of the Proteus Licence may use it under this version instead.",
                 })
            Assert.Contains(phrase, about);
        Assert.DoesNotContain("covers only", about);
        Assert.DoesNotContain("before 18 November 2026", about);
        Assert.Contains("Copies received earlier under the GNU AGPL", Read("XivLiveMesh/NOTICE.md"));
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-10")]
    [Trait("Plan", "EDGE-proteus-licence-2-21")]
    public void Content_made_with_it_is_not_a_fork()   // [AC-proteus-licence-2-10] [EDGE-proteus-licence-2-21]
    {
        var a = Collapse(Part1('A'));
        foreach (var phrase in new[]
                 {
                     "Content made with the software is not a fork", "including any of the software's own meshes or assets",
                     "not a covered work under Part 2", "notwithstanding Part 2, section 2",
                     "may be shared under whatever terms its maker chooses", "as part of that content",
                     "This does not license them apart from that content", "Section B still applies in full",
                 })
            Assert.Contains(phrase, a);
        Assert.DoesNotContain("still governs", a);
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-11")]
    public void Contributions_clause_is_1_0_section_6()   // [AC-proteus-licence-2-11]
    {
        // The hash of 1.0's section 6, first paragraph (61c8c7b6:LICENSE, collapsed), fixed in the plan.
        var f = Collapse(Part1('F'));
        int from = f.IndexOf("If you send a change", StringComparison.Ordinal);
        const string last = "You keep the copyright in your contribution.";
        int to = f.IndexOf(last, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, "section F lost 1.0's contribution grant");
        Assert.Equal("74FA310880A49A88718547E1ACAC3F988B023D48FB3A1A03DD4BC64F7CB6DF1B", Sha(f[from..(to + last.Length)]));
        Assert.Contains("This section covers contributions sent on or after 18 November 2026.", f);
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-7")]
    [Trait("Plan", "EDGE-proteus-licence-2-11")]
    public void Build_output_carries_LICENSE_txt()   // [AC-proteus-licence-2-7] [EDGE-proteus-licence-2-11]
    {
        // The Content item flows from the referenced Proteus project into this output, as THIRD_PARTY_NOTICES.txt does.
        var shipped = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        Assert.True(File.Exists(shipped), "LICENSE.txt is not in the build output");
        Assert.Equal(Licence(), File.ReadAllText(shipped).Replace("\r\n", "\n"));
    }

    [Fact]
    [Trait("Plan", "AC-proteus-licence-2-9")]
    [Trait("Plan", "EDGE-proteus-licence-2-15")]
    public void Licence_1_0_is_only_in_history()   // [AC-proteus-licence-2-9] [EDGE-proteus-licence-2-15]
    {
        // 1.0's version line, only in the licence files: the clean-room records quote it and stay as written.
        foreach (var f in new[] { "LICENSE", "XivLiveMesh/LICENSE" })
            Assert.DoesNotContain("*Version 1.0 —", Read(f));
        // 1.0's fork ban, nowhere in the repository's own files (this file holds the phrase as a literal).
        const string forkBan = "Releasing a fork";
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Root.Value!, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Root.Value!, file).Replace('\\', '/');
            if (SkipDirs.Any(d => ("/" + rel).Contains(d, StringComparison.Ordinal))) continue;
            if (InNestedCheckout(file)) continue;
            if (rel == "Proteus.Tests/LicencePublicationTests.cs") continue;
            var ext = Path.GetExtension(file);
            if (!(ext is ".md" or ".txt" or ".cs") && Path.GetFileName(file) != "LICENSE") continue;
            if (File.ReadAllText(file).Contains(forkBan, StringComparison.Ordinal)) offenders.Add(rel);
        }
        Assert.True(offenders.Count == 0, "1.0's fork ban is still in: " + string.Join(", ", offenders));
        Assert.False(File.Exists(Repo("docs/LICENSE-DRAFT.md")), "docs/LICENSE-DRAFT.md is back.");
        Assert.False(Directory.GetFiles(Root.Value!, "LICENSE-*").Length > 0, "a root LICENSE-* file would read as a second licence");
    }

    [Fact]
    [Trait("Plan", "EDGE-proteus-licence-2-17")]
    public void Notices_still_fit_licence_2()   // [EDGE-proteus-licence-2-17]
    {
        var notices = Read("THIRD_PARTY_NOTICES.txt");
        Assert.Contains("Proteus itself is under the Proteus Licence (LICENSE).", notices);
        Assert.DoesNotContain("Proteus Licence 1", notices);
        Assert.DoesNotContain("Version 1.0", notices);
    }
}
