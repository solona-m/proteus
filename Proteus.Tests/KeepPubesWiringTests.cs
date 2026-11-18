using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// "Keep the new body's pubes", wired from the three panels through to the refit calls. ImGui cannot be drawn headlessly,
/// so the panels and the call sites are read from source; the refit's effect is in <see cref="BodyRetargetTests"/>.
/// </summary>
public class KeepPubesWiringTests
{
    private const string StudioPanel = "Proteus/Gui/BodyRetargetPanel.cs";
    private const string SizeSetPanel = "Proteus/Gui/SizeSetPanel.cs";
    private const string AutoPanel = "Proteus/Gui/AutoRefitPanel.cs";
    private const string Watcher = "Proteus/Services/AutoRefit/AutoRefitWatcher.cs";

    /// <summary>The source with // comments taken out, so a mention in a comment never counts as code.</summary>
    private static string Code(string relative) => Uncommented(SourceText.Read(relative));

    private static string Uncommented(string source)
        => string.Join('\n', source.Split('\n').Select(l =>
        {
            int at = l.IndexOf("//", StringComparison.Ordinal);
            return at < 0 ? l : l[..at];
        }));

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-2")]
    public void Studio_panel_draws_the_box_under_the_skin_box()
    {
        // [AC-refit-keep-pubes-2] After the piercings box, inside the indented block greyed out with the skin box, with its
        // tooltip; the plan takes the gated value and the plan's key the box.
        string code = Code(StudioPanel);
        int piercings = code.IndexOf("ImGui.Checkbox(ps.RetargetPiercings", StringComparison.Ordinal);
        int box = code.IndexOf("ImGui.Checkbox(ps.RetargetPubes, ref carryPubes)", StringComparison.Ordinal);
        int unindent = code.IndexOf("ImGui.Unindent();", piercings, StringComparison.Ordinal);
        Assert.True(piercings > 0 && box > piercings && unindent > box, $"order: {piercings} {box} {unindent}");
        Assert.Contains("using (ImRaii.Disabled(!swap))", code[piercings..box]);
        Assert.Contains("ImGui.SetTooltip(ps.RetargetPubesTip)", code[box..unindent]);

        Assert.Contains("bool pubes = RetargetInputs.CarryPubes(replaceSkin, carryPubes);", code);
        Assert.Matches(@"SlotName, modelRel, shapeNames, piercings, pubes\)", code);
        Assert.Matches(@"RetargetInputs\.StudioPlan\([^;]*carryPubes: pubes\)", code);
        Assert.Matches(@"RetargetInputs\.Key\([^;]*bodiesInPlay, carryPubes\)", code);

        string cross = Code("Proteus/Gui/BodyRetargetPanel.CrossSex.cs");
        Assert.Contains("bool pubes = RetargetInputs.CarryPubes(", cross);
        Assert.Matches(@"CrossSexRefitJob\.EndInputs\([^;]*carryPubes: pubes\)", cross);
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-3")]
    public void Size_set_panel_draws_the_box_bound_to_config()
    {
        // [AC-refit-keep-pubes-3] After the piercings box and before "Keep hidden skin", inside the block disabled while a
        // run goes; saved on change, with its tooltip. Start hands the saved choice to the launch.
        string draw = SourceText.Method(SizeSetPanel, "public void Draw(");
        int disabled = draw.IndexOf("ImRaii.Disabled(busy)", StringComparison.Ordinal);
        int piercings = draw.IndexOf("ImGui.Checkbox(s.Piercings", StringComparison.Ordinal);
        int box = draw.IndexOf("ImGui.Checkbox(s.Pubes", StringComparison.Ordinal);
        int keep = draw.IndexOf("ImGui.Checkbox(s.KeepHiddenSkin", StringComparison.Ordinal);
        Assert.True(disabled >= 0 && piercings > disabled && box > piercings && keep > box,
                    $"order: disabled {disabled}, piercings {piercings}, box {box}, keep {keep}");
        string block = draw[box..keep];
        Assert.Contains("config.SizeSetPubes = pubes;", block);
        Assert.Contains("config.Save();", block);
        Assert.Contains("ImGui.SetTooltip(s.PubesTip)", block);

        string start = SourceText.Method(SizeSetPanel, "private void Start(");
        int prepare = start.IndexOf("SizeSetLaunch.Prepare(", StringComparison.Ordinal);
        int end = start.IndexOf(");", prepare, StringComparison.Ordinal);
        Assert.Contains("pubes: config.SizeSetPubes", start[prepare..end]);
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-3")]
    [Trait("Plan", "EDGE-refit-keep-pubes-12")]
    public void Size_set_refits_take_the_request()
    {
        // [AC-refit-keep-pubes-3] [EDGE-refit-keep-pubes-12] Every size set refit path, men's versions and kept ends too.
        Assert.Contains("carryPubes: request.Pubes", Code("Proteus/Services/SizeSet/SizeSetRefit.cs"));
        Assert.Contains("carryPubes: request.Pubes", Code("Proteus/Services/SizeSet/SizeSetRefit.Ends.cs"));
        Assert.Contains("CarryPubes: request.Pubes", Code("Proteus/Services/SizeSet/SizeSetRefit.CrossSex.cs"));
        Assert.Contains("CarryPubes: request.Pubes", Code("Proteus/Services/SizeSet/SizeSetRefit.CrossSexEnds.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-4")]
    public void Auto_refit_reads_the_setting()
    {
        // [AC-refit-keep-pubes-4] Both auto refit paths take CarryPubesNow, which reads the saved setting; the panel draws
        // the box under the switch, saved on change, with its tooltip.
        foreach (string file in new[] { Watcher, "Proteus/Services/AutoRefit/AutoRefitWatcher.KeepEnds.cs" })
        {
            string code = Code(file);
            Assert.Contains("bool pubes = CarryPubesNow;", code);
            Assert.Contains("carryPubes: pubes", code);
        }
        Assert.Matches(@"bool pubes = config\.AutoRefitPubes;\s*HarnessPubes\(ref pubes\);", Code(Watcher));

        string panel = Code(AutoPanel);
        int box = panel.IndexOf("ImGui.Checkbox(s.Pubes", StringComparison.Ordinal);
        Assert.True(box > 0);
        int disabled = panel.LastIndexOf("using (ImRaii.Disabled(!on))", box, StringComparison.Ordinal);
        Assert.True(disabled > panel.IndexOf("ImGui.Checkbox(s.Piercings", StringComparison.Ordinal));
        string block = panel[box..panel.IndexOf("ImGui.SetTooltip(s.PubesTip)", box, StringComparison.Ordinal)];
        Assert.Contains("config.AutoRefitPubes = pubes;", block);
        Assert.Contains("config.Save();", block);
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-9")]
    public void Adapter_reads_config_and_passes_it()
    {
        // [AC-refit-keep-pubes-9]
        string adapter = Code("Proteus/TestHarness/GameHarnessAdapter.cs");
        Assert.Matches(@"public RefitOptions Options\(bool remake\)[^;]*config\.SizeSetPubes\)", adapter);
        Assert.Matches(@"SizeSetLaunch\.Prepare\([^;]*pubes: options\.Pubes\)", adapter);
    }

    [Fact]
    [Trait("Plan", "AC-refit-keep-pubes-10")]
    public void Auto_refit_pubes_override_is_compiled_out_and_never_saved()
    {
        // [AC-refit-keep-pubes-10] The override lives in the harness-only partial; it never touches the config; put-back
        // clears it whichever override was set; a refit while either stands is noted.
        string partial = SourceText.Read("Proteus/Services/AutoRefit/AutoRefitWatcher.Harness.cs");
        Assert.StartsWith("#if PROTEUS_TEST_HARNESS", partial.TrimStart());
        Assert.Contains("internal bool? HarnessPubesOverride", partial);
        Assert.DoesNotMatch(@"config\.\w+\s*=", Uncommented(partial));
        Assert.Contains("if (harnessOverride >= 0 || harnessPubesOverride >= 0) harnessRefits.TryAdd(modDir, 0);", partial);
        Assert.DoesNotContain("HarnessPubesOverride", Code(Watcher));

        string putBack = SourceText.Method("Proteus/TestHarness/HarnessChanges.cs", "internal (List<SidecarUndo> Sidecar,");
        Assert.Contains("if (autoRefitOverride != null || autoRefitPubesOverride != null)", putBack);
        Assert.Contains("game.SetAutoRefitPubesOverride(null);", putBack);
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-13")]
    public void Harness_never_writes_config()
    {
        // [EDGE-refit-keep-pubes-13] Neither route nor the job writes a setting; both routes accept the field.
        Assert.DoesNotMatch(@"config\.\w+\s*=", Code("Proteus/TestHarness/RefitJob.cs"));
        string refit = SourceText.Method("Proteus/TestHarness/HarnessRouter.cs", "private async Task<HarnessResponse> Refit(");
        string auto = SourceText.Method("Proteus/TestHarness/HarnessRouter.Qa.cs", "private async Task<HarnessResponse> AutoRefitAction(");
        foreach (string route in new[] { refit, auto })
        {
            Assert.DoesNotMatch(@"[Cc]onfig\.\w+\s*=[^=]", Uncommented(route));
            Assert.Matches(@"JsonBody\(request,[^)]*""pubes""\)", route);
        }
    }

    /// <summary>Every statement in the plugin and the headless refit that hands a refit the piercings choice.</summary>
    private static readonly string[] Calls =
    [
        "BodyRetarget.Plan(", "BodyRetarget.SwapSkin(", "BodyRetarget.Rebuild(", "RefitCore.PlanRefit(", "KeepEnds.Plan(",
        "new KeepEnds.StudioPlanInput(", "new CrossSexRefitJob.Inputs(", "CrossSexRefitJob.EndInputs(",
        "RetargetInputs.StudioPlan(", "RetargetInputs.Key(", "new SizeSetRefit.Request(", "SizeSetLaunch.Prepare(",
    ];

    private static readonly string[] OwnValues =
    [
        "request.Pubes", "pubes", "config.AutoRefitPubes", "config.SizeSetPubes", "options.Pubes", "i.CarryPubes",
        "input.CarryPubes", "carryPubes",
    ];

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-12")]
    public void Every_piercings_refit_call_passes_its_own_pubes_value()
    {
        // [EDGE-refit-keep-pubes-12] A site that passes the piercings choice passes the pubes one, as its own surface's
        // value — never a literal false. The headless paths (the CLI, the mod packer's SizeSetBatch) carry none and pass
        // nothing at all: Headless_paths_carry_no_pubes.
        var root = Repo();
        var files = new[] { "Proteus/Services", "Proteus/Gui", "Proteus/TestHarness" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(f) != "SizeSetBatch.cs");
        var missing = new System.Collections.Generic.List<string>();
        int checkedSites = 0;
        foreach (string file in files)
        {
            string code = Uncommented(File.ReadAllText(file));
            foreach (string call in Calls)
                for (int at = code.IndexOf(call, StringComparison.Ordinal); at >= 0;
                     at = code.IndexOf(call, at + call.Length, StringComparison.Ordinal))
                {
                    if (at > 0 && (char.IsLetterOrDigit(code[at - 1]) || code[at - 1] == '.') && !call.StartsWith("new ")) continue;
                    int end = code.IndexOf(';', at);
                    string statement = code[at..end];
                    if (!Regex.IsMatch(statement, @"[Pp]iercings\b|request\.Piercings|config\.\w*Piercings|CarryPiercings"))
                        continue;
                    checkedSites++;
                    bool own = OwnValues.Any(v => Regex.IsMatch(statement, @"(:\s*|,\s*|\(\s*)" + Regex.Escape(v) + @"\s*[,)]"));
                    if (!own) missing.Add($"{Path.GetFileName(file)}: {call}");
                    if (Regex.IsMatch(statement, @"[Pp]ubes:\s*false")) missing.Add($"{Path.GetFileName(file)}: {call} passes false");
                }
        }
        Assert.True(checkedSites >= 15, $"only {checkedSites} sites found: the scan is broken");
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    [Trait("Plan", "EDGE-refit-keep-pubes-17")]
    public void Headless_paths_carry_no_pubes()
    {
        // [EDGE-refit-keep-pubes-17] The CLI and the mod packer's size set never name the setting: the request's default
        // (off) is what they get.
        Assert.DoesNotContain("Pubes", Code("Proteus/Services/SizeSet/SizeSetBatch.cs"));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(Repo(), "Proteus.Refit"), "*.cs", SearchOption.AllDirectories)
                                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            Assert.DoesNotContain("ubes", Uncommented(File.ReadAllText(file)));
    }

    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "proteus.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("proteus.slnx");
    }
}
