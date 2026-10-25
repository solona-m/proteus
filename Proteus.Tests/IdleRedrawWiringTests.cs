using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The redraws Proteus no longer does when nothing it shows has changed, pinned by reading the source:
/// <c>CompositorService</c> cannot be built in a test. Textual and deliberately so: change these with the code. Methods
/// are found by their full declarations, since a call can come before the definition.
/// </summary>
public class IdleRedrawWiringTests
{
    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");

    private static string Source(string relative) => File.ReadAllText(Path.Combine(ProteusDir(), relative));

    /// <summary>The body of the first method whose signature contains <paramref name="signature"/>, braces matched.</summary>
    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced {signature}");
        return "";
    }

    /// <summary>Every call to <paramref name="callee"/> in <paramref name="body"/>, its arguments included.</summary>
    private static string[] Calls(string body, string callee)
        => Regex.Matches(body, Regex.Escape(callee) + @"\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)")
                .Select(m => m.Value).ToArray();

    private static int At(string text, string what)
    {
        int at = text.IndexOf(what, System.StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {what}");
        return at;
    }

    private static string Discover() => Method(Source("Services/CompositorService.CompositeRun.cs"), "private bool Discover(");
    private static string Publish() => Source("Services/CompositorService.Publish.cs");
    private static string Write() => Method(Source("Services/CompositorService.ManagedMod.cs"), "ManifestWrite WriteManagedModJson(");
    private static string Trigger() => Source("Services/CompositorService.Trigger.cs");

    [Fact]
    [Trait("Plan", "AC-no-idle-redraws-1")]
    [Trait("Plan", "EDGE-no-idle-redraws-4")]
    [Trait("Plan", "EDGE-no-idle-redraws-8")]
    [Trait("Plan", "EDGE-no-idle-redraws-9")]
    public void The_empty_branch_gates_only_the_character_reload()
    {
        var body = Discover();
        var call = Assert.Single(Calls(body, "ReloadAndRedraw"));
        Assert.Contains("redraw: redrawsCharacter", call);
        Assert.DoesNotContain("userRequested", call);
        Assert.DoesNotContain("redraw: false", call);
        int decision = At(body, "bool redrawsCharacter = RedrawHold.EmptyPublishRedraws(");
        Assert.True(At(body, "ReconcileInvisibleGlasses(") < decision);
        Assert.True(At(body, "ReconcileEmperorRing(") < decision);
        Assert.DoesNotContain("_redrawOwed", body);

        // Penumbra re-reads the managed mod whatever the decision; true only when the character reload was handed off.
        var reload = Method(Publish(), "private bool ReloadAndRedraw(");
        int gate = At(reload, "RedrawHold.AutoRedrawAllows(config.AutoRedraw, userRequested)");
        Assert.True(At(reload, "ReloadModDirectory(") < gate);
        int yes = At(reload, "return true;");
        Assert.True(gate < yes && yes > At(reload, "RefreshPlayerTextures("));
    }

    [Fact]
    [Trait("Plan", "AC-no-idle-redraws-1")]
    [Trait("Plan", "EDGE-no-idle-redraws-6")]
    [Trait("Plan", "EDGE-no-idle-redraws-7")]
    public void The_branch_takes_alreadyDrawn_from_its_own_write()
    {
        var body = Discover();
        Assert.Contains("var (stamp, alreadyDrawn) = compositor.WriteManagedModJson(", body);
        Assert.DoesNotContain("_emptyOutputDrawn", body);

        var write = Write();
        var locked = write[At(write, "lock (_manifestLock)")..];
        Assert.True(At(locked, "wasDrawn = _emptyOutputDrawn") < At(locked, "_emptyOutputDrawn = false"));

        var publish = Publish();
        Assert.Matches(@"private volatile bool _emptyOutputDrawn;", publish);
        // Set in exactly one place: the mark that checks no write came since.
        var files = Directory.EnumerateFiles(Path.Combine(ProteusDir(), "Services"), "*.cs")
            .Where(f => File.ReadAllText(f).Contains("_emptyOutputDrawn = true")).ToList();
        Assert.Equal("CompositorService.Publish.cs", Path.GetFileName(Assert.Single(files)));
        Assert.Single(Regex.Matches(publish, Regex.Escape("_emptyOutputDrawn = true")));
        Assert.Contains("_emptyOutputDrawn = true", Method(publish, "private void MarkEmptyOutputDrawn("));
    }

    [Fact]
    [Trait("Plan", "AC-no-idle-redraws-2")]
    [Trait("Plan", "EDGE-no-idle-redraws-1")]
    [Trait("Plan", "EDGE-no-idle-redraws-2")]
    [Trait("Plan", "EDGE-no-idle-redraws-5")]
    [Trait("Plan", "EDGE-no-idle-redraws-6")]
    public void Every_write_clears_the_flag_and_a_mark_needs_no_write_since()
    {
        var write = Write();
        var locked = write[At(write, "lock (_manifestLock)")..];
        Assert.Contains("_emptyOutputDrawn = false", locked);
        Assert.Contains("++_manifestWrites", locked);

        var mark = Method(Publish(), "private void MarkEmptyOutputDrawn(");
        Assert.True(At(mark, "lock (_manifestLock)") < At(mark, "_manifestWrites == stamp"));

        // The branch marks with its own write's stamp, and only when the character reload ran or was not needed.
        var body = Discover();
        var reloadCall = Assert.Single(Calls(body, "ReloadAndRedraw"));
        Assert.Contains($"if (compositor.{reloadCall} || !redrawsCharacter)", body);
        Assert.True(At(body, "compositor.MarkEmptyOutputDrawn(stamp)") > At(body, reloadCall));
    }

    [Fact]
    [Trait("Plan", "AC-no-idle-redraws-2")]
    [Trait("Plan", "EDGE-no-idle-redraws-3")]
    public void Withdrawal_latches_a_full_redraw_and_counts_as_withdrawing()
    {
        var body = Discover();
        int withdrawing = At(body, "bool withdrawing =");
        Assert.True(withdrawing < At(body, "if (withdrawing) compositor._needFullRedraw = true;"));
        Assert.True(withdrawing < At(body, "compositor._secondSkinActive = false"));
        Assert.Contains("RedrawHold.EmptyPublishRedraws(alreadyDrawn, withdrawing)", body);

        int latch = At(body, "compositor._needFullRedraw = true");
        int reload = At(body, "ReloadAndRedraw(");
        Assert.True(latch < reload);   // and no ReloadAndRedraw( text, comment or code, above the latch
        Assert.True(reload < At(body, "config.AutoRedraw"));
    }

    [Fact]
    [Trait("Plan", "AC-no-idle-redraws-7")]
    [Trait("Plan", "EDGE-no-idle-redraws-10")]
    public void Refresh_and_cache_clear_forget_the_drawn_flag()
    {
        var trigger = Trigger();
        foreach (var signature in new[] { "public void RefreshAndRecomposite(", "public int ClearTextureCacheAndRecomposite(" })
        {
            var body = Method(trigger, signature);
            Assert.True(At(body, "ForgetEmptyOutputDrawn();") < At(body, "TriggerRecomposite("), signature);
        }
        var full = Method(trigger, "FullRefreshAsync(");
        var keep = full[At(full, "if (keepUpstreams)")..];
        Assert.True(At(keep, "ForgetEmptyOutputDrawn();") < At(keep, "TriggerRecomposite(\"managed-rebuild\""));

        var forget = Method(Publish(), "private void ForgetEmptyOutputDrawn(");
        var locked = forget[At(forget, "lock (_manifestLock)")..];
        Assert.Contains("_manifestWrites++", locked);
        Assert.Contains("_emptyOutputDrawn = false", locked);
    }

    [Fact]
    [Trait("Plan", "AC-no-idle-redraws-3")]
    [Trait("Plan", "EDGE-no-idle-redraws-11")]
    [Trait("Plan", "EDGE-no-idle-redraws-14")]
    public void A_preamble_skipped_for_no_overlays_keeps_the_snapshot_dirty()
    {
        var body = Method(Trigger(), "public void TriggerRecomposite(");
        int declared = At(body, "var preamble = RedrawHold.Preamble.None;");
        int freshBlock = At(body, "if (fresh != null)");
        Assert.True(declared < freshBlock);
        int plan = At(body, "RedrawHold.PlanPreamble(");
        Assert.True(freshBlock < plan);
        Assert.Matches(@"\(\) => discovery\.DiscoverEnabled\(\)\.Count > 0", body);
        Assert.True(freshBlock < At(body, "ReleaseTokenValid(releaseToken)"));
        Assert.Contains("if (preamble == RedrawHold.Preamble.Reload)", body);
        Assert.Contains("if (preamble != RedrawHold.Preamble.KeepDirty) _activeMtrlSnapshotDirty = false;", body);
    }

    [Fact]
    [Trait("Plan", "EDGE-no-idle-redraws-15")]
    public void KeepDirty_skips_saving_an_unchanged_snapshot()
    {
        var body = Method(Trigger(), "public void TriggerRecomposite(");
        var guard = Regex.Match(body, @"if \([^\n]*Preamble\.KeepDirty[^\n]*SetEquals\([^\n]*\)\s*\{");
        Assert.True(guard.Success, "no KeepDirty/SetEquals guard");
        var guarded = Method(body[guard.Index..], "if (");
        Assert.Contains("CachedActiveMaterialPaths", guarded);
        Assert.Contains("config.Save();", guarded);
    }

    [Fact]
    [Trait("Plan", "AC-no-idle-redraws-5")]
    [Trait("Plan", "EDGE-no-idle-redraws-16")]
    [Trait("Plan", "EDGE-no-idle-redraws-17")]
    public void A_post_settle_correction_rewalks_without_a_preamble_reload()
    {
        var check = Method(Source("Services/CompositorService.PostRedraw.cs"), "void SchedulePostRedrawBodyTypeCheck(");
        Assert.DoesNotContain("_activeMtrlSnapshotDirty = true", check);
        Assert.Contains("_activeMtrlSnapshotRewalk = true", check);

        var trigger = Method(Trigger(), "public void TriggerRecomposite(");
        Assert.Contains("_activeMtrlSnapshot == null || _activeMtrlSnapshotDirty || _activeMtrlSnapshotRewalk", trigger);
        Assert.Contains("bool wasDirty = _activeMtrlSnapshotDirty;", trigger);
        Assert.Contains("_activeMtrlSnapshotRewalk = false", trigger);
        Assert.Contains("_activeMtrlSnapshotRewalk = false",
            Method(Source("Services/CompositorService.Events.cs"), "private void OnLocalPlayerRedrawn("));
    }

    [Fact]
    [Trait("Plan", "AC-no-idle-redraws-6")]
    public void The_undo_redraw_is_guarded()
    {
        Assert.Matches(@"if \(AutoRefitDecisions\.UndoRedraws\(results\.Count, keys\.Count\)\)\s*compositor\.RedrawForChangedModel\(""auto refit undone""\);",
            Source("Services/AutoRefit/AutoRefitWatcher.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-no-idle-redraws-7")]
    [Trait("Plan", "EDGE-no-idle-redraws-20")]
    public void User_requested_redraws_are_untouched()
    {
        Assert.Contains("ReloadAndRedraw(\"Proteus output withdrawn\", userRequested: true)",
            Method(Source("Services/CompositorService.Overrides.cs"), "public void SetEnabled("));
        Assert.Contains("compositor.RestoreChangedAccessory()", Source("Gui/SettingsTab.cs"));
        Assert.Contains("penumbra.RedrawPlayer()", Source("Plugin.cs"));
        Assert.Equal(7, Regex.Matches(Source("Gui/PartsPanel.cs"), Regex.Escape("RedrawForChangedModel(\"Studio parts\")")).Count);
    }
}
