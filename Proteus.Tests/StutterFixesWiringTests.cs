using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The stutter fixes' wiring, pinned by reading the source (CompositorService cannot be built in a test; the pattern
/// of <see cref="IdleRedrawWiringTests"/>). Textual and deliberately so: change these with the code.
/// </summary>
public class StutterFixesWiringTests
{
    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");

    private static string Source(string relative) => File.ReadAllText(Path.Combine(ProteusDir(), relative));

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

    private static void Before(string body, string first, string then)
    {
        int a = body.IndexOf(first, System.StringComparison.Ordinal), b = body.IndexOf(then, System.StringComparison.Ordinal);
        Assert.True(a >= 0, $"no {first}");
        Assert.True(b >= 0, $"no {then}");
        Assert.True(a < b, $"{first} must come before {then}");
    }

    private static string Carriers => Source("Services/CompositorService.Carriers.cs");
    private static string Run => Source("Services/CompositorService.CompositeRun.cs");
    private static string PostRedraw => Source("Services/CompositorService.PostRedraw.cs");
    private static string Bridge => Source("Interop/GlamourerBridge.cs");

    // [AC-stutter-fixes-1]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    public void ReconcileOneCarrierRewalksBeforeARetry()
    {
        var body = Method(Carriers, "private void ReconcileOneCarrier(");
        Before(body, "EquipAction.RewalkFirst", "RefreshEquippedModels()");
        Before(body, "RefreshEquippedModels()", "TryBeginAsk(");
    }

    // [AC-stutter-fixes-2] [EDGE-stutter-fixes-9]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-2")]
    [Trait("Plan", "EDGE-stutter-fixes-9")]
    public void CarrierWritesBracketTheInvokeWithAThreadStaticCounter()
    {
        Assert.Matches(@"\[ThreadStatic\]\s*private static int ownCarrierWriteDepth;", Bridge);
        foreach (var sig in new[] { "public bool SetAccessory(", "public bool SetGlasses(" })
        {
            var body = Method(Bridge, sig);
            Assert.Matches(@"ownCarrierWriteDepth\+\+;\s*try \{ ec = set(Bonus)?Item\.Invoke\([^;]*\); \}\s*finally \{ ownCarrierWriteDepth--; \}", body);
        }
        var on = Method(Bridge, "private void OnStateChanged(");
        Before(on, "LocalPlayerStateChangedAny?.Invoke", "GlamourerSignals.RaisesEquipmentChange(");
    }

    // [AC-stutter-fixes-3] [EDGE-stutter-fixes-19]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-3")]
    [Trait("Plan", "EDGE-stutter-fixes-19")]
    public void VacatedCarriersComeOffBetweenSupersedeAndManifestWrite()
    {
        var body = Method(Run, "private bool CommitLocators(");
        Before(body, "compositor.Superseded(epoch)", "compositor.ReleaseVacatedCarriers(");
        Before(body, "compositor.ReleaseVacatedCarriers(", "compositor.WriteManagedModJson(");
        // Not inside a lock on the config gate: it blocks on the framework thread.
        int call = body.IndexOf("compositor.ReleaseVacatedCarriers(", System.StringComparison.Ordinal);
        int lastLock = body.LastIndexOf("lock (compositor.config.Gate)", call, System.StringComparison.Ordinal);
        if (lastLock >= 0)
        {
            var lockBody = Method(body[lastLock..], "lock (compositor.config.Gate)");
            Assert.True(lastLock + lockBody.Length < call, "ReleaseVacatedCarriers runs inside the config gate");
        }
    }

    // [AC-stutter-fixes-4] [EDGE-stutter-fixes-23] [EDGE-stutter-fixes-24]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    [Trait("Plan", "EDGE-stutter-fixes-23")]
    [Trait("Plan", "EDGE-stutter-fixes-24")]
    public void ReChecksAreScheduledAndKeyedOnProbe()
    {
        var check = Method(PostRedraw, "private async Task CheckShellDrawnAsync(");
        Before(check, "CarrierRecheck(expected, reloadWithheld, timeline, recheck, hostGone, afterOwnRedraw, extra)", "ride a ");
        Assert.Contains("ScheduleShellRecheck(expected, reloadWithheld, timeline, recheck + 1", check);
        var schedule = Method(PostRedraw, "private void ScheduleShellRecheck(");
        // Issue sweep D: the superseded test moved into InFlight.ScheduleAsync's proceed (same meaning, new spelling).
        Assert.Contains("() => !_disposed && ReferenceEquals(_shellDrawnCheck, expected)", schedule);
        var recheck = Method(PostRedraw, "private bool CarrierRecheck(");
        // Issue sweep D: the carrier notice is named (PrintShellNotice), still behind the same once-key.
        Before(recheck, "string.Equals(_carrierUndrawnNoticeKey, probeKey", "PrintShellNotice(");
        Assert.Contains("var probeKey = ShellProbeKey(expected);", recheck);
    }

