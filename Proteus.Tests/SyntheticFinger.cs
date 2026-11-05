using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Tests;

/// <summary>
/// A finger at real scale for the nail-socket pass: a tube 7 mm across the radius and 40 mm long along +Y, its top (+Z)
/// facing out, with a nail island laid over a socket cut in the skin the way Rue+, YAB+ and LavaBod+ build their hands —
/// the nail floating in the hole, its outline a hair inside the rim and welded to nothing. Everything is skin material,
/// the tube one UV island and every nail its own, so the hand's nail-bed planner reads it as it reads a real hand.
/// </summary>
public static class SyntheticFinger
{
    internal const string Skin = "/mt_c0201b0001_bibo.mtrl";
    internal const string NailMeshMaterial = "/mt_c0201e6017_glv_a.mtrl";
    internal const float R = 0.007f, L = 0.040f;
    internal const int Around = 48, Along = 40;
    /// <summary>The socket in grid cells: columns either side of the top, rows along the finger.</summary>
    internal const int SocketHalf = 4, SocketRow0 = 15, SocketRow1 = 24;
    internal const float Inset = 0.00005f;

    public enum Variant
    {
        OpenSocket, Closed, Sewn, StrayCrack, UnqualifiedOpen, TopAndTip, ClosedOwner, CoincidentQuad, SparseOwner,
        DomedHigh, TwoLoops, WrapAround, WrappedRim, InsetSocket, DuplicatedRest, TwoTriangleOwner,
        /// <summary>A closed finger with a LONG nail: it rises to 10 mm off the skin (Stabbies, LavaBod+).</summary>
        LongNail,
        /// <summary>A closed finger with a small island rising 15 mm off it, as far as a wrist connector hangs.</summary>
        Connector,
        /// <summary>The open socket with two nail layers over it on two attribute switches, atr_gv_a and atr_gv_c
        /// (Stabbies' alternative nails).</summary>
        AttrLayers,
        /// <summary><see cref="AttrLayers"/> with the second layer only half on atr_gv_c.</summary>
        AttrLayersPartly,
        /// <summary>The open socket ringing a patch of skin of its own, too big to be a nail bed, under the nail.</summary>
        SkinPatchInside,
        /// <summary>A closed finger whose nail is half on atr_gv_c.</summary>
        ClosedPartlyHidden,
        /// <summary>The open socket with the finger itself on atr_gv_a.</summary>
        TubeOnSwitch,
        /// <summary>The open socket with a <see cref="Sewn"/> nail wholly on atr_gv_c and a drawn floating nail (on no
        /// switch) over the same rim (#54).</summary>
        SewnHiddenWithFloating,
        /// <summary><see cref="SewnHiddenWithFloating"/> with only half of the sewn nail on atr_gv_c.</summary>
        SewnPartlyHidden,
    }

    internal static float Y(int row) => row * L / (Along - 1);

    /// <summary>A point on a tube of radius <paramref name="r"/> at angle θ from +Z, height y.</summary>
    internal static Vector3 Pos(float theta, float y, float r) => new(r * MathF.Sin(theta), y, r * MathF.Cos(theta));
    internal static Vector3 Out(float theta) => new(MathF.Sin(theta), 0, MathF.Cos(theta));

    private sealed class MeshBuilder
    {
        public readonly List<Vector3> P = [], N = [];
        public readonly List<Vector2> Uv = [];
        public readonly List<(string, float)[]?> W = [];
        public readonly List<int> T = [];
        public int Add(Vector3 p, Vector3 n, Vector2 uv, (string, float)[]? w = null)
        { P.Add(p); N.Add(n); Uv.Add(uv); W.Add(w); return P.Count - 1; }
        public void Tri(int a, int b, int c) { T.Add(a); T.Add(b); T.Add(c); }
    }

    private static (string, float)[] Weight(float y) => [("j_te_a", 1f - y / L), ("j_te_b", y / L)];

