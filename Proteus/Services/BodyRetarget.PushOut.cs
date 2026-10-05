using System;
using System.Collections.Generic;
using System.Numerics;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

internal static partial class BodyRetarget
{
    /// <summary>
    /// The skin that will actually be DRAWN under the garment once it is worn — what the cloth must end up outside of.
    /// <para/>
    /// That is not the target body. A worn garment REPLACES its own slot's body model: put on a top and the game draws
    /// the top instead of the body's chest, so the chest body is never on screen while the top is. What is drawn in its
    /// place is the garment's own embedded body mesh, retargeted along with everything else — plus the bodies of the
    /// OTHER slots, which still render beside it, like the legs a top's hem hangs over.
    /// <para/>
    /// Pushing against the garment's own slot body was measured and is actively harmful. "This Old Thing" compresses
    /// the chest under its top, so its cloth legitimately sits inside the Neolithe body; testing against that body
    /// pushed 825 nodes out by up to 8 mm and made the refit 50% worse against the author's own hand-fitted size.
    /// <para/>
    /// The inside test is a signed distance along the interpolated normal at the nearest point rather than a winding
    /// number. The garment's own body mesh is an open patch — whatever part of the body the garment happens to show —
    /// and a winding number over an open patch is meaningless. Within <see cref="PushProbeRange"/>, which is all this
    /// pass looks at, the local test is also simply correct.
    /// </summary>
    internal sealed class TargetBody
    {
        private readonly BodySurface[] surfaces;

        /// <summary>
        /// Per surface: whether cloth found inside it AS AUTHORED can be taken for the author's doing. False for a legs
        /// garment's chest body — see <see cref="Build"/>.
        /// </summary>
        private readonly bool[] witnessed;

        private TargetBody(BodySurface[] surfaces, bool[] witnessed)
        {
            this.surfaces = surfaces;
            this.witnessed = witnessed;
        }

        /// <param name="garmentSlot">The slot the garment is worn in, whose body is therefore not drawn. Null to keep
        /// every slot's body, for a caller that cannot say.</param>
        /// <param name="garmentSkin">The garment whose body-skin parts are indexed — as authored for the BEFORE surface,
        /// with the transfer applied for the AFTER one; null when the garment carries no body mesh of its own.</param>
        /// <param name="before">Build the skin drawn under the garment as it was AUTHORED — the source bodies — rather
        /// than as it will be after the refit.</param>
        /// <remarks>
        /// A legs garment's CHEST body, as authored, says nothing about what its author fitted the waist to. Over the
        /// belly the skin on screen is the worn top's own body mesh, and a top's author sculpts that freely; the waist of
        /// legs made to go with it is fitted to the sculpted skin, not to the body mod's chest. Measured on "Victoria's
        /// Secret Sauce" (Bibo+): its top pulls the belly in 3-4 mm, its bottoms' high waist sits up to 4.3 mm inside
        /// the plain Bibo+ chest and 1 mm inside the top's own skin. Refitted to Tre, the top's skin is swapped for
        /// Tre's full belly, and the waist — read as tucked under the skin on purpose — was left 4 mm inside it at
        /// 184 points over the front alone. So cloth inside this surface as authored is a clip to undo like any other,
        /// down to <see cref="ClearDepth"/>. A top's legs pair keeps the rule: under a top's hem the legs on screen are
        /// the body's own or a pair of trousers, never another garment's sculpted belly, and the authors' own sizes
        /// were measured with it.
        /// </remarks>
        public static TargetBody Build(IReadOnlyList<SlotPair> pairs, string? garmentSlot, ModelParts? garmentSkin,
                                       bool before)
        {
            var surfaces = new List<BodySurface>();
            var witnessed = new List<bool>();
            foreach (var pair in pairs)
            {
                if (garmentSlot != null && string.Equals(pair.Slot, garmentSlot, StringComparison.Ordinal)) continue;
                var model = before ? pair.Correspondence.Source : pair.Target;
                var body = new BodySurface(model, BodySurface.CellFor(MeanEdgeOf(model)));
                if (body.IsEmpty) continue;
                surfaces.Add(body);
                witnessed.Add(!(garmentSlot == "_dwn" && pair.Slot == "_top"));
            }

            if (garmentSkin != null)
            {
                var own = new BodySurface(garmentSkin, BodySurface.CellFor(MeanEdgeOf(garmentSkin)));
                if (!own.IsEmpty)
                {
                    surfaces.Add(own);
                    witnessed.Add(true);
                }
            }

            return new TargetBody(surfaces.ToArray(), witnessed.ToArray());
        }

        /// <summary>
        /// The drawn skin this point is deepest INSIDE, or the nearest one when it is outside them all.
        /// <para/>
        /// Not simply the nearest: two of these surfaces lie within a millimetre of each other wherever a garment
        /// carries its own copy of the body, and cloth between them is outside one and inside the other. Judged by the
        /// nearer, such a point reads as clear while the other surface goes straight through it — which is what a
        /// stocking looked like with the body showing through the fabric all up the leg.
        /// </summary>
        public bool Deepest(Vector3 p, float maxDistance, out BodySurface.Hit hit)
        {
            hit = default;
            bool found = false;
            float worst = 0f;
            foreach (var surface in surfaces)
            {
                if (!surface.Nearest(p, maxDistance, out var candidate)) continue;
                float signed = Vector3.Dot(p - candidate.Point, candidate.Normal);
                if (found && signed >= worst) continue;
                worst = signed;
                hit = candidate;
                found = true;
            }
            return found;
        }

        /// <summary>Every point of the drawn skin, with its normal.</summary>
        /// <param name="witnessedOnly">Only the surfaces cloth can have been authored inside of — see
        /// <see cref="Build"/>.</param>
        public IEnumerable<(Vector3 At, Vector3 Normal)> Points(bool witnessedOnly = false)
        {
            for (int s = 0; s < surfaces.Length; s++)
            {
                if (witnessedOnly && !witnessed[s]) continue;
                var surface = surfaces[s];
                foreach (int v in surface.SkinVertices)
                    yield return (surface.PositionOf(v), surface.NormalOf(v));
            }
        }

        /// <summary>The nearest drawn skin within <paramref name="maxDistance"/>.</summary>
        /// <param name="witnessed">Whether cloth inside that surface can be the author's doing — see
        /// <see cref="Build"/>.</param>
        public bool Nearest(Vector3 p, float maxDistance, out BodySurface.Hit hit, out bool witnessed)
        {
            hit = default;
            witnessed = true;
            bool found = false;
            float best = maxDistance;
            for (int s = 0; s < surfaces.Length; s++)
            {
                if (!surfaces[s].Nearest(p, best, out var candidate)) continue;
                best = candidate.Distance;
                hit = candidate;
                witnessed = this.witnessed[s];
                found = true;
            }
            return found;
        }
    }

    /// <summary>
    /// The garment as it stands after the transfer: same parts and normals, moved positions. What
    /// <see cref="TargetBody"/> indexes the garment's own body mesh from.
    /// <para/>
    /// The normals are the author's, not recomputed. A size change turns the skin by a few degrees at most, and the
    /// push-out only uses the normal to tell inside from outside and to pick a direction, neither of which a few degrees
    /// changes.
    /// </summary>
    private static ModelParts Moved(ModelParts garment, Sets sets, Vec3[] nodeDelta)
    {
        var pos = (float[])garment.Positions.Clone();
        int vc = pos.Length / 3;
        for (int i = 0; i < vc; i++)
        {
            var d = nodeDelta[sets.NodeOf[i]];
            pos[i * 3] += d.X;
            pos[i * 3 + 1] += d.Y;
            pos[i * 3 + 2] += d.Z;
        }

        return new ModelParts
        {
            Positions = pos,
            Normals = garment.Normals,
            MeshSpans = garment.MeshSpans,
            Parts = garment.Parts,
            AttributeNames = garment.AttributeNames,
            Min = garment.Min,
            Max = garment.Max,
            ShatteredSubmeshes = garment.ShatteredSubmeshes,
        };
    }

    /// <summary>Where a node stands after the transfer so far: its authored place plus the delta planned for it.</summary>
    private static Vector3 Placed(Sets sets, Vec3[] nodeDelta, int n)
        => new(sets.NodeAt[n].X + nodeDelta[n].X,
               sets.NodeAt[n].Y + nodeDelta[n].Y,
               sets.NodeAt[n].Z + nodeDelta[n].Z);

    /// <summary>How far past a cloth face's edge a skin point may land and still count as under it (0.02 = 2% of the
    /// face, in barycentric terms): a point over an edge or a corner is the neighbouring face's, or the vertex check's.</summary>
    private const float FaceInterior = 0.02f;

