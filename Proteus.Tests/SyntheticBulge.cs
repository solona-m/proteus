using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Proteus.Services.Bulge;

namespace Proteus.Tests;

/// <summary>
/// A TBSE-like legs body built round the shipped bulge's own hole (<see cref="TbseBulge"/>), for CI: a ring of skin (the
/// body's main skin, <c>1.1</c>) whose inner edge is the hole, rigged so the crotch can be found, and a dome plugging the
/// hole on vertex slots of its own (the genital piece, <c>1.2</c>), standing well off the skin as a penis does.
/// </summary>
internal static class SyntheticBulge
{
    internal const string Skin = "/mt_c0101b0001_b.mtrl";

    /// <summary>
    /// The bulge's hole cut to <paramref name="points"/> points as a body cut otherwise would be: fewer by leaving out the
    /// far end of its shortest edges, more by splitting its longest at their midpoints (the hole's edges run 2–23 mm). Each
    /// point is then moved <paramref name="jitter"/> in a fixed pattern (alternately in and out of the hole, never at
    /// random), and the whole <paramref name="forward"/> along +z.
    /// </summary>
    internal static Vector3[] ResampledHole(int points, float jitter = 0f, float forward = 0f)
    {
        var hole = TbseBulge.Shared!.Hole.ToList();
        int n = hole.Count;
        var centre = hole.Aggregate(Vector3.Zero, (s, p) => s + p) / n;
        float Edge(List<Vector3> l, int i) => Vector3.Distance(l[i], l[(i + 1) % l.Count]);
        while (hole.Count > points)
        {
            int i = Enumerable.Range(0, hole.Count).MinBy(k => Edge(hole, k));
            hole.RemoveAt((i + 1) % hole.Count);
        }
        while (hole.Count < points)
        {
            int i = Enumerable.Range(0, hole.Count).MaxBy(k => Edge(hole, k));
            hole.Insert(i + 1, (hole[i] + hole[(i + 1) % hole.Count]) / 2);
        }
        return hole.Select((p, k) => p + Vector3.Normalize(p - centre) * (k % 2 == 0 ? jitter : -jitter) + new Vector3(0, 0, forward)).ToArray();
    }

