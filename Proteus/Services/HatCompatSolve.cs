using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// Works out how a hairstyle has to move to fit under a hat: which vertices to press against the skull, and
/// which parts stand too far off it to be pressed at all and should be hidden instead.
/// <para/>
/// Pure geometry — no game, no files, no Penumbra. Everything it needs is in the model bytes, and everything
/// it returns is addressed the way <see cref="ModelAttributeWriter.AddShape"/> wants it: MESH-RELATIVE
/// vertex indices, per mesh.
/// <para/>
/// The method is the one in Ulli's hat-compatibility guide, which is what every hand-made hat-compatible
/// hair on the workshop was built by: separate the ponytails, then "smush that hair against the scalp".
/// Reading the shapes out of 76 hand-made ones says the press is gentle — a median of about four
/// millimetres and a maximum around three centimetres, almost always inward — and says nothing more than
/// that, because they are freehand sculpts and follow no rule an algorithm could copy. So the rule here is
/// the guide's stated intent rather than a fit to those examples, and it is checked against a real hat.
/// </summary>
public static class HatCompatSolve
{
    /// <summary>
    /// The HAT LINE: how far above the head's centre a hat actually sits on the head, in model units.
    /// <para/>
    /// This is the only boundary that matters, and everything else here follows from it. Above it a hat
    /// covers the hair completely, so the hair can be crushed as hard as you like — flat to the skull, or
    /// inside it — and nothing shows. Below it the hat is simply not there, and moving a single vertex is a
    /// visible defect on a part of the hairstyle the wearer chose.
    /// <para/>
    /// Measured off the reference hat by slicing it horizontally and finding where it comes closest to the
    /// head's own vertical axis: the brim's outer edge reaches 235 mm out at the bottom and narrows steadily
    /// to a wall about 80 mm out from y = 1.540 upwards, which is the opening the head goes through. That is
    /// 13 mm above the head centre; 12 is used, a whisker lower, so the crushed region ends just inside what
    /// the hat covers rather than just outside it.
    /// <para/>
    /// The brim's LOWEST point is not the line and using it would be a bad mistake — a brim flares far away
    /// from the head, so that would have licensed crushing hair a hand's width below anything a hat touches.
    /// </summary>
    public const float HatLine = 0.012f;

    /// <summary>
    /// Where the covered hair is driven to, as a FRACTION of the scalp's own radius in that direction.
    /// <para/>
    /// Inside the head, not onto its surface. There is nothing to lose by overshooting — this geometry is
    /// under a hat and cannot be seen wherever it ends up — and a great deal to lose by stopping short,
    /// because a strand left level with the skull still shows through the hat as a white sliver.
    /// <para/>
    /// A fraction rather than a fixed depth, and that is the fix for the last thing measurement caught. The
    /// "scalp" is read off the wearer's face model, which includes the brow, nose and lashes — so in the
    /// forward direction it reaches a good deal further out than the forehead a hat actually presses
    /// against. A fixed 10 mm inset left the front hairline still outside the hat on four hairstyles;
    /// scaling the radius shortens the overshoot exactly where the head model is longest.
    /// </summary>
    public const float ScalpFraction = 0.75f;

    /// <summary>
    /// The most shape values one model may carry: <c>ShapeValueCount</c> in the model header is a u16.
    /// <para/>
    /// Left short of 65535 so a hairstyle that already has a shape of its own still has room, and because a
    /// value is spent per INDEX SLOT — a vertex drawn by six triangles costs six — which makes the true cost
    /// of a candidate hard to eyeball and easy to under-estimate.
    /// </summary>
    public const int MaxShapeValues = 60000;

