using System;
using System.Collections.Generic;

namespace Proteus.Services;
using static Proteus.Services.SecondSkinWriter;
using static Proteus.Services.MeshMath;

internal static partial class BodyBridge
{
    private sealed class BodySmoothing
    {
        private readonly byte[] mdl;
        private readonly SecondSkinLayer gate;
        private readonly float nippleStrength;
        private readonly Action<string>? log;
        private readonly float foldStrength;
        private Source src = null!;
        private byte[] outBytes = null!;
        private byte[] s = null!;
        private int meshesTouched;
        private int vertsMoved;
        private int normalsWritten;
        private float most;
        private List<string> matNames = null!;
        private List<Vec3> skinAt = null!;
        private List<Vec3> skinMove = null!;
        private List<(Vec3 A, Vec3 B, Vec3 C, Vec3 N)> foldTris = null!;
        private int end;
        private byte[]? result;

        public BodySmoothing(byte[] mdl, SecondSkinLayer gate, float nippleStrength, Action<string>? log, float foldStrength)
        {
            this.mdl = mdl;
            this.gate = gate;
            this.nippleStrength = nippleStrength;
            this.log = log;
            this.foldStrength = foldStrength;
        }

        public byte[]? Run()
        {
            if (!Prepare()) return result;
            if (!SmoothMeshes()) return result;
            return CarrySkinMoves();
        }

        private bool Prepare()
        {
            if (mdl is not { Length: > 0 } || (nippleStrength <= 0f && foldStrength <= 0f)) { result = null; return false; }

            
            try { src = Parse(mdl); }
            catch { result = null; return false; }

            outBytes = (byte[])mdl.Clone();
            s = src.S;
            meshesTouched = 0;
            vertsMoved = 0;
            normalsWritten = 0;
            most = 0f;

            // Skin meshes only, by the shell's filter: a body model also carries smallclothes, nails and piercings.
            matNames = ReadMaterialNames(s, src);
            // Where the skin moved, for carrying the move onto the meshes riding on it.
            skinAt = new List<Vec3>();
            skinMove = new List<Vec3>();
            foldTris = new List<(Vec3 A, Vec3 B, Vec3 C, Vec3 N)>();

            end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
            return true;
        }

