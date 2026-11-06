using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

public static partial class SecondSkinWriter
{
    /// <summary>Least summed toe-bone weight (<see cref="IsToeBone"/>) every corner of a toe triangle carries.</summary>
    internal const float ToeRegionWeight = 0.5f;

    /// <summary>Fewer lit texels than this is no toe region (a stray weighted triangle).</summary>
    internal const int ToeRegionMinTexels = 16;

    /// <summary>
    /// Where the toes are in the bodies' UV, at <paramref name="size"/>²: every LOD0 skin triangle whose three corners
    /// each carry at least <see cref="ToeRegionWeight"/> on toe bones, lit 255 (a sub-texel triangle lights its bounding
    /// texels, as <see cref="CapFootprintMask"/> does). Read only, writes nothing. Null when fewer than
    /// <see cref="ToeRegionMinTexels"/> texels are lit — no toe-weighted skin in this UV (#62: the built-in toe cap's
    /// coverage gate is measured over this, not the whole sheet).
    /// </summary>
    internal static byte[]? ToeRegionMask(IEnumerable<byte[]> bodies, int size)
    {
        var mask = new byte[size * size];
        static float ToeWeight((string Bone, float W)[] ws)
        {
            float s = 0f;
            foreach (var (bone, w) in ws) if (IsToeBone(bone)) s += w;
            return s;
        }
        int Wrap(int v) => (v % size + size) % size;
        foreach (var t in CollectSkinTriangles(bodies.ToList(), skinOnly: true, dropIslands: false))
        {
            if (ToeWeight(t.Wa) < ToeRegionWeight || ToeWeight(t.Wb) < ToeRegionWeight || ToeWeight(t.Wc) < ToeRegionWeight)
                continue;
            float ax = t.Ua.U * size, ay = t.Ua.V * size, bx = t.Ub.U * size, by = t.Ub.V * size;
            float cx = t.Uc.U * size, cy = t.Uc.V * size;
            int x0 = (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))), x1 = (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx)));
            int y0 = (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))), y1 = (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy)));
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 1 << 18) continue;   // a seam-straddling triangle
            float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            bool small = x1 - x0 <= 2 && y1 - y0 <= 2;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    bool inside = small;
                    if (!inside && MathF.Abs(den) > 1e-12f)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float l1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den;
                        float l2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                        inside = l1 >= 0 && l2 >= 0 && 1 - l1 - l2 >= 0;
                    }
                    if (inside) mask[Wrap(y) * size + Wrap(x)] = 255;
                }
        }
        int lit = 0;
        foreach (var b in mask) if (b != 0) lit++;
        return lit >= ToeRegionMinTexels ? mask : null;
    }
}
