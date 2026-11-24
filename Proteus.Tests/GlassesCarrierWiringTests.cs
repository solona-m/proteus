using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The face-over-body facewear reservation and the glasses carrier's wiring, pinned by reading the source (the
/// compositor and the shell build cannot be built in a test; the pattern of <see cref="StutterFixesWiringTests"/>).
/// Textual and deliberately so: change these with the code.
/// </summary>
public class GlassesCarrierWiringTests
{
    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");

    private static string Source(string relative) => File.ReadAllText(Path.Combine(ProteusDir(), relative));

    internal static string Method(string source, string signature)
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

    internal static void Before(string body, string first, string then)
    {
        int a = body.IndexOf(first, System.StringComparison.Ordinal), b = body.IndexOf(then, System.StringComparison.Ordinal);
        Assert.True(a >= 0, $"no {first}");
        Assert.True(b >= 0, $"no {then}");
        Assert.True(a < b, $"{first} must come before {then}");
    }

    private static string ShellSetBuild => Source("Services/SecondSkinService.ShellSetBuild.cs");
    private static string Allocation => Source("Services/ShellHostAllocation.cs");

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-10")]
    public void DisplacedBodyLineIsDeduplicated() // [EDGE-face-glasses-priority-10]
    {
        var body = Method(ShellSetBuild, "private void LogFacewearReservation(");
        Before(body, "LogLineChanged(ref service.lastFacewearDisplacedLog", "didn't fit because the facewear carrier");
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-11")]
    public void RunRerunsWithoutTheReservationWhenUnused() // [EDGE-face-glasses-priority-11]
    {
        var body = Method(Allocation, "public static State Run(");
        Before(body, "ReservationUnused(s)", "RunWith(null,");
        Before(body, "RunWith(null,", "ReservationDropped = true");
    }

    private static string Carriers => Source("Services/CompositorService.Carriers.cs");
    private static string Run => Source("Services/CompositorService.CompositeRun.cs");
    private static string ShellPhase => Source("Services/CompositorService.ShellPhase.cs");
    private static string Events => Source("Services/CompositorService.Events.cs");
    private static string Timeline => Source("Services/CompositorService.Timeline.cs");
    private static string Publish => Source("Services/CompositorService.Publish.cs");

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int at = haystack.IndexOf(needle, System.StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, System.StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    [Trait("Plan", "AC-face-glasses-priority-4")]
    public void ReconcileActsOnTheDecision() // [AC-face-glasses-priority-4]
    {
        var body = Method(Carriers, "private void ReconcileInvisibleGlasses(");
        Assert.Contains("DecideGlasses(", body);
        Assert.DoesNotContain("(shellBuilt && !hostedOnFacewear)", body);
        Assert.Contains("CarrierDecisions.Glasses(", Method(Carriers, "private CarrierDecisions.GlassesDecision DecideGlasses("));
    }

    [Fact]
    [Trait("Plan", "AC-face-glasses-priority-4")]
    public void FacewearComesFromPublishedHostsOnly() // [AC-face-glasses-priority-4] [EDGE-face-glasses-priority-21]
    {
        var body = Method(ShellPhase, "private void ApplyShells(");
        Assert.Contains("run.shellOnFacewear = shells.HostModelPaths.Any(", body);
        Assert.DoesNotContain("run.shellOnFacewear = hostsInUse", body);
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-21")]
    public void WriteFailedIsSetBeforeTheNullBuildCheck() // [EDGE-face-glasses-priority-21]
    {
        var build = Method(ShellPhase, "private void BuildAndApply(");
        Before(build, "run.failedShellHosts.Add(host)", "run.facewearWriteFailed = ");
        Assert.DoesNotContain("run.facewearWriteFailed = run", Method(ShellPhase, "private void ApplyShells("));
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-21")]
    public void StoredWriteFailedReachesEveryLastReconcile() // [EDGE-face-glasses-priority-21]
    {
        Assert.Contains("facewearWriteFailed: writeFailed", Events);
        Assert.Contains("LastFacewearWriteFailed", Events);
        Assert.Contains("facewearWriteFailed: LastFacewearWriteFailed", Method(Carriers, "private void OnCarrierRetry("));
        Assert.Contains("facewearWriteFailed: compositor.LastFacewearWriteFailed", Method(Run, "private bool GateUnchanged("));
    }

    [Fact]
    [Trait("Plan", "AC-face-glasses-priority-5")]
    public void GlassesComeOffBeforeTheManifestIsWritten() // [AC-face-glasses-priority-5] [EDGE-face-glasses-priority-15]
    {
        var body = Method(Run, "private bool CommitLocators(");
        Before(body, "compositor.ReleaseVacatedCarriers(", "compositor.ReleaseVacatedGlasses(");
        Before(body, "compositor.ReleaseVacatedGlasses(", "compositor.WriteManagedModJson(");
        // Outside the config gate: the release blocks on the framework thread.
        int release = body.IndexOf("compositor.ReleaseVacatedGlasses(", System.StringComparison.Ordinal);
        int gate = body.LastIndexOf("lock (compositor.config.Gate)", release, System.StringComparison.Ordinal);
        if (gate >= 0)
        {
            var between = body[gate..release];
            Assert.True(Count(between, "{") <= Count(between, "}"), "ReleaseVacatedGlasses must not run inside the config gate");
        }
    }

    [Fact]
    [Trait("Plan", "AC-face-glasses-priority-7")]
    public void RestingHidesTheFacewearCarrierFromTheChooser() // [AC-face-glasses-priority-7]
    {
        var gather = Method(ShellPhase, "private void GatherInputs(");
        var rest = gather[gather.IndexOf("run.glassesResting", System.StringComparison.Ordinal)..];
        Assert.Contains("invisibleGlassesSet = null;", rest);
        Assert.Contains("OurGlassesAreOn(restingGlasses)", gather);
        Assert.DoesNotContain("Resting(", ShellPhase);
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-27")]
    public void RestStateIsInTheShellFingerprint() // [AC-face-glasses-priority-7] [EDGE-face-glasses-priority-27]
    {
        var fp = Method(Timeline, "private string BuildCompositeFingerprint(");
        Assert.Contains("if (!skinOnly) sb.Append(\"glassesRest:\")", fp);

        var gate = Method(Run, "private bool GateUnchanged(");
        Assert.Equal(1, Count(Run, "_glassesChurn.Resting("));
        Before(gate, "_glassesChurn.Resting(", "glassesResting: glassesResting");
        int skinCall = gate.IndexOf("skinOnly: true", System.StringComparison.Ordinal);
        int skinStart = gate.LastIndexOf("BuildCompositeFingerprint(", skinCall, System.StringComparison.Ordinal);
        Assert.DoesNotContain("glassesResting", gate[skinStart..skinCall]);

        var reconcile = Method(Carriers, "private void ReconcileInvisibleGlasses(");
        Before(reconcile, "_glassesChurn.NoteFailureRemoval(", "TriggerRecomposite(\"glasses-rest-over\"");
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-29")]
    public void NoNewTriggerOnGlassesRemoval() // [EDGE-face-glasses-priority-29]
    {
        var reconcile = Method(Carriers, "private void ReconcileInvisibleGlasses(");
        int remove = reconcile.IndexOf("case CarrierDecisions.GlassesAction.Remove:", System.StringComparison.Ordinal);
        var removal = reconcile[remove..];
        Assert.Equal(1, Count(removal, "TriggerRecomposite("));
        Assert.Contains("TriggerRecomposite(\"glasses-rest-over\"", removal);
    }

    [Fact]
    [Trait("Plan", "AC-face-glasses-priority-8")]
    public void GapCheckRunsAfterEveryRedrawAndIsLogOnly() // [AC-face-glasses-priority-8] [EDGE-face-glasses-priority-28]
    {
        int at = Events.IndexOf("Put the carriers back now", System.StringComparison.Ordinal);
        var hook = Events[at..];
        var gateLine = hook[..hook.IndexOf('{')];
        Assert.Contains("modelWalkOk", gateLine);
        Assert.Contains("_compositesInFlight", gateLine);
        Assert.DoesNotContain("_secondSkinActive", gateLine);
        var shellBlock = Method(hook, "if (shellActive)");
        Assert.Contains("ReconcileInvisibleGlasses(", shellBlock);
        Assert.DoesNotContain("CheckGlassesRedirectGap(", shellBlock);
        Before(hook, "ReconcileInvisibleGlasses(", "CheckGlassesRedirectGap();");

        var check = Method(Carriers, "private void CheckGlassesRedirectGap(");
        Before(check, "OurGlassesAreOn(", "PublishedTarget(");
        Assert.Contains("LogLineChanged(", check);
        Assert.Contains("GlassesGapLogKey(", check);
        Assert.DoesNotContain("ChatGui", check);
        Assert.DoesNotContain("PrintShellNotice", check);
    }

    [Fact]
    [Trait("Plan", "AC-face-glasses-priority-8")]
    public void PublishSequenceIsBumpedAndReadUnderTheHistoryLock() // [AC-face-glasses-priority-8] [EDGE-face-glasses-priority-25]
    {
        var record = Method(Publish, "private IDictionary<string, string>? RecordPublish(");
        Before(record, "lock (_publishHistoryLock)", "_publishSeq++");
        var target = Method(Publish, "private string? PublishedTarget(");
        Before(target, "lock (_publishHistoryLock)", "publishSeq = _publishSeq");
    }

    [Fact]
    [Trait("Plan", "AC-face-glasses-priority-9")]
    public void LiveCheckUsesTryCanonicaliseAndNamesTheResolvedFile() // [AC-face-glasses-priority-9] [EDGE-face-glasses-priority-30]
    {
        var live = Method(Carriers, "private bool GlassesRedirectIsLive(");
        Assert.Contains("PublishedMetKeys(", live);
        Assert.Contains("TryCanonicalise(System.IO.Path.Combine(managedModDir, rel))", live);
        Assert.Contains("TryCanonicalise(penumbra.ResolvePlayer(key))", live);

        var decide = Method(Carriers, "private CarrierDecisions.GlassesDecision DecideGlasses(");
        Assert.Contains("redirectLive: () => liveCheck && GlassesRedirectIsLive(", decide);

        var reconcile = Method(Carriers, "private void ReconcileInvisibleGlasses(");
        var stop = reconcile[reconcile.IndexOf("case CarrierDecisions.GlassesAction.StopPolling:", System.StringComparison.Ordinal)..];
        Assert.Contains("liveResolved", stop[..stop.IndexOf("break;", System.StringComparison.Ordinal)]);
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-19")]
    public void RemoveOnlyJudgesTheRecordBeforeRemoving() // [EDGE-face-glasses-priority-19]
    {
        var body = Method(Carriers, "private void ReconcileInvisibleGlasses(");
        Before(body, "JudgeGlassesRecord(g)", "SetGlassesOnFramework(0)");
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-20")]
    public void RemoveOnlyReadsGlamourerOnlyWhenRemoving() // [EDGE-face-glasses-priority-20]
    {
        var body = Method(Carriers, "private void ReconcileInvisibleGlasses(");
        Before(body, "if (removeOnly", "JudgeGlassesRecord(g)");
        Before(body, "!= CarrierDecisions.GlassesAction.Remove)", "JudgeGlassesRecord(g)");
        Assert.Contains("liveCheck: false", body);
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-26")]
    public void SharedTicksUseInterlocked() // [EDGE-face-glasses-priority-26]
    {
        var src = Carriers;
        foreach (var field in new[] { "_lastGlassesInjectTick", "_glassesRemovedAt", "_glassesLivePolls", "_lastFacewearWriteFailed" })
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(src, $@"\b{field}\b"))
            {
                int lineStart = src.LastIndexOf('\n', m.Index) + 1;
                int lineEnd = src.IndexOf('\n', m.Index);
                var line = src[lineStart..lineEnd];
                if (line.TrimStart().StartsWith("private ") || line.TrimStart().StartsWith("//")) continue;   // the declaration
                Assert.True(line.Contains("Interlocked.") || line.Contains("Volatile."), $"{field} used without Interlocked/Volatile: {line.Trim()}");
            }
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-face-glasses-priority-12")]
    public void UnbuiltReservedHostIsCheckedAfterTheModelsAreBuilt() // [EDGE-face-glasses-priority-12]
    {
        var body = Method(ShellSetBuild, "private void BuildHostModels(");
        Before(body, "BuildHostModel(pushSweep, h)", "ShellHostAllocation.ReservedButUnbuilt(");
        Before(body, "ShellHostAllocation.ReservedButUnbuilt(", "LogLineChanged(ref service.lastFacewearUnbuiltLog");
    }
}