    /// <summary>
    /// Build a finger. <paramref name="weights"/> skins it to two bones varying along its length;
    /// <paramref name="nailMesh"/> adds a separate non-skin nail riding 0.1 mm over the top nail, as Rue+ has.
    /// <paramref name="tubeUvScale"/> shrinks the tube's UVs: at 1 its socket rim spans a sixth of the sheet, at 0.25
    /// about what a real hand's does (a few percent).
    /// </summary>
    internal static byte[] Build(Variant v, bool weights = false, bool nailMesh = false, float tubeUvScale = 1f)
    {
        int half = v switch { Variant.WrapAround => 13, Variant.WrappedRim => 6, _ => SocketHalf };
        int row0 = SocketRow0, row1 = SocketRow1;
        // Which cells of the tube are cut away.
        bool Cut(int col, int row)
        {
            int dc = col - Around / 2;   // the top is column Around/2
            switch (v)
            {
                case Variant.Closed or Variant.StrayCrack or Variant.CoincidentQuad or Variant.LongNail or Variant.Connector
                     or Variant.ClosedPartlyHidden:
                    return false;
                case Variant.UnqualifiedOpen:
                    // Two holes touching at one corner: the corner vertex has four open edges, so it is no loop.
                    return (dc >= -half && dc < 0 && row >= row0 && row < 20) || (dc >= 0 && dc < half && row >= 20 && row < row1);
                case Variant.TwoLoops:
                    return dc >= -half && dc < half && row >= row0 && row < row1 && row != 19;
                case Variant.TwoTriangleOwner:
                    return dc == 0 && row == 19;
                default:
                    return dc >= -half && dc < half && row >= row0 && row < row1;
            }
        }

        var m = new MeshBuilder();
        var grid = new int[Around, Along];
        for (int r = 0; r < Along; r++)
            for (int c = 0; c < Around; c++)
            {
                float th = (c - Around / 2) * MathF.Tau / Around;
                grid[c, r] = m.Add(Pos(th, Y(r), R), Out(th), new Vector2(tubeUvScale * c / Around, tubeUvScale * 0.7f * r / (Along - 1)),
                                   weights ? Weight(Y(r)) : null);
            }
        // The stray crack: one triangle takes its own copy of a corner, 30 µm off — four open edges in a tiny loop,
        // three millimetres from the nail.
        int crackFrom = -1, crackTo = -1;
        if (v == Variant.StrayCrack)
        {
            crackFrom = grid[Around / 2, 12];
            var p = m.P[crackFrom];
            crackTo = m.Add(p + new Vector3(0.00003f, 0, 0), m.N[crackFrom], m.Uv[crackFrom], m.W[crackFrom]);
        }
        var restTris = new List<int>();
        for (int r = 0; r < Along - 1; r++)
            for (int c = 0; c < Around; c++)
            {
                if (Cut(c, r)) continue;
                int c1 = (c + 1) % Around;
                int a = grid[c, r], b = grid[c1, r], d = grid[c, r + 1], e = grid[c1, r + 1];
                int b2 = (v == Variant.StrayCrack && b == crackFrom && r == 11) ? crackTo : b;
                restTris.AddRange([a, b2, d]);
                restTris.AddRange([b, e, d]);
            }
        m.T.AddRange(restTris);
        if (v == Variant.DuplicatedRest) m.T.AddRange(restTris);   // an attribute variant's exact second copy

        // The rest quad a coincident nail sits on: its own corners, 30 µm out, one shared with the tube so it stays
        // part of the skin's island.
        if (v == Variant.CoincidentQuad)
        {
            int c0 = Around / 2, r0 = 19;
            int q0 = grid[c0, r0];
            Vector3 Up(int c, int r) { float th = (c - Around / 2) * MathF.Tau / Around; return Pos(th, Y(r), R + 0.00003f); }
            int q1 = m.Add(Up(c0 + 1, r0), Out(0), new Vector2(0.5f, 0.35f));
            int q2 = m.Add(Up(c0, r0 + 1), Out(0), new Vector2(0.5f, 0.36f));
            int q3 = m.Add(Up(c0 + 1, r0 + 1), Out(0), new Vector2(0.51f, 0.36f));
            m.Tri(q0, q1, q2); m.Tri(q1, q3, q2);
        }

        // ---- Nail islands, each its own UV island in the bottom of the sheet. ----
        float uvBase = 0.8f;
        void Patch(float th0, float th1, float y0, float y1, int nc, int nr, Func<float, float, float> radius,
                   bool closedUnder = false)
        {
            var idx = new int[nc + 1, nr + 1];
            for (int j = 0; j <= nr; j++)
                for (int i = 0; i <= nc; i++)
                {
                    float th = th0 + (th1 - th0) * i / nc, y = y0 + (y1 - y0) * j / nr;
                    idx[i, j] = m.Add(Pos(th, y, radius(th, y)), Out(th),
                                      new Vector2(0.4f + 0.2f * i / nc, uvBase + 0.1f * j / nr), weights ? Weight(y) : null);
                }
            for (int j = 0; j < nr; j++)
                for (int i = 0; i < nc; i++)
                {
                    m.Tri(idx[i, j], idx[i + 1, j], idx[i, j + 1]);
                    m.Tri(idx[i + 1, j], idx[i + 1, j + 1], idx[i, j + 1]);
                }
            if (closedUnder)
            {
                // The same outline closed underneath: a pillow with no boundary at all.
                var low = new int[nc + 1, nr + 1];
                for (int j = 0; j <= nr; j++)
                    for (int i = 0; i <= nc; i++)
                    {
                        bool edge = i == 0 || j == 0 || i == nc || j == nr;
                        if (edge) { low[i, j] = idx[i, j]; continue; }
                        float th = th0 + (th1 - th0) * i / nc, y = y0 + (y1 - y0) * j / nr;
                        low[i, j] = m.Add(Pos(th, y, R - 0.0003f), Out(th), new Vector2(0.4f + 0.2f * i / nc, uvBase + 0.1f * j / nr));
                    }
                for (int j = 0; j < nr; j++)
                    for (int i = 0; i < nc; i++)
                    {
                        m.Tri(low[i, j], low[i, j + 1], low[i + 1, j]);
                        m.Tri(low[i + 1, j], low[i, j + 1], low[i + 1, j + 1]);
                    }
            }
            uvBase -= 0.12f;
        }

        float cell = MathF.Tau / Around;
        float thA = -half * cell, thB = half * cell;
        float yA = Y(row0), yB = Y(row1);
        float ti = Inset / R;
        float Proud(float th, float y) => R + 0.0003f;
        int sewnFrom = -1, sewnTo = -1;
        switch (v)
        {
            case Variant.SewnHiddenWithFloating or Variant.SewnPartlyHidden:
                // The sewn nail (its outline the rim), then a floating one over it on no switch.
                sewnFrom = m.T.Count;
                Patch(thA, thB, yA, yB, 2 * half, row1 - row0, (th, y) =>
                {
                    bool rim = MathF.Abs(MathF.Abs(th) - thB) < 1e-6f || MathF.Abs(y - yA) < 1e-6f || MathF.Abs(y - yB) < 1e-6f;
                    return rim ? R : R + 0.0003f;
                });
                sewnTo = m.T.Count;
                Patch(thA + ti, thB - ti, yA + Inset, yB - Inset, 16, 18, (_, _) => R + 0.0006f);
                break;
            case Variant.Sewn:
                // Its outline IS the socket's rim: the same positions, so it is sewn in.
                Patch(thA, thB, yA, yB, 2 * half, row1 - row0, (th, y) =>
                {
                    bool rim = MathF.Abs(MathF.Abs(th) - thB) < 1e-6f || MathF.Abs(y - yA) < 1e-6f || MathF.Abs(y - yB) < 1e-6f;
                    return rim ? R : R + 0.0003f;
                });
                break;
            case Variant.TopAndTip:
            {
                float yM = Y(22);
                Patch(thA + ti, thB - ti, yA + Inset, yM, 16, 14, Proud);
                // The tip: over the far end of the socket and on past the finger, apart from the top.
                Patch(thA / 4, thB / 4, yM + 0.0002f, yB + 0.004f, 2, 4, Proud);
                break;
            }
            case Variant.ClosedOwner:
                Patch(thA + ti, thB - ti, yA + Inset, yB - 0.0025f, 12, 10, Proud, closedUnder: true);
                Patch(thA + ti, thB - ti, yB - 0.0023f, yB - Inset, 8, 2, Proud);
                break;
            case Variant.CoincidentQuad:
            {
                float t0 = 0, t1 = cell;
                Patch(t0, t1, Y(19), Y(20), 1, 1, (_, _) => R + 0.00003f);
                break;
            }
            case Variant.SparseOwner:
                Patch(thA + ti, thB - ti, yA + Inset, yB - Inset, 3, 4, Proud);
                break;
            case Variant.DomedHigh:
                Patch(thA + ti, thB - ti, yA + Inset, yB - Inset, 16, 18, (th, y) =>
                {
                    float u = (th - thA) / (thB - thA) * 2 - 1, w = (y - yA) / (yB - yA) * 2 - 1;
                    return R + 0.00005f + 0.001f * MathF.Max(0f, 1 - u * u) * MathF.Max(0f, 1 - w * w);
                });
                break;
            case Variant.InsetSocket:
                Patch(thA + ti, thB - ti, yA + Inset, yB - Inset, 16, 18, (_, _) => R - 0.0004f);
                break;
            case Variant.TwoTriangleOwner:
                Patch(ti, cell - ti, Y(19) + Inset, Y(20) - Inset, 1, 1, Proud);
                break;
            case Variant.StrayCrack or Variant.Closed:
                Patch(thA + ti, thB - ti, yA + Inset, yB - Inset, 16, 18, Proud);
                break;
            case Variant.LongNail or Variant.Connector:
            {
                // Rising off the closed finger along its length, to 10 mm (a long nail) or 15 mm (a connector) at the end.
                float rise = v == Variant.LongNail ? 0.010f : 0.015f;
                Patch(thA, thB, yA, yB, 8, 9, (_, y) => R + 0.0003f + rise * (y - yA) / (yB - yA));
                break;
            }
            default:
                Patch(thA + ti, thB - ti, yA + Inset, yB - Inset, 16, 18, Proud);
                break;
        }

        // The second nail layer of AttrLayers, just over the first, on a switch of its own.
        int layerB = m.T.Count;
        if (v is Variant.AttrLayers or Variant.AttrLayersPartly)
            Patch(thA + ti, thB - ti, yA + Inset, yB - Inset, 16, 18, (_, _) => R + 0.0006f);

        // A patch of skin inside the socket, under the nail: an island of its own, too big to be a nail bed.
        if (v == Variant.SkinPatchInside)
        {
            const int nc = 28, nr = 18;
            float g = 0.0005f;
            var idx = new int[nc + 1, nr + 1];
            for (int j = 0; j <= nr; j++)
                for (int i = 0; i <= nc; i++)
                {
                    float y = yA + g + (yB - yA - 2 * g) * j / nr;
                    float th = thA + g / R + (thB - thA - 2 * g / R) * i / nc;
                    float u = 2f * i / nc - 1, w = 2f * j / nr - 1;
                    idx[i, j] = m.Add(Pos(th, y, R - 0.0002f + 0.0003f * (1 - u * u) * (1 - w * w)), Out(th),
                                      new Vector2(0.7f + 0.25f * i / nc, 0.72f + 0.06f * j / nr), weights ? Weight(y) : null);
                }
            for (int j = 0; j < nr; j++)
                for (int i = 0; i < nc; i++)
                {
                    m.Tri(idx[i, j], idx[i + 1, j], idx[i, j + 1]);
                    m.Tri(idx[i + 1, j], idx[i + 1, j + 1], idx[i, j + 1]);
                }
        }

        (uint, int[])[] submeshes = [(0u, [.. m.T])];
        if (v == Variant.TubeOnSwitch)
            submeshes = [(1u, [.. m.T.Take(restTris.Count)]), (0u, [.. m.T.Skip(restTris.Count)])];
        if (v == Variant.ClosedPartlyHidden)
        {
            // The nail's first half on atr_gv_c (bit 1).
            int tube = restTris.Count, nailHalf = tube + (m.T.Count - tube) / 6 * 3;
            submeshes = [(0u, [.. m.T.Take(tube), .. m.T.Skip(nailHalf)]), (2u, [.. m.T.Skip(tube).Take(nailHalf - tube)])];
        }
        if (v is Variant.AttrLayers or Variant.AttrLayersPartly)
        {
            // The tube on no switch, the first layer on atr_gv_a (bit 0), the second on atr_gv_c (bit 1) — all of it, or
            // only its first half (the rest on no switch).
            int tube = restTris.Count, bHalf = v == Variant.AttrLayersPartly ? layerB + (m.T.Count - layerB) / 6 * 3 : m.T.Count;
            submeshes =
            [
                (0u, [.. m.T.Take(tube), .. m.T.Skip(bHalf)]),
                (1u, [.. m.T.Skip(tube).Take(layerB - tube)]),
                (2u, [.. m.T.Skip(layerB).Take(bHalf - layerB)]),
            ];
        }
        if (v is Variant.SewnHiddenWithFloating or Variant.SewnPartlyHidden)
        {
            // The tube and the floating nail on no switch; the sewn nail on atr_gv_c (bit 1) — all of it, or its first half.
            int hideTo = v == Variant.SewnPartlyHidden ? sewnFrom + (sewnTo - sewnFrom) / 6 * 3 : sewnTo;
            submeshes =
            [
                (0u, [.. m.T.Take(sewnFrom), .. m.T.Skip(hideTo)]),
                (2u, [.. m.T.Skip(sewnFrom).Take(hideTo - sewnFrom)]),
            ];
        }
        var meshes = new List<SyntheticModel.SurfaceMesh>
        {
            new(Skin, [.. m.P], [.. m.N], [.. m.Uv], [.. m.W], submeshes),
        };
        if (nailMesh)
        {
            // A non-skin nail riding 0.1 mm over the top nail, as Rue+ carries one.
            var n = new MeshBuilder();
            int nc = 8, nr = 9;
            var idx = new int[nc + 1, nr + 1];
            for (int j = 0; j <= nr; j++)
                for (int i = 0; i <= nc; i++)
                {
                    float th = thA + ti + (thB - thA - 2 * ti) * i / nc, y = yA + Inset + (yB - yA - 2 * Inset) * j / nr;
                    idx[i, j] = n.Add(Pos(th, y, R + 0.0004f), Out(th), new Vector2((float)i / nc, (float)j / nr), weights ? Weight(y) : null);
                }
            for (int j = 0; j < nr; j++)
                for (int i = 0; i < nc; i++)
                {
                    n.Tri(idx[i, j], idx[i + 1, j], idx[i, j + 1]);
                    n.Tri(idx[i + 1, j], idx[i + 1, j + 1], idx[i, j + 1]);
                }
            meshes.Add(new(NailMeshMaterial, [.. n.P], [.. n.N], [.. n.Uv], [.. n.W], [(0u, [.. n.T])]));
        }
        return SyntheticModel.BuildSurface(v is Variant.AttrLayers or Variant.AttrLayersPartly or Variant.ClosedPartlyHidden or Variant.TubeOnSwitch
                                                 or Variant.SewnHiddenWithFloating or Variant.SewnPartlyHidden
                                           ? ["atr_gv_a", "atr_gv_c"] : [], [.. meshes]);
    }
}