    /// <summary>
    /// Skin coming through the MIDDLE of a cloth face while every corner of the face is outside it — which the
    /// vertex check above cannot see. A garment's cloth is flat between its vertices and a body is round, so a coarse
    /// cuff round a finer calf is clear at its corners and cut through between them. Measured on the Comfy Valentione
    /// Skirt's leg warmers refitted onto Neolithe: the cuff's eight corners all 2-10 mm off the calf, and the calf 1 mm
    /// through the middle of the wall faces at the front and back — a pale notch in game. Vanilla's calf was as coarse
    /// as the cuff, and the two never crossed.
    /// <para/>
    /// The same rule as the vertex check — undo what the refit did, and nothing else: only faces whose corners are all
    /// being considered (none authored inside the skin, unless the body is being cleared), only faces the skin is
    /// actually THROUGH now, and only faces the old skin was not already through as authored. A face the skin merely
    /// comes near is left alone: snug cloth the refit never moved is not this pass's to touch. A face that is through is
    /// pushed until it clears by the clearance, or by the corners' own standoff where that is less; each corner takes
    /// the whole of it, along the face's outward normal, and the spread and fold guard that follow treat it like any
    /// other push.
    /// </summary>
    /// <returns>Whether any face needed a push.</returns>
    private static bool SkinThroughFaces(FaceCheck check, Vec3[] nodeDelta, float[] need, Vec3[] dir, bool[] hasDir)
    {
        bool any = false;
        foreach (var face in check.Faces)
        {
            var (push, outward) = check.Need(face, nodeDelta);
            if (push <= 0f) continue;   // near the skin, perhaps, but not through it
            foreach (int n in new[] { face.A, face.B, face.C })
            {
                if (push <= need[n]) continue;
                need[n] = push;
                // The face decides this corner's push, so it goes the way the face has to: along the corner's own
                // nearest-skin normal it could come up short of clearing the face.
                dir[n] = ToVec(outward);
                hasDir[n] = true;
            }
            any = true;
        }
        return any;
    }

    /// <summary>
    /// The cloth faces <see cref="SkinThroughFaces"/> judges, and the test itself, built once so
    /// <see cref="ClearFaces"/> can ask it again once the push-out's fold guard has halved the face pushes away and the
    /// fold relax has had its turn.
    /// </summary>
    internal sealed class FaceCheck
    {
        private readonly Sets sets;
        private readonly float[] authored;
        private readonly bool clearBody;
        private readonly Dictionary<(int, int, int), List<(Vector3 At, Vector3 Normal)>> drawn;

        /// <summary>Faces whose corners are all considered and that the old skin was not already through as authored,
        /// with which of their edges another face shares (the one opposite each corner).</summary>
        public readonly List<(int A, int B, int C, bool EdgeA, bool EdgeB, bool EdgeC)> Faces = [];

        /// <summary>Every triangle of the garment, by node — what a push has to leave facing the way it was drawn.</summary>
        public readonly List<(int A, int B, int C)> Tris = [];

        /// <summary>Per triangle of <see cref="Tris"/>: cloth that is not one of <see cref="Faces"/> — authored through
        /// the skin, or with a corner the push-out leaves where it is — so nothing will lift it if a push sinks it.</summary>
        public readonly List<bool> Unliftable = [];

        private readonly List<(bool A, bool B, bool C)> triEdges = [];

        /// <summary>Per node, the indices into <see cref="Faces"/> it is a corner of.</summary>
        public readonly List<int>[] FacesOf;

        /// <summary>Per node, the indices into <see cref="Tris"/> it is a corner of.</summary>
        public readonly List<int>[] TrisOf;

        /// <summary>Per triangle of <see cref="Tris"/>: every corner is cloth.</summary>
        public readonly List<bool> IsCloth = [];

        /// <summary>The nodes the push-out considers: the only ones a lift may move.</summary>
        public readonly bool[] Considered;

        /// <summary>Each node's neighbours (<see cref="Sets.Adj"/>).</summary>
        public List<int>[] Adj => sets.Adj;

        /// <summary>How many nodes the garment has.</summary>
        public int NodeCount => sets.NodeCount;


        public FaceCheck(Sets sets, IReadOnlyList<int> nodes, TargetBody before, TargetBody after, float[] authored,
                         bool clearBody)
        {
            this.sets = sets;
            this.authored = authored;
            this.clearBody = clearBody;
            drawn = Grid(after.Points());
            var was = clearBody ? null : Grid(before.Points(witnessedOnly: true));

            var considered = Considered = new bool[sets.NodeCount];
            foreach (int n in nodes) considered[n] = true;

            // Edges two DIFFERENT cloth faces share. A skin point that lands on one is still under the cloth — the
            // cuff's top edge, where its wall meets its cap — while one landing on an edge only one face uses is past
            // the cloth's hem, where the body going on wider (a thigh above a stocking) is no clip at all. Faces are
            // counted once per set of corners: double-sided or lined cloth draws every face twice, and counted twice
            // its hems would all look shared.
            var distinct = new HashSet<(int, int, int)>();
            var edgeUses = new Dictionary<(int, int), int>();
            var tris = new List<(int, int, int)>();
            for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
            {
                if (sets.Tris[t] < 0 || sets.Tris[t + 1] < 0 || sets.Tris[t + 2] < 0) continue;
                if (sets.Tris[t] >= sets.NodeOf.Length || sets.Tris[t + 1] >= sets.NodeOf.Length
                    || sets.Tris[t + 2] >= sets.NodeOf.Length) continue;
                int x = sets.NodeOf[sets.Tris[t]], y = sets.NodeOf[sets.Tris[t + 1]], z = sets.NodeOf[sets.Tris[t + 2]];
                if (x == y || y == z || z == x) continue;
                tris.Add((x, y, z));
                if (!distinct.Add(FaceKey(x, y, z))) continue;
                foreach (var key in new[] { EdgeKey(x, y), EdgeKey(y, z), EdgeKey(z, x) })
                    edgeUses[key] = edgeUses.GetValueOrDefault(key) + 1;
            }
            bool Shared(int x, int y) => edgeUses.GetValueOrDefault(EdgeKey(x, y)) >= 2;

            FacesOf = new List<int>[sets.NodeCount];
            TrisOf = new List<int>[sets.NodeCount];
            for (int n = 0; n < sets.NodeCount; n++)
            {
                FacesOf[n] = [];
                TrisOf[n] = [];
            }

            var cloth = new bool[sets.NodeCount];
            foreach (int n in sets.ClothNodes) cloth[n] = true;

            foreach (var (a, b, c) in tris)
            {
                foreach (int n in new[] { a, b, c }) TrisOf[n].Add(Tris.Count);
                bool edgeA = Shared(b, c), edgeB = Shared(c, a), edgeC = Shared(a, b);
                Tris.Add((a, b, c));
                triEdges.Add((edgeA, edgeB, edgeC));
                bool isCloth = cloth[a] && cloth[b] && cloth[c];
                IsCloth.Add(isCloth);
                Unliftable.Add(isCloth);   // until it is found to be a face below

                if (!considered[a] || !considered[b] || !considered[c]) continue;
                // Already through as authored: the author's, and left as it is.
                if (was != null && Through(was, ToVector(sets.NodeAt[a]), ToVector(sets.NodeAt[b]),
                                           ToVector(sets.NodeAt[c]), edgeA, edgeB, edgeC).Depth > 0f)
                    continue;
                foreach (int n in new[] { a, b, c }) FacesOf[n].Add(Faces.Count);
                Faces.Add((a, b, c, edgeA, edgeB, edgeC));
                Unliftable[^1] = false;
            }
        }

        /// <summary>Where node <paramref name="n"/> stands now.</summary>
        public Vector3 At(int n, Vec3[] nodeDelta) => Placed(sets, nodeDelta, n);

        /// <summary>Where node <paramref name="n"/> stands with <paramref name="delta"/> applied.</summary>
        public Vector3 AtWith(int n, Vec3 delta)
            => new(sets.NodeAt[n].X + delta.X, sets.NodeAt[n].Y + delta.Y, sets.NodeAt[n].Z + delta.Z);

        /// <summary>How far the drawn skin is through face <paramref name="f"/> of <see cref="Faces"/> — negative when it
        /// is clear, by how much — and the face's way out; no way out when no skin is under it.</summary>
        public (float Depth, Vector3 Outward) Signed(int f, Vec3[] nodeDelta)
        {
            var face = Faces[f];
            return Through(drawn, Placed(sets, nodeDelta, face.A), Placed(sets, nodeDelta, face.B),
                           Placed(sets, nodeDelta, face.C), face.EdgeA, face.EdgeB, face.EdgeC);
        }

        /// <summary>How far the drawn skin is through triangle <paramref name="t"/> of <see cref="Tris"/>, 0 when clear.</summary>
        public float Depth(int t, Vec3[] nodeDelta)
        {
            var (a, b, c) = Tris[t];
            var (ea, eb, ec) = triEdges[t];
            return MathF.Max(0f, Through(drawn, Placed(sets, nodeDelta, a), Placed(sets, nodeDelta, b),
                                         Placed(sets, nodeDelta, c), ea, eb, ec).Depth);
        }

