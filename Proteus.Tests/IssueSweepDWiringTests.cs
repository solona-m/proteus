using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Issue sweep D's wiring (#91, the owner's shell message, #96's harness job), pinned by reading the source:
/// CompositorService and the shell build cannot run in a test. Textual and deliberately so: change these with the code.
/// </summary>
public class IssueSweepDWiringTests
{
    private static string Root([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "..");
    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root(), "Proteus", relative));

    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
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

    private static void Before(string body, string first, string then)
    {
        int a = body.IndexOf(first, StringComparison.Ordinal), b = body.IndexOf(then, StringComparison.Ordinal);
        Assert.True(a >= 0, $"no {first}");
        Assert.True(b >= 0, $"no {then}");
        Assert.True(a < b, $"{first} must come before {then}");
    }

    private static string Recovery => Source("Services/CompositorService.ShellRecovery.cs");
    private static string PostRedraw => Source("Services/CompositorService.PostRedraw.cs");
    private static string CompositeRun => Source("Services/CompositorService.CompositeRun.cs");

    // ── #96 ──

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-1")]
    public void MatcapJob_maps_nothing_to_undo_to_no_reload()
    {
        // [AC-issue-sweep-d-1] A refusal (nothing-to-undo included) changes nothing, so there is no reload or redraw.
        var job = Source("TestHarness/MatcapJob.cs");
        Assert.Contains("bool changed = outcome.Ok || librarySetting != null;", job);
        Assert.Contains("if (outcome.Ok && choice?.Library is { } chosenLib)", job);
        Assert.Contains("outcome.Code == MatcapWriter.CodeNothing ? \"nothing-to-undo\"", job);
        Before(job, "bool changed = outcome.Ok", "if (changed)");
    }

    // ── #91: what counts ──

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-22")]
    public void Counting_sites_catch_only_OutputWriteException()
    {
        // [EDGE-issue-sweep-d-22] A failed source read or bad input is never counted.
        var bake = Source("Services/SecondSkinService.ShellTextureBake.cs");
        var set = Source("Services/SecondSkinService.ShellSetBuild.cs");
        var host = Source("Services/SecondSkinService.HostModelBuild.cs");
        Assert.Contains("catch (OutputWriteException ex)", bake);
        Assert.Contains("catch (OutputWriteException ex)", set);
        Assert.Contains("if (ex is OutputWriteException) CountWriteFailure(hIdx);", set);
        Assert.Contains("catch (OutputWriteException)", host);
        // The source is read as the argument, before WriteIfChanged runs: its failure is never wrapped.
        var copy = Method(Source("Services/SecondSkinService.cs"), "internal static bool CopyPackFile(");
        Assert.Contains("WriteIfChanged(dstDisk, File.ReadAllBytes(srcDisk))", copy);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-6")]
    public void Counted_at_the_drop_not_in_WriteShellSlot()
    {
        // [EDGE-issue-sweep-d-6] The parallel pre-pass never counts; the slot loop counts at the drop.
        var ss = Source("Services/SecondSkinService.cs");
        Assert.DoesNotContain("CountWriteFailure", Method(ss, "private WriteOutcome WriteShellSlot("));
        var bake = Source("Services/SecondSkinService.ShellTextureBake.cs");
        var parallel = bake[bake.IndexOf("Parallel.For(", StringComparison.Ordinal)..bake.IndexOf("private void AssignSlots()", StringComparison.Ordinal)];
        Assert.DoesNotContain("CountWriteFailure", parallel);
        Assert.Contains("if (texWriteFailed) build.CountWriteFailure(hIdx);", bake);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-36")]
    public void Mtrl_write_failure_drops_only_its_layer()
    {
        // [EDGE-issue-sweep-d-36]
        var assign = Method(Source("Services/SecondSkinService.ShellTextureBake.cs"), "private void AssignSlots()");
        var at = assign.IndexOf("try { build.shellChanged |= WriteIfChanged(matDisk, mtrl); }", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var after = assign[at..];
        Before(after, "build.CountWriteFailure(hIdx);", "continue;");
        Before(after, "continue;", "build.redirects[matGamePath]");
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-40")]
    public void Dropped_host_loses_its_material_redirects()
    {
        // [EDGE-issue-sweep-d-40]
        var publish = Method(Source("Services/SecondSkinService.HostModelBuild.cs"), "private void PublishModel()");
        var c = publish[publish.IndexOf("catch (OutputWriteException)", StringComparison.Ordinal)..];
        Assert.Contains(".EndsWith(\".mtrl\"", c);
        Assert.Contains("{host.Prefix}{host.SetId:D4}/", c);
        Assert.Contains("_{host.Slot}_", c);
        Before(c, "build.redirects.Remove(k)", "throw;");
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-5")]
    public void Build_hands_failures_back_on_every_path()
    {
        // [AC-issue-sweep-d-5] In a finally, so an early return or a throw still hands the count back.
        var build = Method(Source("Services/SecondSkinService.cs"), "internal Result? Build(");
        Assert.Contains("try { return build.Run(); }", build);
        Assert.Contains("finally { writes = build.WriteFailures(); }", build);
        var phase = Source("Services/CompositorService.ShellPhase.cs");
        Assert.Contains("out var writes", phase);
        Assert.Contains("run.shellWriteFailures += writes.Count;", phase);
        Assert.Contains("catch (OutputWriteException ex)", phase);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-8")]
    public void Skin_write_failures_counted_and_skin_publish_guarded()
    {
        // [EDGE-issue-sweep-d-8]
        var mat = Source("Services/CompositorService.MaterialComposite.cs");
        Assert.Contains("if (outcome == WriteOutcome.OutputWriteFailed) Interlocked.Increment(ref run.skinWriteFailures);", mat);
        Assert.Equal(3, Regex.Matches(mat, @"WriteSkin\(base[DNM]").Count);
        var publish = Method(CompositeRun, "private void Publish()");
        Assert.Contains("compositor._lastSkinPublish = keepSkin", publish);
    }

    // ── #91: latch, gate, timer ──

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-10")]
    public void Latch_only_in_Publish()
    {
        // [EDGE-issue-sweep-d-10] A superseded run never reaches Publish, so the latch is untouched.
        Assert.Single(Regex.Matches(CompositeRun, @"RecordPublishOutcome\("));
        Assert.Contains("RecordPublishOutcome(", Method(CompositeRun, "private void Publish()"));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-5")]
    public void Publish_records_through_the_latch()
    {
        // [AC-issue-sweep-d-5] No bare fingerprint assignment; stamp first, then the latch.
        var publish = Method(CompositeRun, "private void Publish()");
        Assert.DoesNotContain("_lastCompositeFingerprint = fingerprint", publish);
        Before(publish, "Interlocked.Increment(ref compositor._publishStamp)", "RecordPublishOutcome(");
        var record = Method(Recovery, "private OutputWriteRetry.Decision RecordPublishOutcome(");
        Before(record, "bool autoRedraw = AutoRedrawOn;", "willSchedule: autoRedraw");
        Before(record, "_writeRetry.OnPublished(", "OutputWriteRetry.Record(");
        Before(record, "OutputWriteRetry.Record(", "ScheduleWriteRetry(");
        Assert.Contains("compositor._shellOutputIncomplete", CompositeRun);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-24")]
    [Trait("Plan", "EDGE-issue-sweep-d-39")]
    [Trait("Plan", "EDGE-issue-sweep-d-12")]
    public void Retry_trigger_uses_keepHighlight_and_deferWhileHeld()
    {
        // [EDGE-issue-sweep-d-24] [EDGE-issue-sweep-d-39] [EDGE-issue-sweep-d-12]
        var schedule = Method(Recovery, "private void ScheduleWriteRetry(");
        Assert.Contains("TriggerRecomposite(\"output-write-retry\", force: false, deferWhileHeld: true, keepHighlight: true)", schedule);
        Assert.Contains("redrawHold.IsHeld", schedule);
        Assert.Contains("_recompositesPending", schedule);
        Assert.Contains("if (!keepHighlight) Highlighter?.Clear();", Source("Services/CompositorService.Trigger.cs"));
        var record = Method(Recovery, "private OutputWriteRetry.Decision RecordPublishOutcome(");
        Before(record, "case OutputWriteRetry.Kind.Retry when autoRedraw:", "ScheduleWriteRetry(");
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-43")]
    public void Checks_and_timers_go_through_InFlight()
    {
        // [EDGE-issue-sweep-d-43]
        Assert.Contains("InFlight.ScheduleAsync(_shellChecksInFlight", Method(PostRedraw, "private void SchedulePostRedrawShellCheck("));
        Assert.Contains("InFlight.ScheduleAsync(_shellChecksInFlight", Method(PostRedraw, "private void ScheduleShellRecheck("));
        Assert.Contains("InFlight.ScheduleAsync(_writeRetryTimers", Method(Recovery, "private void ScheduleWriteRetry("));
        Assert.DoesNotContain("Interlocked.Increment(ref _shellChecksInFlight", PostRedraw + Recovery);
        Assert.DoesNotContain("Task.Run(", Method(PostRedraw, "private void ScheduleShellRecheck("));
    }

    // ── give-up chat ──

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-11")]
    public void Giveup_with_warn_prints_the_named_notice_for_failed_hosts()
    {
        // [AC-issue-sweep-d-11]
        var record = Method(Recovery, "private OutputWriteRetry.Decision RecordPublishOutcome(");
        var giveUp = record[record.IndexOf("case OutputWriteRetry.Kind.GiveUp:", StringComparison.Ordinal)..];
        Before(giveUp, "if (!d.Warn)", "log.Warning(");
        Assert.Contains("if (failedHosts.Count > 0) PrintShellNotice(failedHosts, partly: true);", giveUp);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-31")]
    public void No_hosts_no_chat()
    {
        // [EDGE-issue-sweep-d-31]
        var print = Method(Recovery, "private void PrintShellNotice(");
        Before(print, "if (hostModels.Count == 0) return;", "ChatGui.Print");
    }

    // ── the undrawn shell ──

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-7")]
    public void Undrawn_shell_redraws_before_any_chat()
    {
        // [AC-issue-sweep-d-7] The two failure branches decide; only the withheld-reload notice prints from the check itself.
        var check = Method(PostRedraw, "private async Task CheckShellDrawnAsync(");
        Assert.Equal(2, Regex.Matches(check, @"DecideUndrawn\(").Count);
        Assert.Single(Regex.Matches(check, @"ChatGui\.Print"));
        var decide = Method(Recovery, "private void DecideUndrawn(");
        Before(decide, "case ShellNotDrawnNotice.Step.AutoRedraw:", "RedrawPlayerLogged(");
        Assert.Contains("penumbra.RedrawPlayer", decide);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-16")]
    public void Confirmed_draw_clears_the_once_keys()
    {
        // [EDGE-issue-sweep-d-16]
        var check = Method(PostRedraw, "private async Task CheckShellDrawnAsync(");
        Before(check, "_shellConfirmedDrawnKey = ShellProbeKey(expected);", "ClearUndrawnKeys(expected);");
        var clear = Method(Recovery, "private void ClearUndrawnKeys(");
        Assert.Contains("_shellAutoRedrawKey = null", clear);
        Assert.Contains("_shellNotDrawnNoticeKey = null", clear);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-18")]
    public void Unreached_redraw_notifies_now()
    {
        // [EDGE-issue-sweep-d-18]
        var decide = Method(Recovery, "private void DecideUndrawn(");
        Assert.Contains("if (reached)", decide);
        Before(decide, "ScheduleShellRecheck(expected, reloadWithheld, null, 0, 0, afterOwnRedraw: true, extra: 0);", "NotifyUndrawn(expected, missing, partly);");
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-28")]
    public void Every_terminal_return_after_own_redraw_goes_through_EndAfterOwnRedraw()
    {
        // [EDGE-issue-sweep-d-28] No read, inconclusive, every host gone, a host gone, carrier still settling.
        var check = Method(PostRedraw, "private async Task CheckShellDrawnAsync(");
        Assert.Equal(5, Regex.Matches(check, @"EndAfterOwnRedraw\(expected, reloadWithheld, afterOwnRedraw, extra\)").Count);
        var end = Method(Recovery, "private bool EndAfterOwnRedraw(");
        Before(end, "if (extra < 1)", "NotifyUndrawn(");
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-29")]
    public void CarrierRecheck_passes_afterOwnRedraw_and_names_the_carrier()
    {
        // [EDGE-issue-sweep-d-29]
        var recheck = Method(PostRedraw, "private bool CarrierRecheck(");
        Assert.Equal(2, Regex.Matches(recheck, @"ScheduleShellRecheck\([^;]*afterOwnRedraw, extra\)").Count);
        Assert.Contains("EndAfterOwnRedraw(expected, reloadWithheld, afterOwnRedraw, extra);", recheck);
        Before(recheck, "_carrierUndrawnNoticeKey = probeKey;", "PrintShellNotice(");
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-9")]
    public void Withheld_branch_unchanged()
    {
        // [AC-issue-sweep-d-9] Auto redraw off and the shell needed the reload: the existing notice, once per shell.
        var check = Method(PostRedraw, "private async Task CheckShellDrawnAsync(");
        var withheld = check[check.IndexOf("if (reloadWithheld)", StringComparison.Ordinal)..];
        Before(withheld, "_redrawWithheldNoticeKey = probeKey;", "Chat.ShellNeedsRedraw");
        Before(check, "if (reloadWithheld)", "DecideUndrawn(");
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-10")]
    public void Old_shell_chat_keys_are_gone()
    {
        // [AC-issue-sweep-d-10]
        var proteus = Path.Combine(Root(), "Proteus");
        foreach (var f in Directory.EnumerateFiles(proteus, "*.cs", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(proteus, "Localization"), "*.json")))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("Chat.ShellMaterialFailed", text);
            Assert.DoesNotContain("Chat.ShellNotDrawing", text);
        }
        foreach (var loc in new[] { "en", "de", "es", "fr", "ja", "ko", "ru", "zh" })
        {
            var json = File.ReadAllText(Path.Combine(proteus, "Localization", loc + ".json"));
            foreach (var key in new[] { "Chat.ShellPartNotDrawn.Fmt", "Chat.ShellNotDrawn.Fmt", "Chat.ShellCarrierNotDrawn.Fmt", "Chat.ShellNotDrawnRedrawOff.Fmt" })
                Assert.Contains($"\"{key}\"", json);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-38")]
    public void Prune_delete_survives_a_locked_file()
    {
        // [EDGE-issue-sweep-d-38]
        Assert.Contains("try { File.Delete(f); } catch { }", Source("Services/CompositorService.Publish.cs"));
    }

    // ── harness hooks and the override ──

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-14")]
    [Trait("Plan", "EDGE-issue-sweep-d-34")]
    public void Fault_filter_and_heal_sit_where_they_must()
    {
        // [AC-issue-sweep-d-14] [EDGE-issue-sweep-d-34] Just before the composite's manifest write, and before our redraw.
        var commit = CompositeRun;
        Before(commit, "#if PROTEUS_TEST_HARNESS", "compositor.HarnessFilterPublish(redirects);");
        Before(commit, "compositor.HarnessFilterPublish(redirects);", "compositor.WriteManagedModJson(redirects, manipulations);");
        var decide = Method(Recovery, "private void DecideUndrawn(");
        Before(decide, "HarnessBeforeOwnShellRedraw(expected);", "RedrawPlayerLogged(");
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-41")]
    public void Heal_rebuilds_from_the_live_manifest_with_manipulations()
    {
        // [EDGE-issue-sweep-d-41]
        var heal = Method(Source("Services/CompositorService.Harness.cs"), "private void HarnessBeforeOwnShellRedraw(");
        Before(heal, "CompositeBusy || !ReferenceEquals(_shellDrawnCheck, expected)", "WriteManagedModJson(");
        Before(heal, "PenumbraModMeta.TryReadDefaultData(managedModDir)", "WriteManagedModJson(");
        Assert.Contains("WriteManagedModJson(healed, live.Manipulations);", heal);
        Before(heal, "WriteManagedModJson(healed, live.Manipulations);", "RecordPublish(healed)");
        Before(heal, "RecordPublish(healed)", "WaitForManifestLive(healed");
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-d-15")]
    public void Compositor_reads_AutoRedrawOn()
    {
        // [AC-issue-sweep-d-15] Every compositor read goes through the override.
        foreach (var f in new[] { "CompositeRun", "Publish", "Trigger", "PostRedraw" })
            Assert.DoesNotContain("config.AutoRedraw", Source($"Services/CompositorService.{f}.cs"));
        const string definition = "internal bool AutoRedrawOn => HarnessAutoRedrawOverride ?? config.AutoRedraw;";
        Assert.Contains(definition, Recovery);
        Assert.DoesNotContain("config.AutoRedraw", Recovery.Replace(definition, ""));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-d-45")]
    public void Qa_guide_documents_not_exercised_and_rate_limit()
    {
        // [EDGE-issue-sweep-d-45]
        var doc = File.ReadAllText(Path.Combine(Root(), "docs", "agent-rules", "adversarial-qa.md"));
        Assert.Contains("**Not exercised is never a pass.**", doc);
        Assert.Contains("**Automatic redraws are limited to 2 per 5 minutes**", doc);
    }
}