    /// <summary>The body; <paramref name="hole"/> null uses the bulge's own, anything else a hole the bulge does not fit.</summary>
    /// <param name="height">How far the dome stands off the hole (6 cm: a penis).</param>
    /// <param name="rings">Rings of the dome between rim and apex: 5 gives 169 vertices and 308 triangles, room for the bulge.</param>
    /// <param name="outerRings">Rings of main skin beyond the first two (each a little further out): a big body, so the bulge's
    /// own vertices are a small share of it (as on a real body, where a same-mesh landing reads as identity).</param>
    /// <param name="mergedDome">The dome in the main skin's own submesh (TBSE-X's erect options, rule b): rig it to a penis
    /// bone with <paramref name="domeBone"/> so the reading finds it.</param>
    /// <param name="piercing">A genital piercing: a body piercing material's mesh 2 mm off the dome's top.</param>
    /// <param name="basePubes">Pubic hair on the main skin round the hole, on the hips (never the penis's).</param>
    /// <param name="feet">A skin triangle 90 cm below the hole, so the body stands as tall as real legs (the crotch at about
    /// 80% of its height) rather than being the crotch alone.</param>
    internal static byte[] Body(IReadOnlyList<Vector3>? hole = null, float height = 0.06f, int rings = 5, string? domeBone = null,
                                int outerRings = 0, string? ringBone = null, bool mergedDome = false, bool piercing = false,
                                bool basePubes = false, bool feet = false)
    {
        hole ??= TbseBulge.Shared!.Hole;
        int h = hole.Count;
        var centre = hole.Aggregate(Vector3.Zero, (s, p) => s + p) / h;
        // Newell's normal of the hole, turned to face forward.
        var normal = Vector3.Zero;
        for (int i = 0; i < h; i++)
        {
            var a = hole[i] - centre;
            var b = hole[(i + 1) % h] - centre;
            normal += Vector3.Cross(a, b);
        }
        normal = Vector3.Normalize(normal);
        if (normal.Z < 0) normal = -normal;

        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var w = new List<(string, float)[]?>();
        // The main skin: the hole, and two rings outward from it, 6 and 12 cm out.
        var bodyW = new (string, float)[] { ("j_kosi", 0.6f), ("j_asi_a_l", 0.2f), ("j_asi_a_r", 0.2f) };
        // With ringBone, the hole ring carries a share on that bone (TBSE-X weights its crotch skin a little to the penis).
        var ringW = ringBone == null ? bodyW : new (string, float)[] { ("j_kosi", 0.4f), ("j_asi_a_l", 0.2f), ("j_asi_a_r", 0.2f), (ringBone, 0.2f) };
        var ringsOut = new List<float> { 0f, 1.5f, 3f };
        for (int r = 1; r <= outerRings; r++) ringsOut.Add(3f + r * 0.05f);
        // The extra outer rings hang on the hips alone, so the crotch is found where it was.
        var hipsW = new (string, float)[] { ("j_kosi", 1f) };
        for (int r = 0; r < ringsOut.Count; r++)
            foreach (var p in hole)
            {
                float k = ringsOut[r];
                var d = p - centre;
                pos.Add(centre + d * (1f + k) - normal * (k * 0.01f));
                nrm.Add(normal);
                w.Add(r == 0 ? ringW : r < 3 ? bodyW : hipsW);
            }
        var skinTris = new List<int>();
        for (int r = 0; r < ringsOut.Count - 1; r++)
            for (int i = 0; i < h; i++)
            {
                int a = r * h + i, b = r * h + (i + 1) % h, a2 = a + h, b2 = b + h;
                skinTris.AddRange([a, a2, b, b, a2, b2]);
            }
        // The dome: its own slots — the rim again at the hole's points, rings toward an apex `height` out.
        int domeStart = pos.Count;
        var domeW = new (string, float)[] { (domeBone ?? "j_kosi", 1f) };
        for (int r = 0; r <= rings; r++)
        {
            float t = r / (float)(rings + 1);
            foreach (var p in hole)
            {
                pos.Add(centre + (p - centre) * (1f - t) + normal * (height * MathF.Sin(t * MathF.PI / 2)));
                nrm.Add(normal);
                w.Add(r == 0 ? ringW : domeW);
            }
        }
        int apex = pos.Count;
        pos.Add(centre + normal * height);
        nrm.Add(normal);
        w.Add(domeW);
        var domeTris = new List<int>();
        for (int r = 0; r < rings; r++)
            for (int i = 0; i < h; i++)
            {
                int a = domeStart + r * h + i, b = domeStart + r * h + (i + 1) % h, a2 = a + h, b2 = b + h;
                domeTris.AddRange([a, b, a2, b, b2, a2]);
            }
        for (int i = 0; i < h; i++)
            domeTris.AddRange([domeStart + rings * h + i, domeStart + rings * h + (i + 1) % h, apex]);

        var pa = pos.ToArray();
        var meshes = new List<SyntheticModel.SurfaceMesh>
        {
            new(Skin, pa, nrm.ToArray(), pa.Select(p => new Vector2(p.X + 0.5f, p.Y)).ToArray(), w.ToArray(),
                mergedDome ? [(0u, [.. skinTris, .. domeTris])] : [(0u, skinTris.ToArray()), (0u, domeTris.ToArray())]),
        };
        // An extra mesh copying some triangles of this body's, each corner moved `off` along the normal.
        SyntheticModel.SurfaceMesh Copy(string material, IEnumerable<int> tris, float off, (string, float)[] weights)
        {
            var map = new Dictionary<int, int>();
            var p2 = new List<Vector3>();
            var t2 = new List<int>();
            foreach (int v in tris)
            {
                if (!map.TryGetValue(v, out int o)) { map[v] = o = p2.Count; p2.Add(pa[v] + normal * off); }
                t2.Add(o);
            }
            return new SyntheticModel.SurfaceMesh(material, [.. p2], [.. p2.Select(_ => normal)], [.. p2.Select(p => new Vector2(p.X + 0.5f, p.Y))],
                                                  [.. p2.Select(_ => weights)], [(0u, t2.ToArray())]);
        }
        if (piercing)   // the dome's last ring and apex
            meshes.Add(Copy("/mt_c0101b0001_genitalpierce.mtrl", domeTris.Skip(domeTris.Count - 3 * h), 0.002f, domeW));
        if (basePubes)  // the main skin's first ring out from the hole
            meshes.Add(Copy("/mt_c0101b0001_bibopube.mtrl", skinTris.Take(6 * h), 0.001f, bodyW));
        if (feet)
        {
            var at = centre - new Vector3(0, 0.9f, 0);
            Vector3[] fp = [at, at + new Vector3(0.05f, 0, 0), at + new Vector3(0, 0, 0.05f)];
            meshes.Add(new SyntheticModel.SurfaceMesh(Skin, fp, [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
                [.. fp.Select(p => new Vector2(p.X + 0.5f, p.Y))], [hipsW, hipsW, hipsW], [(0u, new[] { 0, 2, 1 })]));
        }
        return SyntheticModel.BuildSurface([], [.. meshes]);
    }
}