        /// <summary>Facing the other way from how the author drew it — the push's own fold test.</summary>
        public bool TurnedOver(int t, Vec3[] nodeDelta)
        {
            var (a, b, c) = Tris[t];
            var was = ToVector(sets.NodeAt[a]);
            var n0 = Vector3.Cross(ToVector(sets.NodeAt[b]) - was, ToVector(sets.NodeAt[c]) - was);
            if (n0.Length() <= 1e-12f) return false;   // degenerate as authored
            var now = Placed(sets, nodeDelta, a);
            var n1 = Vector3.Cross(Placed(sets, nodeDelta, b) - now, Placed(sets, nodeDelta, c) - now);
            return Vector3.Dot(n0, n1) <= 0f;
        }

        /// <summary>How far each corner of the face has to go out for it to clear the drawn skin by the clearance (or
        /// by the corners' own standoff where that is less), and which way is out; zero when no skin is through it.</summary>
        public (float Push, Vector3 Outward) Need((int A, int B, int C, bool EdgeA, bool EdgeB, bool EdgeC) face,
                                                  Vec3[] nodeDelta, float tolerance = 0f)
        {
            var (depth, outward) = Through(drawn, Placed(sets, nodeDelta, face.A), Placed(sets, nodeDelta, face.B),
                                           Placed(sets, nodeDelta, face.C), face.EdgeA, face.EdgeB, face.EdgeC);
            if (depth <= tolerance) return (0f, default);
            float standoff = clearBody ? Clearance
                           : MathF.Min(Clearance, MathF.Min(authored[face.A], MathF.Min(authored[face.B], authored[face.C])));
            return (depth + standoff, outward);
        }

        // The skin's points, bucketed so a face only looks at the skin near it.
        private static Dictionary<(int, int, int), List<(Vector3 At, Vector3 Normal)>> Grid(
            IEnumerable<(Vector3 At, Vector3 Normal)> points)
        {
            var grid = new Dictionary<(int, int, int), List<(Vector3 At, Vector3 Normal)>>();
            foreach (var (at, normal) in points)
            {
                var key = ((int)MathF.Floor(at.X / FaceCell), (int)MathF.Floor(at.Y / FaceCell), (int)MathF.Floor(at.Z / FaceCell));
                if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = [];
                bucket.Add((at, normal));
            }
            return grid;
        }

        // How far the skin is through the face abc — the deepest point of it on the outside — and which way is out.
        // Negative when the skin under the face is all behind it (the face's clearance); zero, with no way out, when no
        // skin is under it at all.
        private static (float Depth, Vector3 Outward) Through(Dictionary<(int, int, int), List<(Vector3 At, Vector3 Normal)>> grid,
                                                              Vector3 pa, Vector3 pb, Vector3 pc,
                                                              bool edgeA, bool edgeB, bool edgeC)
        {
            var face = Vector3.Cross(pb - pa, pc - pa);
            if (face.LengthSquared() < 1e-14f) return (0f, default);
            face = Vector3.Normalize(face);

            // The skin points under the face: its middle, or an edge another face shares.
            var under = new List<(Vector3 Offset, Vector3 Normal)>();
            var lo = Vector3.Min(pa, Vector3.Min(pb, pc)) - new Vector3(ClearDepth);
            var hi = Vector3.Max(pa, Vector3.Max(pb, pc)) + new Vector3(ClearDepth);
            var facing = Vector3.Zero;
            for (int x = (int)MathF.Floor(lo.X / FaceCell); x <= (int)MathF.Floor(hi.X / FaceCell); x++)
            for (int y = (int)MathF.Floor(lo.Y / FaceCell); y <= (int)MathF.Floor(hi.Y / FaceCell); y++)
            for (int z = (int)MathF.Floor(lo.Z / FaceCell); z <= (int)MathF.Floor(hi.Z / FaceCell); z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (var (s, sn) in bucket)
                {
                    var q = BrushTransfer.ClosestOnTriangle(s, pa, pb, pc, out float u, out float v, out float w);
                    if (Vector3.DistanceSquared(s, q) > ClearDepth * ClearDepth) continue;
                    bool offA = u < FaceInterior, offB = v < FaceInterior, offC = w < FaceInterior;
                    int off = (offA ? 1 : 0) + (offB ? 1 : 0) + (offC ? 1 : 0);
                    if (off > 1) continue;   // on a corner: the vertex check's
                    if ((offA && !edgeA) || (offB && !edgeB) || (offC && !edgeC)) continue;   // past a hem
                    if (sn.LengthSquared() < 1e-12f) continue;
                    var n = Vector3.Normalize(sn);
                    under.Add((s - q, n));
                    facing += n;
                }
            }
            if (under.Count == 0) return (0f, default);

            // One outward for the face, the way the skin under it faces as a whole: its winding says nothing about which
            // side of the body it is on, and deciding point by point lets skin behind a crease, facing away, read as
            // through.
            var outward = Vector3.Dot(face, facing) >= 0f ? face : -face;

            // Signed: negative when every skin point is behind the face, by how much — the face's clearance.
            float deepest = float.NegativeInfinity;
            foreach (var (offset, n) in under)
            {
                // Skin that faces the other way is the far side of a fold, not the surface this face lies over; and on
                // an edge, only the face that looks the way the skin does — the cuff's wall, not its cap, which meets
                // the calf edge-on.
                if (Vector3.Dot(n, outward) < FaceFacing) continue;
                deepest = MathF.Max(deepest, Vector3.Dot(offset, outward));
            }
            return float.IsNegativeInfinity(deepest) ? (0f, default) : (deepest, outward);
        }
    }

    /// <summary>Grid cell the face check buckets skin points by (10 mm).</summary>
    private const float FaceCell = 0.01f;

    /// <summary>How squarely the skin under a face must look the face's way out to count (cos 60°): skin facing
    /// elsewhere is the far side of a fold, or meets the face edge-on.</summary>
    private const float FaceFacing = 0.5f;

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

