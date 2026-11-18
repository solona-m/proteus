using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using XivLiveMesh;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The record of the clean-room rewrite of <see cref="PbdFile"/> (docs/pbd-clean-room.md): the spec Agent A wrote,
/// the log of what every participant opened, and the new file's header, surface and notices.
/// </summary>
public sealed class PbdCleanRoomRecordTests
{
    private static readonly Lazy<string?> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    });

    private static string Read(string relative)
    {
        Assert.True(Root.Value != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        var path = Path.Combine(Root.Value!, relative);
        Assert.True(File.Exists(path), $"{relative} is missing.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static readonly string[] SpecHeadings =
    [
        "## 1. File layout", "## 2. A deformer block", "## 3. Bad input", "## 4. The race chain",
        "## 5. One bone through a chain", "## 6. Conventions", "## 7. Test vectors",
    ];

    private static string Section(string text, string heading)
    {
        int at = text.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
        Assert.True(at >= 0, $"missing section {heading}");
        int next = text.IndexOf("\n## ", at + heading.Length + 2, StringComparison.Ordinal);
        return next < 0 ? text[at..] : text[at..next];
    }

    [Fact]
    [Trait("Plan", "AC-pbd-clean-room-1")]
    public void Spec_has_every_required_section_and_no_code()
    {
        var spec = Read("docs/pbd-format-spec.md");
        int last = -1;
        foreach (var h in SpecHeadings)
        {
            int at = spec.IndexOf("\n" + h + "\n", StringComparison.Ordinal);
            Assert.True(at > last, $"{h} is missing or out of order");
            last = at;
        }
        Assert.DoesNotContain("```", spec);

        var conventions = Section(spec, "## 6. Conventions");
        Assert.Contains("Worked example", conventions);
        Assert.Matches(@"\d+\.\d{6}", conventions);

        var vectors = Section(spec, "## 7. Test vectors");
        Assert.Contains("1e-5", vectors);
        Assert.Matches(@"\|\s*Bone\s*\|\s*Parent\s*\|", vectors);

        var markers = Regex.Matches(spec, @"Required change from the old reader:\s*\(([^)]*)\)");
        Assert.Equal(Regex.Matches(spec, "Required change from the old reader:").Count, markers.Count);
        foreach (Match m in markers)
            Assert.True(Regex.IsMatch(m.Groups[1].Value, @"R-1|offset|shared|link|parent", RegexOptions.IgnoreCase),
                        $"a marker cites neither R-1 nor a doc §3 item: ({m.Groups[1].Value})");
    }

    // ---- The log ----

    private static readonly string[] KnownSections =
    [
        "Planner", "Plan-critic 1", "Plan-critic 2", "Plan-critic 3", "Plan-critic 3 (re-check of rev 4)",
        "Plan-critic 4 (check of rev 5)", "Planner (rev 4)", "Planner (rev 5)", "Planner (rev 6)",
        "Planning orchestrator", "Agent A (self-report)", "Orchestrator", "Agent B", "Code-critic",
    ];

    private static Dictionary<string, string> LogSections(string log)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(log, @"^## ([^\n]+)\n(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline))
            sections[m.Groups[1].Value.Trim()] = m.Groups[2].Value;
        return sections;
    }

    /// <summary>The "- " items under a "Label:" line, up to the first line that is not an item.</summary>
    private static List<string> Items(string section, string label)
    {
        var lines = section.Split('\n');
        int at = Array.FindIndex(lines, l => l.TrimEnd() == label);
        Assert.True(at >= 0, $"no '{label}' list");
        var items = new List<string>();
        for (int i = at + 1; i < lines.Length && lines[i].StartsWith("- ", StringComparison.Ordinal); i++)
            items.Add(lines[i][2..].Trim().Trim('`'));
        return items;
    }

    private static string Norm(string s) => Regex.Replace(s.Replace('\\', '/').ToLowerInvariant(), @"\s+", " ").Trim();

    private static readonly string[] ForbiddenMarkers =
    [
        "glamourer/", "penumbra.api/", "luna/", "/bin/", "/obj/", "meddle.utils", "skintattoo",
        "draganddroptexturing", "/memory/", "scratchpad-agenta",
    ];

    private static bool AllowedOpened(string entry)
    {
        var e = Norm(entry);
        if (e.StartsWith("bin/", StringComparison.Ordinal) || e.StartsWith("obj/", StringComparison.Ordinal)) return false;
        if (ForbiddenMarkers.Any(e.Contains)) return false;
        bool repoRelative = !Regex.IsMatch(e, "^[a-z]:") && !e.StartsWith('/') && !e.Split('/').Contains("..");
        bool toolPath = e.StartsWith("c:/users/solon/.claude/plugins/", StringComparison.Ordinal)
                        || (e.Contains("/temp/claude/") && e.Contains("/scratchpad/") && !e.Contains("scratchpad-agenta"));
        return repoRelative || toolPath;
    }

    private const string W = "e:/orca/workspaces/proteus/meddle";
    private const string DotnetTest = W + @"/proteus\.tests/proteus\.tests\.csproj -c release( --logger ""console;verbosity=normal"")? --filter ""[^""]*""( --logger ""console;verbosity=normal"")?";

    private static readonly Regex[] BAllowed =
    [
        new($@"^dotnet build {W}/proteus/proteus\.csproj -c release$"),
        new($@"^dotnet test {DotnetTest}$"),
    ];

    private static readonly Regex[] CriticAllowed =
    [
        new($@"^git -c {W} diff test -- \. ':!xivlivemesh/pbdfile\.cs'$"),
        new($@"^git -c {W} show head:xivlivemesh/pbdfile\.cs$"),
        new($@"^dotnet test {DotnetTest}$"),
    ];

    [Fact]
    [Trait("Plan", "AC-pbd-clean-room-2")]
    [Trait("Plan", "EDGE-pbd-clean-room-17")]
    public void Log_lists_every_participant_and_the_clean_side_kept_clear()
    {
        var log = Read("docs/pbd-clean-room-log.md");
        var head = log[..log.IndexOf("\n## ", StringComparison.Ordinal)];
        Assert.Contains("merge commit message", head);

        var sections = LogSections(log);
        foreach (var name in KnownSections)
        {
            Assert.True(sections.ContainsKey(name), $"the log has no '{name}' section");
            Assert.Contains("Opened:", sections[name]);
        }
        foreach (var name in sections.Keys.Where(k => Regex.IsMatch(k, @"^(Plan-critic \d+( \(.+\))?|Planner \(rev \d+\))$")))
            Assert.Contains("Opened:", sections[name]);

        var planning = sections["Planning orchestrator"];
        Assert.Contains("readme-update", planning);
        Assert.Contains("Glamourer/Penumbra.GameData/Files/", planning);
        Assert.Contains("no code content", planning);
        Assert.Contains("before the rule", planning);

        var a = sections["Agent A (self-report)"];
        Assert.Contains("self-report", a);
        Assert.Contains("Identifier-grep hits: 0", a);

        foreach (var (name, allowed) in new[] { ("Agent B", BAllowed), ("Code-critic", CriticAllowed) })
        {
            var s = sections[name];
            foreach (var entry in Items(s, "Opened:"))
                Assert.True(AllowedOpened(entry), $"{name} opened '{entry}'");
            var shell = Items(s, "Shell commands:");
            foreach (var cmd in shell.Where(c => c != "none"))
            {
                var n = Norm(cmd);
                Assert.True(allowed.Any(r => r.IsMatch(n)), $"{name} ran a command outside its allowlist: {cmd}");
                Assert.DoesNotContain(ForbiddenMarkers, m => n.Contains(m));
            }
            if (name == "Agent B")
                Assert.DoesNotContain(shell, c => Norm(c).StartsWith("git", StringComparison.Ordinal));
            Assert.Contains("Sub-agents: none", s);
            Assert.Contains("Grep without line numbers: 0", s);
            Assert.Contains("forbidden locations in listing/search results: 0", s);
        }

        Assert.Contains("dotnet build", log);
        Assert.Contains("not counted", log);
        Assert.Contains("files_with_matches", log);
        Assert.Contains("single-file", log);
    }

    [Fact]
    [Trait("Plan", "AC-pbd-clean-room-3")]
    public void Public_surface_is_exactly_the_documented_one()
    {
        var t = typeof(PbdFile);
        Assert.True(t.IsPublic && t.IsSealed && t.IsClass);
        Assert.Equal("XivLiveMesh", t.Namespace);

        var ctor = Assert.Single(t.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal([typeof(ReadOnlySpan<byte>)], ctor.GetParameters().Select(p => p.ParameterType).ToArray());

        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var chainType = typeof(IReadOnlyList<IReadOnlyDictionary<string, Matrix4x4>>);
        var methods = t.GetMethods(Declared).Where(m => !m.IsSpecialName).OrderBy(m => m.Name).ToArray();
        Assert.Equal(["Chain", "Lists", "Resolve"], methods.Select(m => m.Name).ToArray());

        var chain = methods[0];
        Assert.False(chain.IsStatic);
        Assert.Equal(chainType, chain.ReturnType);
        Assert.Equal([typeof(ushort), typeof(ushort)], chain.GetParameters().Select(p => p.ParameterType).ToArray());

        Type[] boneArgs = [chainType, typeof(string), typeof(Func<string, string?>)];
        var lists = methods[1];
        Assert.True(lists.IsStatic);
        Assert.Equal(typeof(bool), lists.ReturnType);
        Assert.Equal(boneArgs, lists.GetParameters().Select(p => p.ParameterType).ToArray());

        var resolve = methods[2];
        Assert.True(resolve.IsStatic);
        Assert.Equal(typeof(Matrix4x4), resolve.ReturnType);
        Assert.Equal(boneArgs, resolve.GetParameters().Select(p => p.ParameterType).ToArray());

        Assert.Empty(t.GetProperties(Declared));
        Assert.Empty(t.GetFields(Declared));
        Assert.Empty(t.GetEvents(Declared));
        Assert.Empty(t.GetNestedTypes(BindingFlags.Public));
    }

    [Fact]
    [Trait("Plan", "AC-pbd-clean-room-4")]
    public void Header_names_the_spec_not_a_port()
    {
        var source = Read("XivLiveMesh/PbdFile.cs");
        var lines = source.Split('\n');
        Assert.Equal("// SPDX-License-Identifier: LicenseRef-Proteus", lines[0].TrimEnd());
        Assert.Equal("// Copyright (c) Solona", lines[1].TrimEnd());
        Assert.Contains(lines.Take(5), l => l.Contains("docs/pbd-format-spec.md", StringComparison.Ordinal));
        Assert.DoesNotContain("ported from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Meddle", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SkinTattoo", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Plan", "AC-pbd-clean-room-8")]
    public void Notices_no_longer_name_Meddle()
    {
        var notices = Read("THIRD_PARTY_NOTICES.txt");
        var liveMesh = Read("XivLiveMesh/NOTICE.md");
        Assert.DoesNotContain("Meddle", notices, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Meddle", liveMesh, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(liveMesh.Split('\n'), l => l.TrimStart().StartsWith('|') && l.Contains("PbdFile.cs", StringComparison.Ordinal));
    }
}