    // [AC-stutter-fixes-4]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    public void DeferralIsEvaluatedBeforeTheCapWarning()
    {
        var recheck = Method(PostRedraw, "private bool CarrierRecheck(");
        // The slot's last ask is read at re-check time, inside the verdict, and Schedule/Info come before Warn.
        Before(recheck, "long lastAsk = _carrierAsks.LastAskTick(slot);", "ShellRecheck.Next(");
        Before(recheck, "ShellRecheck.Verdict.Schedule", "ShellRecheck.Verdict.Warn");
        Before(recheck, "ShellRecheck.Verdict.Info", "log.Warning(");
    }

    // [AC-stutter-fixes-1] [EDGE-stutter-fixes-39]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    [Trait("Plan", "EDGE-stutter-fixes-39")]
    public void ConfirmationIsStampedAndComparedWithRedrawAndAsk()
    {
        var check = Method(PostRedraw, "private async Task CheckShellDrawnAsync(");
        Before(check, "_shellConfirmedDrawnKey = ShellProbeKey(expected);", "Interlocked.Exchange(ref _shellConfirmedDrawnAtTick");
        var since = Method(Carriers, "private bool ShellConfirmedSince(");
        Assert.Contains("CarrierDecisions.ConfirmationCounts(Interlocked.Read(ref _shellConfirmedDrawnAtTick), lastAskTick,", since);
        Assert.Contains("Interlocked.Read(ref _lastObservedRedrawTick)", since);
        Assert.Contains("ShellConfirmedSince(lastAsk)", Method(Carriers, "private void ReconcileOneCarrier("));
    }

    // [AC-stutter-fixes-1] [EDGE-stutter-fixes-40] [EDGE-stutter-fixes-47]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    [Trait("Plan", "EDGE-stutter-fixes-40")]
    [Trait("Plan", "EDGE-stutter-fixes-47")]
    public void OnlyReconcileAndVerifySelfSchedulesTheRetry()
    {
        var body = Method(Carriers, "private void ReconcileOneCarrier(");
        Assert.Contains("if (selfScheduleRetry) ScheduleCarrierRetry(", body);
        Assert.Contains("selfScheduleRetry: true", Method(Run, "private void ReconcileAndVerify("));
        // Everywhere else the reconcile is called without it.
        var all = Regex.Matches(Source("Services/CompositorService.Events.cs") + Run + Carriers,
                                @"ReconcileEmperorRing\([^;]*\);").Select(m => m.Value).ToList();
        Assert.Single(all, c => c.Contains("selfScheduleRetry: true"));
    }

    // [AC-stutter-fixes-6] [EDGE-stutter-fixes-48]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-6")]
    [Trait("Plan", "EDGE-stutter-fixes-48")]
    public void RetryTimerReArmsInsteadOfReturning()
    {
        var body = Method(Carriers, "private void OnCarrierRetry(");
        Assert.Matches(@"if \(Volatile\.Read\(ref _compositesInFlight\) > 0\)\s*\{\s*foreach \(var slot in slots\) ScheduleCarrierRetry\(", body);
    }

    /// <summary>QA F1: a hosted ring the drawn check sees gone, never asked for, gets its ask (and a fresh walk).</summary>
    // [AC-stutter-fixes-4]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    public void AHostedRingNeverAskedForIsAskedFromTheDrawnCheck()
    {
        var recheck = Method(PostRedraw, "private bool CarrierRecheck(");
        Before(recheck, "_carrierAsks.Count(slot, epochNow) == 0 && !_carrierRetry.PendingFor(slot)", "RefreshEquippedModels");
        Before(recheck, "RefreshEquippedModels", "foreach (var slot in owed) ScheduleCarrierRetry(0, slot);");
        Assert.Contains("recheck < 2", recheck);
    }

    /// <summary>QA F3: the ring retry does not re-ask the glasses.</summary>
    // [AC-stutter-fixes-1]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    public void TheRingRetryLeavesTheGlassesAlone()
    {
        var body = Method(Carriers, "private void OnCarrierRetry(");
        Before(body, "if (slots.Contains(\"glasses\"))", "ReconcileInvisibleGlasses(");
    }

    /// <summary>QA: a recorded ring the (stale) walk cannot see is still taken off at unload when Glamourer holds it.</summary>
    // [AC-stutter-fixes-3]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-3")]
    public void UnloadAsksGlamourerWhenTheWalkCannotSeeOurRing()
    {
        var body = Method(Carriers, "public void RemoveInjectedRing(");
        Before(body, "bool ours = recorded && (IsOurRingWorn(slot, r.ModelSet) || !AccessorySnapshotKnown);",
               "if (recorded && !ours && GlamourerSlotItem(eqdpSlot) == r.ItemId) ours = true;");
        Before(body, "GlamourerSlotItem(eqdpSlot) == r.ItemId", "SetAccessoryOnFramework(0, slot)");
    }

