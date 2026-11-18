using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The record of the clean-room replacement of Caraxi's and AzureRavie's code (docs/contrib-clean-room-plan.md): the
/// spec Agent A wrote, the log, the new IpcProvider header, and the paths the removal left. These tests only read files.
/// </summary>
public sealed class ContribCleanRoomRecordTests
{
    private static readonly Lazy<string?> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "proteus.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    });

    private static string Path_(string relative)
    {
        Assert.True(Root.Value != null, $"Could not find proteus.slnx above {AppContext.BaseDirectory}.");
        return Path.Combine(Root.Value!, relative);
    }

    private static string Read(string relative)
    {
        var path = Path_(relative);
        Assert.True(File.Exists(path), $"{relative} is missing.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static readonly string[] SpecHeadings =
    [
        "## 1. What is replaced", "## 2. Public IPC surface", "## 3. IPC behaviour", "## 4. The refresh command",
        "## 5. Overlay discovery: multi-select groups and per-option tables", "## 6. Status window: mod name and priority",
        "## 7. Opening a mod in Penumbra", "## 8. Glamourer: equip cooldown and lock conflicts", "## 9. Seams the tests need",
        "## 10. Existing tests that exercise these lines", "## 11. Dropped or changed behaviour", "## 12. Security-relevant behaviour",
    ];

    private static readonly string[] MixedSet =
    [
        "Proteus/Interop/IpcProvider.cs", "Proteus/Interop/PenumbraBridge.cs", "Proteus/Interop/GlamourerBridge.cs",
        "Proteus/Plugin.cs", "Proteus/Services/SidecarDiscoveryService.cs", "Proteus/Gui/StatusWindow.cs", "Proteus/Gui/ModsTab.cs",
        "Proteus/Services/CompositorService.Carriers.cs", "Proteus/Services/CompositorService.cs",
    ];

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-5")]
    public void Spec_has_every_required_section_and_no_code()   // [AC-contrib-clean-room-5]
    {
        var spec = Read("docs/contrib-clean-room-spec.md");
        int last = -1;
        foreach (var h in SpecHeadings)
        {
            int at = spec.IndexOf("\n" + h + "\n", StringComparison.Ordinal);
            Assert.True(at > last, $"{h} is missing or out of order");
            last = at;
        }
        Assert.DoesNotContain("```", spec);
        Assert.DoesNotContain("CLEAN-ROOM HOLE", spec);
        Assert.Matches(@"\|\s*Label\s*\|\s*Kind\s*\|\s*Arguments\s*\|\s*Returns\s*\|", spec);
        Assert.Matches(@"\|\s*Row\s*\|\s*Area\s*\|\s*Behaviour\s*\|", spec);
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-5")]
    public void IpcProvider_header_is_pinned()   // [AC-contrib-clean-room-5]
    {
        var lines = Read("Proteus/Interop/IpcProvider.cs").Split('\n');
        Assert.Equal("// SPDX-License-Identifier: LicenseRef-Proteus", lines[0].TrimEnd());
        Assert.Equal("// Copyright (c) Solona", lines[1].TrimEnd());
        Assert.Contains(lines.Take(5), l => l.Contains("docs/contrib-clean-room-spec.md", StringComparison.Ordinal));
        Assert.DoesNotContain("ported from", string.Join("\n", lines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-5")]
    public void No_hole_markers_remain()   // [AC-contrib-clean-room-5]
    {
        foreach (var f in MixedSet)
            Assert.DoesNotContain("CLEAN-ROOM HOLE", Read(f));
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-3")]
    public void Sample_gone_and_slnx_present()   // [AC-contrib-clean-room-3]
    {
        Assert.False(Directory.Exists(Path_("samples/ExampleMultiGroupOverlayMod")));
        Assert.True(File.Exists(Path_("proteus.slnx")));
    }

    [Fact]
    [Trait("Plan", "EDGE-contrib-clean-room-28")]
    public void For_Creators_names_only_existing_samples()   // [EDGE-contrib-clean-room-28]
    {
        var doc = Read("For Creators.md");
        Assert.DoesNotContain("ExampleMultiGroupOverlayMod", doc);
        foreach (Match m in Regex.Matches(doc, @"samples/([A-Za-z0-9_\-]+)"))
            Assert.True(Directory.Exists(Path_("samples/" + m.Groups[1].Value)), $"For Creators names a missing sample {m.Value}");
    }

    // ---- The log ----

    private static string Log() => Read("docs/contrib-clean-room-log.md");

    private static Dictionary<string, string> LogSections(string log)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(log, @"^## ([^\n]+)\n(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline))
            sections[m.Groups[1].Value.Trim()] = m.Groups[2].Value;
        return sections;
    }

    /// <summary>The delivered phases, from the log's single "Phases delivered:" line (no new test package: plan 12b).</summary>
    public static IEnumerable<object[]> DeliveredPhases()
    {
        var lines = Regex.Matches(Log(), @"^Phases delivered: (.+)$", RegexOptions.Multiline);
        Assert.Single(lines);
        var value = lines[0].Groups[1].Value.Trim();
        Assert.Contains(value, new[] { "A", "A,B", "A,B,C" });
        foreach (var p in value.Split(',')) yield return [p];
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-5")]
    [Trait("Plan", "EDGE-contrib-clean-room-55")]
    public void Log_states_phases_delivered()   // [AC-contrib-clean-room-5] [EDGE-contrib-clean-room-55]
    {
        var phases = DeliveredPhases().Select(o => (string)o[0]).ToList();
        Assert.Equal("A", phases[0]);
        var log = Log();
        if (phases.Count < 3)
        {
            // A fallback: the abandoned phase's records sit under "Attempted, not delivered".
            Assert.Contains("Attempted, not delivered: Phase", log);
            Assert.Contains("Blocker:", log);
        }
    }

    [Theory]
    [MemberData(nameof(DeliveredPhases))]
    [Trait("Plan", "AC-contrib-clean-room-3")]
    [Trait("Plan", "AC-contrib-clean-room-6")]
    [Trait("Plan", "AC-contrib-clean-room-7")]
    [Trait("Plan", "EDGE-contrib-clean-room-53")]
    public void Log_reports_no_contributor_lines(string phase)   // [AC-contrib-clean-room-3] [AC-contrib-clean-room-6] [AC-contrib-clean-room-7] [EDGE-contrib-clean-room-53]
    {
        var log = Log();
        switch (phase)
        {
            case "A":
                Assert.Contains("`<R0>` = `<R>` = `7ce8851b`", log);
                Assert.Contains("unmatched: 0, blank: 0, bump: 0, other: 0", log);
                Assert.Contains("contributor non-trivial 0", log);
                Assert.Contains("1032 passed, 0 failed and 0 skipped", log);
                Assert.Contains("1009 passed, 0 failed, 0 skipped", log);
                Assert.Contains("dependabot", log);
                break;
            case "B":
                Assert.Contains("1901 passed, 0 failed, 0 skipped", log);
                Assert.Contains("`Test-Path` is False for `Luna.dll`, `Penumbra.Api.dll` and `Glamourer.Api.dll`", log);
                Assert.Contains("a new file by B2 (named)", log);
                // AC-7: Phases A and B changed no licence or notice file.
                Assert.Contains("Phases A and B changed no licence or notice file", log);
                break;
            case "C":
                Assert.Contains("Phases delivered: A,B,C", log);
                Assert.Contains("C1 (XivLiveMesh usage check)", log);
                Assert.Contains("Phase C blame", log);
                break;
        }
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-5")]
    public void Record_docs_carry_no_local_user_paths()   // [AC-contrib-clean-room-5]
    {
        foreach (var doc in new[] { "docs/contrib-clean-room-log.md", "docs/contrib-clean-room-plan.md", "docs/contrib-clean-room-spec.md", "docs/ipc-contract.md" })
            Assert.False(Regex.IsMatch(Read(doc), @"[A-Za-z]:[\\/]Users[\\/]", RegexOptions.IgnoreCase), $"{doc} holds a local user path");
    }

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-4")]
    [Trait("Plan", "EDGE-contrib-clean-room-4")]
    public void Similarity_check_reports_no_expressive_hits()   // [AC-contrib-clean-room-4] [EDGE-contrib-clean-room-4]
    {
        var d = LogSections(Log())["Agent D (self-report)"];
        Assert.Contains("First measurement (B commit 9ae845ea)", d);
        Assert.Contains("Expressive hits: 0", d);
        Assert.Contains("Files at Jaccard >= 0.40: none", d);
    }

    private static readonly string[] CleanAgents =
        ["Agent B", "Agent B2", "QA-lite smoke agent", "Web-fetch agent", "Code-critic", "Security review", "Adversarial QA"];
    private static readonly string[] DirtyAgents =
        ["Agent A (self-report)", "Agent A3 (self-report)", "Agent C (self-report)", "Agent D (self-report)"];

    [Fact]
    [Trait("Plan", "AC-contrib-clean-room-5")]
    [Trait("Plan", "EDGE-contrib-clean-room-13")]
    [Trait("Plan", "EDGE-contrib-clean-room-54")]
    public void Log_lists_every_participant_and_the_clean_side_kept_clear()   // [AC-contrib-clean-room-5] [EDGE-contrib-clean-room-13] [EDGE-contrib-clean-room-54]
    {
        var log = Log();
        var sections = LogSections(log);
        foreach (var name in new[] { "Planning participants", "Orchestrator", "Agent B (run 1, void)" }.Concat(CleanAgents).Concat(DirtyAgents))
            Assert.True(sections.ContainsKey(name), $"the log has no section '{name}'");

        foreach (var name in CleanAgents)
        {
            var s = sections[name];
            // A gate step that could not run (a QA Blocker) records the block instead of an agent's reads.
            if (name == "Adversarial QA" && s.Contains("Blocked:", StringComparison.Ordinal)
                && s.Contains("No agent was started", StringComparison.Ordinal) && !s.Contains("Opened:", StringComparison.Ordinal))
            {
                Assert.Contains("UNVERIFIED", s);   // a block is never recorded silently
                continue;
            }
            Assert.Contains("Opened:", s);
            Assert.Contains("Sub-agents: none", s);
            Assert.Contains("Grep without line numbers: 0", s);
            Assert.Contains("Shell commands outside the allowlist: 0", s);
            Assert.Contains("forbidden locations in listing/search results: 0", s);
        }
        foreach (var name in DirtyAgents)
        {
            Assert.Contains("Opened:", sections[name]);
            Assert.Contains("Identifier-grep hits: 0", sections[name]);
        }
        // The void run is recorded as void, and the orchestrator's own reads are accounted for.
        Assert.Contains("VOID", sections["Agent B (run 1, void)"]);
        Assert.Contains("self-check 1 (whole session): 0 beyond disclosed exceptions", log);
        Assert.Contains("self-check 2 (from 2026-11-17T23:38:46.968Z): 0 beyond named exceptions", log);
        Assert.Contains("orchestrator Cecil/ildasm/decompiler runs outside the test: 0", log);
        Assert.Contains("the 1b failure path added no file", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docs/pbd-clean-room.md", string.Join("\n", log.Split('\n').Take(12)), StringComparison.Ordinal);
    }
}
