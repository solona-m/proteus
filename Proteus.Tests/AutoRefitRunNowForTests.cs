using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// <c>AutoRefitWatcher.RunNowFor</c>, the forced refit of one mod's worn pieces the harness's worn-refit trigger runs. The
/// watcher cannot be built in a test, so its wiring is pinned by reading the source, as <see cref="IdleDiskIoWiringTests"/>
/// does. Textual on purpose: change these with the code.
/// </summary>
public class AutoRefitRunNowForTests
{
    private static string Source([CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", "Services", "AutoRefit", "AutoRefitWatcher.cs"));

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

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-37")]
    public void RunNowFor_ReportsEachSlot_OnlyThatMod()
    {
        var src = Source();
        var runNowFor = Method(src, "RunNowFor(string modRoot)");
        // Refuses (null) when auto refit cannot run, and names why nothing could be looked at.
        Assert.Contains("return null;", runNowFor);
        foreach (var reason in new[] { "not-enqueued:blank-models", "not-enqueued:no-collection", "not-enqueued:no-preferred-body" })
            Assert.Contains(reason, runNowFor);
        // Only the slots resolving into the mod, each through the walk's own per-slot code, forced.
        Assert.Contains("resolvedOf[slot].Root", runNowFor);
        Assert.Contains("WalkSlot(slot, now, resolvedOf, pref, collection.Id, all: true, tick)", runNowFor);
        // Never arms "refit everything".
        Assert.DoesNotContain("forceAll", runNowFor);
        Assert.DoesNotContain("walkDueAt", runNowFor);
    }

    [Fact]
    [Trait("Plan", "EDGE-split-waist-seam-37")]
    public void Walk_And_RunNowFor_Share_The_PerSlot_Helper()
    {
        var src = Source();
        var walk = Method(src, "private void Walk()");
        Assert.Contains("WalkSlot(slot, now, resolvedOf, pref, collection.Id, all, tick)", walk);
        Assert.DoesNotContain("Enqueue(new Request", walk);
        var slot = Method(src, "private string WalkSlot(");
        // The declined piece forced clears the decline and saves, the just-refitted window and ShouldRefit apply.
        Assert.Contains("pref.Declined.Remove(worn.Key);", slot);
        Assert.Contains("config.Save();", slot);
        Assert.Contains("\"just-refitted\"", slot);
        Assert.Contains("AutoRefitDecisions.ShouldRefit(", slot);
        Assert.Single(Regex.Matches(src, Regex.Escape("Enqueue(new Request(slot, worn, resolved, root,")));
    }
}