    private static (int, int, int) FaceKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    /// <summary>
    /// Push cloth the refit drove INTO the drawn skin back out of it — and nothing else.
    /// </summary>
    /// <param name="candidates">
    /// The CLOTH nodes, minus the ones the transfer snapped — see <see cref="Sets"/>. Both exclusions are set
    /// membership and never a distance test, deliberately: a snapped node sits exactly ON a body surface, where inside
    /// and outside are a coin flip, and the garment's own body mesh is SUPPOSED to coincide with the body. A cloth
    /// vertex 0.01 mm inside and a skin vertex exactly on the surface are the same number with opposite right answers,
    /// so no epsilon can separate them.
    /// </param>
    /// <param name="before">The skin drawn under the garment as AUTHORED.</param>
    /// <param name="after">The same skin after the refit.</param>
    /// <remarks>
    /// A third exclusion is made here, and it is the one that decides whether this pass helps at all: cloth the author
    /// put INSIDE the drawn skin is left exactly where the refit carried it. Garment authors routinely leave the whole
    /// body mesh under the fabric, where it is hidden, so cloth sitting behind a skin surface is very often authored
    /// rather than a clip. Measured on "This Old Thing": every one of the 536 nodes an unconditional push-out moved was
    /// already inside its own body mesh as shipped, by 4 mm on average and up to 19 mm, and pushing them made the refit
    /// 30% worse against the author's own hand-fitted size. So the rule is to undo only clips the refit CREATED —
    /// authored outside, now inside — and by the least that clears them.
    /// </remarks>
    /// <returns>How many nodes moved.</returns>
    /// <param name="clearBody">Clear cloth out of the body wherever it is buried, not only where the refit buried it
    /// — see <see cref="BodyRetarget.Plan"/>. The exclusions above are what it drops: cloth the author tucked under the
    /// skin comes out too, and the standoff it is pushed to is the clearance rather than the author's own.</param>
    /// <param name="faceCheck">The face test it built, for <see cref="ClearFaces"/> to run again once the fold relax
    /// has had its turn.</param>
    /// <param name="pushedBy">How far it pushed each node, for the same.</param>
    private static int PushOut(Sets sets, IReadOnlyList<int> candidates, TargetBody before, TargetBody after,
                               Vec3[] nodeDelta, bool clearBody, out float worst, out FaceCheck faceCheck,
                               out float[] pushedBy)
    {
        worst = 0f;
        pushedBy = new float[sets.NodeCount];

        // The authored-inside nodes out of the set entirely, before anything else looks at it: neither a source of
        // push nor a receiver of the spread below, which would otherwise drag hidden cloth out at the boundary.
        var nodes = new List<int>(candidates.Count);
        var authored = new float[sets.NodeCount];
        foreach (int n in candidates)
        {
            if (clearBody)
            {
                // Asked to clear the body: cloth just under the skin comes out whoever put it there, and is held to
                // the clearance rather than to an authored standoff. Cloth buried DEEPER than ClearDepth is left: at
                // that depth it is the garment's structure — an inner layer, a sole, a lining — and dragging it to the
                // surface tears the mesh without uncovering anything (measured: 17,212 nodes moved up to 30 mm, edges
                // grown by 54 mm, and the body still showed through).
                authored[n] = float.MaxValue;
                nodes.Add(n);
                continue;
            }

            var p0 = ToVector(sets.NodeAt[n]);

            // Reached much further than the push itself: a point DEEP inside the body is authored-inside just as much
            // as one just under the surface, and at the short range the two were indistinguishable from cloth far
            // outside. A heeled shoe's foot sits well inside where the body's flat foot is drawn, and reading it as
            // "outside" had the push-out balloon the stocking by up to 30 mm.
            // The NEAREST drawn surface, not the deepest: this asks what the author did, and their standoff is from
            // the surface their cloth sits on. Reading it against whichever surface the point is deepest inside counts
            // more cloth as deliberately hidden and quietly stops the pass undoing clips it should — measured on a
            // stocking, 350 buried points became 622 with the default settings.
            if (before.Nearest(p0, AuthoredProbeRange, out var h0, out bool witnessed))
            {
                float s0 = Vector3.Dot(p0 - h0.Point, h0.Normal);
                if (s0 < 0f)
                {
                    // Inside a surface that cannot say what the author saw (a legs garment's chest — see TargetBody):
                    // a clip to undo, held to the clearance as clearBody holds it. Deeper than ClearDepth it is still
                    // the garment's own structure.
                    if (witnessed || s0 < -ClearDepth) continue;
                    authored[n] = float.MaxValue;
                }
                else authored[n] = s0;
            }
            else
            {
                authored[n] = float.MaxValue;   // nowhere near skin as authored, so certainly not tucked under it
            }
            nodes.Add(n);
        }

        var need = new float[sets.NodeCount];
        var dir = new Vec3[sets.NodeCount];
        var hasDir = new bool[sets.NodeCount];
        bool any = false;

        foreach (int n in nodes)
        {
            var p = ToVector(new Vec3(sets.NodeAt[n].X + nodeDelta[n].X,
                                      sets.NodeAt[n].Y + nodeDelta[n].Y,
                                      sets.NodeAt[n].Z + nodeDelta[n].Z));

            if (!after.Deepest(p, PushProbeRange, out var hit)) continue;

            dir[n] = ToVec(hit.Normal);
            hasDir[n] = true;

            float s1 = Vector3.Dot(p - hit.Point, hit.Normal);
            if (s1 >= 0f) continue;
            if (clearBody && s1 < -ClearDepth) continue;   // buried deep on purpose — see above

            // Back to the clearance, or to the author's own standoff where that was less: un-clipping, not re-fitting.
            // Along the SKIN's normal at the landing, not along (p - landing): for a point inside, that difference aims
            // further in, and near the surface it is numerically meaningless as well.
            float d = (clearBody ? Clearance : MathF.Min(authored[n], Clearance)) - s1;
            if (d <= 0f) continue;

            need[n] = d;
            any = true;
        }

        faceCheck = new FaceCheck(sets, nodes, before, after, authored, clearBody);
        any |= SkinThroughFaces(faceCheck, nodeDelta, need, dir, hasDir);

        if (!any) return 0;

        // Spread the requirement outward with a slope limit, so the push does not step where it stops. Without this
        // the boundary between a pushed node and its unpushed neighbour is a crease of exactly the push distance.
        float step = MathF.Max(sets.MeanEdge, 1e-6f) * PushSlope;
        for (int round = 0; round < PushSpreadRounds; round++)
        {
            bool changed = false;
            foreach (int n in nodes)
            {
                float most = need[n];
                foreach (int m in sets.Adj[n])
                {
                    float from = need[m] - step;
                    if (from > most) most = from;
                }
                if (most <= need[n] + 1e-9f) continue;
                need[n] = most;
                changed = true;
            }
            if (!changed) break;
        }

        // The same slope limit against the cloth this pass left out — authored inside the skin, or nowhere near it.
        // Those nodes never move, and the spread above cannot see them, so a node beside one could be pushed its whole
        // distance while its neighbour held: on a stocking over a heeled foot that stretched a 0.3 mm edge to 7.1 mm,
        // which in game is a spike through the shoe. A left-out node counts as no push, and its neighbours are capped.
        var inSet = new bool[sets.NodeCount];
        foreach (int n in nodes) inSet[n] = true;

        // Only cloth this pass considered and then left out holds its neighbours back. Skin, and a node that snapped
        // onto the body, were never candidates: they are not cloth that stayed put, they are the surface the cloth is
        // being cleared from, and counting them as "no push" pinned the fabric to the skin it had to come out of.
        var candidate = new bool[sets.NodeCount];
        foreach (int n in candidates) candidate[n] = true;

        void Slope()
        {
            for (int round = 0; round < PushSpreadRounds; round++)
            {
                bool changed = false;
                foreach (int n in nodes)
                {
                    float cap = need[n];
                    foreach (int m in sets.Adj[n])
                    {
                        if (!candidate[m]) continue;
                        float limit = (inSet[m] ? need[m] : 0f) + step;
                        if (limit < cap) cap = limit;
                    }
                    if (cap >= need[n] - 1e-9f) continue;
                    need[n] = cap;
                    changed = true;
                }
                if (!changed) break;
            }
        }

        Slope();

        // Back the push off wherever it would turn a triangle over. Each node is pushed along the skin's normal
        // where IT landed, and neighbours can land on surfaces facing different ways — a sleeve at the wrist lands on
        // the fingers, which face every way at once — so two nodes pushed a few millimetres each can step past one
        // another, and a turned-over triangle is drawn from behind, which on cloth is black. Halved until none does,
        // which terminates because scaling a push toward zero moves its triangles back toward where they started.
        // The real test on the real triangles, not a limit on how far or how differently nodes move: cloth wrapping a
        // convex body corner is pushed two ways at once and grows, which is not a fold (measured on this jacket: 17
        // faces of up to 9.6 mm² turned over, and no proxy for it left the pass able to clear a shoulder).
        Vector3 Trial(int n)
        {
            var at = Placed(sets, nodeDelta, n);
            if (need[n] <= 0f) return at;
            var d = hasDir[n] ? dir[n] : sets.NodeNormal[n];
            return at + ToVector(d) * need[n];
        }

        for (int pass = 0; pass < PushUnfoldPasses; pass++)
        {
            int folded = 0;
            for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
            {
                int va = sets.Tris[t], vb = sets.Tris[t + 1], vc = sets.Tris[t + 2];
                if (va < 0 || vb < 0 || vc < 0
                    || va >= sets.NodeOf.Length || vb >= sets.NodeOf.Length || vc >= sets.NodeOf.Length) continue;
                int a = sets.NodeOf[va], b = sets.NodeOf[vb], c = sets.NodeOf[vc];
                if (a == b || b == c || c == a) continue;
                if (need[a] <= 0f && need[b] <= 0f && need[c] <= 0f) continue;

                // Against the AUTHOR's orientation, not against where the transfer left the triangle. Measured on a
                // sheer corset refitted Bibo+ to Neolithe: the transfer turns a triangle in the under-bust crease most
                // of the way over, the push tips it the rest, and each step passes a test that only looks at its own
                // step — 33 folds before the push, 95 after, every one of them over the bust. What the garment is
                // drawn with is the author's winding, so that is what the push has to leave alone.
                var was = ToVector(sets.NodeAt[a]);
                var n0 = Vector3.Cross(ToVector(sets.NodeAt[b]) - was, ToVector(sets.NodeAt[c]) - was);
                if (n0.Length() <= 1e-12f) continue;   // degenerate as authored; not this pass's doing
                var now = Trial(a);
                var n1 = Vector3.Cross(Trial(b) - now, Trial(c) - now);
                if (Vector3.Dot(n0, n1) > 0f) continue;

                need[a] *= 0.5f;
                need[b] *= 0.5f;
                need[c] *= 0.5f;
                folded++;
            }
            // Nothing folded, so nothing was halved and the slope limit still holds from the last time: done. Halving
            // three corners cuts their push below what their neighbours kept, which is the very step Slope exists to
            // take out — a node backed off to nothing beside one pushed its whole 7 mm is a crease, and a crease is
            // not a fold, so no later pass would notice. Slope only ever LOWERS a push, so the next pass re-tests.
            if (folded == 0) break;
            Slope();
        }

        // How far each node has been pushed out, so the settle below reports pushes and not the smoothing that rides
        // along with them.
        int pushed = 0;
        foreach (int n in nodes)
        {
            if (need[n] <= 0f) continue;
            var d = hasDir[n] ? dir[n] : sets.NodeNormal[n];
            if (d.X == 0f && d.Y == 0f && d.Z == 0f) continue;

            nodeDelta[n] = new Vec3(nodeDelta[n].X + d.X * need[n],
                                    nodeDelta[n].Y + d.Y * need[n],
                                    nodeDelta[n].Z + d.Z * need[n]);
            pushedBy[n] = need[n];
            if (need[n] > worst) worst = need[n];
            pushed++;
        }
        // Whatever the fold guard backed off from is still inside: settled rather than left.
        if (!Tuned.NoSettle) pushed += Settle(sets, nodes, after, nodeDelta, authored, clearBody, pushedBy, ref worst);

        return pushed;
    }

