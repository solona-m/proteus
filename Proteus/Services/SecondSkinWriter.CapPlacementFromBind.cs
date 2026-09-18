using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

public static partial class SecondSkinWriter
{
    private sealed partial class CapPlacementFromBind
    {
        private readonly byte[] bind;
        private readonly IReadOnlyList<byte[]> bodies;
        private readonly Action<string>? diag;
        private readonly byte[]? capMdl;
        private readonly int stride;
        private List<SkinTri> tris = null!;
        private List<SkinTri> allTris = null!;
        private BinaryReader r = null!;
        private int version;
        private int meshCount;
        private List<CapPlacement> outp = null!;
        private List<CapPlacement>? result;

        public CapPlacementFromBind(byte[] bind, IReadOnlyList<byte[]> bodies, Action<string>? diag, byte[]? capMdl, int stride)
        {
            this.bind = bind;
            this.bodies = bodies;
            this.diag = diag;
            this.capMdl = capMdl;
            this.stride = stride;
        }

        public List<CapPlacement>? Run()
        {
            if (!ReadBinding()) return result;
            return PlaceMeshes();
        }

        private bool ReadBinding()
        {
            if (bind.Length < 12 || BitConverter.ToUInt32(bind, 0) != CapBindMagic) { result = null; return false; }
            tris = BindSurface(bodies);
            if (tris.Count == 0) { result = null; return false; }
            // The unfiltered surface, kept for the nail-socket discs and the diagnostic; tris is reassigned to
            // the bone-filtered subset below.
            allTris = tris;

            r = new BinaryReader(new MemoryStream(bind));
            r.ReadUInt32();
            version = r.ReadInt32();
            // 1: (u, v, offset, side, facing). 2: the same plus a residual in the landing's tangent frame.
            // Version 1 still loads — a cap bound before the residual existed is imperfect, not unusable.
            if (version is not (1 or 2)) { diag?.Invoke($"cap bind: version {version} not understood"); result = null; return false; }

            int partCount = r.ReadInt32();
            var parts = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < partCount; i++) parts.Add(r.ReadString());
            // Only the part of the body the cap was bound to. See the note in BakeCapBind: without this the
            // toe cap finds its own atlas coordinate on the torso and lands at the waist.
            tris = tris.Where(t => t.Wa.Any(x => parts.Contains(x.Bone))
                                || t.Wb.Any(x => parts.Contains(x.Bone))
                                || t.Wc.Any(x => parts.Contains(x.Bone))).ToList();
            if (tris.Count == 0) { diag?.Invoke("cap bind: this body has none of the bound bones"); result = null; return false; }
            // Diagnostic only, and gated on someone listening: the scoring probe passes diag: null.
            if (diag != null)
            {
                (float, float, float, float) Span(List<SkinTri> ts)
                {
                    float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
                    foreach (var t in ts)
                        foreach (var c in new[] { t.Ua, t.Ub, t.Uc })
                        {
                            var tile = TileOf(t);
                            u0 = MathF.Min(u0, c.U - tile.U); u1 = MathF.Max(u1, c.U - tile.U);
                            v0 = MathF.Min(v0, c.V - tile.V); v1 = MathF.Max(v1, c.V - tile.V);
                        }
                    return (u0, u1, v0, v1);
                }
                var sp = Span(tris);
                diag($"cap bind: {tris.Count} of {allTris.Count} triangle(s) carry the bound bones; "
                   + $"their atlas spans u {sp.Item1:F3}..{sp.Item2:F3} v {sp.Item3:F3}..{sp.Item4:F3}");
            }

            meshCount = r.ReadInt32();

            outp = new List<CapPlacement>();
            return true;
        }

        private List<CapPlacement>? PlaceMeshes()
        {
            for (int mi = 0; mi < meshCount; mi++)
            {
                PlaceMesh();
            }
            return outp;
        }

        /// <summary>Places one cap mesh on the body from its binding, or reports why it cannot be placed.</summary>
        private void PlaceMesh()
        {
            new CapMeshPlacement(this).Run();
        }
    }
}
