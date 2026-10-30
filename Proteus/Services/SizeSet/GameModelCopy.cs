using System;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// Whether a mod's model is the game's own model of that item, re-saved or an older version of it, by its vertices: how
/// many of each model's vertices have one of the other's within <see cref="Tolerance"/>, both ways. Order-free, and blind
/// to the split duplicates a re-save adds — the TexTools copy BOObies ships for Midlander men is 2312 vertices to the
/// game's 1577, 99% of them on the game's. Pure: the size set reads the models (see <see cref="SizeSetRefit.PiecesOf"/>).
/// </summary>
internal static class GameModelCopy
{
    /// <summary>How near a vertex must be to one of the other model's to count: 0.1 mm.</summary>
    internal const float Tolerance = 1e-4f;

    /// <summary>
    /// At or over this share both ways, a copy of the game's model. Measured: the copies TexTools packs sit at 95.4–100%;
    /// an author's per-race hand fits of a body mod at 58.6%, and a men's fit edited from the game's trousers at up to 92%.
    /// </summary>
    internal const int CopyPercent = 95;

    /// <summary>Under this share either way, the author's own model: real designs sit at 3.1% or less.</summary>
    internal const int AuthorPercent = 50;

    /// <summary>Grid cells per axis are clamped to this, so a huge coordinate never overflows its key.</summary>
    private const long MaxCell = 1L << 40;

    /// <summary>
    /// How many of <paramref name="a"/>'s vertices have one of <paramref name="b"/>'s within <see cref="Tolerance"/>, of
    /// how many, and the same of <paramref name="b"/>'s. Positions are x, y, z per vertex; a vertex with a coordinate that
    /// is not finite never matches.
    /// </summary>
    internal static (int MatchedA, int TotalA, int MatchedB, int TotalB) Match(float[] a, float[] b)
        => (Matched(a, b), a.Length / 3, Matched(b, a), b.Length / 3);

    /// <summary>A copy of the game's model: at least <see cref="CopyPercent"/>% matched both ways, counted in integers.
    /// Either model empty is none.</summary>
    internal static bool IsCopy(float[] mine, float[] games)
    {
        var (ma, ta, mb, tb) = Match(mine, games);
        return ta > 0 && tb > 0 && (long)ma * 100 >= (long)CopyPercent * ta && (long)mb * 100 >= (long)CopyPercent * tb;
    }

    /// <summary>The author's own model: under <see cref="AuthorPercent"/>% matched either way, counted in integers. Either
    /// model empty is the author's — nothing of the game's to be.</summary>
    internal static bool IsAuthors(float[] mine, float[] games)
    {
        var (ma, ta, mb, tb) = Match(mine, games);
        return ta == 0 || tb == 0 || (long)ma * 100 < (long)AuthorPercent * ta || (long)mb * 100 < (long)AuthorPercent * tb;
    }

    /// <summary>The smaller of the two shares, 0 to 1: for logs and tests.</summary>
    internal static double Likeness(float[] a, float[] b)
    {
        var (ma, ta, mb, tb) = Match(a, b);
        return ta == 0 || tb == 0 ? 0 : Math.Min((double)ma / ta, (double)mb / tb);
    }

    private static int Matched(float[] from, float[] to)
    {
        int n = from.Length / 3, m = to.Length / 3;
        if (n == 0 || m == 0) return 0;
        var grid = new Dictionary<(long, long, long), List<int>>();
        for (int j = 0; j < m; j++)
        {
            if (!Finite(to, j)) continue;
            var key = Cell(to, j);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(j);
        }

        const double reach = (double)Tolerance * Tolerance;
        int matched = 0;
        for (int i = 0; i < n; i++)
        {
            if (!Finite(from, i)) continue;
            var (cx, cy, cz) = Cell(from, i);
            bool found = false;
            for (long dx = -1; dx <= 1 && !found; dx++)
            for (long dy = -1; dy <= 1 && !found; dy++)
            for (long dz = -1; dz <= 1 && !found; dz++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                foreach (int j in list)
                {
                    // Confirmed by the real distance, in doubles: a clamped cell holds points far apart.
                    double ex = (double)to[3 * j] - from[3 * i], ey = (double)to[3 * j + 1] - from[3 * i + 1],
                           ez = (double)to[3 * j + 2] - from[3 * i + 2];
                    if (ex * ex + ey * ey + ez * ez <= reach)
                    {
                        found = true;
                        break;
                    }
                }
            }
            if (found) matched++;
        }
        return matched;
    }

    private static bool Finite(float[] p, int v)
        => float.IsFinite(p[3 * v]) && float.IsFinite(p[3 * v + 1]) && float.IsFinite(p[3 * v + 2]);

    private static (long, long, long) Cell(float[] p, int v)
        => (Axis(p[3 * v]), Axis(p[3 * v + 1]), Axis(p[3 * v + 2]));

    private static long Axis(float x) => (long)Math.Clamp(Math.Floor((double)x / Tolerance), -MaxCell, MaxCell);
}
