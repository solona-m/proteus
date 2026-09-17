using System;
using System.Collections.Generic;

namespace Proteus.Services;
using static Proteus.Services.MeshMath;

public static partial class SecondSkinWriter
{
    private sealed partial class JoinCutPlan
    {
        private readonly IReadOnlyList<ConnectorProfile?> profiles;
        private readonly float coverEps;
        private readonly Action<string>? diag;
        private Dictionary<int, HashSet<ushort>>[] del = null!;
        private Dictionary<(long, long, long), List<(int Src, Vec3 P)>> vgrid = null!;
        private CoverGrid surface = null!;

        public JoinCutPlan(IReadOnlyList<ConnectorProfile?> profiles, float coverEps, Action<string>? diag)
        {
            this.profiles = profiles;
            this.coverEps = coverEps;
            this.diag = diag;
        }

        public Dictionary<int, HashSet<ushort>>[] Run(out int flapVerts)
        {
            flapVerts = 0;
            BucketVertices(ref flapVerts);
            IndexSurfaces();
            return PlanParts(ref flapVerts);
        }

        private void BucketVertices(ref int flapVerts)
        {
            flapVerts = 0;
            del = new Dictionary<int, HashSet<ushort>>[profiles.Count];
            for (int i = 0; i < del.Length; i++) del[i] = [];

            // Every part's vertices, bucketed at the weld radius, tagged with the part they came from.
            vgrid = new Dictionary<(long, long, long), List<(int Src, Vec3 P)>>();
            for (int i = 0; i < profiles.Count; i++)
            {
                if (profiles[i] is not { } p) continue;
                foreach (var mesh in p.Meshes)
                {
                    // Only vertices a triangle uses. A profile with submeshes taken out (ConnectorProfile.Without)
                    // still holds their positions, and a vertex nothing draws must not make a ring.
                    var used = new bool[mesh.Pos.Length / 3];
                    foreach (ushort u in mesh.Tris) if (u < used.Length) used[u] = true;
                    for (int v = 0; v < mesh.Pos.Length / 3; v++)
                    {
                        if (!used[v]) continue;
                        var q = new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]);
                        var key = VCell(q);
                        if (!vgrid.TryGetValue(key, out var list)) vgrid[key] = list = [];
                        list.Add((i, q));
                    }
                }
            }
        }

        private void IndexSurfaces()
        {
            // Every part's SURFACE, for the coverage judgement, tagged by part.
            surface = new CoverGrid(coverEps);
            for (int i = 0; i < profiles.Count; i++)
            {
                if (profiles[i] is not { } p) continue;
                foreach (var mesh in p.Meshes)
                    for (int t = 0; t < mesh.Tris.Length / 3; t++)
                    {
                        ushort ia = mesh.Tris[t * 3], ib = mesh.Tris[t * 3 + 1], ic = mesh.Tris[t * 3 + 2];
                        surface.Add(new Vec3(mesh.Pos[ia * 3], mesh.Pos[ia * 3 + 1], mesh.Pos[ia * 3 + 2]),
                                    new Vec3(mesh.Pos[ib * 3], mesh.Pos[ib * 3 + 1], mesh.Pos[ib * 3 + 2]),
                                    new Vec3(mesh.Pos[ic * 3], mesh.Pos[ic * 3 + 1], mesh.Pos[ic * 3 + 2]), i);
                    }
            }
        }

        private Dictionary<int, HashSet<ushort>>[] PlanParts(ref int flapVerts)
        {
            for (int src = 0; src < profiles.Count; src++)
            {
                PlanPart(ref flapVerts, src);
            }

            return del;
        }

        /// <summary>Finds the flap one part draws past its joins with the parts around it.</summary>
        private void PlanPart(ref int flapVerts, int src)
        {
            if (profiles[src] is not { } profile) return;
            foreach (var mesh in profile.Meshes)
            {
                PlanMesh(ref flapVerts, src, profile, mesh);
            }
        }

        /// <summary>Finds the vertices of one mesh that lie past a join with another part.</summary>
        private void PlanMesh(ref int flapVerts, int src, ConnectorProfile profile, ConnectorProfile.MeshProfile mesh)
        {
            new MeshJoinCut(this, src, profile, mesh).Run(ref flapVerts);
        }

        private (long, long, long) VCell(Vec3 p) => ((long)MathF.Floor(p.X / JoinWeld),
                                             (long)MathF.Floor(p.Y / JoinWeld),
                                             (long)MathF.Floor(p.Z / JoinWeld));

        // How many of a component's non-ring vertices lie within the extent of a part it is JOINED to (shares
        // ring vertices with), widened by FlapTuckReach. Only joined parts: another part's extent may span the hips.
        private int InsideJoinedParts(ConnectorProfile.MeshProfile mesh, HashSet<ushort> comp, bool[] ringOf, int src)
        {
            var joined = new HashSet<int>();
            foreach (ushort v in comp)
            {
                if (!ringOf[v]) continue;
                var q = new Vec3(mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2]);
                var (cx, cy, cz) = VCell(q);
                for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                for (long dz = -1; dz <= 1; dz++)
                    if (vgrid.TryGetValue((cx + dx, cy + dy, cz + dz), out var others))
                        foreach (var (osrc, op) in others)
                            if (osrc != src && Dist(q, op) <= JoinWeld) joined.Add(osrc);
            }
            int inside = 0;
            foreach (ushort v in comp)
            {
                if (ringOf[v]) continue;
                float x = mesh.Pos[v * 3], y = mesh.Pos[v * 3 + 1], z = mesh.Pos[v * 3 + 2];
                foreach (int j in joined)
                {
                    if (profiles[j]?.PartBox is not { Empty: false } b) continue;
                    if (x >= b.MinX - FlapTuckReach && x <= b.MaxX + FlapTuckReach
                        && y >= b.MinY - FlapTuckReach && y <= b.MaxY + FlapTuckReach
                        && z >= b.MinZ - FlapTuckReach && z <= b.MaxZ + FlapTuckReach)
                    { inside++; break; }
                }
            }
            return inside;
        }
    }
}
