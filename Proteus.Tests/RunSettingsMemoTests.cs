using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// #55: a composite run reads each body mod's Penumbra settings once, shared by the skin phase's bust standoff and the
/// shell phase — but only a successful read is kept, so a failed one (a loading screen) in the skin phase never leaves
/// the shell covering every chest for the rest of the session.
/// </summary>
public class RunSettingsMemoTests
{
    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-1")]
    [Trait("Plan", "EDGE-issue-sweep-b-4")]
    [Trait("Plan", "EDGE-issue-sweep-b-29")]
    public void Null_read_is_not_cached()
    {
        var memo = new ShellBodyAtr.RunSettingsMemo();
        int reads = 0;
        var settings = new ShellBodyAtr.ModSettings(true, new Dictionary<string, List<string>>());
        ShellBodyAtr.ModSettings? Read(string _) => ++reads == 1 ? null : settings;
        Assert.Null(memo.Get("NIRAE", Read));               // the skin phase: a failed read, not kept
        Assert.Equal(settings, memo.Get("nirae", Read));    // the shell phase reads again, and succeeds
        Assert.Equal(settings, memo.Get("NIRAE", Read));    // later readers share it
        Assert.Equal(2, reads);

        var build = Source("Services/SecondSkinService.ShellSetBuild.cs");
        Assert.Contains("(SettingsReader ?? service.PlayerModSettings)(modDir)", build);
        Assert.Contains("settingsOf: run.RunModSettings", Source("Services/CompositorService.ShellPhase.cs"));
    }

    private static string Source(string relative, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", relative));
}