    /// <summary>
    /// How far off the scalp a part may still sit AFTER the press before it is offered for hiding.
    /// <para/>
    /// After, not before, and that distinction is the whole criterion. Judged on where a part starts, this
    /// condemns the entire hairstyle — ordinary hair is three or four centimetres thick, so on raw standoff
    /// every part of every model clears any threshold worth setting, and the first version of this offered
    /// to hide between 82% and 100% of twelve real hairstyles, which in game made the wearer bald.
    /// <para/>
    /// Now that everything above <see cref="HatLine"/> is driven inside the skull, almost nothing survives
    /// this test, and that is the correct answer rather than a broken one: hair the press could not deal
    /// with is hair BELOW the line, which hangs out from under a hat exactly as real hair does and must not
    /// be hidden. The list is kept for the genuine exception — a part that reaches above the line and still
    /// stands proud because the budget ran out before it.
    /// </summary>
    public const float HideThreshold = 0.03f;

    /// <summary>Directional bins over the sphere. 32 around by 16 up — about 11 degrees, which is finer than
    /// the features of a skull and coarse enough that every bin a hair covers holds several vertices.</summary>
    private const int BinsU = 32, BinsV = 16;

    /// <summary>
    /// How far, in bins, the scalp floor looks for the hair's nearest approach to the skull — about 22
    /// degrees either way.
    /// <para/>
    /// Wide enough to reach a parting or the nape where hair lies right on the scalp, narrow enough that
    /// the crown does not inherit its radius from the temple. This is the single most sensitive number in
    /// the pass: too small and the floor is the hair's own inner surface, so the press does nothing; too
    /// large and the floor becomes one global minimum and the press flattens the head to a sphere.
    /// </summary>
    private const int MinRadiusBins = 2;

    /// <param name="Moved">Per LOD0 mesh, the mesh-relative vertices to move and where to.</param>
    /// <param name="Hide">Parts that cannot be pressed under a hat and should be tagged <c>atr_kam</c>.</param>
    /// <param name="Centre">The skull centre the press was computed about.</param>
    /// <param name="Radius">Median scalp radius about that centre.</param>
    public sealed record Result(
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> Moved,
        IReadOnlyList<ModelPart> Hide,
        Vector3 Centre,
        float Radius,
        float MedianPress,
        float MaxPressed,
        int Considered)
    {
        /// <summary>Nothing to do — for a hairstyle whose author already made it hat-compatible.</summary>
        public static Result None { get; } = new(
            new Dictionary<int, IReadOnlyDictionary<int, Vector3>>(), [], Vector3.Zero, 0, 0, 0, 0);
    }

    /// <summary>One LOD0 mesh's own vertices, indexed the way the model's index buffer indexes them.</summary>
    internal sealed record MeshVerts(int Mesh, Vector3[] Positions);

    /// <summary>
    /// Read every LOD0 mesh's positions separately.
    /// <para/>
    /// Separately, and that is the point: <see cref="ModelPartReader"/> and
    /// <see cref="SecondSkinWriter.TryReadLod0Geometry"/> both concatenate the meshes and rebase the
    /// indices, which is right for drawing and useless for editing — the writer needs the number the index
    /// buffer actually holds, and a rebased one silently addresses a different vertex.
    /// </summary>
    internal static List<MeshVerts> ReadLod0Meshes(byte[] mdl)
    {
        var src = SecondSkinWriter.Parse(mdl);
        var all = new List<MeshVerts>();
        int end = Math.Min(src.Lod0MeshIndex + src.Lod0MeshCount, src.MeshCount);
        Span<float> t = stackalloc float[4];

        for (int m = src.Lod0MeshIndex; m < end; m++)
        {
            int mo = src.MeshStart + m * 36;
            if (mo + 36 > mdl.Length) break;
            ushort vc = BitConverter.ToUInt16(mdl, mo);
            if (vc == 0 || m >= src.Decls.Length) continue;

            var pos = Array.Find(src.Decls[m], e => e.Usage == SecondSkinWriter.UsePosition);
            if (!Array.Exists(src.Decls[m], e => e.Usage == SecondSkinWriter.UsePosition)) continue;
            byte stride = mdl[mo + 32 + pos.Stream];
            if (stride == 0) continue;
            int at = src.Vb + (int)BitConverter.ToUInt32(mdl, mo + 20 + pos.Stream * 4);
            if (at < 0 || at + vc * stride > mdl.Length) continue;

            var p = new Vector3[vc];
            for (int v = 0; v < vc; v++)
            {
                SecondSkinWriter.ReadTyped(mdl, at + v * stride + pos.Offset, pos.Type, t);
                p[v] = new Vector3(t[0], t[1], t[2]);
            }
            all.Add(new MeshVerts(m, p));
        }
        return all;
    }

