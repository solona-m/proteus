using System;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The vertex compare that tells a TexTools copy of the game's model from an author's own: two-way, order-free, counted
/// in integers against the bars.
/// </summary>
public class GameModelCopyTests
{
    /// <summary><paramref name="n"/> vertices 1 cm apart on a grid, none within the tolerance of another.</summary>
    private static float[] Grid(int n, float x0 = 0f)
    {
        var p = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            p[3 * i] = x0 + i % 10 * 0.01f;
            p[3 * i + 1] = i / 10 % 10 * 0.01f;
            p[3 * i + 2] = i / 100 * 0.01f;
        }
        return p;
    }

    /// <summary>The first <paramref name="count"/> vertices raised by <paramref name="by"/> in z.</summary>
    private static float[] Moved(float[] p, int count, float by)
    {
        var q = (float[])p.Clone();
        for (int i = 0; i < count; i++) q[3 * i + 2] += by;
        return q;
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-1")]
    public void Order_and_duplicates_do_not_matter()   // [EDGE-old-game-copies-1]
    {
        var game = Grid(100);
        Assert.Equal(1.0, GameModelCopy.Likeness(game, game));

        // Shuffled, vertex by vertex.
        var order = Enumerable.Range(0, 100).OrderBy(i => (i * 37) % 101).ToArray();
        var shuffled = order.SelectMany(i => new[] { game[3 * i], game[3 * i + 1], game[3 * i + 2] }).ToArray();
        Assert.Equal(1.0, GameModelCopy.Likeness(shuffled, game));

        // Every vertex twice, as a re-save splits them along its seams.
        var doubled = game.Concat(game).ToArray();
        Assert.Equal((200, 200, 100, 100), GameModelCopy.Match(doubled, game));
        Assert.True(GameModelCopy.IsCopy(doubled, game));
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-1")]
    public void A_move_past_the_tolerance_unmatches_a_vertex()   // [EDGE-old-game-copies-1]
    {
        var game = Grid(100);
        Assert.Equal((70, 100, 70, 100), GameModelCopy.Match(Moved(game, 30, 1e-3f), game));   // 1 mm
        Assert.Equal((100, 100, 100, 100), GameModelCopy.Match(Moved(game, 30, 5e-5f), game)); // 0.05 mm
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-2")]
    public void A_subset_is_judged_by_its_weaker_way()   // [EDGE-old-game-copies-2]
    {
        var game = Grid(100);
        var subset = game.Take(40 * 3).ToArray();
        Assert.Equal((40, 40, 40, 100), GameModelCopy.Match(subset, game));
        Assert.False(GameModelCopy.IsCopy(subset, game));
        Assert.True(GameModelCopy.IsAuthors(subset, game));   // 40% of the game's: under the author's bar

        // A near-complete subset — the game added a few triangles since — is still a copy.
        var most = game.Take(97 * 3).ToArray();
        Assert.True(GameModelCopy.IsCopy(most, game));
        // A superset, the other way about.
        Assert.False(GameModelCopy.IsCopy(game, subset));
        Assert.Equal(0.4, GameModelCopy.Likeness(game, subset), 6);
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-3")]
    public void The_bars_are_counted_in_integers()   // [EDGE-old-game-copies-3]
    {
        var game = Grid(100);
        Assert.True(GameModelCopy.IsCopy(Moved(game, 5, 0.01f), game));    // 95 of 100
        Assert.False(GameModelCopy.IsCopy(Moved(game, 6, 0.01f), game));   // 94 of 100
        Assert.True(GameModelCopy.IsAuthors(Moved(game, 51, 0.01f), game));  // 49 of 100
        Assert.False(GameModelCopy.IsAuthors(Moved(game, 50, 0.01f), game)); // 50 of 100
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-17")]
    public void Empty_models_are_no_copy_and_the_author_s()   // [EDGE-old-game-copies-17]
    {
        var game = Grid(10);
        Assert.False(GameModelCopy.IsCopy([], game));
        Assert.False(GameModelCopy.IsCopy(game, []));
        Assert.True(GameModelCopy.IsAuthors([], game));
        Assert.Equal(0, GameModelCopy.Likeness([], []));
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-17")]
    public void Coordinates_that_are_not_finite_or_are_huge_never_match()   // [EDGE-old-game-copies-17]
    {
        float[] odd = [float.NaN, 0, 0, float.PositiveInfinity, 0, 0, 0, float.NegativeInfinity, 0, 1e30f, 0, 0, -1e30f, 0, 0];
        // The huge ones match themselves (they are the same point); the NaN and infinite ones never do.
        Assert.Equal((2, 5, 2, 5), GameModelCopy.Match(odd, odd));

        // Two far-apart huge points clamp into one grid cell, and still don't match.
        Assert.Equal((0, 1, 0, 1), GameModelCopy.Match([1e30f, 0, 0], [2e30f, 0, 0]));
        Assert.Equal((0, 1, 0, 1), GameModelCopy.Match([-1e30f, 5, 0], [-3e30f, 5, 0]));
        Assert.Equal(0, GameModelCopy.Likeness(odd, Grid(5)));
    }
}
