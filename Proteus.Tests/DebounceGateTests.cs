using System;
using System.Collections.Concurrent;
using System.Threading;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for <see cref="DebounceGate"/> — the debounce slot behind the recomposite trigger and the
/// Glamourer design watcher. Pure threading logic, no Dalamud or game data.
/// <para/>
/// The hammer tests are the regression guard for the crash this class was written to end: a token read
/// off a source another thread had already disposed threw ObjectDisposedException out of the plugin
/// constructor, so Dalamud reported the install as failed. Move the <c>current.Token</c> read outside
/// the gate's lock, or reinstate the <c>Dispose()</c> on the superseded source, and they fail.
/// </summary>
public class DebounceGateTests
{
    private const int Threads    = 8;
    private const int Iterations = 5000;

    /// Exercises a token the way real callers do: read its state, then register with its source — which
    /// is what Task.Delay(ms, token) and Task.Run(.., token) do internally, and what throws if the
    /// source behind the token has been disposed.
    private static void Use(CancellationToken token)
    {
        _ = token.IsCancellationRequested;
        token.Register(static () => { });
    }

    private static void Hammer(Action<int> body)
    {
        var errors  = new ConcurrentQueue<Exception>();
        var start   = new ManualResetEventSlim(false);
        var workers = new Thread[Threads];

        for (var t = 0; t < Threads; t++)
        {
            var index = t;
            workers[t] = new Thread(() =>
            {
                start.Wait();
                for (var i = 0; i < Iterations; i++)
                {
                    try { body(index); }
                    catch (Exception ex) { errors.Enqueue(ex); }
                }
            });
            workers[t].Start();
        }

        start.Set();
        foreach (var worker in workers) worker.Join();

        Assert.Empty(errors);
    }

    [Fact]
    public void Next_UnderContention_NeverThrows()
    {
        var gate = new DebounceGate();
        Hammer(_ => Use(gate.Next()));
    }

    /// Models Dispose racing a trigger: one thread stops the gate while the others keep asking for
    /// tokens. Every caller must come back with a cancelled token rather than an exception.
    [Fact]
    public void Next_RacingStop_NeverThrows()
    {
        var gate = new DebounceGate();
        Hammer(index =>
        {
            if (index == 0) gate.Stop();
            else            Use(gate.Next());
        });
    }

    [Fact]
    public void Next_CancelsTheRunItReplaced()
    {
        var gate = new DebounceGate();

        var first = gate.Next();
        Assert.False(first.IsCancellationRequested);

        var second = gate.Next();
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void Stop_CancelsInFlightAndEverythingAfter()
    {
        var gate = new DebounceGate();

        var inFlight = gate.Next();
        gate.Stop();

        Assert.True(inFlight.IsCancellationRequested);
        // A trigger arriving after teardown gets a pre-cancelled token, so it bails through its normal
        // cancellation path instead of starting work against a disposed service.
        Assert.True(gate.Next().IsCancellationRequested);
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        var gate = new DebounceGate();
        gate.Next();
        gate.Stop();
        gate.Stop();
    }
}