    /// <summary>
    /// Where the head is, judged from the hair alone.
    /// <para/>
    /// The centroid of every vertex is not it — long hair hangs well below the skull and drags the answer
    /// down the neck. The scalp is at the TOP, so the estimate uses only the upper part of the model's own
    /// height, and takes the middle of that band's extent rather than its centroid so that a dense ponytail
    /// on one side does not pull it sideways.
    /// </summary>
    internal static (Vector3 Centre, float Radius) HeadFrame(IReadOnlyList<MeshVerts> meshes)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var mv in meshes)
            foreach (var p in mv.Positions) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        if (lo.X > hi.X) return (Vector3.Zero, 0f);

        // The top third of the model's height is scalp and crown on every hairstyle; below that a bob is
        // still head-shaped but a long style is not.
        float cut = hi.Y - (hi.Y - lo.Y) * 0.34f;
        var band = new List<Vector3>();
        foreach (var mv in meshes)
            foreach (var p in mv.Positions)
                if (p.Y >= cut) band.Add(p);
        if (band.Count < 16) return ((lo + hi) * 0.5f, (hi - lo).Length() * 0.25f);

        var blo = new Vector3(float.MaxValue);
        var bhi = new Vector3(float.MinValue);
        foreach (var p in band) { blo = Vector3.Min(blo, p); bhi = Vector3.Max(bhi, p); }

        // Centred in x and z on the band, and dropped below its top by a skull's radius — the crown of the
        // head is the top of that band, not its middle.
        float r = MathF.Max(bhi.X - blo.X, bhi.Z - blo.Z) * 0.5f;
        var centre = new Vector3((blo.X + bhi.X) * 0.5f, bhi.Y - r, (blo.Z + bhi.Z) * 0.5f);
        return (centre, r);
    }

    /// <summary>
    /// The head's own centre and radius, from a face model.
    /// <para/>
    /// Exact where the hair-derived guess is not. Three unrelated face mods measured here agree on the
    /// centre to four decimal places, because they are all edits of the same base head — so this is a
    /// property of the race, recovered per wearer rather than tabulated, which is what makes it work for a
    /// modded head too.
    /// </summary>
    internal static (Vector3 Centre, float Radius)? HeadFrameFrom(byte[] head)
    {
        var meshes = ReadLod0Meshes(head);
        if (meshes.Count == 0) return null;

        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        int n = 0;
        foreach (var mv in meshes)
            foreach (var p in mv.Positions) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); n++; }
        if (n < 64 || lo.X > hi.X) return null;

        var centre = (lo + hi) * 0.5f;
        // The UPPER half only. The jaw and chin hang well below the cranium and would drag a whole-model
        // radius down, and it is the cranium a hat sits on.
        var up = new List<float>();
        foreach (var mv in meshes)
            foreach (var p in mv.Positions)
                if (p.Y > centre.Y) up.Add((p - centre).Length());
        if (up.Count == 0) return null;
        up.Sort();
        return (centre, up[up.Count / 2]);
    }

    /// <summary>
    /// The scalp itself, per direction — the distance from the head centre out to the head's surface.
    /// <para/>
    /// The FURTHEST vertex in each direction, not the nearest: a head model carries eyes, lashes, teeth and
    /// the inside of the mouth, and pressing hair onto the nearest of those would drive it through the
    /// skull. Directions the head does not reach — straight down the neck — inherit the median so that a
    /// gap never reads as "the scalp is at the centre".
    /// </summary>
    private static float[] HeadFloor(IReadOnlyList<MeshVerts> head, Vector3 centre, float radius, float minY)
    {
        var ceil = new float[BinsU * BinsV];
        foreach (var mv in head)
            foreach (var p in mv.Positions)
            {
                // THE CRANIUM ONLY — the part of the head above the hat line, which is the part hair is
                // pressed against. A face model is a whole head, nose and chin included, and in the forward
                // direction the nose reaches 125 mm from the head's centre where the forehead reaches about
                // 100. Letting it in makes the "scalp" bulge forward by 25 mm, so hair at the front hairline
                // reads as already tucked inside the head when it is in fact outside the hat, and the press
                // leaves it alone. That was the last place strands were coming through.
                if (p.Y < minY) continue;

                var d = p - centre;
                float len = d.Length();
                if (len < 1e-5f) continue;
                int b = BinOf(d / len);
                if (len > ceil[b]) ceil[b] = len;
            }

        var seen = ceil.Where(f => f > 0f).OrderBy(f => f).ToArray();
        float median = seen.Length > 0 ? seen[seen.Length / 2] : radius;
        for (int i = 0; i < ceil.Length; i++) if (ceil[i] <= 0f) ceil[i] = median;

        var smooth = new float[ceil.Length];
        for (int iv = 0; iv < BinsV; iv++)
            for (int iu = 0; iu < BinsU; iu++)
            {
                float sum = 0;
                int n = 0;
                for (int dv = -1; dv <= 1; dv++)
                {
                    int jv = iv + dv;
                    if (jv < 0 || jv >= BinsV) continue;
                    for (int du = -1; du <= 1; du++) { sum += ceil[jv * BinsU + ((iu + du + BinsU) % BinsU)]; n++; }
                }
                smooth[iv * BinsU + iu] = sum / n;
            }
        return smooth;
    }

    /// <summary>Bin index for a direction, and the bin's neighbours for smoothing.</summary>
    private static int BinOf(Vector3 dir)
    {
        float u = MathF.Atan2(dir.Z, dir.X);                       // -pi..pi
        float v = MathF.Acos(Math.Clamp(dir.Y, -1f, 1f));          // 0..pi
        int iu = (int)MathF.Floor((u + MathF.PI) / (2 * MathF.PI) * BinsU) % BinsU;
        int iv = Math.Clamp((int)MathF.Floor(v / MathF.PI * BinsV), 0, BinsV - 1);
        if (iu < 0) iu += BinsU;
        return iv * BinsU + iu;
    }

    /// <summary>
    /// The hair's own scalp: per direction, the CLOSEST the hair comes to the head centre.
    /// <para/>
    /// A hairstyle is a shell around a skull, so its inner surface is the skull — which means the target to
    /// press towards can be read off the hair itself and no head model is needed. Smoothed across
    /// neighbouring directions afterwards, because one stray vertex tucked deep inside would otherwise
    /// become the target for everything around it and punch a dent in the result.
    /// </summary>
    private static float[] ScalpFloor(IReadOnlyList<MeshVerts> meshes, Vector3 centre, float fallback)
    {
        var floor = new float[BinsU * BinsV];
        Array.Fill(floor, float.MaxValue);
        foreach (var mv in meshes)
            foreach (var p in mv.Positions)
            {
                var d = p - centre;
                float len = d.Length();
                if (len < 1e-5f) continue;
                int b = BinOf(d / len);
                if (len < floor[b]) floor[b] = len;
            }

        // A bin no vertex fell in gets the median of the ones that did, so a gap cannot read as "the scalp
        // is at the centre" and collapse anything pointing at it.
        var seen = floor.Where(f => f < float.MaxValue).OrderBy(f => f).ToArray();
        float median = seen.Length > 0 ? seen[seen.Length / 2] : fallback;
        for (int i = 0; i < floor.Length; i++) if (floor[i] == float.MaxValue) floor[i] = median;

        // A MIN filter first, over a wide neighbourhood, and this is the difference between pressing hair
        // onto the head and barely pressing it at all. The nearest vertex in one direction is only the
        // scalp where the hair actually touches down; over a thick style it is still most of the hair's
        // thickness away from the skull, and a floor built from it leaves every vertex already at its
        // target. Hair touches the scalp SOMEWHERE nearby, though, so widening the search finds that
        // contact and carries its radius across the directions between — which is what "smush it against
        // the scalp" means and what the local hull can never express.
        var pulled = new float[floor.Length];
        for (int iv = 0; iv < BinsV; iv++)
            for (int iu = 0; iu < BinsU; iu++)
            {
                float lo = float.MaxValue;
                for (int dv = -MinRadiusBins; dv <= MinRadiusBins; dv++)
                {
                    int jv = iv + dv;
                    if (jv < 0 || jv >= BinsV) continue;
                    for (int du = -MinRadiusBins; du <= MinRadiusBins; du++)
                        lo = MathF.Min(lo, floor[jv * BinsU + ((iu + du + BinsU) % BinsU)]);
                }
                pulled[iv * BinsU + iu] = lo;
            }

        // Then a mean, so the min filter's flat plateaus and their step edges do not print themselves into
        // the deformed surface as facets.
        var smooth = new float[floor.Length];
        for (int iv = 0; iv < BinsV; iv++)
            for (int iu = 0; iu < BinsU; iu++)
            {
                float sum = 0;
                int n = 0;
                for (int dv = -1; dv <= 1; dv++)
                {
                    int jv = iv + dv;
                    if (jv < 0 || jv >= BinsV) continue;
                    for (int du = -1; du <= 1; du++)
                    {
                        sum += pulled[jv * BinsU + ((iu + du + BinsU) % BinsU)];
                        n++;
                    }
                }
                smooth[iv * BinsU + iu] = sum / n;
            }
        return smooth;
    }

    /// <summary>
    /// Work out the press.
    /// <para/>
    /// Every vertex standing proud of its own scalp floor is moved back along the line to the head centre
    /// through the scalp to <see cref="ScalpFraction"/> of its radius, and nothing below <see cref="HatLine"/>
    /// out below the brim line so that hair on the neck and shoulders — which no hat touches — is left
    /// exactly where the author put it.
    /// </summary>
    /// <param name="parts">Read once by the caller and passed in, so the confirmation UI and the solve are
    /// looking at the same island split rather than two independent ones.</param>
    /// <param name="head">The wearer's face model (<c>..._fac.mdl</c>), which carries the whole cranium and
    /// not merely a face. Strongly preferred: it IS the scalp the guide says to smush against, it is exact
    /// for whatever head the player is actually wearing, and it costs nothing to obtain because the live
    /// model list the hair path came from already names it. Without it the skull has to be guessed from the
    /// hair's own inner surface, which fails outright on a style that never touches the scalp.</param>
    public static Result Solve(byte[] mdl, ModelParts parts, byte[]? head = null,
                               float depth = ScalpFraction)
    {
        var meshes = ReadLod0Meshes(mdl);
        var moved = new Dictionary<int, IReadOnlyDictionary<int, Vector3>>();
        if (meshes.Count == 0)
            return new Result(moved, [], Vector3.Zero, 0, 0, 0, 0);

        var frame = head != null ? HeadFrameFrom(head) : null;
        var (centre, radius) = frame ?? HeadFrame(meshes);
        var floor = frame != null
            ? HeadFloor(ReadLod0Meshes(head!), centre, radius, centre.Y + HatLine)
            : ScalpFloor(meshes, centre, radius);

        // THE HAT LINE. A hard cut, with no feathering across it, and that is deliberate: a fade band moves
        // vertices near the boundary by a fraction of what their neighbours move, which on a real hairstyle
        // reads as jagged, crunchy hair in exactly the place a hat draws the eye to. Above the line
        // everything is crushed equally; below it nothing moves at all.
        float hatLine = centre.Y + HatLine;

        // What each vertex would cost to shape: one value per index slot naming it.
        var valence = Valence(mdl, SecondSkinWriter.Parse(mdl), meshes);

        // Every vertex poking out through the ceiling, with what it costs and how far out it is.
        var candidates = new List<(int Mesh, int Vertex, Vector3 To, float Press, int Cost)>();
        foreach (var mv in meshes)
        {
            valence.TryGetValue(mv.Mesh, out var cost);
            for (int v = 0; v < mv.Positions.Length; v++)
            {
                var p = mv.Positions[v];
                if (p.Y < hatLine) continue;                        // below the hat: never touched

                var d = p - centre;
                float len = d.Length();
                if (len < 1e-5f) continue;

                // Hair already inside the cranium's own radius is inside the hat too, so it needs nothing
                // and — the reason this test is here rather than a nicety — a shape value spent on it is
                // one the format cannot spare. The header counts values in a u16, and pressing every
                // covered vertex exhausted that budget on four of these hairstyles, at which point the
                // ones dropped were whichever the sort reached last.
                float scalp = floor[BinOf(d / len)];
                if (len <= scalp) continue;

                float target = MathF.Max(0.005f, scalp * depth);
                if (len <= target) continue;

                int slots = cost != null && v < cost.Length ? cost[v] : 0;
                if (slots == 0) continue;                           // drawn by nothing; a spare would do nothing
                candidates.Add((mv.Mesh, v, centre + d * (target / len), len - target, slots));
            }
        }

        // Under the budget, everything goes. Over it, the ones poking out FURTHEST go first — so what gets
        // left behind is the hair already closest to fitting, and the shape degrades by getting gentler
        // rather than by developing a hard edge somewhere arbitrary.
        // Whatever the model already spends on its own shapes comes off the top — the count in the header is
        // for the whole file, not per shape.
        int already = BitConverter.ToUInt16(mdl, SecondSkinWriter.Parse(mdl).Mh + 20);
        int budget = Math.Max(0, MaxShapeValues - already), spent = 0;
        if (candidates.Sum(c => c.Cost) > budget)
            candidates.Sort((a, b) => b.Press.CompareTo(a.Press));

        var presses = new List<float>();
        foreach (var c in candidates)
        {
            if (spent + c.Cost > budget) continue;
            spent += c.Cost;
            if (!moved.TryGetValue(c.Mesh, out var here))
                moved[c.Mesh] = here = new Dictionary<int, Vector3>();
            ((Dictionary<int, Vector3>)here)[c.Vertex] = c.To;
            presses.Add(c.Press);
        }

        presses.Sort();
        return new Result(
            moved,
            HideList(mdl, parts, centre, floor, moved, hatLine),
            centre, radius,
            presses.Count > 0 ? presses[presses.Count / 2] : 0f,
            presses.Count > 0 ? presses[^1] : 0f,
            presses.Count);
    }

    /// <summary>
    /// How many index slots name each vertex, per mesh — the price of shaping it.
    /// <para/>
    /// A shape value rewires ONE slot, so a vertex shared by six triangles needs six of them. Budgeting on
    /// vertex counts instead of this under-counts by roughly six to one, which on a real hairstyle is the
    /// difference between a shape that fits the format and one that cannot be written.
    /// </summary>
    private static Dictionary<int, int[]> Valence(
        byte[] mdl, SecondSkinWriter.Source src, IReadOnlyList<MeshVerts> meshes)
    {
        var result = new Dictionary<int, int[]>();
        foreach (var mv in meshes)
        {
            int mo = src.MeshStart + mv.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;
            uint ic = BitConverter.ToUInt32(mdl, mo + 4), start = BitConverter.ToUInt32(mdl, mo + 16);
            if ((long)src.Ib + (start + ic) * 2 > mdl.Length) continue;

            var count = new int[mv.Positions.Length];
            for (uint s = 0; s < ic; s++)
            {
                int idx = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + s) * 2);
                if (idx < count.Length) count[idx]++;
            }
            result[mv.Mesh] = count;
        }
        return result;
    }

    /// <summary>
    /// Parts that stand so far off the scalp that pressing them is pointless — ponytails, side tails, the
    /// long fall at the back — which the guide tags <c>atr_kam</c> so the game drops them under a hat.
    /// <para/>
    /// Judged on the part's MEDIAN standoff rather than its worst vertex: every hairstyle has a few strands
    /// flying, and one of them should not condemn the whole scalp. And only whole parts are offered, because
    /// hiding half an island leaves a cut edge showing.
    /// </summary>
    private static List<ModelPart> HideList(
        byte[] mdl, ModelParts parts, Vector3 centre, float[] floor,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> moved, float hatLine)
    {
        var src = SecondSkinWriter.Parse(mdl);
        var meshes = ReadLod0Meshes(mdl).ToDictionary(m => m.Mesh, m => m.Positions);
        var hide = new List<ModelPart>();

        foreach (var part in parts.Parts)
        {
            // WHOLE SUBMESHES ONLY. An attribute is carried by a submesh record, so hiding an island means
            // first cutting it out into its own submesh — and none of that is needed here, because this is
            // how the hairstyles that ship hat compatibility are already built: every hand-made one
            // inspected tags whole submeshes, the author having separated the ponytail in Blender before
            // export exactly as the guide says to. Offering islands would add a splitting step to serve a
            // case real hair does not present.
            if (part.Island >= 0) continue;
            if (!meshes.TryGetValue(part.Mesh, out var pos)) continue;
            moved.TryGetValue(part.Mesh, out var disp);

            var stand = new List<float>();
            foreach (var v in VerticesOf(mdl, src, part))
            {
                if (v >= pos.Length) continue;
                // WHERE THE PRESS LEAVES IT, not where the author put it.
                var p = disp != null && disp.TryGetValue(v, out var np) ? np : pos[v];

                // ONLY WHERE THE HAT IS. Hair below the brim is not something a hat collides with — it
                // hangs out underneath one, which is what hair does. Judging a part on all of its
                // geometry condemned every long hairstyle: the press deliberately fades out below the
                // brim, so that hair keeps its full standoff, and the test then read a fringe and a
                // waist-length fall as equally impossible. In game that offered to hide everything and
                // made the wearer bald.
                if (p.Y < hatLine) continue;

                var d = p - centre;
                float len = d.Length();
                if (len < 1e-5f) continue;
                stand.Add(len - floor[BinOf(d / len)]);
            }
            // Nothing of this part is up where a hat is, so a hat cannot be clipping through it.
            if (stand.Count == 0) continue;
            stand.Sort();
            if (stand[stand.Count / 2] > HideThreshold) hide.Add(part);
        }
        return hide;
    }

    /// <summary>
    /// The MESH-RELATIVE vertices a part draws.
    /// <para/>
    /// Recovered through the part's <see cref="ModelPart.Ordinals"/> and its submesh's own index range,
    /// never through <see cref="ModelPart.Triangles"/> — those are rebased across meshes for drawing and
    /// skip meshes the reader could not decode, so using them here would edit a different mesh's vertices
    /// and the model would still load.
    /// </summary>
    internal static IEnumerable<int> VerticesOf(byte[] mdl, SecondSkinWriter.Source src, ModelPart part)
    {
        int mo = src.MeshStart + part.Mesh * 36;
        if (mo + 36 > mdl.Length) yield break;
        ushort subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);
        if (part.Submesh < 0 || part.Submesh >= subCount) yield break;

        int ss = src.SubmeshStart + (subIdx + part.Submesh) * 16;
        uint idxOffset = BitConverter.ToUInt32(mdl, ss), idxCount = BitConverter.ToUInt32(mdl, ss + 4);

        // Ordinals index the SUBMESH's triangles; a whole-submesh part carries none and means all of them.
        var ordinals = part.Island < 0 && part.Ordinals.Length == 0
            ? Enumerable.Range(0, (int)(idxCount / 3))
            : part.Ordinals;
        foreach (var t in ordinals)
        {
            if (t < 0 || (t + 1) * 3 > idxCount) continue;
            for (int k = 0; k < 3; k++)
            {
                int at = src.Ib + (int)(idxOffset + t * 3 + k) * 2;
                if (at + 2 <= mdl.Length) yield return BitConverter.ToUInt16(mdl, at);
            }
        }
    }
}
