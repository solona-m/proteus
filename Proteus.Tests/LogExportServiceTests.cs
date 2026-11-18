using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Copy Logs writes a file meant to be posted: the whole of it — header, Proteus lines and the dalamud.log slice — is
/// scrubbed of the Windows user name, the character's name and every collection's, and reading the names never hangs it.
/// </summary>
public class LogExportServiceTests
{
    // [AC-mod-path-containment-7][EDGE-mod-path-containment-27]
    [Fact]
    public void ComposeExportBuildsTheFileAndScrubsEverySection()
    {
        var header = "Proteus log\r\nRefresh:   done\r\n".Replace("\r\n", Environment.NewLine);
        var text = ComposeExportOf(header, ["[Proteus] drew Jane Doe", "[Proteus] collection Rue Mod"],
                                   @"Penumbra: Jane Doe in Rue Mod from C:\Users\jane\AppData");

        Assert.DoesNotContain("Jane Doe", text);
        Assert.DoesNotContain("Rue Mod", text);
        Assert.DoesNotContain(@"\jane\", text);
        Assert.Contains("[Proteus] drew <CHARACTER>", text);
        Assert.Contains(@"Penumbra: <CHARACTER> in <COLLECTION> from C:\Users\<USER>\AppData", text);
        var nl = Environment.NewLine;
        Assert.StartsWith(header + nl + "==== Proteus, all levels (2 lines) ====" + nl, text);
        Assert.True(text.IndexOf("==== Proteus, all levels", StringComparison.Ordinal)
                    < text.IndexOf("==== dalamud.log, same window ====", StringComparison.Ordinal));
    }

    private static string ComposeExportOf(string header, IReadOnlyList<string> lines, string slice)
        => LogExportService.ComposeExport(header, lines, slice, null, ["Rue Mod"], "Jane Doe");

    // [AC-mod-path-containment-7][EDGE-mod-path-containment-28]
    [Fact]
    public async Task GatherNamesMergesStartAndEndAndToleratesFailure()
    {
        var start = await LogExportService.GatherNamesAsync(
            () => Task.FromResult<(IReadOnlyCollection<string>, string?)>((["Main", "Rue"], "Jane Doe")), null, TimeSpan.FromSeconds(5));
        var end = await LogExportService.GatherNamesAsync(
            () => Task.FromResult<(IReadOnlyCollection<string>, string?)>((["Alt"], "John Roe")), start, TimeSpan.FromSeconds(5));

        Assert.Equal(new HashSet<string> { "Main", "Rue", "Alt" }, end.Collections);
        Assert.Equal("John Roe", end.Character);

        var failed = await LogExportService.GatherNamesAsync(() => throw new InvalidOperationException("no framework"), start, TimeSpan.FromSeconds(5));
        Assert.Equal(start.Collections, failed.Collections);
        Assert.Equal("Jane Doe", failed.Character);

        var faulted = await LogExportService.GatherNamesAsync(
            () => Task.FromException<(IReadOnlyCollection<string>, string?)>(new InvalidOperationException()), start, TimeSpan.FromSeconds(5));
        Assert.Equal("Jane Doe", faulted.Character);

        var empty = await LogExportService.GatherNamesAsync(
            () => Task.FromResult<(IReadOnlyCollection<string>, string?)>(([], null)), null, TimeSpan.FromSeconds(5));
        Assert.Empty(empty.Collections);
        Assert.Null(empty.Character);
    }

    // [AC-mod-path-containment-7][EDGE-mod-path-containment-28] A framework thread that never answers costs the timeout,
    // never the export.
    [Fact]
    public async Task GatherNamesTimesOutAndKeepsEarlierNames()
    {
        var never = new TaskCompletionSource<(IReadOnlyCollection<string>, string?)>();
        var earlier = (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Main" }, (string?)"Jane Doe");
        var clock = Stopwatch.StartNew();

        var names = await LogExportService.GatherNamesAsync(() => never.Task, earlier, TimeSpan.FromMilliseconds(50));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
        Assert.Equal(["Main"], names.Collections);
        Assert.Equal("Jane Doe", names.Character);
    }

    // [AC-mod-path-containment-7][EDGE-mod-path-containment-28] A read given up on that faults later is observed, never an
    // unobserved task exception.
    [Fact]
    public void AbandonedNamesTaskFaultIsObserved()
    {
        var tag = "late names " + Guid.NewGuid().ToString("N");
        int unobserved = 0;
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerException is TimeoutException t && t.Message == tag) Interlocked.Increment(ref unobserved);
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            AbandonThenFault(tag);
            for (int i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    /// Everything that could root the abandoned task lives and dies in here, so the finalizer gets to judge it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AbandonThenFault(string tag)
    {
        var late = new TaskCompletionSource<(IReadOnlyCollection<string>, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        LogExportService.GatherNamesAsync(() => late.Task, null, TimeSpan.FromMilliseconds(10)).GetAwaiter().GetResult();
        late.SetException(new TimeoutException(tag));
        Thread.Sleep(50);   // any continuation runs
    }
}
