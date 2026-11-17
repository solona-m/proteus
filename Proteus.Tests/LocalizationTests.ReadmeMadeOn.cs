using System;
using System.Linq;
using Xunit;

namespace Proteus.Tests;

/// <summary>KAN-6: every README's <b>Made on</b> bullet says a piece without enough skin is placed by its names, and that
/// <b>Check in game</b> says so — in its own language's labels.</summary>
public sealed partial class LocalizationTests
{
    [Theory]
    [MemberData(nameof(AllLocales))]
    [Trait("Plan", "AC-kan-6-skinless-made-on-9")]
    public void Readme_made_on_bullet_names_check_in_game(string lang) // [AC-kan-6-skinless-made-on-9]
    {
        var t = Load(lang);
        string bullet = "- " + Bold(t, "SizeSet.MadeOn");
        string[] lines = ReadmeText(lang).Split('\n').Where(l => l.StartsWith(bullet, StringComparison.Ordinal)).ToArray();
        Assert.True(lines.Length == 1, $"[{lang}] README has {lines.Length} lines starting \"{bullet}\"; expected one");
        string checkInGame = "**" + t["SizeSet.CheckInGame"].Message.TrimEnd(' ', ':', '：') + "**";
        Assert.True(lines[0].Contains(checkInGame, StringComparison.Ordinal),
                    $"[{lang}] the Made on bullet does not name {checkInGame}");
    }
}
