using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Proteus Licence as published (plan D28/D29): the package carries it, Proteus's own code says so, XivLiveMesh
/// follows it (the C1 usage check found no other user), and the notices list everything third-party the package ships.
/// These tests read repository files only.
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
    public void Package_carries_the_licence()   // [AC-contrib-clean-room-11]
    {
        var csproj = Read("Proteus/Proteus.csproj");
        Assert.Contains("<Content Include=\"..\\LICENSE\" Link=\"LICENSE.txt\">", csproj);
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-12")]
    public void Spdx_headers_follow_the_licence()   // [AC-contrib-clean-room-12]
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
    public void XivLiveMesh_licence_follows_the_usage_check()   // [AC-contrib-clean-room-12] [EDGE-contrib-clean-room-48]
    {
        // C1 found no other user of XivLiveMesh, so it moves to the Proteus Licence outright (no dual licence).
        Assert.Equal(File.ReadAllBytes(Repo("LICENSE")), File.ReadAllBytes(Repo("XivLiveMesh/LICENSE")));
        Assert.False(File.Exists(Repo("XivLiveMesh/LICENSE-PROTEUS.md")));
        Assert.Contains("Proteus Licence", Read("XivLiveMesh/NOTICE.md"));
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-12")]
    public void Projects_carry_no_AGPL_licence_expression()   // [AC-contrib-clean-room-12]
    {
        foreach (var p in new[] { "Proteus/Proteus.csproj", "XivLiveMesh/XivLiveMesh.csproj" })
        {
            var text = Read(p);
            Assert.DoesNotContain("PackageLicenseExpression", text);
            Assert.DoesNotContain("AGPL", text);
        }
    }

    // Files that may mention the AGPL: the historical records, the third-party listings, the licence's own preamble
    // (earlier versions stay AGPL), the reviewed draft kept as a fixture, and tests that assert its absence.
    private static readonly string[] MayMentionAgpl =
    [
        "LICENSE", "XivLiveMesh/LICENSE", "XivLiveMesh/NOTICE.md", "THIRD_PARTY_NOTICES.txt",
        "docs/pbd-clean-room.md", "docs/pbd-clean-room-plan.md", "docs/pbd-clean-room-log.md",
        "docs/contrib-clean-room-plan.md", "docs/contrib-clean-room-log.md", "docs/contrib-clean-room-spec.md",
        "docs/ipc-contract.md",
        "Proteus.Tests/ReadmeTermsTests.cs", "Proteus.Tests/ShippedAssembliesTests.cs",
        "Proteus.Tests/LicencePublicationTests.cs",
    ];

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-12")]
    [Trait("Plan", "EDGE-contrib-clean-room-50")]
    public void No_file_claims_AGPL_for_Proteus_code()   // [AC-contrib-clean-room-12] [EDGE-contrib-clean-room-50]
    {
        var skipDirs = new[] { "/.git/", "/.claude/worktrees/", "/bin/", "/obj/", "/node_modules/", "/Glamourer/", "/Luna/", "/Penumbra.Api/" };
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
    public void Package_inventory_classifies_every_entry()   // [AC-contrib-clean-room-13] [EDGE-contrib-clean-room-52]
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
    public void Existing_notices_are_kept_and_the_new_ones_added()   // [AC-contrib-clean-room-13]
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
}
