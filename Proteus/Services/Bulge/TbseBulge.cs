using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace Proteus.Services.Bulge;

/// <summary>
/// The owner's featureless bulge for The Body SE (path A of plan featureless-bulge): a mesh that plugs the hole The Body
/// SE's genital piece (<c>0.3</c>) plugs, embedded as <c>Resources/tbse_bulge.json</c>. It ships only her sculpt and the
/// 28 points of that hole (to know a body it fits), never The Body SE's mesh.
/// <para/>
/// Generated from her FBX by <c>BulgeAssetLocalTests.Generate_tbse_bulge</c>: its rim snapped onto the hole, one vertex
/// per (point, uv), and for each vertex the uv a garment made on a bibo body should land it by (<see cref="File.land"/>),
/// in The Body SE's layout.
/// </summary>
internal sealed class TbseBulge
{
    internal const string Resource = "Proteus.Resources.tbse_bulge.json";

    /// <summary>The embedded file.</summary>
    /// <param name="source">The sha256 of the FBX it was made from.</param>
    /// <param name="hole">The hole's points, in order round it: body space, metres.</param>
    /// <param name="positions">Each vertex's position, body space.</param>
    /// <param name="uv">Each vertex's uv, as drawn (the owner's).</param>
    /// <param name="land">Each vertex's landing uv for a bibo garment, The Body SE's layout; null where none.</param>
    /// <param name="weights">Each vertex's bones by name (rim vertices take the body's own when drawn).</param>
    /// <param name="rim">Per vertex, the hole point it sits on, or −1.</param>
    /// <param name="triangles">Corner vertex indices, in threes.</param>
    internal sealed record File(string source, float[][] hole, float[][] positions, float[][] uv, float[]?[] land,
                                Dictionary<string, float>[] weights, int[] rim, int[] triangles);

    public required File Data { get; init; }
    public required Vector3[] Hole { get; init; }
    public required Vector3[] Positions { get; init; }

    private static readonly Lazy<TbseBulge?> shared = new(() => Load(null, out _));

    /// <summary>The embedded bulge, or null when there is none (it ships only once the owner's sculpt exists).</summary>
    internal static TbseBulge? Shared => shared.Value;

    internal static TbseBulge? Load(string? json, out string? why)
    {
        why = null;
        try
        {
            if (json == null)
            {
                using var s = typeof(TbseBulge).Assembly.GetManifestResourceStream(Resource);
                if (s == null) { why = "no TBSE bulge is embedded"; return null; }
                using var r = new StreamReader(s);
                json = r.ReadToEnd();
            }
            var f = JsonSerializer.Deserialize<File>(json);
            if (f == null || f.positions.Length == 0 || f.triangles.Length % 3 != 0
                || f.uv.Length != f.positions.Length || f.land.Length != f.positions.Length || f.weights.Length != f.positions.Length
                || f.rim.Length != f.positions.Length || f.triangles.Any(t => t < 0 || t >= f.positions.Length) || f.hole.Length < 3)
            {
                why = "the TBSE bulge file is malformed";
                return null;
            }
            static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
            return new TbseBulge { Data = f, Hole = f.hole.Select(V).ToArray(), Positions = f.positions.Select(V).ToArray() };
        }
        catch (Exception e)
        {
            why = "the TBSE bulge could not be read: " + e.Message;
            return null;
        }
    }
}
