using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Where Proteus's own reloads meet the Auto redraw setting, pinned by reading the source: <c>CompositorService</c>
/// cannot be built in a test. Textual and deliberately so: change these with the code. Methods are found by their full
/// declarations, since a call can come before the definition.
/// </summary>
public class AutoRedrawWiringTests
{
    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");

    private static string Source(string relative) => File.ReadAllText(Path.Combine(ProteusDir(), relative));

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

    private static string Discover() => Method(Source("Services/CompositorService.CompositeRun.cs"), "private bool Discover(");

    /// <summary>The "no enabled mods" branch of Discover, up to and including its reload.</summary>
    private static string EmptyBranch()
    {
        var body = Discover();
        int start = body.IndexOf("if (entries.Count == 0)", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "no empty branch");
        return Method(body[start..], "if (entries.Count == 0)");
    }

    [Fact]
    [Trait("Plan", "AC-auto-redraw-off-empty-1")]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-1")]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-9")]
    public void The_no_enabled_mods_withdrawal_obeys_auto_redraw()
    {
        var call = Assert.Single(Calls(Discover(), "ReloadAndRedraw"));
        Assert.DoesNotContain("userRequested", call);
        Assert.DoesNotContain("redraw: false", call);
    }

    [Fact]
    [Trait("Plan", "AC-auto-redraw-off-empty-1")]
    [Trait("Plan", "AC-auto-redraw-off-empty-2")]
    public void Both_reload_gates_go_through_AutoRedrawAllows()
    {
        var publish = Source("Services/CompositorService.Publish.cs");
        const string gate = "RedrawHold.AutoRedrawAllows(config.AutoRedraw, userRequested)";
        var reload = Method(publish, "void ReloadAndRedraw(");
        Assert.Contains(gate, reload);
        // The withdrawal reaches Penumbra whatever the setting; only the redraw is gated.
        int dir = reload.IndexOf("ReloadModDirectory(", System.StringComparison.Ordinal);
        Assert.True(dir >= 0 && dir < reload.IndexOf(gate, System.StringComparison.Ordinal));
        Assert.Contains(gate, Method(publish, "void RefreshPlayerTextures("));
    }

    [Fact]
    [Trait("Plan", "AC-auto-redraw-off-empty-3")]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-4")]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-8")]
    public void Only_switching_Proteus_off_is_exempt_from_auto_redraw()
    {
        var root = ProteusDir();
        var hits = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             .Any(seg => seg is "bin" or "obj"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), Regex.Escape("userRequested: true"))
                                  .Select(_ => Path.GetFileName(f)))
            .ToList();
        Assert.Equal("CompositorService.Overrides.cs", Assert.Single(hits));
        Assert.Contains("userRequested: true",
            Method(Source("Services/CompositorService.Overrides.cs"), "public void SetEnabled("));
    }

    [Fact]
    [Trait("Plan", "AC-auto-redraw-off-empty-4")]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-1")]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-6")]
    public void The_empty_path_latches_a_full_redraw_before_its_reload()
    {
        var body = Discover();
        int latch = body.IndexOf("compositor._needFullRedraw = true", System.StringComparison.Ordinal);
        Assert.True(latch >= 0 && latch < body.IndexOf("ReloadAndRedraw(", System.StringComparison.Ordinal));
        Assert.DoesNotContain("_needFullRedraw = false", body);
        Assert.Contains("if (autoRedraw) compositor.TriggerRecomposite(\"auto-redraw-enabled\"",
            Source("Gui/SettingsTab.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-2")]
    public void The_empty_path_still_takes_off_carriers_with_auto_redraw_off()
    {
        var branch = EmptyBranch();
        foreach (var call in new[] { "ReconcileInvisibleGlasses(gearWanted: false", "ReconcileEmperorRing(gearWanted: false",
                                     "RememberHostDecision(gearWanted: false" })
            Assert.Contains(call, branch);
        var beforeReload = branch[..branch.IndexOf("ReloadAndRedraw(", System.StringComparison.Ordinal)];
        Assert.DoesNotContain("AutoRedraw", beforeReload);
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-3")]
    public void The_normal_publish_withholds_its_redraw_with_auto_redraw_off()
    {
        var body = Method(Source("Services/CompositorService.Publish.cs"), "bool ReloadAndRedrawWhenReady(");
        int withheld = body.IndexOf("if (!config.AutoRedraw) return false;", System.StringComparison.Ordinal);
        Assert.True(withheld >= 0 && withheld < body.IndexOf("RefreshPlayerTextures(", System.StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-5")]
    public void A_disabled_Proteus_composites_nothing()
    {
        var body = Method(Source("Services/CompositorService.Trigger.cs"), "public void TriggerRecomposite(");
        int off = body.IndexOf("!config.PluginEnabled", System.StringComparison.Ordinal);
        Assert.True(off >= 0 && off < body.IndexOf("RedrawHold.Admit(", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// Records today's behaviour, not a requirement: an owed auto-refit redraw is the watchdog's to pay and ignores
    /// Auto redraw (carved out of AC-1). Gating the watchdog later is a deliberate change to this test.
    /// </summary>
    [Fact]
    [Trait("Plan", "EDGE-auto-redraw-off-empty-10")]
    public void The_owed_redraw_watchdog_is_independent_of_the_empty_path()
    {
        var watch = Method(Source("Services/CompositorService.RedrawHold.cs"), "private void WatchOwedRedraw(");
        Assert.Contains("RedrawHold.OwedRedrawDue(", watch);
        Assert.DoesNotContain("AutoRedraw", watch);
        Assert.DoesNotContain("_redrawOwed", Discover());
    }
}
