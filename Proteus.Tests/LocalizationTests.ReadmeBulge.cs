using System;
using System.Linq;
using Xunit;

namespace Proteus.Tests;

public sealed partial class LocalizationTests
{
    /// <summary>
    /// Every README says the featureless bulge (plan featureless-bulge): exactly one line ending <c>&lt;!-- bulge --&gt;</c>,
    /// a bullet in the Studio's Body size list right after the pubes bullet, naming the bodies it serves (TBSE, TBSE-X).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-featureless-bulge-10")]
    public void Readme_body_size_says_the_bulge(string lang)
    {
        var lines = ReadmeText(lang).Split('\n');
        var at = Enumerable.Range(0, lines.Length).Where(i => lines[i].EndsWith("<!-- bulge -->", StringComparison.Ordinal)).ToList();
        Assert.True(at.Count == 1, $"[{lang}] README has {at.Count} bulge lines; expected exactly one");
        var line = lines[at[0]];
        Assert.StartsWith("- **", line);
        Assert.Contains("TBSE", line);
        Assert.Contains("TBSE-X", line);
        Assert.Contains("Proteus", line);
        // The fifth bullet of the Studio's Body size list: right after the new skin, hidden skin, piercings and pubes ones.
        int above = 0;
        for (int i = at[0] - 1; i >= 0 && lines[i].StartsWith("- ", StringComparison.Ordinal); i--) above++;
        Assert.True(above == 4, $"[{lang}] the bulge bullet follows {above} bullets in its list; expected the four before it");
    }
}
