using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The wiring of the auto refit's redraw hold that no unit test can reach — <c>CompositorService</c> and the watcher
/// cannot be built in a test — pinned by reading the source. Textual and deliberately so: change these with the code.
/// </summary>
public class RedrawHoldWiringTests
{
    private static string Source(string relative, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", relative));

    /// <summary>The body of the first method whose signature contains <paramref name="signature"/>, braces matched.</summary>
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

    /// <summary>Every call to <paramref name="callee"/> in <paramref name="body"/>, its arguments included.</summary>
    private static string[] Calls(string body, string callee)
        => Regex.Matches(body, Regex.Escape(callee) + @"\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)")
                .Select(m => m.Value).ToArray();

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-3")]
    [Trait("Plan", "EDGE-fewer-redraws-19")]
    [Trait("Plan", "EDGE-fewer-redraws-20")]
    [Trait("Plan", "EDGE-fewer-redraws-22")]
    public void Every_composite_a_setting_event_starts_carries_whether_it_was_the_batchs_own()
    {
        var events = Source("Services/CompositorService.Events.cs");
        foreach (var handler in new[] { "void OnModSettingChanged(", "void OnModAdded(" })
        {
            var body = Method(events, handler);
            // Read first, on the writer's thread, before anything can hop off it.
            int own = body.IndexOf("bool own = IsOwnRefitWrite(modDir);", System.StringComparison.Ordinal);
            Assert.True(own >= 0, handler);
            Assert.True(own < body.IndexOf("Task.Run", System.StringComparison.Ordinal) || !body.Contains("Task.Run"));
            foreach (var call in Calls(body, "TriggerRecomposite").Concat(Calls(body, "EvaluateSurfaceModOffThread")))
                Assert.Contains("deferWhileHeld: own", call);
        }
        foreach (var method in new[] { "void EvaluateSurfaceModOffThread(", "void ScheduleMovedBaseRecheck(" })
        {
            var body = Method(events, method);
            var calls = Calls(body, "TriggerRecomposite").Concat(Calls(body, "ScheduleMovedBaseRecheck")).ToArray();
            Assert.NotEmpty(calls);
            foreach (var call in calls) Assert.Contains("deferWhileHeld: deferWhileHeld", call);
        }
        // The batch's own evaluation leaves the snapshot to the release, not to whichever composite runs next.
        Assert.Contains("MarkMaterialsDirty(owedWhileHeld: deferWhileHeld)", Method(events, "void EvaluateSurfaceModOffThread("));
    }

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-3")]
    [Trait("Plan", "EDGE-fewer-redraws-19")]
    [Trait("Plan", "EDGE-fewer-redraws-20")]
    public void Nothing_but_the_auto_refits_own_work_is_held()
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "Proteus", "Plugin.cs")))!;
        // Any opt-in at all, literal or not, outside the three places the batch's own work passes through.
        var optIns = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && Regex.IsMatch(File.ReadAllText(f), @"deferWhileHeld:"))
            .Select(f => Path.GetFileName(f)).OrderBy(f => f).ToArray();
        Assert.Equal(["AutoRefitWatcher.cs", "CompositorService.Events.cs", "RefitModService.cs"], optIns);
        Assert.Single(Regex.Matches(Source("Services/AutoRefit/AutoRefitWatcher.cs"), @"deferWhileHeld:"));
        Assert.Single(Regex.Matches(Source("Services/RefitModService.cs"), @"deferWhileHeld:"));

        // The literal opt-ins are the switch-on redraw only; the registrations go through the watcher's flag.
        var watcher = Source("Services/AutoRefit/AutoRefitWatcher.cs");
        Assert.Single(Regex.Matches(watcher, @"deferWhileHeld:\s*true"));
        Assert.Contains("RedrawForChangedModel($\"auto refit: {r.Slot} switched on\", deferWhileHeld: true)", watcher);
        Assert.Contains("deferWhileHeld: ownedByAutoRefit", Source("Services/RefitModService.cs"));
        Assert.Contains("ownedByAutoRefit: true", watcher);
        Assert.DoesNotContain("ownedByAutoRefit", Source("Gui/PartsPanel.cs"));

        // The batch marks a mod before every write it makes to it.
        var apply = Method(watcher, "Task<string?> Apply(");
        Assert.True(apply.IndexOf("ExpectOwnRefitWrite(dir)") < apply.IndexOf("ReloadModDirectory(dir)"));
        int lastMark = apply.LastIndexOf("ExpectOwnRefitWrite(dir)");
        Assert.True(lastMark < apply.IndexOf("SetModEnabled("));
        Assert.True(lastMark < apply.IndexOf("SetModOption("));
        Assert.True(lastMark < apply.IndexOf("ChangeHold("));
        var refitMods = Source("Services/RefitModService.cs");
        Assert.True(refitMods.IndexOf("ExpectOwnRefitWrite(dirName)") < refitMods.IndexOf("AddModDirectory(dirName)"));
        Assert.True(refitMods.IndexOf("ExpectOwnRefitWrite(p.DirName)") < refitMods.IndexOf("SetModEnabled(collId.Value, p.DirName"));
    }

    private static string Here([CallerFilePath] string here = "") => here;

    [Fact]
    [Trait("Plan", "AC-fewer-redraws-1")]
    [Trait("Plan", "EDGE-fewer-redraws-8")]
    [Trait("Plan", "EDGE-fewer-redraws-10")]
    [Trait("Plan", "EDGE-fewer-redraws-5")]
    public void A_held_trigger_takes_no_debounce_token_and_the_runner_lets_go_only_its_own_key()
    {
        var trigger = Method(Source("Services/CompositorService.Trigger.cs"), "public void TriggerRecomposite(");
        Assert.Single(Regex.Matches(trigger, Regex.Escape("recompositeGate.Next()")));
        Assert.True(trigger.IndexOf("RedrawHold.Admit(") < trigger.IndexOf("recompositeGate.Next()"));
        // No redraw of its own: an owed redraw is answered by the watchdog or the publish, never by the trigger task.
        Assert.DoesNotContain("RedrawPlayerLogged(", trigger);
        Assert.DoesNotContain("RedrawPlayer(", trigger);

        var hold = Source("Services/CompositorService.RedrawHold.cs");
        Assert.Single(Regex.Matches(hold, Regex.Escape("redrawHold.Poll()")));
        var poll = Method(hold, "void OnRedrawHoldPoll(");
        Assert.Contains("redrawHold.Poll()", poll);
        // The release's composite is fired unheld: a batch starting in its debounce cannot hold back what it replaced.
        var release = Calls(poll, "TriggerRecomposite");
        Assert.Single(release);
        Assert.DoesNotContain("deferWhileHeld", release[0]);

        var watcher = Source("Services/AutoRefit/AutoRefitWatcher.cs");
        var runQueue = Method(watcher, "async Task RunQueue(");
        Assert.Contains("AutoRefitDecisions.RunnerExitRelease(releasedAtDrain)", runQueue);
        Assert.DoesNotContain("\"auto-refit", watcher);
    }
}
