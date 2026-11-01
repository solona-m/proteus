using System;
using System.Threading;
using System.Threading.Tasks;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class CaptureGateTests
{
    [Fact]
    public async Task NeverCompletes_TimesOut_GateReleased()
    {
        // [AC-qa-harness-7] [EDGE-qa-harness-29] A capture that never completes times out and frees the gate.
        var gate = new CaptureGate(TimeSpan.FromMilliseconds(100));
        var never = new TaskCompletionSource<int>();
        await Assert.ThrowsAsync<CaptureTimeoutException>(() => gate.Run(_ => never.Task));
        Assert.Equal(5, await gate.Run(_ => Task.FromResult(5)));
    }

    [Fact]
    public async Task LateResult_Disposed()
    {
        // [EDGE-qa-harness-29] The result that arrives after the timeout is handed to the disposer.
        var gate = new CaptureGate(TimeSpan.FromMilliseconds(50));
        var late = new TaskCompletionSource<string>();
        var disposed = new TaskCompletionSource<string>();
        await Assert.ThrowsAsync<CaptureTimeoutException>(() => gate.Run(_ => late.Task, s => disposed.TrySetResult(s)));
        late.SetResult("frame");
        Assert.Equal("frame", await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Concurrent_Busy()
    {
        // [EDGE-qa-harness-28]
        var gate = new CaptureGate(TimeSpan.FromSeconds(5));
        var slow = new TaskCompletionSource<int>();
        var first = gate.Run(_ => slow.Task);
        await Assert.ThrowsAsync<CaptureBusyException>(() => gate.Run(_ => Task.FromResult(1)));
        slow.SetResult(1);
        await first;
    }

    [Fact]
    public async Task TimeoutCancelsTheCapture()
    {
        var gate = new CaptureGate(TimeSpan.FromMilliseconds(50));
        CancellationToken seen = default;
        await Assert.ThrowsAsync<CaptureTimeoutException>(() => gate.Run(ct => { seen = ct; return Task.Delay(-1, ct).ContinueWith(_ => 0); }));
        Assert.True(seen.IsCancellationRequested);
    }
}
