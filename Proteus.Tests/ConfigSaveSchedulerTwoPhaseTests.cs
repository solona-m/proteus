using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The two-phase settings save (stutter fixes, item 5): the text is made on the framework thread inside the gate, the
/// disk write runs on one background writer, and unload waits for that writer, then writes synchronously, last.
/// A user's log had 61 framework-thread saves at 12–104 ms each.
/// </summary>
public class ConfigSaveSchedulerTwoPhaseTests
{
    private sealed class Rig
    {
        public readonly object Gate = new();
        public readonly IPluginLog Log = Substitute.For<IPluginLog>();
        public readonly Queue<Action> Posted = new();
        public bool OnFramework = true;
        public int Serial;
        public readonly List<(bool Framework, bool InGate)> SerializeSeen = [];
        public readonly ConcurrentQueue<string> Persisted = new();
        public readonly ConcurrentQueue<string> Order = new();
        public readonly List<bool> PersistInGate = [];
        public Func<string, Task>? PersistBody;
        public int SyncSaves;
        public bool SyncInGate;
        public Action? SerializeBody;
        public readonly ConfigSaveScheduler Saver;

        public Rig()
        {
            Saver = new ConfigSaveScheduler(Gate,
                () =>
                {
                    SerializeSeen.Add((OnFramework, Monitor.IsEntered(Gate)));
                    SerializeBody?.Invoke();
                    return "json" + Interlocked.Increment(ref Serial);
                },
                async json =>
                {
                    lock (PersistInGate) PersistInGate.Add(Monitor.IsEntered(Gate));
                    if (PersistBody != null) await PersistBody(json).ConfigureAwait(false);
                    Persisted.Enqueue(json);
                    Order.Enqueue("bg:" + json);
                },
                () =>
                {
                    SyncSaves++;
                    SyncInGate = Monitor.IsEntered(Gate);
                    Order.Enqueue("sync");
                },
                () => OnFramework, a => Posted.Enqueue(a), Log);
        }