    // [EDGE-stutter-fixes-43]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-43")]
    public void ReleaseVacatedCarriersUsesALocalWalk()
    {
        var sweep = Method(Carriers, "private void SweepCarriers(") + Method(Carriers, "private bool? PostReadWalkShowsOurs(");
        Assert.DoesNotContain("RefreshEquippedModels(", sweep);
        Assert.DoesNotContain("ApplyEquippedModels(", sweep);
    }

    // [AC-stutter-fixes-3] [EDGE-stutter-fixes-46]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-3")]
    [Trait("Plan", "EDGE-stutter-fixes-46")]
    public void LocalWalkUsesHasBodySlotModel()
    {
        var body = Method(Carriers, "private bool? PostReadWalkShowsOurs(");
        Assert.Contains("CarrierDecisions.IsValidWalk(models,", body);
        Assert.Contains("DrawnModelPaths.HasBodySlotModel(", body);
    }

    /// <summary>Every assignment of the accessory walk map stamps its tick: a missed site makes walks look stale.</summary>
    // [AC-stutter-fixes-1]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-1")]
    public void AccessoryWalkIsStampedAtEveryAssignment()
    {
        int sites = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(ProteusDir(), "Services"), "CompositorService*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"_equippedAccessoryModels = [^;]+;\s*(?<next>[^\r\n]*)"))
            {
                sites++;
                Assert.Contains("NoteAccessoryWalk();", m.Groups["next"].Value);
            }
        Assert.True(sites >= 2, $"expected the two walk sites, found {sites}");
    }

    /// <summary>A shell whose only hosts are undrawn ring carriers is re-checked too (no echo composite does it).</summary>
    // [AC-stutter-fixes-4]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-4")]
    public void AnAllCarrierShellNeverBackIsReCheckedToo()
    {
        var check = Method(PostRedraw, "private async Task CheckShellDrawnAsync(");
        Before(check, "if (!hostEverDrawn)", "CarrierRecheck(expected, reloadWithheld, timeline, recheck, expected.Materials, afterOwnRedraw, extra)");
        Before(check, "CarrierRecheck(expected, reloadWithheld, timeline, recheck, expected.Materials, afterOwnRedraw, extra)", "the host accessory was not back");
    }

    // [AC-stutter-fixes-7]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-7")]
    public void GearVariantsFrameworkStepDoesOnlyGameReads()
    {
        var src = Source("Services/VariantRepoint/VariantRepointWatcher.cs");
        var raw = Method(src, "private RawWorn? ReadWornRaw(");
        Assert.Contains("GetActivePlayerResourceMap()", raw);
        Assert.Contains("EquippedSlotVariants.Read(", raw);
        Assert.DoesNotContain("GetAllModsQuiet", raw);
        Assert.DoesNotContain("WornItems(", raw);
        var read = Method(src, "private WornState? ReadWorn(");
        Assert.Contains("OnFramework(ReadWornRaw, \"read\")", read);
        Assert.Contains("GetAllModsQuiet()", read);
    }

    // [EDGE-stutter-fixes-38]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-38")]
    public void GearVariantsBlankWalkStillRetries()
    {
        var raw = Method(Source("Services/VariantRepoint/VariantRepointWatcher.cs"), "private RawWorn? ReadWornRaw(");
        Assert.Contains("Request(ShaderUpgradePlan.Signal.BlankRetry)", raw);
        Assert.Contains("DrawnModelPaths.HasBodySlotModel(models)", raw);
    }

    // [AC-stutter-fixes-9]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-9")]
    public void IdleEarlyOutSitsAfterDebounceBeforeSettle()
    {
        var src = Source("Services/CompositorService.Trigger.cs");
        var trigger = Method(src, "public void TriggerRecomposite(");
        Before(trigger, "var tDebounced = PhaseCounter.Begin();", "if (IdleEarlyOut(force)) return;");
        Before(trigger, "if (IdleEarlyOut(force)) return;", "WaitForRaceToSettle(");
        Before(trigger, "if (IdleEarlyOut(force)) return;", "WaitForDrawStateToSettle(");
        var early = Method(src, "private bool IdleEarlyOut(");
        Assert.Contains("RefreshBurstReasonsSnapshot()", early);
        Assert.Contains("_forcePending", early);
        Assert.Contains("_skinForcePending", early);
    }

    // [AC-stutter-fixes-10]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-10")]
    public void SkipLineCarriesTheSetupBreakdown()
    {
        Assert.Contains("recomposite skipped — inputs unchanged ({0:F0}ms; setup: {1})", Run);
        Assert.Contains("DescribeSetup(PhaseCounter.Begin())", Run);
        Assert.Contains("setup steps: {64}", Source("Services/CompositorService.BlendStats.cs"));
    }
}
