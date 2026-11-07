using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// #74: the "no recomposite" line for an Edited event Proteus expected credited every reload to the live brush, though
/// the test harness, the hair watcher, auto refit and others call <c>ExpectOwnModEdit</c> too. Each caller now says who.
/// </summary>
public class OwnModEditTests
{
    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-8")]
    public void Line_names_who()
    {
        var line = CompositorService.OwnEditLine("Some Mod", "the test harness");
        Assert.Contains("Some Mod", line);
        Assert.Contains("the test harness's own save", line);
        Assert.DoesNotContain("live brush", line);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-8")]
    public void Every_caller_says_who()
    {
        var root = Path.Combine(Here(), "..", "Proteus");
        var calls = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\.ExpectOwnModEdit\(([^;]*)\);").Select(m => (f, m.Groups[1].Value)))
            .ToList();
        Assert.True(calls.Count >= 13, $"found {calls.Count} calls");
        foreach (var (f, args) in calls)
            Assert.Matches(@",\s*""[^""]+""\s*$", args);   // a second, literal "who" argument

        Assert.Contains("ExpectOwnModEdit(modDir, \"the test harness\")", Source("TestHarness/GameHarnessAdapter.cs"));
        Assert.Contains("ExpectOwnModEdit(modDir, \"the hair watcher\")", Source("Services/HairPort/HairPortWatcher.cs"));
        Assert.Contains("log.Debug(OwnEditLine(modDir, ownEdit.By));", Source("Services/CompositorService.Events.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-22")]
    public void Last_reason_wins()
    {
        // One entry per mod, overwritten (not added to) by a later expectation: the later caller's name and deadline win.
        var pub = Source("Services/CompositorService.Publish.cs");
        Assert.Contains("ConcurrentDictionary<string, (long Until, string By)> _ownModEditUntil", pub);
        Assert.Contains("=> _ownModEditUntil[modDir] = (Environment.TickCount64 + 2000, by);", pub);
    }

    private static string Here([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Here(), "..", "Proteus", relative));
}