        public void WaitIdle()
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (Saver.Pending && DateTime.UtcNow < until) Thread.Sleep(5);
            Thread.Sleep(20);
        }
    }

    // [AC-stutter-fixes-8]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-8")]
    public void SerialiseOnFrameworkInGatePersistInBackground()
    {
        var rig = new Rig();
        rig.Saver.Save("ui");
        rig.WaitIdle();

        Assert.Single(rig.SerializeSeen);
        Assert.True(rig.SerializeSeen[0].Framework && rig.SerializeSeen[0].InGate);
        Assert.Equal(["json1"], rig.Persisted);
        Assert.All(rig.PersistInGate, inGate => Assert.False(inGate));
        Assert.Equal(0, rig.SyncSaves);
    }

    // [AC-stutter-fixes-8]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-8")]
    public void SlowSaveLogsSerialiseAndWriteSeparately()
    {
        var rig = new Rig
        {
            SerializeBody = () => Thread.Sleep((int)ConfigSaveScheduler.SlowWriteMs + 20),
        };
        rig.PersistBody = async _ => await Task.Delay((int)ConfigSaveScheduler.SlowWriteMs + 20).ConfigureAwait(false);
        rig.Saver.Save("ui");

        // One Warning (the first slow half), the other half at Debug: both named apart.
        var halves = new List<string>();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            halves.Clear();
            foreach (var c in rig.Log.ReceivedCalls())
            {
                var args = c.GetArguments();
                if (args.Length == 2 && args[0] is string fmt && fmt.Contains("settings {0} took")
                    && args[1] is object[] a && a.Length > 0 && a[0] is string what)
                    halves.Add(what);
            }
            if (halves.Contains("write")) break;
            Thread.Sleep(10);
        }
        Assert.Contains("serialise", halves);
        Assert.Contains("write", halves);
        // The off-thread write cannot hitch a frame: never a Warning.
        rig.Log.DidNotReceive().Warning(Arg.Any<string>(), Arg.Is<object[]>(a => a.Length > 0 && Equals(a[0], "write")));
    }

    // [AC-stutter-fixes-8]
    [Fact]
    [Trait("Plan", "AC-stutter-fixes-8")]
    public void UnloadWritesSynchronouslyWithTheFullSaveLast()
    {
        var rig = new Rig();
        rig.Saver.Save("ui");
        rig.Saver.FlushAndClose();

        Assert.Equal(1, rig.SyncSaves);
        Assert.True(rig.SyncInGate);
        var order = rig.Order.ToArray();
        Assert.Equal("sync", order[^1]);
    }

    // [EDGE-stutter-fixes-25]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-25")]
    public void UnloadWaitsForTheInFlightWriteWithoutTheGateThenSavesSync()
    {
        var rig = new Rig();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ManualResetEventSlim();
        rig.PersistBody = async _ => { started.Set(); await release.Task.ConfigureAwait(false); };
        rig.Saver.Save("ui");
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        var unload = new Thread(rig.Saver.FlushAndClose) { IsBackground = true };
        unload.Start();
        Thread.Sleep(50);
        // While unload waits on the writer, the gate is free (a worker can still take it).
        Assert.True(Monitor.TryEnter(rig.Gate, TimeSpan.FromSeconds(5)));
        Monitor.Exit(rig.Gate);
        Assert.Equal(0, rig.SyncSaves);

        release.SetResult();
        Assert.True(unload.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(["bg:json1", "sync"], rig.Order);
    }

    private sealed class SingleThreadContext : SynchronizationContext
    {
        // Anything posted here would never run: the only thread is blocked in FlushAndClose.
        public int PostedCount;
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref PostedCount);
        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref PostedCount);
    }

    // [EDGE-stutter-fixes-26]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-26")]
    public void UnloadFromASingleThreadedContextCompletes()
    {
        var rig = new Rig();
        rig.PersistBody = async _ => await Task.Delay(50).ConfigureAwait(false);
        bool done = false;
        var t = new Thread(() =>
        {
            var ctx = new SingleThreadContext();
            SynchronizationContext.SetSynchronizationContext(ctx);
            rig.Saver.Save("ui");
            rig.Saver.FlushAndClose();
            done = true;
        }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "unload deadlocked on a single-threaded context");
        Assert.True(done);
        Assert.Equal(1, rig.SyncSaves);
    }

    // [EDGE-stutter-fixes-27]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-27")]
    public void NoWriteIsIssuedAfterClose()
    {
        var rig = new Rig();
        rig.Saver.FlushAndClose();
        rig.Saver.Save("late framework");
        rig.OnFramework = false;
        rig.Saver.Save("late worker");
        Thread.Sleep(50);

        Assert.Empty(rig.Persisted);
        Assert.Empty(rig.Posted);
        Assert.Equal(1, rig.SyncSaves);
    }

    /// <summary>
    /// The real race: a framework save has serialised and is about to hand its text to the writer when unload closes.
    /// That older text must never land after unload's synchronous save.
    /// </summary>
    // [EDGE-stutter-fixes-27]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-27")]
    public void NoWriteIsIssuedAfterCloseEvenMidSerialise()
    {
        var rig = new Rig();
        var inSerialize = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        rig.SerializeBody = () => { inSerialize.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        var save = new Thread(() => rig.Saver.Save("ui")) { IsBackground = true };
        save.Start();
        Assert.True(inSerialize.Wait(TimeSpan.FromSeconds(10)));

        // Unload starts while the serialise holds the gate: it closes the writer, then waits for the gate.
        var unload = new Thread(rig.Saver.FlushAndClose) { IsBackground = true };
        unload.Start();
        // Until unload is blocked on the gate the save holds: it has closed the writer by then.
        var until = DateTime.UtcNow.AddSeconds(10);
        while ((unload.ThreadState & ThreadState.WaitSleepJoin) == 0 && DateTime.UtcNow < until) Thread.Yield();
        release.Set();
        Assert.True(save.Join(TimeSpan.FromSeconds(10)));
        Assert.True(unload.Join(TimeSpan.FromSeconds(10)));
        Thread.Sleep(50);

        Assert.Empty(rig.Persisted);              // the serialised text was never issued
        Assert.Equal(["sync"], rig.Order);
    }

    // [EDGE-stutter-fixes-28]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-28")]
    public void AFailedBackgroundWriteStaysPending()
    {
        var rig = new Rig();
        int calls = 0;
        rig.PersistBody = _ => Interlocked.Increment(ref calls) == 1
                                   ? Task.FromException(new System.IO.IOException("disk full"))
                                   : Task.CompletedTask;
        rig.Saver.Save("ui");
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref calls) < 1 && DateTime.UtcNow < until) Thread.Sleep(5);
        Thread.Sleep(30);
        Assert.True(rig.Saver.Pending);
        rig.Log.Received(1).Error(Arg.Any<Exception>(), Arg.Any<string>(), Arg.Any<object[]>());

        // A posted (deferred) tick with no new change still writes, because the last write failed.
        rig.OnFramework = false;
        rig.Saver.Save("worker");
        rig.OnFramework = true;
        while (rig.Posted.Count > 0) rig.Posted.Dequeue()();
        rig.WaitIdle();
        Assert.False(rig.Saver.Pending);
        Assert.Equal(["json2"], rig.Persisted);
    }

    // [EDGE-stutter-fixes-29]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-29")]
    public void LatestPayloadWins()
    {
        var rig = new Rig();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new ManualResetEventSlim();
        int n = 0;
        rig.PersistBody = async _ =>
        {
            if (Interlocked.Increment(ref n) == 1) { firstStarted.Set(); await release.Task.ConfigureAwait(false); }
        };
        rig.Saver.Save("ui");   // json1, held in flight
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10)));
        rig.Saver.Save("ui");   // json2, queued
        rig.Saver.Save("ui");   // json3, replaces json2
        release.SetResult();
        rig.WaitIdle();

        Assert.Equal(["json1", "json3"], rig.Persisted);
    }

    // [EDGE-stutter-fixes-44]
    [Fact]
    [Trait("Plan", "EDGE-stutter-fixes-44")]
    public void FlushWaitsOnTheNewestIssuedWrite()
    {
        var rig = new Rig();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new ManualResetEventSlim();
        var secondStarted = new ManualResetEventSlim();
        int n = 0;
        rig.PersistBody = async _ =>
        {
            if (Interlocked.Increment(ref n) == 1) { firstStarted.Set(); await first.Task.ConfigureAwait(false); }
            else { secondStarted.Set(); await second.Task.ConfigureAwait(false); }
        };
        rig.Saver.Save("ui");
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(10)));
        rig.Saver.Save("ui");
        first.SetResult();
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));

        var unload = new Thread(rig.Saver.FlushAndClose) { IsBackground = true };
        unload.Start();
        Thread.Sleep(50);
        Assert.Equal(0, rig.SyncSaves);   // still waiting on the second (newest) write
        second.SetResult();
        Assert.True(unload.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(["bg:json1", "bg:json2", "sync"], rig.Order);
    }
}