    /// <summary>Rounds <see cref="ClearFaces"/> gets.</summary>
    private const int ClearFaceRounds = 8;

    /// <summary>
    /// The most a face may ask of <see cref="ClearFaces"/> (2 mm, its depth plus the standoff — skin about a millimetre
    /// through it): a face the skin is further through than that is not sagging into the skin between its corners, and
    /// is left.
    /// <para/>
    /// What it was instead, on a corset: the breast the corset presses bulges up over its top edge, the rim band joins
    /// the shell to the lining so its edges read as shared, and the bulge above the rim read as skin through the rim's
    /// faces. Chasing it pushed the rim out by up to 20 mm — "Sirius" refitted to Neolithe S had cloth movement over the
    /// top of the cups 13 mm out of line with its neighbours (5.9 without the pass), onto Rue+ Large 19: the jagged top
    /// edge of the cups in game, back again after the layer guard.
    /// </summary>
    internal const float ClearFaceDeepest = 0.002f;

    /// <summary>The most <see cref="ClearFaces"/> moves any one node in all, over every round (3 mm).</summary>
    internal const float ClearFaceMost = 0.003f;

    /// <summary>
    /// How far off the underside of the breast <see cref="LiftUnderbust"/> holds the cloth, whether or not the skin is
    /// through it yet (1 mm).
    /// <para/>
    /// Everywhere else a face is only pushed once the skin is THROUGH it, and only back to the author's own standoff. Under
    /// a large breast that left the cloth resting on the skin: refitted onto Rue+ and YAB+ Large, "Sirius" had 166
    /// underbust vertices within half a millimetre of the skin and 422 faces it came up to 0.7 mm through, where Neolithe,
    /// Tre and Bibo+ Large all came out 1-2 mm off. The breast's underside is where a pose presses skin and cloth together,
    /// so resting on it is clipping as soon as the character moves. User's judgement in game: "it just needs to come down
    /// 1 mm".
    /// </summary>
    internal const float UnderbustClearance = 1e-3f;

    /// <summary>
    /// Whether a face whose way out is <paramref name="outward"/> lies over the underside of the breast: facing down
    /// (more than about 25° below level) and forward, so the underside of an arm or the armpit, which face down but
    /// sideways, are not counted.
    /// </summary>
    internal static bool IsUnderbust(Vector3 outward) => outward.Y < -0.4f && outward.Z > 0.1f;

    /// <summary>How much of a point's skin must be weighted to the breast bones for <see cref="BreastTest"/> to call it the
    /// breast (a third).</summary>
    internal const float BreastWeight = 0.15f;

    /// <summary>
    /// Whether a point lies over the breast of the body in <paramref name="pair"/>: the skin nearest it (within 2 cm) is
    /// weighted at least <see cref="BreastWeight"/> to the breast bones (<c>j_mune_*</c>, YAS's <c>iv_c_mune_*</c>). Read
    /// off the body's own weights, so it is the same answer on every body mod however the breast is shaped. Null when the
    /// pair carries no body file to read.
    /// </summary>
    internal static Func<Vector3, bool>? BreastTest(SlotPair pair)
        => RegionTest([pair], bone => bone.Contains("mune", StringComparison.Ordinal), BreastWeight);

    /// <summary>How far off the hip and upper thigh <see cref="LiftUnderbust"/> holds the cloth (3 mm).</summary>
    /// <remarks>
    /// The breast's 1 mm was not enough here: the hip bends with the leg, and on "Sirius" refitted onto YAB+ Large the
    /// cloth over it — about 2 mm off at rest, and rigged exactly as the skin under it — still showed the hip through it
    /// when the character sat. User's call in game: "pull it out a little bit".
    /// </remarks>
    internal const float HipClearance = 0.003f;

    /// <summary>How much of a point's skin must be weighted to the pelvis, thigh and buttock bones for
    /// <see cref="HipTest"/> to call it the hip (a half).</summary>
    internal const float HipWeight = 0.5f;

    /// <summary>
    /// Whether a point lies over the side of the hip or the upper thigh of the bodies in <paramref name="pairs"/>: the skin
    /// nearest it is weighted at least <see cref="HipWeight"/> to the pelvis (<c>j_kosi</c>), the thighs (<c>j_asi_a_*</c>)
    /// or YAS's buttocks (<c>iv_shiri_*</c>), and it is off to the side — the middle, where the same bones rig the belly,
    /// the crotch and the small of the back, is left. Null when no pair carries a body file.
    /// </summary>
    internal static Func<Vector3, bool>? HipTest(IReadOnlyList<SlotPair> pairs)
    {
        var test = RegionTest(pairs, bone => bone == "j_kosi" || bone.StartsWith("j_asi_a", StringComparison.Ordinal)
                                             || bone.StartsWith("iv_shiri", StringComparison.Ordinal), HipWeight);
        return test == null ? null : p => MathF.Abs(p.X) > 0.07f && test(p);
    }

