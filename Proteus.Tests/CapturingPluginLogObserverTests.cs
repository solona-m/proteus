using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The log's line observer, which the QA test harness's /log listens on. Always compiled.</summary>
public class CapturingPluginLogObserverTests
{
    [Fact]
    public void Observer_GetsLinesOutsideSession_CaptureStillWorks_ThrowingObserverHarmless()
    {
        // [AC-qa-harness-6] Every rendered line reaches the observer, a capture session still records, and an observer
        // that throws never breaks logging.
        var log = new CapturingPluginLog(Substitute.For<IPluginLog>());
        var seen = new List<string>();
        log.LineObserver = seen.Add;

        log.Debug("first {0}", 1);
        Assert.Single(seen);
        Assert.Contains("[DBG] first 1", seen[0]);

        log.BeginCapture();
        log.Warning("second {0}", "x");
        var captured = log.EndCapture();
        Assert.Equal(2, seen.Count);
        Assert.Contains("[WRN] second \"x\"", seen[1]);   // Serilog quotes string arguments
        Assert.Single(captured);
        Assert.Contains("second \"x\"", captured[0]);

        log.LineObserver = _ => throw new InvalidOperationException("listener broke");
        log.Information("third");   // must not throw
        log.LineObserver = null;
        log.Information("fourth");
        Assert.Equal(2, seen.Count);
    }
}
