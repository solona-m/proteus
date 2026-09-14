using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// A measuring instrument for how close a shell can sit to the skin: scales the push by height, so one
/// build shows a whole ladder of offsets at once and a single redraw answers what would otherwise take a
/// build per value.
/// <para/>
/// Enabled by CREATING <c>%TEMP%\proteus-push-sweep.txt</c>, the same opt-in as the shell dump — it does
/// nothing until the file exists, and deleting it is the only way off. Read on every composite, so the
/// ladder can be edited between redraws without restarting the game.
/// <para/>
/// It multiplies the WHOLE push, <c>BaseOffset + LayerSeparation * layer</c>, so a band at 0.5 halves both
/// the skin-to-shell gap and the gaps between shells. That is the question being asked: how far can the
/// stack come in together.
/// <para/>
/// Not a shipping behaviour. Where the body clips depends on the pose as much as the height — shells are
/// offset in bind pose and only then skinned — so the answer this is for is one global offset chosen at
/// the worst region, not a per-region one.
/// </summary>
public sealed class PushSweep
{
    public const string FileName = "proteus-push-sweep.txt";

    /// <summary>Written when the file is created empty, so an empty file still means something.</summary>
    public const string DefaultLadder =
        "# Push sweep. Each line: <height in metres> <multiplier>. A band runs from its height up to the\n" +
        "# next line's; below the first line the first multiplier applies. The multiplier scales the whole\n" +
        "# push (1.00 = the shipped offset; chat prints the millimetres). Delete this file to stop.\n" +
        "#\n" +
        "# The feet stay at 1.00 on purpose: the toe cap has its own height logic and is the control.\n" +
        "0.00  1.00   # feet, ankles\n" +
        "0.12  4.00   # shins\n" +
        "0.45  3.00   # thighs\n" +
        "0.80  2.00   # hips\n" +
        "0.95  1.50   # waist\n" +
        "1.10  1.00   # chest\n" +
        "1.28  0.50   # shoulders\n" +
        "1.42  0.20   # neck, head\n";

    private readonly float[] from;
    private readonly float[] scale;

    // Tallies, so the log can prove the breakpoints landed where they were meant to. Model space is the
    // guess this whole instrument rests on, and a ladder whose bands all fall on the calves looks, in game,
    // exactly like a body that clips everywhere.
    private readonly int[] hits;
    private float minY = float.MaxValue, maxY = float.MinValue;

    private PushSweep(float[] from, float[] scale)
    {
        this.from = from;
        this.scale = scale;
        hits = new int[from.Length];
    }

    public int BandCount => from.Length;

    /// <summary>
    /// The ladder in <paramref name="text"/>, or null when it holds no bands. A malformed line is reported
    /// through <paramref name="problems"/> and skipped rather than failing the whole ladder: a typo in one
    /// band should not silently turn the sweep off.
    /// </summary>
    public static PushSweep? Parse(string text, List<string>? problems = null)
    {
        var bands = new List<(float From, float Scale)>();
        int lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            int hash = raw.IndexOf('#');
            var line = (hash >= 0 ? raw[..hash] : raw).Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                || !float.IsFinite(y) || !float.IsFinite(s))
            {
                problems?.Add($"line {lineNo}: expected '<height> <multiplier>', got '{line}'");
                continue;
            }
            // Negative would push the shell INTO the body, which is not a point on the ladder, just a way to
            // make every band fail.
            if (s < 0f)
            {
                problems?.Add($"line {lineNo}: multiplier {s} is negative");
                continue;
            }
            bands.Add((y, s));
        }
        if (bands.Count == 0) return null;

        // Sorted, so the file can be written in any order — reversing the ladder to separate "this height
        // is hard" from "this multiplier is too low" should mean swapping numbers, not reordering lines.
        bands.Sort((a, b) => a.From.CompareTo(b.From));
        return new PushSweep(bands.Select(b => b.From).ToArray(), bands.Select(b => b.Scale).ToArray());
    }

    /// <summary>
    /// The ladder from <c>%TEMP%</c>, or null when the file does not exist. An empty file is given the
    /// default ladder, so creating it is enough to start.
    /// </summary>
    public static PushSweep? LoadFromTemp(List<string>? problems = null)
    {
        var path = Path.Combine(Path.GetTempPath(), FileName);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            File.WriteAllText(path, DefaultLadder);
            text = DefaultLadder;
        }
        return Parse(text, problems);
    }

    /// <summary>The band <paramref name="y"/> falls in: the last one starting at or below it.</summary>
    public int BandAt(float y)
    {
        int band = 0;
        for (int i = 1; i < from.Length; i++)
            if (y >= from[i]) band = i;
            else break;
        return band;
    }

    public float MultiplierAt(float y) => scale[BandAt(y)];

    /// <summary><see cref="MultiplierAt"/>, counting the vertex toward the report.</summary>
    public float Take(float y)
    {
        int band = BandAt(y);
        hits[band]++;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
        return scale[band];
    }

    public string DescribeLadder()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < from.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            float mm = SecondSkinWriter.BaseOffset * scale[i] * 1000f;
            float apart = SecondSkinWriter.LayerSeparation * scale[i] * 1000f;
            sb.Append(CultureInfo.InvariantCulture,
                $"y>={from[i]:0.00}: x{scale[i]:0.00} ({mm:0.###}mm to skin, {apart:0.###}mm between shells)");
        }
        return sb.ToString();
    }

    /// <summary>
    /// What the shells got since the last report — vertex count per band and the height range seen — and
    /// starts the next tally, so each host's build reports on its own vertices.
    /// </summary>
    public string TakeReport()
    {
        int total = hits.Sum();
        var sb = new StringBuilder();
        if (total == 0) sb.Append("no shell vertices were pushed");
        else
        {
            sb.Append(CultureInfo.InvariantCulture, $"shell vertices y {minY:0.000}..{maxY:0.000}; per band ");
            for (int i = 0; i < hits.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(CultureInfo.InvariantCulture, $"[{from[i]:0.00}]={hits[i]}");
            }
        }
        Array.Clear(hits);
        minY = float.MaxValue;
        maxY = float.MinValue;
        return sb.ToString();
    }
}