    /// <summary>Whether the skin of <paramref name="pairs"/>' bodies nearest a point (within 2 cm) is weighted at least
    /// <paramref name="least"/> to bones <paramref name="isBone"/> picks. Null when no pair carries a body file.</summary>
    private static Func<Vector3, bool>? RegionTest(IEnumerable<SlotPair> pairs, Func<string, bool> isBone, float least)
    {
        var bodies = new List<(BodySurface Surface, float[] Share)>();
        foreach (var pair in pairs)
        {
            if (pair.TargetModel == null || ModelSkinReader.Read(pair.TargetModel, null, null) is not { } skin
                || skin.VertexCount * 3 != pair.Target.Positions.Length) continue;
            var share = new float[skin.VertexCount];
            for (int v = 0; v < skin.VertexCount; v++)
                for (int k = 0; k < XivLiveMesh.SkinnedMesh.MaxInfluences; k++)
                {
                    float x = skin.BoneWeights[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
                    int b = skin.BoneIndices[v * XivLiveMesh.SkinnedMesh.MaxInfluences + k];
                    if (x > 0f && b < skin.BoneNames.Length && isBone(skin.BoneNames[b])) share[v] += x;
                }
            bodies.Add((new BodySurface(pair.Target, BodySurface.CellFor(MeanEdgeOf(pair.Target))), share));
        }
        if (bodies.Count == 0) return null;
        return p =>
        {
            float best = 0.02f, value = 0f;
            bool found = false;
            foreach (var (surface, share) in bodies)
            {
                if (!surface.Nearest(p, best, out var h)) continue;
                best = h.Distance;
                value = share[h.A] * h.U + share[h.B] * h.V + share[h.C] * h.W;
                found = true;
            }
            return found && value >= least;
        };
    }

    /// <summary>How far round a face that needs lifting <see cref="LiftUnderbust"/> lifts the cloth with it (6 mm):
    /// past the thickness of a lined corset, so its lining and shell go up together.</summary>
    internal const float UnderbustLiftReach = 0.006f;

    /// <summary>How much of the gap between a lifted node and the cloth in front of it <see cref="LiftUnderbust"/> keeps
    /// at most (0.5 mm).</summary>
    internal const float UnderbustLayerGap = 0.0005f;

    /// <summary>Rounds of smoothing <see cref="LiftUnderbust"/> gives the lift, so it fades in rather than stepping.</summary>
    private const int UnderbustLiftSmoothRounds = 3;

    /// <summary>
    /// Hold the cloth over the underside of the breast <see cref="UnderbustClearance"/> off the skin, every layer of it
    /// in its order.
    /// <para/>
    /// Each considered cloth node over the underside of the breast closer than that to the skin is lifted to it, along
    /// the skin's normal. Cloth within <see cref="UnderbustLiftReach"/> IN FRONT of a lifted node — the shell over a
    /// lining — takes as much of that lift as would otherwise close the gap between them, so the layers keep their order
    /// without everything being raised. The lift is then smoothed over the cloth, never below what a node itself needs,
    /// so it fades in rather than stepping. <see cref="ClearFaces"/> runs next for any face middle still through.
    /// <para/>
    /// Not through <see cref="ClearFaces"/>: there each corner moves on its own and its layer guard puts back any push
    /// that crosses other cloth, and under the breast that was nearly all of them — on "Sirius" refitted onto Rue+ Large,
    /// 823 of the lifts were put back and the 200 underbust vertices resting on the skin did not move. Sized from each
    /// face's deepest point instead of each vertex, and spread at full strength 6 mm round, the lift overshot: the median
    /// underbust vertex went from 1.0 mm off the skin to 3.3.
    /// </summary>
    /// <param name="drawn">The skin drawn under the garment — what the clearance is measured from.</param>
    /// <param name="isBreast">Whether a point lies over the breast, read off the new body's own breast-bone weights
    /// (see <see cref="BreastTest"/>); null to hold the underside alone. Over the breast it is each FACE that is held the
    /// clearance off, at its deepest point, not just its corners: on "Sirius" refitted onto YAB+ Large, 351 visible faces
    /// over the breast were within 1 mm of the skin (18 on Neolithe Large), and skin showed through them in game.</param>
    /// <returns>How many nodes it lifted.</returns>
    /// <param name="isHip">Whether a point lies over the hip (see <see cref="HipTest"/>), whose faces are held
    /// <see cref="HipClearance"/> off the same way; null to leave the hip.</param>
    internal static int LiftUnderbust(FaceCheck check, TargetBody drawn, Vec3[] nodeDelta, float[] pushedBy,
                                      ref float worst, bool[]? stay = null, Func<Vector3, bool>? isBreast = null,
                                      Func<Vector3, bool>? isHip = null)
    {
        int count = check.NodeCount;
        var need = new float[count];
        var dir = new Vector3[count];

        // What each node over the underside of the breast needs to stand the clearance off it.
        var seeds = new List<int>();
        for (int n = 0; n < count; n++)
        {
            if (!check.Considered[n]) continue;
            var p = check.At(n, nodeDelta);
            if (!drawn.Deepest(p, PushProbeRange, out var hit) || !IsUnderbust(hit.Normal)) continue;
            float s = Vector3.Dot(p - hit.Point, hit.Normal);
            if (s >= UnderbustClearance || s < -ClearDepth) continue;   // clear already, or buried on purpose
            need[n] = UnderbustClearance - s;
            dir[n] = hit.Normal;
            seeds.Add(n);
        }

        // And each face over the breast, at its deepest point. Not one the skin is more than a millimetre through: that
        // is not cloth lying close but something else — the breast bulging over a corset's top edge — and chasing it
        // jagged the rim (see ClearFaceDeepest).
        if (isBreast != null || isHip != null)
        {
            for (int f = 0; f < check.Faces.Count; f++)
            {
                var face = check.Faces[f];
                var centre = (check.At(face.A, nodeDelta) + check.At(face.B, nodeDelta) + check.At(face.C, nodeDelta)) / 3f;
                float wanted = isBreast != null && isBreast(centre) ? UnderbustClearance
                             : isHip != null && isHip(centre) ? HipClearance : 0f;
                if (wanted <= 0f) continue;
                var (depth, outward) = check.Signed(f, nodeDelta);
                if (outward == default || depth > UnderbustClearance) continue;
                float short_ = wanted + depth;   // clearance wanted, less the clearance it has
                if (short_ <= 0f) continue;
                foreach (int n in new[] { face.A, face.B, face.C })
                {
                    if (short_ <= need[n]) continue;
                    if (need[n] == 0f) seeds.Add(n);
                    need[n] = short_;
                    dir[n] = outward;
                }
            }
        }
        if (seeds.Count == 0) return 0;

        // Cloth in front of a lifted node goes up by as much as would otherwise close the gap.
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / UnderbustLiftReach),
                                            (int)MathF.Floor(p.Y / UnderbustLiftReach),
                                            (int)MathF.Floor(p.Z / UnderbustLiftReach));
        for (int n = 0; n < count; n++)
        {
            if (!check.Considered[n]) continue;
            var c = Cell(check.At(n, nodeDelta));
            if (!grid.TryGetValue(c, out var bucket)) grid[c] = bucket = [];
            bucket.Add(n);
        }
        var lift = (float[])need.Clone();
        var way = (Vector3[])dir.Clone();
        foreach (int s in seeds)
        {
            var at = check.At(s, nodeDelta);
            var (cx, cy, cz) = Cell(at);
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            for (int z = cz - 1; z <= cz + 1; z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
                foreach (int m in bucket)
                {
                    var off = check.At(m, nodeDelta) - at;
                    float ahead = Vector3.Dot(off, dir[s]);
                    // In front (not beside, which the smoothing answers) and within reach.
                    if (ahead <= 0f || ahead > UnderbustLiftReach
                        || (off - dir[s] * ahead).LengthSquared() > ahead * ahead) continue;
                    // Up to UnderbustLayerGap of the gap is kept, so a shell a hair over its lining does not end up
                    // lying on it.
                    float carry = need[s] - ahead + MathF.Min(ahead, UnderbustLayerGap);
                    if (carry <= lift[m]) continue;
                    lift[m] = carry;
                    way[m] = dir[s];
                }
            }
        }

        // Smoothed over the cloth, out onto the neighbours too, so the lift fades in rather than stepping; never below
        // what a node needs or was carried.
        var floor = (float[])lift.Clone();
        for (int round = 0; round < UnderbustLiftSmoothRounds; round++)
        {
            var next = (float[])lift.Clone();
            for (int n = 0; n < count; n++)
            {
                if (!check.Considered[n] || check.Adj[n].Count == 0) continue;
                float sum = 0f, most = 0f;
                int from = -1;
                foreach (int m in check.Adj[n])
                {
                    sum += lift[m];
                    if (lift[m] > most) { most = lift[m]; from = m; }
                }
                if (most <= 0f) continue;
                next[n] = MathF.Max(floor[n], 0.5f * lift[n] + 0.5f * sum / check.Adj[n].Count);
                if (way[n] == default && from >= 0) way[n] = way[from];
            }
            lift = next;
        }

