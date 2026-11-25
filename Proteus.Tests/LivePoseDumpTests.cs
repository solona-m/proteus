using Proteus.Interop;
using Xunit;

namespace Proteus.Tests;

/// <summary>The pose dump's log line: the folder (it carries the Windows user name) kept out when asked. Ships in every
/// build, so tested outside the harness folder (CI runs it).</summary>
public class LivePoseDumpTests
{
    [Fact]
    [Trait("Plan", "AC-knee-standoff-10")]
    [Trait("Plan", "EDGE-knee-standoff-24")]
    public void The_folder_stays_out_of_the_log_when_asked()
    {
        const string dir = @"C:\Users\someone\AppData\Roaming\XIVLauncher\pluginConfigs\Proteus\test-harness\posedumps\s\idle";
        var hidden = LivePoseDump.Line(120, 201, 7, dir, logFolder: false);
        Assert.DoesNotContain(dir, hidden);
        Assert.DoesNotContain("someone", hidden);
        Assert.EndsWith("-> harness posedump folder", hidden);
        Assert.EndsWith(dir, LivePoseDump.Line(120, 201, 7, dir, logFolder: true));   // the owner's /proteus posedump unchanged
    }
}
