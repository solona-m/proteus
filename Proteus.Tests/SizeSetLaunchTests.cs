using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The size set's launch, shared by the Studio's "Make every size" button and the QA test harness: the request it
/// prepares, and that the after-change is posted however a run ends. Always compiled: the Studio depends on it.
/// </summary>
public class SizeSetLaunchTests
{
    private static readonly IPluginLog Log = Substitute.For<IPluginLog>();
    private static readonly Dictionary<string, string> NoMods = new();

    private static SizeSetLaunch.Prepared Prepare(IReadOnlyList<string> keys, Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>?>? resolve = null,
                                                  bool yas = true, bool remake = false, bool ends = false, bool piercings = false,
                                                  string? madeOn = null)
        => SizeSetLaunch.Prepare(NoMods, @"C:\mods", resolve ?? (_ => null), @"C:\mods\Outfit", keys, yas, remake, ends,
                                 piercings, madeOn, _ => null, Log);

    [Fact]
    public void Prepare_RequestMatchesPanelInputs()
    {
        // [EDGE-qa-harness-51] The request is the panel's own: every setting carried, the mod's folder, no bodies yet.
        var asked = new List<string>();
        var prepared = Prepare(["rue", "tbse@women"], paths => { asked.AddRange(paths); return new Dictionary<string, string?>(); },
                               yas: false, remake: true, ends: false, piercings: true, madeOn: "Some Body");
        var r = prepared.Request;
        Assert.Equal(@"C:\mods\Outfit", r.ModRoot);
        Assert.Equal(["rue", "tbse@women"], r.Keys);
        Assert.False(r.Yas);
        Assert.True(r.Remake);
        Assert.False(r.Ends);
        Assert.True(r.Piercings);
        Assert.Equal("Some Body", r.MadeOn);
        Assert.Empty(r.Bodies);
        Assert.NotNull(r.Across);
        Assert.Equal(2, asked.Count);
        Assert.Equal(@"C:\mods", prepared.ModsRoot);
    }

    [Fact]
    public void Prepare_NoMensKeys_NeverAsksForSkeletons()
    {
        // [EDGE-qa-harness-51] Only a men's version needs the collection's skeletons.
        bool asked = false;
        var prepared = Prepare(["rue"], _ => { asked = true; return null; });
        Assert.False(asked);
        Assert.Null(prepared.Request.Across);
    }

    private sealed class Posts
    {
        public int Count;
        public int? Thread;
        public void Post(Action a)
        {
            Interlocked.Increment(ref Count);
            Thread = Environment.CurrentManagedThreadId;
            a();
        }
    }

    [Fact]
    public async Task Start_RunsPreparedRequest_ResultOf_AfterChangeOnFramework()
    {
        // [AC-qa-harness-8] The run gets the prepared request (with the index's bodies) and its result is ResultOf's.
        var prepared = Prepare(["rue"]);
        SizeSetRefit.Request? ran = null;
        int afterChanges = 0;
        var posts = new Posts();
        var outcome = await SizeSetLaunch.Start(prepared, new BodyModIndex(), null, Log, _ => { }, () => afterChanges++,
                                                posts.Post, CancellationToken.None,
                                                (request, runLog, _) => { ran = request; runLog.Notes.Add("left out"); return 3; });
        Assert.NotNull(ran);
        Assert.Equal(prepared.Request.ModRoot, ran!.ModRoot);
        Assert.Equal(3, outcome.Result.Made);
        Assert.Equal(["left out"], outcome.Result.Notes);
        Assert.Equal(SizeSetRefit.Summary.Added, outcome.Result.Summary);
        Assert.Equal(1, posts.Count);
        Assert.Equal(1, afterChanges);
    }

    [Fact]
    public async Task Success_AfterChangeOnce()
    {
        // [EDGE-qa-harness-51] Done: told once.
        var posts = new Posts();
        await SizeSetLaunch.Start(Prepare(["rue"]), new BodyModIndex(), null, Log, _ => { }, () => { }, posts.Post,
                                  CancellationToken.None, (_, _, _) => 0);
        Assert.Equal(1, posts.Count);
    }

    [Fact]
    public async Task RunThrows_AfterChangeStillCalled()
    {
        // [EDGE-qa-harness-51] A run that threw after its first save must still tell Penumbra.
        var posts = new Posts();
        var task = SizeSetLaunch.Start(Prepare(["rue"]), new BodyModIndex(), null, Log, _ => { }, () => { }, posts.Post,
                                       CancellationToken.None, (_, _, _) => throw new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal(1, posts.Count);
    }

    [Fact]
    public async Task CancelledMidRun_AfterChangeStillCalled()
    {
        // [EDGE-qa-harness-51] Stopped mid-run: told too.
        using var cts = new CancellationTokenSource();
        var posts = new Posts();
        var task = SizeSetLaunch.Start(Prepare(["rue"]), new BodyModIndex(), null, Log, _ => { }, () => { }, posts.Post,
                                       cts.Token, (_, _, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return 0; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, posts.Count);
    }

    [Fact]
    public async Task CancelBeforeStart_NoAfterChange_RunNeverCalled()
    {
        // [EDGE-qa-harness-42] A cancel before the worker starts: nothing ran, nothing written, no reload — the Studio's
        // behaviour, kept.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var posts = new Posts();
        bool ran = false;
        var task = SizeSetLaunch.Start(Prepare(["rue"]), new BodyModIndex(), null, Log, _ => { }, () => { }, posts.Post,
                                       cts.Token, (_, _, _) => { ran = true; return 0; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(ran);
        Assert.Equal(0, posts.Count);
    }

    [Fact]
    public async Task GcLatencyRestored_CandidatesForgotten()
    {
        // [EDGE-qa-harness-51] The run lowers GC latency for itself only; a throwing post never escapes the worker.
        var before = System.Runtime.GCSettings.LatencyMode;
        System.Runtime.GCLatencyMode during = before;
        await SizeSetLaunch.Start(Prepare(["rue"]), new BodyModIndex(), null, Log, _ => { }, () => { },
                                  _ => throw new ObjectDisposedException("framework"), CancellationToken.None,
                                  (_, _, _) => { during = System.Runtime.GCSettings.LatencyMode; return 0; });
        Assert.Equal(System.Runtime.GCLatencyMode.SustainedLowLatency, during);
        Assert.Equal(before, System.Runtime.GCSettings.LatencyMode);
    }
}
