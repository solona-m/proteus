using Proteus.Gui;
using Xunit;

namespace Proteus.Tests;

/// <summary>What a dropdown's search box matches. Sizes are single letters, which is what makes this more than a Contains.</summary>
public class ComboSearchTests
{
    [Theory]
    [InlineData("", "NEOBELLY ALMOND · NSFW Almond L", true)]
    [InlineData("almond nsfw l", "NEOBELLY ALMOND · NSFW Almond L", true)]
    [InlineData("nsfw almond", "NEOBELLY ALMOND · NSFW Almond L", true)]
    [InlineData("ALM", "NEOBELLY ALMOND · NSFW Almond L", true)]
    [InlineData("pushup m", "DEFAULT PUSHUP · SFW Pushup M", true)]
    [InlineData("xs", "DEFAULT · SFW XS", true)]
    public void Finds_what_was_typed(string filter, string label, bool expected)
        => Assert.Equal(expected, ComboSearch.Matches(filter, label));

    [Theory]
    // A single letter is a size, so it must be a whole word: "L" is inside every "Almond", "S" inside every "SFW".
    [InlineData("l", "NEOBELLY ALMOND · NSFW Almond M")]
    [InlineData("s", "DEFAULT · SFW M")]
    [InlineData("m", "DEFAULT MACADAMIA · SFW Macadamia L")]
    // Every word typed has to be there, not just one of them.
    [InlineData("almond hazelnut", "NEOBELLY ALMOND · NSFW Almond L")]
    // Words match from their start, not from the middle.
    [InlineData("mond", "NEOBELLY ALMOND · NSFW Almond L")]
    public void Does_not_find_what_was_not_typed(string filter, string label)
        => Assert.False(ComboSearch.Matches(filter, label));
}