        int lifted = 0;
        for (int n = 0; n < count; n++)
        {
            if (lift[n] <= 0f || way[n] == default || (stay != null && stay[n])) continue;
            var d = way[n] * lift[n];
            nodeDelta[n] = new Vec3(nodeDelta[n].X + d.X, nodeDelta[n].Y + d.Y, nodeDelta[n].Z + d.Z);
            pushedBy[n] += lift[n];
            if (pushedBy[n] > worst) worst = pushedBy[n];
            lifted++;
        }
        return lifted;
    }

    /// <summary>
    /// Clear what the push-out leaves: skin still through the MIDDLE of a cloth face whose corners may all be clear.
    /// <para/>
    /// The push-out asks for it (<see cref="SkinThroughFaces"/>), but its fold guard halves a face's push as readily as a
    /// vertex's, and <see cref="Settle"/> only looks at vertices. Measured on "Auburn" (Neolithe Almond XS to Rue+ Yiggle
    /// Large): 356 of the 520 cloth faces over the breasts still had skin through them — 21 at Medium, 45 as authored.
    /// A refit onto a much larger breast stretches the cloth's faces (15 mm long to 22) over a rounder surface, and a flat
    /// face sags into a round one between its corners; in game that is patches of skin the shape of the faces.
    /// <para/>
    /// Push alone, no smoothing: each corner of a face still through goes out along the face's way out by as much as the
    /// face needs, the most any of its faces asks. Folded into <see cref="Settle"/>'s push-and-smooth rounds instead, the
    /// smoothing drew every pushed corner back in and the next push sent it out again: the whole patch ratcheted outward,
    /// cloth already 10-20 mm off the skin went 5-15 mm further, and the sleeves and back moved too.
    /// <para/>
    /// A push never turns a triangle over, and never sinks one nothing will lift. Each corner goes the way its own face
    /// needs, and where neighbouring faces face different ways — a crease, a pleat standing off the surface — that can
    /// tip a triangle beside them onto its back, which draws black; or carry a corner sideways under a triangle the pass
    /// may not lift (authored through the skin, or with a corner the push-out holds), 8 mm through going to 11 on "This
    /// Old Thing" L to Rue L. So a round's pushes are tried, and every triangle they newly turn over or sink has its pushed
    /// corners put back and left for the rest of the pass: the face stays through rather than harm its neighbour, the
    /// push-out's own rule. Put back, not halved: halving is the guard that left these faces through in the first place.
    /// <para/>
    /// Every face around a corner a round moved is looked at again in the next, not just the faces that were through: a
    /// corner pushed along one face's way out moves the faces beside it too.
    /// <para/>
    /// It runs last, after <see cref="Unfold"/>: the fold relax only keeps VERTICES out of the skin, and run before it
    /// the faces it cleared were sunk again. Last, and turning nothing over, it also leaves the relax's work as it was:
    /// run inside the push-out instead, the relax started from different cloth and left more folds standing ("This Old
    /// Thing" L to Rue L, 21 to 31) though this pass had turned none over itself.
    /// </summary>
    /// <param name="pushedBy">How far each node has been pushed so far, added to here.</param>
    /// <param name="stay">Nodes it must not move — the hard pieces, once they have been put back whole.</param>
    /// <returns>How many nodes it pushed that nothing before it had.</returns>
    internal static int ClearFaces(FaceCheck check, Vec3[] nodeDelta, float[] pushedBy, ref float worst,
                                   bool[]? stay = null)
    {
        int count = pushedBy.Length;
        var need = new float[count];
        var dir = new Vector3[count];
        var held = stay != null ? (bool[])stay.Clone() : new bool[count];
        var cleared = new float[count];
        var touched = new List<int>();
        int newly = 0;

        // Turned over before this pass moved anything: the transfer's or the push's, and not this pass's to answer for.
        var foldedBefore = new bool[check.Tris.Count];
        for (int t = 0; t < check.Tris.Count; t++) foldedBefore[t] = check.TurnedOver(t, nodeDelta);

        var candidates = new HashSet<int>();
        for (int f = 0; f < check.Faces.Count; f++) candidates.Add(f);

        for (int round = 0; round < ClearFaceRounds && candidates.Count > 0; round++)
        {
            foreach (int n in touched) need[n] = 0f;
            touched.Clear();
            foreach (int f in candidates)
            {
                var face = check.Faces[f];
                var (push, outward) = check.Need(face, nodeDelta, SettleTolerance);
                if (push <= 0f || push > ClearFaceDeepest) continue;   // not sag: see ClearFaceDeepest
                foreach (int n in new[] { face.A, face.B, face.C })
                {
                    if (held[n]) continue;
                    // Never more than ClearFaceMost in all, over every round.
                    float allowed = MathF.Min(push, ClearFaceMost - cleared[n]);
                    if (allowed <= 0f) continue;
                    if (need[n] == 0f) touched.Add(n);
                    if (allowed <= need[n]) continue;
                    need[n] = allowed;
                    dir[n] = outward;
                }
            }
            if (touched.Count == 0) break;

            // How deep the skin is through each triangle beside a push that nothing would lift again, before the push.
            var sunkBefore = new Dictionary<int, float>();
            foreach (int n in touched)
                foreach (int t in check.TrisOf[n])
                    if (check.Unliftable[t] && !sunkBefore.ContainsKey(t)) sunkBefore[t] = check.Depth(t, nodeDelta);

            var was = new Dictionary<int, Vec3>(touched.Count);
            foreach (int n in touched)
            {
                was[n] = nodeDelta[n];
                var d = dir[n] * need[n];
                nodeDelta[n] = new Vec3(nodeDelta[n].X + d.X, nodeDelta[n].Y + d.Y, nodeDelta[n].Z + d.Z);
            }

            // Put back every push that turned a triangle over, until none does. Putting one back can turn another over
            // against a neighbour that kept its push, so it repeats; it ends, because each round puts back at least one
            // of finitely many pushes, and with all of them back the mesh is as it was.
            bool changed = true;
            while (changed)
            {
                changed = false;

                // Through another layer of cloth: put back. A lined or layered garment's inner layer sits nearer the
                // skin than its shell, so it is the layer the skin comes through, and pushed clear of the skin it went
                // straight through the shell — skin through a lining the shell covers was never seen, a lining through
                // the shell is. Measured on "Sirius" (a corset, Neolithe XS to S): 211 of the 439 nodes this pass moved
                // went through the layer over them, up to 10.6 mm, the jagged top edge of the cups in game. Carrying the
                // layer in front along instead was measured and is worse everywhere (lumpier, deeper, more faces through).
                var cloth = Tuned.NoLayerGuard ? [] : ClothGrid(check, nodeDelta);
                foreach (int n in touched)
                {
                    if (Tuned.NoLayerGuard) break;
                    if (held[n] || !was.TryGetValue(n, out var back)) continue;
                    if (CrossedCloth(check, cloth, nodeDelta, n, check.AtWith(n, back), check.At(n, nodeDelta)) < 0) continue;
                    nodeDelta[n] = back;
                    held[n] = true;
                    changed = true;
                }

                foreach (int n in touched)
                {
                    if (held[n]) continue;
                    foreach (int t in check.TrisOf[n])
                    {
                        bool folds = !foldedBefore[t] && check.TurnedOver(t, nodeDelta);
                        bool sinks = sunkBefore.TryGetValue(t, out float depth)
                                  && check.Depth(t, nodeDelta) > depth + SettleTolerance;
                        if (!folds && !sinks) continue;
                        var (a, b, c) = check.Tris[t];
                        foreach (int m in new[] { a, b, c })
                        {
                            if (!was.TryGetValue(m, out var back) || held[m]) continue;
                            nodeDelta[m] = back;
                            held[m] = true;
                            changed = true;
                        }
                    }
                }
            }

            candidates.Clear();
            foreach (int n in touched)
            {
                if (held[n]) continue;
                if (pushedBy[n] <= 0f) newly++;
                pushedBy[n] += need[n];
                cleared[n] += need[n];
                if (pushedBy[n] > worst) worst = pushedBy[n];
                foreach (int f in check.FacesOf[n]) candidates.Add(f);
            }
        }
        return newly;
    }

    /// <summary>Grid cell <see cref="ClothGrid"/> buckets cloth triangles by (10 mm).</summary>
    private const float ClothCell = 0.01f;

    /// <summary>The garment's cloth triangles where they stand now, bucketed by every cell their bounds touch.</summary>
    private static Dictionary<(int, int, int), List<int>> ClothGrid(FaceCheck check, Vec3[] nodeDelta)
    {
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int t = 0; t < check.Tris.Count; t++)
        {
            if (!check.IsCloth[t]) continue;
            var (a, b, c) = check.Tris[t];
            Vector3 pa = check.At(a, nodeDelta), pb = check.At(b, nodeDelta), pc = check.At(c, nodeDelta);
            var lo = Vector3.Min(pa, Vector3.Min(pb, pc));
            var hi = Vector3.Max(pa, Vector3.Max(pb, pc));
            for (int x = (int)MathF.Floor(lo.X / ClothCell); x <= (int)MathF.Floor(hi.X / ClothCell); x++)
            for (int y = (int)MathF.Floor(lo.Y / ClothCell); y <= (int)MathF.Floor(hi.Y / ClothCell); y++)
            for (int z = (int)MathF.Floor(lo.Z / ClothCell); z <= (int)MathF.Floor(hi.Z / ClothCell); z++)
            {
                if (!grid.TryGetValue((x, y, z), out var bucket)) grid[(x, y, z)] = bucket = [];
                bucket.Add(t);
            }
        }
        return grid;
    }

    /// <summary>
    /// The cloth triangle node <paramref name="n"/> passes through, moving from <paramref name="from"/> to
    /// <paramref name="to"/> — one it is not a corner of, where that triangle stands now — or -1.
    /// </summary>
    private static int CrossedCloth(FaceCheck check, Dictionary<(int, int, int), List<int>> grid, Vec3[] nodeDelta,
                                    int n, Vector3 from, Vector3 to)
    {
        var seg = to - from;
        if (seg.LengthSquared() < 1e-14f) return -1;
        var lo = Vector3.Min(from, to);
        var hi = Vector3.Max(from, to);
        var seen = new HashSet<int>();
        for (int x = (int)MathF.Floor(lo.X / ClothCell); x <= (int)MathF.Floor(hi.X / ClothCell); x++)
        for (int y = (int)MathF.Floor(lo.Y / ClothCell); y <= (int)MathF.Floor(hi.Y / ClothCell); y++)
        for (int z = (int)MathF.Floor(lo.Z / ClothCell); z <= (int)MathF.Floor(hi.Z / ClothCell); z++)
        {
            if (!grid.TryGetValue((x, y, z), out var bucket)) continue;
            foreach (int t in bucket)
            {
                if (!seen.Add(t)) continue;
                var (a, b, c) = check.Tris[t];
                if (a == n || b == n || c == n) continue;
                if (SegmentHits(from, seg, check.At(a, nodeDelta), check.At(b, nodeDelta), check.At(c, nodeDelta)))
                    return t;
            }
        }
        return -1;
    }

    /// <summary>Möller–Trumbore: whether the segment from + s·seg, s in (0, 1], meets triangle abc.</summary>
    private static bool SegmentHits(Vector3 from, Vector3 seg, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(seg, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-14f) return false;
        float inv = 1f / det;
        var s = from - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(seg, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        float t = Vector3.Dot(e2, q) * inv;
        return t > 1e-4f && t <= 1f;
    }

    /// <summary>Rounds of push-then-smooth <see cref="Settle"/> gets.</summary>
    private const int SettleRounds = 32;

    /// <summary>How many rings of neighbours around the cloth still inside <see cref="Settle"/> moves with it.</summary>
    private const int SettleRings = 4;

    /// <summary>How far each settle round takes a node toward its neighbours' movement.</summary>
    private const float SettleRate = 0.5f;

    /// <summary>How deep in the skin a node must still be for <see cref="Settle"/> to run (0.1 mm).</summary>
    private const float SettleTolerance = 1e-4f;

    /// <summary>Push-only rounds <see cref="Settle"/> finishes with, for a node one push left inside the skin on the
    /// other side of a crease.</summary>
    private const int SettleFinishRounds = 4;

    /// <summary>
    /// Clear what the push above could not: cloth still inside the drawn skin once its push has been applied.
    /// <para/>
    /// The push is one step along each node's own skin normal, and where the skin under neighbouring nodes faces
    /// different ways that step turns their triangles over, so the fold guard halves it — all the way to nothing where
    /// the disagreement is large. That is the underbust crease of a garment refitted onto a larger breast: the breast's
    /// underside faces down and back, the ribs below it face forward, and the cloth the transfer carried into the crease
    /// needs pushing both ways at once. Measured on "Coat of Many Colors" (Neolithe XS to Rue+/YAB+ Large): the guard
    /// took pushes of 8-10 mm to zero and left 180 cloth vertices up to 8.8 mm inside the breasts, a dark band under
    /// each one in 3ds Max.
    /// <para/>
    /// What cloth does there is span the crease rather than follow it into the fold, and that is what alternating the
    /// two finds: push whatever is inside out along the skin's normal, then move each node part way toward its
    /// neighbours' movement, so the patch moves together and draws taut across the crease instead of splitting along
    /// it. The movement, not the position, is what is smoothed, so the author's own wrinkles ride along. Only the cloth
    /// still inside and a few rings around it move. It finishes with a few rounds of pushing alone, because one push
    /// along one surface's normal can land a point in a crease inside the surface on its other side; a point that is
    /// STILL inside after those is left there rather than chased further. Nothing here runs when the push above
    /// cleared everything, which is every refit it already handled.
    /// </summary>
    /// <param name="pushedBy">How far the push above moved each node, added to here: the report counts a node the
    /// settle pushes once, and gives the furthest total push, not how far the smoothing carried a node.</param>
    /// <returns>How many nodes it pushed that the push above had not.</returns>
    private static int Settle(Sets sets, List<int> nodes, TargetBody after, Vec3[] nodeDelta, float[] authored,
                              bool clearBody, float[] pushedBy, ref float worst)
    {
        var inSet = new bool[sets.NodeCount];
        foreach (int n in nodes) inSet[n] = true;

        // How far out along the skin's normal a node has to go to be clear, or 0 when it is.
        float Need(int n, out Vector3 normal)
        {
            normal = default;
            var p = Placed(sets, nodeDelta, n);
            if (!after.Deepest(p, PushProbeRange, out var hit)) return 0f;
            float s = Vector3.Dot(p - hit.Point, hit.Normal);
            if (s >= -SettleTolerance) return 0f;
            if (clearBody && s < -ClearDepth) return 0f;   // buried deep on purpose — see PushOut
            normal = hit.Normal;
            return (clearBody ? Clearance : MathF.Min(authored[n], Clearance)) - s;
        }

        var region = new List<int>();
        var inRegion = new bool[sets.NodeCount];
        foreach (int n in nodes)
            if (Need(n, out _) > 0f) { region.Add(n); inRegion[n] = true; }
        if (region.Count == 0) return 0;

        // The rings around it: the cloth that has to move with it for the patch to stay in one piece.
        var ring = new List<int>(region);
        for (int r = 0; r < SettleRings; r++)
        {
            var next = new List<int>();
            foreach (int n in ring)
                foreach (int m in sets.Adj[n])
                {
                    if (!inSet[m] || inRegion[m]) continue;
                    inRegion[m] = true;
                    region.Add(m);
                    next.Add(m);
                }
            ring = next;
        }

        var wasPushed = new bool[region.Count];
        for (int i = 0; i < region.Count; i++) wasPushed[i] = pushedBy[region[i]] > 0f;
        // The triangles the region touches, and which of them were already turned over before the settle: those are
        // the push's or the transfer's, and not this pass's to take back.
        var tris = new List<(int A, int B, int C)>();
        for (int t = 0; t + 2 < sets.Tris.Length; t += 3)
        {
            int va = sets.Tris[t], vb = sets.Tris[t + 1], vc = sets.Tris[t + 2];
            if (va < 0 || vb < 0 || vc < 0
                || va >= sets.NodeOf.Length || vb >= sets.NodeOf.Length || vc >= sets.NodeOf.Length) continue;
            int a = sets.NodeOf[va], b = sets.NodeOf[vb], c = sets.NodeOf[vc];
            if (a == b || b == c || c == a || !(inRegion[a] || inRegion[b] || inRegion[c])) continue;
            tris.Add((a, b, c));
        }
        var foldedBefore = new bool[tris.Count];
        for (int t = 0; t < tris.Count; t++) foldedBefore[t] = TurnedOver(tris[t]);

        // Push everything in the region still inside out along the skin's normal; whether anything was.
        bool Push()
        {
            bool inside = false;
            foreach (int n in region)
            {
                float need = Need(n, out var normal);
                if (need <= 0f) continue;
                inside = true;
                nodeDelta[n] = new Vec3(nodeDelta[n].X + normal.X * need, nodeDelta[n].Y + normal.Y * need,
                                        nodeDelta[n].Z + normal.Z * need);
                pushedBy[n] += need;
            }
            return inside;
        }

        var smoothed = new Vec3[region.Count];
        for (int round = 0; round < SettleRounds; round++)
        {
            if (!Push()) break;

            // Toward the neighbours' movement, all at once so the order the nodes are visited in does not matter.
            // Neighbours outside the region count as they are and do not move.
            for (int i = 0; i < region.Count; i++)
            {
                int n = region[i];
                var d = nodeDelta[n];
                if (sets.Adj[n].Count == 0) { smoothed[i] = d; continue; }
                var sum = default(Vec3);
                foreach (int m in sets.Adj[n]) sum = new Vec3(sum.X + nodeDelta[m].X, sum.Y + nodeDelta[m].Y, sum.Z + nodeDelta[m].Z);
                float inv = 1f / sets.Adj[n].Count;
                smoothed[i] = new Vec3(d.X + (sum.X * inv - d.X) * SettleRate, d.Y + (sum.Y * inv - d.Y) * SettleRate,
                                       d.Z + (sum.Z * inv - d.Z) * SettleRate);
            }
            for (int i = 0; i < region.Count; i++) nodeDelta[region[i]] = smoothed[i];
        }

        // The smoothing may have had the last word, and a push may have landed a point inside the skin across a
        // crease: pushing alone until clear, or until these rounds run out.
        for (int round = 0; round < SettleFinishRounds; round++)
            if (!Push()) break;

        // Pushed along each node's own normal with nothing to stop two neighbours stepping past each other: a triangle
        // the settle turned over has its region corners moved toward their neighbours' movement and pushed clear again.
        // Not halved back toward where the settle found them, the push's own guard — measured here that put 42 points
        // back inside the breast AND turned more triangles over (78 -> 99), the failure the settle exists to undo. This
        // way nothing goes back inside; it takes back few folds (Rue L 78 -> 79, "This Old Thing" L to Rue L 20 -> 21),
        // because the push after the relax re-tips most of them, and what it leaves is the fold relax's (Unfold).
        for (int pass = 0; pass < PushUnfoldPasses; pass++)
        {
            var folded = new HashSet<int>();
            for (int t = 0; t < tris.Count; t++)
            {
                if (foldedBefore[t] || !TurnedOver(tris[t])) continue;
                foreach (int n in new[] { tris[t].A, tris[t].B, tris[t].C })
                    if (inRegion[n]) folded.Add(n);
            }
            if (folded.Count == 0) break;
            var relaxed = new Dictionary<int, Vec3>(folded.Count);
            foreach (int n in folded)
            {
                if (sets.Adj[n].Count == 0) continue;
                var sum = default(Vec3);
                foreach (int m in sets.Adj[n]) sum = new Vec3(sum.X + nodeDelta[m].X, sum.Y + nodeDelta[m].Y, sum.Z + nodeDelta[m].Z);
                float inv = 1f / sets.Adj[n].Count;
                var d = nodeDelta[n];
                relaxed[n] = new Vec3(d.X + (sum.X * inv - d.X) * SettleRate, d.Y + (sum.Y * inv - d.Y) * SettleRate,
                                      d.Z + (sum.Z * inv - d.Z) * SettleRate);
            }
            foreach (var (n, d) in relaxed) nodeDelta[n] = d;
            Push();
        }

        int newlyPushed = 0;
        for (int i = 0; i < region.Count; i++)
        {
            int n = region[i];
            if (pushedBy[n] <= 0f) continue;
            if (!wasPushed[i]) newlyPushed++;
            if (pushedBy[n] > worst) worst = pushedBy[n];
        }
        return newlyPushed;

        // Facing the other way from how the author drew it — the push's own test.
        bool TurnedOver((int A, int B, int C) t)
        {
            var was = ToVector(sets.NodeAt[t.A]);
            var n0 = Vector3.Cross(ToVector(sets.NodeAt[t.B]) - was, ToVector(sets.NodeAt[t.C]) - was);
            if (n0.Length() <= 1e-12f) return false;   // degenerate as authored
            var now = Placed(sets, nodeDelta, t.A);
            var n1 = Vector3.Cross(Placed(sets, nodeDelta, t.B) - now, Placed(sets, nodeDelta, t.C) - now);
            return Vector3.Dot(n0, n1) <= 0f;
        }
    }
}