        private bool SmoothMeshes()
        {
            // Outside the mesh loop: a stackalloc inside it grows the frame until return.
            Span<float> tmp = stackalloc float[4];
            for (int m = src.Lod0MeshIndex; m < end; m++)
            {
                int mo = src.MeshStart + m * 36;
                if (mo + 36 > s.Length) break;
                ushort vc = BitConverter.ToUInt16(s, mo);
                if (vc == 0) continue;

                ushort matIdx = BitConverter.ToUInt16(s, mo + 8);
                if (matIdx >= matNames.Count || SkinMaterialBodyType(matNames[matIdx]) == null) continue;

                var decl = m < src.Decls.Length ? src.Decls[m] : [];
                VElem? pe = null, ne = null, ue = null;
                foreach (var el in decl)
                {
                    if (el.Usage == UsePosition) pe ??= el;
                    else if (el.Usage == UseNormal) ne ??= el;
                    else if (el.Usage == UseUV && el.UsageIndex == 0) ue ??= el;
                }
                if (pe is not { } pos || ne is not { } nrm || ue is not { } uvE) continue;
                if (pos.Stream > 2 || nrm.Stream > 2 || uvE.Stream > 2) continue;

                uint[] vbo = { BitConverter.ToUInt32(s, mo + 20), BitConverter.ToUInt32(s, mo + 24),
                               BitConverter.ToUInt32(s, mo + 28) };
                byte[] bs = { s[mo + 32], s[mo + 33], s[mo + 34] };
                if (bs[pos.Stream] == 0) continue;

                // Meshes naming neither bust bone drop out cheaply; the fold rides the legs part, not the torso.
                var bust = nippleStrength > 0f
                    ? MeshRegionWeights(src, m, vc, decl, vbo, bs, BustBones)
                    : null;
                var fold = foldStrength > 0f
                    ? MeshRegionWeights(src, m, vc, decl, vbo, bs, [HipBone], [ThighBoneL, ThighBoneR])
                    : null;
                // The same region without the thigh veto, for the fold's relax. See FoldPlan's `near`.
                var foldNear = fold != null
                    ? MeshRegionWeights(src, m, vc, decl, vbo, bs, [HipBone, ThighBoneL, ThighBoneR])
                    : null;
                if (bust == null && fold == null) continue;

                ushort subIdx = BitConverter.ToUInt16(s, mo + 10), subCount = BitConverter.ToUInt16(s, mo + 12);
                var tris = MeshTriangles(src, subIdx, subCount);
                if (tris.Length < 3) continue;

                var p3 = new Vec3[vc];
                var n3 = new Vec3[vc];
                var uv = new (float U, float V)[vc];
                for (int i = 0; i < vc; i++)
                {
                    ReadTyped(s, src.Vb + (int)vbo[pos.Stream] + i * bs[pos.Stream] + pos.Offset, pos.Type, tmp);
                    p3[i] = new Vec3(tmp[0], tmp[1], tmp[2]);
                    ReadTyped(s, src.Vb + (int)vbo[nrm.Stream] + i * bs[nrm.Stream] + nrm.Offset, nrm.Type, tmp);
                    float nx = tmp[0], ny = tmp[1], nz = tmp[2];
                    if (nrm.Type == 8) { nx = nx * 2 - 1; ny = ny * 2 - 1; nz = nz * 2 - 1; }
                    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    n3[i] = len > 1e-6f ? new Vec3(nx / len, ny / len, nz / len) : new Vec3(0, 0, 1);
                    ReadTyped(s, src.Vb + (int)vbo[uvE.Stream] + i * bs[uvE.Stream] + uvE.Offset, uvE.Type, tmp);
                    uv[i] = (tmp[0], tmp[1]);
                }

                // Sampled with WRAP, like every other body-UV read here: a body's UVs need not sit in the
                // [0,1] tile, and wrapping makes an integer tile offset irrelevant rather than putting a whole
                // mesh on row 0.
                var covered = CoveredVertices(uv, gate, vc);

                var plan = bust == null ? null
                      // pinBoundary: the body's part-seam rims must not move, same as for the fold.
                    : BustBridgeSolve(p3, n3, tris, bust, 0f, log, covered, nippleStrength,
                                      pinBoundary: true);

                if (fold != null && foldNear != null)
                    plan = MergePlans(plan, FoldPlan(p3, n3, tris, fold, foldNear, covered, foldStrength, log));

                if (plan == null) continue;

                // Reshade: the shell is cut from this and pushed out along its normals, so a stale normal
                // splays the garment as well as mis-shading the skin.
                var finalNrm = RelaxedNormals(p3, n3, plan.Delta, plan.NodeOf, plan.NodeWeight,
                                              plan.NodeNormal, tris);

                int stride = bs[pos.Stream];
                int nStride = bs[nrm.Stream];
                for (int i = 0; i < vc; i++)
                {
                    // A vertex moves only if its own delta is real, but reshades anywhere in the relaxed region:
                    // a normal depends on its neighbours.
                    var d = plan.Delta[i];
                    float mag = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                    skinAt.Add(p3[i]);
                    skinMove.Add(mag > BustBridgeEpsilon ? d : default);
                    if (mag > BustBridgeEpsilon)
                    {
                        WriteXYZ(outBytes, src.Vb + (int)vbo[pos.Stream] + i * stride + pos.Offset, pos.Type,
                                 p3[i].X + d.X, p3[i].Y + d.Y, p3[i].Z + d.Z);
                        vertsMoved++;
                        most = MathF.Max(most, mag);
                    }

                    if (plan.NodeWeight[plan.NodeOf[i]] <= 0f) continue;
                    var fn = finalNrm[i];
                    if (WriteNormal(outBytes, src.Vb + (int)vbo[nrm.Stream] + i * nStride + nrm.Offset,
                                    nrm.Type, fn.X, fn.Y, fn.Z))
                        normalsWritten++;
                }
                // Every covered skin triangle round the smoothed area, moved or not, for PullBehindSkin.
                float fx0 = float.MaxValue, fy0 = float.MaxValue, fz0 = float.MaxValue;
                float fx1 = float.MinValue, fy1 = float.MinValue, fz1 = float.MinValue;
                if (fold != null)
                    for (int i = 0; i < vc; i++)
                    {
                        if (Len(plan.Delta[i]) <= BustBridgeEpsilon) continue;
                        fx0 = MathF.Min(fx0, p3[i].X); fy0 = MathF.Min(fy0, p3[i].Y); fz0 = MathF.Min(fz0, p3[i].Z);
                        fx1 = MathF.Max(fx1, p3[i].X); fy1 = MathF.Max(fy1, p3[i].Y); fz1 = MathF.Max(fz1, p3[i].Z);
                    }
                if (fold != null && fx1 >= fx0)
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                        if (a >= vc || b >= vc || c >= vc) continue;
                        // Only skin the garment covers.
                        if (covered != null && (!covered[a] || !covered[b] || !covered[c])) continue;
                        var pa = p3[a];
                        if (pa.X < fx0 - PullReach || pa.X > fx1 + PullReach || pa.Y < fy0 - PullReach || pa.Y > fy1 + PullReach
                            || pa.Z < fz0 - PullReach || pa.Z > fz1 + PullReach) continue;
                        Vec3 At(int v) => new(p3[v].X + plan.Delta[v].X, p3[v].Y + plan.Delta[v].Y, p3[v].Z + plan.Delta[v].Z);
                        foldTris.Add((At(a), At(b), At(c),
                            Normalize(new Vec3(finalNrm[a].X + finalNrm[b].X + finalNrm[c].X,
                                               finalNrm[a].Y + finalNrm[b].Y + finalNrm[c].Y,
                                               finalNrm[a].Z + finalNrm[b].Z + finalNrm[c].Z)) ?? finalNrm[a]));
                    }
                meshesTouched++;
            }

            if (vertsMoved == 0) { result = null; return false; }
            log?.Invoke($"body smooth: {vertsMoved} vertex(es) across {meshesTouched} mesh(es), "
                      + $"moved by up to {most:0.#####}, {normalsWritten} normal(s) reshaded");
            return true;
        }

        private byte[]? CarrySkinMoves()
        {
            // Non-skin meshes resting on the skin move with it: each vertex takes the inverse-distance average of the
            // nearby skin's movement, fading with distance.
            int carried = CarrySkinMove(outBytes, src, matNames, skinAt, skinMove);
            if (carried > 0)
                log?.Invoke($"body smooth: {carried} vertex(es) of the meshes on the skin moved with it");

            // Whatever of another mesh still sticks out in front of the smoothed crotch is pulled back behind it.
            if (foldTris.Count > 0)
            {
                int pulled = PullBehindSkin(outBytes, src, matNames, foldTris, out float deepest);
                if (pulled > 0)
                    log?.Invoke($"body smooth: {pulled} vertex(es) of the meshes on the skin pulled back behind the smoothed "
                              + $"crotch, by up to {deepest * 1000:0.#}mm");
            }
            return outBytes;
        }
    }
}
