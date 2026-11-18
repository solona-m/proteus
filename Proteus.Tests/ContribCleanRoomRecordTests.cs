using System;
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
        Assert.Equal("// SPDX-License-Identifier: AGPL-3.0-or-later", lines[0].TrimEnd());
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
}
