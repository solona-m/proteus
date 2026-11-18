using System.Globalization;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The drawn race code's bookkeeping, through the pure step the compositor's update wraps:
/// today's rules unchanged, and the line it logs never names a character — the log is exported for posting.
/// </summary>
public class DrawnRaceLogTests
{
    // [AC-mod-path-containment-8][EDGE-mod-path-containment-29]
    [Theory]
    [InlineData("0801", "Jane Doe", null, "John Roe", null, "John Roe", "another character")]
    [InlineData("0801", "Jane Doe", null, null, null, null, "nobody drawn")]
    [InlineData("0801", "Jane Doe", null, "Jane Doe", "0801", "Jane Doe", null)]
    [InlineData("0801", "Jane Doe", "1401", "John Roe", "1401", "John Roe", null)]
    [InlineData(null, null, "0801", "Jane Doe", "0801", "Jane Doe", null)]
    public void DrawnRaceStepNamesNoCharacterAndKeepsTheRules(string? oldCode, string? oldOwner, string? newCode, string? owner,
                                                               string? code, string? keptOwner, string? says)
    {
        var step = CompositorService.DrawnRaceStep(oldCode, oldOwner, newCode, owner);

        Assert.Equal(code, step.Code);
        Assert.Equal(keptOwner, step.Owner);
        if (says == null) { Assert.Null(step.Line); return; }

        var line = step.Line!.Value;
        var text = string.Format(CultureInfo.InvariantCulture, line.Template, line.Args);
        Assert.Contains(says, text);
        Assert.Contains("c0801", text);
        foreach (var name in new[] { "Jane Doe", "John Roe" })
        {
            Assert.DoesNotContain(name, text);
            Assert.DoesNotContain(line.Args, a => a?.ToString()?.Contains(name) == true);
        }
    }
}
