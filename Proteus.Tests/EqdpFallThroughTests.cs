using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The guard on the carrier EQDP pair (SecondSkinService.Build): emptying the wearer's own entry only
/// lands the shell in cut space when cut space is on the wearer's fall-through chain, same gender.
/// </summary>
public class EqdpFallThroughTests
{
    [Theory]
    // The ordinary case the carrier branch exists for: a race whose body draws as Midlander female.
    [InlineData("0801", "0201")]  // Miqo'te female  -> Midlander female
    [InlineData("0601", "0201")]  // Elezen female   -> Midlander female
    [InlineData("1001", "0201")]  // Roegadyn female -> Midlander female
    [InlineData("1401", "0201")]  // Au Ra female    -> Midlander female
    [InlineData("1601", "0201")]  // Hrothgar female -> Midlander female
    [InlineData("1801", "0201")]  // Viera female    -> Midlander female
    [InlineData("0701", "0101")]  // Miqo'te male    -> Midlander male
    // The game's one odd male hop, and the hop beyond it.
    [InlineData("1501", "0901")]  // Hrothgar male   -> Roegadyn male
    [InlineData("1501", "0101")]  // Hrothgar male   -> Roegadyn male -> Midlander male
    public void ReachableSameGender_IsAllowed(string from, string to)
        => Assert.True(SecondSkinService.CanFallThrough(from, to));

    [Theory]
    // The reported bug: a Midlander female emptied onto Midlander male. The hop is real in the game's
    // table, but reaching it means the shell was cut from male body parts — a wrong vote, not a deform.
    [InlineData("0201", "0101")]
    [InlineData("1201", "1101")]  // the other cross-gender hop, Lalafell female -> Lalafell male
    // Cut space nowhere on the chain: a stale snapshot naming a race the wearer isn't.
    [InlineData("0201", "0801")]  // Midlander female claiming Miqo'te cut space
    [InlineData("1401", "1601")]  // Au Ra female -> Hrothgar female: siblings, not parent/child
    [InlineData("0901", "1501")]  // Roegadyn male -> Hrothgar male: the real hop, backwards
    // Midlander male is the root — there is nothing under it to fall to.
    [InlineData("0101", "0201")]
    [InlineData("0101", "0901")]
    public void UnreachableOrCrossGender_IsRejected(string from, string to)
        => Assert.False(SecondSkinService.CanFallThrough(from, to));

    [Theory]
    [InlineData(null, "0201")]
    [InlineData("0201", null)]
    [InlineData("", "0201")]
    [InlineData("2", "0201")]     // PathCharCode's \d+ can yield a single digit
    [InlineData("0201", "x")]
    [InlineData("0000", "0201")]  // no such race index
    // Out of the playable 1..18 range. Unbounded, EqdpFallbackIndex's catch-all made these look like
    // children of Midlander and the pair was accepted.
    [InlineData("9101", "0101")]  // Penumbra's unknown-male-NPC range
    [InlineData("9201", "0201")]  // unknown female NPC
    [InlineData("1901", "0101")]  // one past Viera female
    public void MalformedCodes_AreRejectedNotThrown(string? from, string? to)
        => Assert.False(SecondSkinService.CanFallThrough(from, to));
}
