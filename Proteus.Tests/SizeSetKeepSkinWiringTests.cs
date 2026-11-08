using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The size set's "Keep hidden skin" box, wired from the panel through the launch to the refit calls. ImGui cannot be
/// drawn headlessly, so the panel and the call sites are read from source; the run's effect on real models is in
/// <see cref="SizeSetKeepSkinLocalTests"/>.
/// </summary>
public class SizeSetKeepSkinWiringTests
{
    private const string Panel = "Proteus/Gui/SizeSetPanel.cs";
    private const string Refit = "Proteus/Services/SizeSet/SizeSetRefit.cs";

    /// <summary>The source with // comments taken out, so a mention in a comment never counts as code.</summary>
    private static string Code(string relative)
        => string.Join('\n', SourceText.Read(relative).Split('\n').Select(l =>
        {
            int at = l.IndexOf("//", StringComparison.Ordinal);
            return at < 0 ? l : l[..at];
        }));

    [Fact]
    [Trait("Plan", "AC-size-set-keep-skin-1")]
    [Trait("Plan", "EDGE-size-set-keep-skin-2")]
    public void Panel_draws_the_box_bound_to_config()
    {
        // [AC-size-set-keep-skin-1] [EDGE-size-set-keep-skin-2] Between the piercings box and "Make again", inside the
        // block disabled while a run is going; saved on change, with its tooltip.
        string draw = SourceText.Method(Panel, "public void Draw(");
        int disabled = draw.IndexOf("ImRaii.Disabled(busy)", StringComparison.Ordinal);
        int piercings = draw.IndexOf("ImGui.Checkbox(s.Piercings", StringComparison.Ordinal);
        int box = draw.IndexOf("ImGui.Checkbox(s.KeepHiddenSkin", StringComparison.Ordinal);
        int remake = draw.IndexOf("ImGui.Checkbox(s.Remake", StringComparison.Ordinal);
        Assert.True(disabled >= 0 && piercings > disabled && box > piercings && remake > box,
                    $"order: disabled {disabled}, piercings {piercings}, box {box}, remake {remake}");
        string block = draw[box..remake];
        Assert.Contains("config.SizeSetKeepHiddenSkin = keepSkin;", block);
        Assert.Contains("config.Save();", block);
        Assert.Contains("ImGui.SetTooltip(s.KeepHiddenSkinTip)", block);
        Assert.Contains("bool keepSkin = config.SizeSetKeepHiddenSkin;", draw[piercings..box]);
    }

    [Fact]
    [Trait("Plan", "AC-size-set-keep-skin-1")]
    public void Panel_start_passes_the_saved_choice()
    {
        // [AC-size-set-keep-skin-1]
        string start = SourceText.Method(Panel, "private void Start(");
        int prepare = start.IndexOf("SizeSetLaunch.Prepare(", StringComparison.Ordinal);
        Assert.True(prepare >= 0);
        int end = start.IndexOf(");", prepare, StringComparison.Ordinal);
        Assert.Contains("config.SizeSetKeepHiddenSkin", start[prepare..end]);
    }

    [Fact]
    [Trait("Plan", "AC-size-set-keep-skin-3")]
    public void Own_sex_plan_takes_the_request()
    {
        // [AC-size-set-keep-skin-3]
        string code = Code(Refit);
        Assert.Contains("cutHidden: !request.KeepHiddenSkin", code);
        Assert.DoesNotContain("cutHidden: true", code);
    }

    [Fact]
    [Trait("Plan", "AC-size-set-keep-skin-4")]
    public void Mens_versions_take_the_request()
    {
        // [AC-size-set-keep-skin-4]
        string code = Code("Proteus/Services/SizeSet/SizeSetRefit.CrossSex.cs");
        int run = code.IndexOf("CrossSexRefitJob.Run(new CrossSexRefitJob.Inputs(", StringComparison.Ordinal);
        Assert.True(run >= 0);
        int end = code.IndexOf("));", run, StringComparison.Ordinal);
        Assert.Contains("CutHidden: !request.KeepHiddenSkin", code[run..end]);
    }

    [Fact]
    [Trait("Plan", "AC-size-set-keep-skin-5")]
    [Trait("Plan", "EDGE-size-set-keep-skin-5")]
    public void Ends_paths_never_read_the_box()
    {
        // [AC-size-set-keep-skin-5] [EDGE-size-set-keep-skin-5] Shoes and gloves, own and men's, keep their forced cut.
        Assert.DoesNotContain("KeepHiddenSkin", Code("Proteus/Services/SizeSet/SizeSetRefit.Ends.cs"));
        Assert.DoesNotContain("KeepHiddenSkin", Code("Proteus/Services/SizeSet/SizeSetRefit.CrossSexEnds.cs"));
        Assert.DoesNotContain("CutHidden", Code("Proteus/Services/SizeSet/SizeSetRefit.CrossSexEnds.cs"));
        Assert.Contains("cutHidden: true", Code("Proteus/Services/AutoRefit/KeepEnds.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-size-set-keep-skin-7")]
    public void Adapter_reads_config_and_passes_it()
    {
        // [AC-size-set-keep-skin-7]
        const string adapter = "Proteus/TestHarness/GameHarnessAdapter.cs";
        Assert.Contains("config.SizeSetKeepHiddenSkin", SourceText.Method(adapter, "public RefitOptions Options("));
        Assert.Contains("options.KeepHiddenSkin", SourceText.Method(adapter, "public SizeSetLaunch.Prepared PrepareSizeSet("));
    }

    [Fact]
    [Trait("Plan", "EDGE-size-set-keep-skin-10")]
    public void Harness_never_writes_config()
    {
        // [EDGE-size-set-keep-skin-10] The override is for the run alone.
        var assign = new Regex(@"config\.\w+\s*=[^=]");
        Assert.DoesNotMatch(assign, Code("Proteus/TestHarness/RefitJob.cs"));
        Assert.DoesNotMatch(assign, SourceText.Method("Proteus/TestHarness/HarnessRouter.cs",
                                                       "private async Task<HarnessResponse> Refit("));
        Assert.Contains("\"keepHiddenSkin\"", SourceText.Method("Proteus/TestHarness/HarnessRouter.cs",
                                                                "private async Task<HarnessResponse> Refit("));
    }

    [Fact]
    [Trait("Plan", "EDGE-size-set-keep-skin-11")]
    public void Batch_does_not_set_the_box()
    {
        // [EDGE-size-set-keep-skin-11] The mod packer and the CLI's --size-set keep cutting.
        Assert.DoesNotContain("KeepHiddenSkin", SourceText.Read("Proteus/Services/SizeSet/SizeSetBatch.cs"));
    }
}
