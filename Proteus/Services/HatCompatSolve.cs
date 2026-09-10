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
    /// Which generation of this solve produced a patch. BUMP IT whenever a constant below changes, or when
    /// the press or the classifier starts producing different geometry.
    /// <para/>
    /// A patch is a one-time write into someone's mod, so nothing ever looks at a hairstyle that already
    /// carries one — which meant that every improvement to the numbers here reached only hairstyles nobody
    /// had worn yet. Every already-fitted hairstyle kept whatever the build that fitted it decided, and the
    /// only way back was to undo and re-fit each one by hand. Stamping the record makes the watcher able to
    /// tell a current patch from a stale one, so it can redo the stale one by itself.
    /// </summary>
    public const int Version = 13;

    /// <summary>
    /// The HAT LINE: how far above the head's centre a hat actually sits on the head, in model units.
    /// <para/>
    /// This is the only boundary that matters, and everything else here follows from it. Above it a hat
    /// covers the hair completely, so the hair can be crushed as hard as you like — flat to the skull, or
    /// inside it — and nothing shows. Below it the hat is simply not there, and moving a single vertex is a
    /// visible defect on a part of the hairstyle the wearer chose.
    /// <para/>
    /// Measured as VISIBILITY, because that is what the line now decides. Geometry above it is deleted
    /// outright, so the only question that matters is whether a viewer could have seen it: walk the scalp,
    /// cast outward from each point along the directions a viewer occupies — level, and a little above and
    /// below — and a point is hidden only when the hat blocks every one of them. The highest point that is
    /// NOT hidden is the lowest a cut may safely go.
    /// <para/>
    /// The three real hats answer 17 mm (Wrangler's), 21 mm (Battlemage's) and 22 mm (Coronal Straw) above
    /// the head's centre, and the line takes the least generous. The other four reference pieces are
    /// glasses, a corsage and a veil, which cover almost nothing and would drag the line to the top of the
    /// skull; they are not hats and do not get a vote.
    /// <para/>
    /// Three earlier attempts were wrong in instructive ways, and all three measured the wrong thing rather
    /// than measuring it badly. Taking the brim's lowest point licensed crushing hair a hand's width below
    /// anything a hat touches. Taking the height where the hat comes closest to the head's vertical AXIS
    /// found the pointed tip of a witch's hat and called it the band. Asking what a hat covers from directly
    /// OVERHEAD answered "all of it" — a brim shades the whole cranium from above, so by that test a cut
    /// could go anywhere, while the cut edge was plainly visible from the side. A cut shows because someone
    /// looks at it, so the test has to be a line of sight.
    /// </summary>
    public const float HatLine = 0.022f;

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
    /// How far below the hat line a strand may dip and still count as wholly covered by a hat.
    /// <para/>
    /// Reported, not enforced. It used to gate the press: a strand reaching further than this below the line
    /// was refused entirely, because the press moves vertices rather than strands and a half-pressed strand
    /// grows triangles bridging from inside the skull out to wherever the hair still is — the flat ribbons
    /// seen slicing through a hat. That was true, but refusing the whole strand was far too blunt a way to
    /// avoid it, and left the tops of ordinary locks standing outside the hat.
    /// <para/>
    /// The bridge is now prevented exactly where it forms, by freezing the corners of any triangle that
    /// reaches below the line — see FrozenVertices. That is the same guarantee at triangle granularity, so
    /// this survives only as a measurement the diagnostics report.
    /// </summary>
    public const float PressDip = 0.005f;


    /// <summary>
    /// How far below the hat line a strand must hang before it counts as a TAIL — something a hat cannot
    /// cover, offered for hiding.
    /// <para/>
    /// A separate question from <see cref="PressDip"/>, and keeping them separate matters. Pressing asks
    /// "is this certainly hidden?", so it must be strict and refuses almost everything. Hiding asks "is
    /// this a ponytail?", and taking it as the complement of the first made a hide candidate of every lock
    /// that dips below the hat — which is nearly all of a hairstyle, and ticking the box went bald.
    /// <para/>
    /// A floor, not the working test — see <see cref="TailReach"/>, which does the separating. This only
    /// keeps something sitting ON the head from being called a tail because it happens to stand out.
    /// </summary>
    public const float TailDrop = 0.05f;

    /// <summary>
    /// How far off the scalp a strand must reach to count as a tail, in model units.
    /// <para/>
    /// The test that actually separates the two, and reach does it where depth could not. Sweeping the cut
    /// over the hairstyle measured, everything between 100 mm and 250 mm accounts for 2.4% of the model —
    /// a plateau, meaning almost nothing lives there — while above 250 mm the share falls away steadily as
    /// the cut rises, which is the signature of cutting through a population rather than between two.
    /// So the tail begins at 250 mm and spreads upward from it, and any threshold higher than that leaves
    /// part of the tail behind.
    /// <para/>
    /// That is exactly how this went wrong three times. At 500 mm only a third of a ponytail was tagged and
    /// the rest stayed on show; at 400 mm, half of it; at 250 mm, a handful of stray strands. Since the
    /// whole 100–250 band is worth 2.4% of the model, taking the bottom of the plateau rather than the top
    /// costs almost nothing and catches the strays — the cheapest end of the range to be wrong at.
    /// </summary>
    public const float TailReach = 0.10f;


    // No SCALP CLEARANCE here, and it was tried. A hat never comes within 12 mm of the scalp on any of the
    // seven measured, so hair already nearer than that cannot clip and skipping it looks like free budget —
    // and the budget is what this format is short of. Swept across 25 installed hairstyles it was a bad
    // trade: it cleared the budget on four that were drawing correctly anyway, did not change the leak on
    // any of the three that were not, by so much as one vertex, and put 1 to 5 vertices through the hat on
    // twelve hairstyles that had been at zero. Left here as a signpost, because it is an appealing idea.

    /// <summary>
    /// How far BELOW the hat line the press keeps working before it fades to nothing, in model units.
    /// <para/>
    /// Everything above the hat line is pressed at full strength; from the line down, the press fades
    /// smoothly to zero over this distance, so hair returns to exactly where its author put it. That fade
    /// is the whole point and not a safety margin — it is what lets the press reach hair AT the brim at all.
    /// <para/>
    /// Before it existed the press stopped dead at the hat line, and hair below the line was never touched
    /// however far it stood out. That is where the last of the clipping lived: measured on one hairstyle,
    /// nothing at all stood proud of the Wrangler's hat above the line, while 128 drawn corners in the 80 mm
    /// below it were standing up through the brim. No amount of tuning the line could reach them, because
    /// moving the line down only moves the same hard edge somewhere else.
    /// <para/>
    /// It also retires the freeze rule. That rule pinned any triangle crossing the hat line, because a
    /// pressed corner next to a fixed one drags a ribbon out of the triangle between them. A fade removes
    /// the fixed corner: neighbours a millimetre apart now move by nearly the same amount, everywhere, so
    /// there is no discontinuity for a ribbon to form across. Continuity by construction rather than by
    /// prohibition — which is also why it can afford to reach so much further than the freeze ever could.
    /// <para/>
    /// Sized against <see cref="HatLine"/> rather than chosen on its own, because the two only mean anything
    /// together: the fade runs from the line down, so raising the line by 29 mm lifted the bottom of the
    /// fade by 29 mm too and left a band of hair that had been pressed suddenly untouched. It reaches to
    /// about 68 mm below the head's centre, which is roughly where a brim stops being able to hide anything.
    /// <para/>
    /// It is not free in either direction. Every millimetre flattens hair a hat does not cover, faintly but
    /// visibly, and spends shape values the format counts in a u16 — so the fade is the reason the press
    /// budget matters at all, now that everything above the line is dropped rather than pressed.
    /// </summary>
    public const float FanBelow = 0.09f;

    /// <summary>
    /// How many edge-rings the press takes to reach full strength, counting in from the last vertex it is
    /// not allowed to move.
    /// <para/>
    /// Without this the press is all-or-nothing across one edge: a vertex driven inside the skull sits next
    /// to a frozen one still out at the hair's surface, and the surface turns a near right angle between
    /// them. That corner is visible under a brim even though both its ends are legal, and it is what makes a
    /// correctly-fitted hairstyle look broken.
    /// <para/>
    /// Measured in RINGS, along the mesh's own edges — not in millimetres of height. An earlier attempt
    /// faded by height, over a band above the hat line, and it read as crunchy for a reason worth keeping:
    /// height cuts across every strand at once, at an angle that has nothing to do with how any of them run,
    /// so a lock passing through the band is squeezed in its middle. Edge distance follows the hair, so each
    /// strand eases off along its OWN length, which is the direction it was modelled in.
    /// <para/>
    /// Four rings is a couple of centimetres on a typical hair mesh — long enough to read as a bend rather
    /// than a crease, short enough that the full press still reaches everything deep under the hat.
    /// </summary>
    public const int PressRamp = 4;

    /// <summary>
    /// The most shape values one model may carry: <c>ShapeValueCount</c> in the model header is a u16.
    /// <para/>
    /// A value is spent per INDEX SLOT — a vertex drawn by six triangles costs six — which makes the true
    /// cost of a candidate hard to eyeball and easy to under-estimate. On a dense hairstyle this ceiling
    /// genuinely binds: one measured here wanted to move 8161 vertices and could afford 5034, so nearly two
    /// in five were left standing.
    /// <para/>
    /// Close to the format's own 65535 on purpose. This used to sit at 60000 to leave room for a hairstyle
    /// that already carries a shape of its own — but the solve already subtracts exactly that, reading the
    /// model's own count out of the header, so the margin was being taken twice and cost a tenth of the
    /// budget for nothing. What is left is slack against the arithmetic, not against the model.
    /// </summary>
    public const int MaxShapeValues = 65000;

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
    /// <param name="Dropped">Vertices the press wanted to move and could not afford — see
    /// <see cref="MaxShapeValues"/>. Non-zero means the shape is a partial one and some hair is left standing
    /// wherever the budget ran out, which is otherwise indistinguishable from the press deciding it was
    /// already fine.</param>
    public sealed record Result(
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> Moved,
        IReadOnlyList<ModelPart> Hide,
        Vector3 Centre,
        float Radius,
        float MedianPress,
        float MaxPressed,
        int Considered,
        int Dropped = 0)
    {
        /// <summary>
        /// The pieces above the hat line, to be dropped outright.
        /// <para/>
        /// Separate from <see cref="Hide"/>, and it must be: hiding ponytails is a setting the wearer may
        /// switch off, while cutting at the hat line is how the fit works at all. Carried on the same list,
        /// unticking the box would quietly stop the hairstyle fitting.
        /// </summary>
        public IReadOnlyList<ModelPart> Cut { get; init; } = [];

        /// <summary>Nothing to do — for a hairstyle whose author already made it hat-compatible.</summary>
        public static Result None { get; } = new(
            new Dictionary<int, IReadOnlyDictionary<int, Vector3>>(), [], Vector3.Zero, 0, 0, 0, 0);
    }

    /// <summary>One LOD0 mesh's own vertices, indexed the way the model's index buffer indexes them.</summary>
    internal sealed record MeshVerts(int Mesh, Vector3[] Positions);

    /// <summary>
    /// The head frame and scalp floor the press would use, for diagnostics to interrogate.
    /// <para/>
    /// Exposed rather than reconstructed, for the same reason <see cref="Strands"/> is: a diagnostic that
    /// builds its own copy of a rule ends up reporting on the copy. Asking why the press skipped a vertex
    /// means asking against the very floor it consulted, not one built the same way twice.
    /// </summary>
    internal static (Vector3 Centre, float Radius, float[] Floor)? FrameAndFloor(byte[] mdl, byte[]? head)
    {
        var meshes = ReadLod0Meshes(mdl);
        if (meshes.Count == 0) return null;
        var frame = head != null ? HeadFrameFrom(head) : null;
        var (centre, radius) = frame ?? HeadFrame(meshes);
        var floor = frame != null
            ? HeadFloor(ReadLod0Meshes(head!), centre, radius, centre.Y + HatLine)
            : ScalpFloor(meshes, centre, radius);
        return (centre, radius, floor);
    }

    /// <summary>Which directional bin a direction falls in — for diagnostics reading <see cref="FrameAndFloor"/>.</summary>
    internal static int BinFor(Vector3 dir) => BinOf(dir);

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
    /// <param name="fan">How far below the hat line the press fades out over. A test seam, so the distance
    /// can be swept against real hats rather than argued about. See <see cref="FanBelow"/>.</param>
    public static Result Solve(byte[] mdl, ModelParts parts, byte[]? head = null,
                               float depth = ScalpFraction, float fan = FanBelow)
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
        var parsed = SecondSkinWriter.Parse(mdl);
        var valence = Valence(mdl, parsed, meshes);
        // Only hair a hat actually covers may be pressed; everything else is left exactly as its author
        // made it, and offered for hiding instead.
        var strands = Strands(mdl, parsed, parts, meshes, hatLine, centre, floor);

        // ONLY a tail is hidden. Nothing else gives up any geometry at all.
        //
        // Cutting the above-the-line triangles out of every other strand did fit under a hat, and it looked
        // wrong doing it: hair ended along a hard horizontal line under the brim, with the cut edge in plain
        // view. That is not a matter of picking a better line. A deletion leaves a visible boundary wherever
        // the hat's real silhouette differs from the measured hat line, the two differ somewhere on every
        // hat, and a brim is exactly the place a player looks up under. It is why the guide reaches for a
        // shape key and not the delete key: a pressed vertex that guessed wrong is hair in a slightly odd
        // place, while a deleted one is a hole.
        var tails = strands.Where(s => s.Hideable).Select(s => s.Part).ToList();

        // Everything the hat covers outright goes, rather than being pressed. See CutAtHatLine.
        var (drop, gone) = CutAtHatLine(mdl, parsed, meshes, hatLine);

        // Tails are NOT excluded from the press any more, and that reversal is worth explaining because it
        // undoes an earlier fix. Excluding them was right when the press was a hard cut-off at the hat line:
        // it moved a ponytail's root and left its length where it was, which folded the tail flat against
        // the head. The fade removes exactly that failure — a tail is now pressed hard where the hat covers
        // it and released smoothly along its own length — so the exclusion has stopped protecting anything
        // and only leaves ponytails standing through the brim, now that hiding them is gone too.

        // Where the press stops entirely. Everything between here and the hat line is faded, not cut off.
        float fanBottom = hatLine - fan;

        // Every vertex poking out through the ceiling, with what it costs and how far out it is.
        var candidates = new List<Candidate>();
        foreach (var mv in meshes)
        {
            valence.TryGetValue(mv.Mesh, out var cost);
            for (int v = 0; v < mv.Positions.Length; v++)
            {
                var p = mv.Positions[v];
                if (p.Y < fanBottom) continue;                      // past the fade: the author's hair, untouched
                if (gone.Contains(VertexKey(mv.Mesh, v))) continue;      // cut away; a shape value would move nothing

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

                // ONTO the scalp below the line, INTO the skull above it, and the difference is whether the
                // hat is there to hide the result. Above the line nothing shows however hard it is crushed,
                // and overshooting is free insurance against a stray sliver. Below the line the hair is in
                // plain view: driving it to three quarters of the scalp radius buries it in the head, which
                // is why hair was reading as squashed against the face well under the brim. The furthest it
                // may go there is the scalp itself.
                float target = p.Y >= hatLine ? MathF.Max(0.005f, scalp * depth) : scalp;
                if (len <= target) continue;

                int slots = cost != null && v < cost.Length ? cost[v] : 0;
                if (slots == 0) continue;                           // drawn by nothing; a spare would do nothing

                // The fade. Full strength at the hat line and above, tapering to nothing at the bottom of
                // the fan, so hair rejoins the author's silhouette without a step anywhere along the way.
                float height = p.Y >= hatLine || fan <= 0f
                    ? 1f
                    : Math.Clamp((p.Y - fanBottom) / fan, 0f, 1f);
                if (height <= 0f) continue;

                candidates.Add(new Candidate(
                    mv.Mesh, v, p, Vector3.Lerp(p, centre + d * (target / len), height),
                    len - target, (len - target) * height, slots));
            }
        }

        // Ease the press in from its own edge, so the surface bends into the skull instead of breaking into
        // it. Everything the press is not touching — frozen, tailed, or below the line — is the boundary,
        // and depth is the number of edges in from it.
        var free = new HashSet<long>();
        foreach (var c in candidates) free.Add(VertexKey(c.Mesh, c.Vertex));
        var ringsFromEdge = PressDepth(mdl, parsed, meshes, free);
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            int rings = ringsFromEdge.TryGetValue(VertexKey(c.Mesh, c.Vertex), out var d) ? d : PressRamp;
            float w = MathF.Min(rings / (float)PressRamp, 1f);
            // Need is deliberately left alone: it is the priority, and the ramp must not reorder the queue.
            candidates[i] = c with { To = Vector3.Lerp(c.From, c.To, w), Press = c.Press * w };
        }

        // Under the budget, everything goes. Over it, the ones poking out FURTHEST go first — so what gets
        // left behind is the hair already closest to fitting, and the shape degrades by getting gentler
        // rather than by developing a hard edge somewhere arbitrary.
        // Whatever the model already spends on its own shapes comes off the top — the count in the header is
        // for the whole file, not per shape.
        int already = BitConverter.ToUInt16(mdl, SecondSkinWriter.Parse(mdl).Mh + 20);
        int budget = Math.Max(0, MaxShapeValues - already), spent = 0;
        if (candidates.Sum(c => c.Cost) > budget)
            candidates.Sort((a, b) => b.Need.CompareTo(a.Need));

        var presses = new List<float>();
        int dropped = 0;
        foreach (var c in candidates)
        {
            if (spent + c.Cost > budget) { dropped++; continue; }
            spent += c.Cost;
            if (!moved.TryGetValue(c.Mesh, out var here))
                moved[c.Mesh] = here = new Dictionary<int, Vector3>();
            ((Dictionary<int, Vector3>)here)[c.Vertex] = c.To;
            presses.Add(c.Press);
        }

        presses.Sort();
        // The SAME strands the press refused to touch. That is the whole coherence of the two settings:
        // hair that hangs off the head is either hidden under a hat or left exactly as its author made it,
        // and never something in between. The earlier list was computed separately, from how far a whole
        // submesh still stood proud afterwards — which on a hairstyle keeping its scalp and its tails in
        // one submesh nominated that submesh, and hiding it made the wearer bald.
        return new Result(
            moved,
            tails,
            centre, radius,
            presses.Count > 0 ? presses[presses.Count / 2] : 0f,
            presses.Count > 0 ? presses[^1] : 0f,
            presses.Count,
            dropped) { Cut = drop };
    }

    /// <summary>
    /// Cut the hairstyle at the hat line: what to drop, and which vertices go with it.
    /// <para/>
    /// Above the line a hat covers the hair completely, so the hair there does not need to be moved — it
    /// needs to be gone. Dropping it costs NOTHING, because a submesh tagged <c>atr_kam</c> simply is not
    /// drawn, where pressing the same geometry costs a shape value per index slot naming it. That is the
    /// difference between fitting a dense hairstyle and not: one measured here needed about 176000 values
    /// against a format ceiling of 65535, and left whatever it could not afford standing through the hat.
    /// <para/>
    /// The cut follows TRIANGLES, not a true geometric slice — a triangle goes only if all three of its
    /// corners clear the line. What is left is a ragged fringe up to one triangle tall, and the press deals
    /// with it: those corners are above the line, so they are driven onto the scalp where the hat hides
    /// them. A real slice would put the edge exactly on the line instead, at the cost of inserting new
    /// vertices and triangles into the model; under an opaque hat the two look the same.
    /// </summary>
    /// <returns>The submesh pieces to tag, and the vertices that no surviving triangle draws — those need no
    /// shape value, and spending one on them is what the budget cannot afford.</returns>
    private static (List<ModelPart> Drop, HashSet<long> Gone) CutAtHatLine(
        byte[] mdl, SecondSkinWriter.Source src, IReadOnlyList<MeshVerts> meshes, float hatLine)
    {
        var drop = new List<ModelPart>();
        var gone = new HashSet<long>();

        foreach (var mv in meshes)
        {
            int mo = src.MeshStart + mv.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;
            ushort subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);
            var pos = mv.Positions;
            var uses = new int[pos.Length];
            var lost = new int[pos.Length];

            for (int s = 0; s < subCount; s++)
            {
                int ss = src.SubmeshStart + (subIdx + s) * 16;
                if (ss + 16 > mdl.Length) break;
                uint io = BitConverter.ToUInt32(mdl, ss), ic = BitConverter.ToUInt32(mdl, ss + 4);
                if ((long)src.Ib + (io + ic) * 2 > mdl.Length) break;

                var above = new List<int>();
                for (uint t = 0; t + 3 <= ic; t += 3)
                {
                    int a = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t) * 2);
                    int b = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t + 1) * 2);
                    int c = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t + 2) * 2);
                    if (a >= pos.Length || b >= pos.Length || c >= pos.Length) continue;

                    uses[a]++; uses[b]++; uses[c]++;
                    if (pos[a].Y < hatLine || pos[b].Y < hatLine || pos[c].Y < hatLine) continue;
                    lost[a]++; lost[b]++; lost[c]++;
                    above.Add((int)(t / 3));
                }

                // A submesh entirely above the line is claimed whole; Island < 0 tells IsolateParts there is
                // nothing to cut, which saves it splitting a submesh into itself.
                if (above.Count == 0) continue;
                bool whole = above.Count == ic / 3;
                drop.Add(new ModelPart
                {
                    Mesh = mv.Mesh,
                    Submesh = s,
                    Island = whole ? -1 : 0,
                    Label = $"{mv.Mesh}.{s}",
                    Material = "",
                    Triangles = [],
                    Ordinals = whole ? [] : above.ToArray(),
                    AttributeMask = 0,
                    Min = Vector3.Zero,
                    Max = Vector3.Zero,
                    Toggleable = false,
                });
            }

            for (int v = 0; v < pos.Length; v++)
                if (uses[v] > 0 && uses[v] == lost[v]) gone.Add(VertexKey(mv.Mesh, v));
        }
        return (drop, gone);
    }

    /// <summary>One vertex the press wants to move, and how far.</summary>
    /// <param name="From">Where its author put it — kept so the ramp can interpolate rather than recompute.</param>
    /// <param name="Need">How far out of the scalp it started, BEFORE the ramp. This and not
    /// <paramref name="Press"/> is what orders the budget: need is how badly the vertex has to move, press is
    /// merely how far it is allowed to. Ordering by the latter spends the budget backwards — a vertex
    /// standing right out of the hat but sitting near the ramp's edge has a small press, sorts last, and is
    /// dropped in favour of hair that was nearly fine already.</param>
    /// <param name="Cost">Shape values it would spend: one per index slot naming it.</param>
    private readonly record struct Candidate(
        int Mesh, int Vertex, Vector3 From, Vector3 To, float Need, float Press, int Cost);

    /// <summary>
    /// How far each pressed vertex sits, in mesh edges, from the nearest vertex the press may not move.
    /// <para/>
    /// A multi-source breadth-first walk out from everything the press is leaving alone, over the mesh's own
    /// edges. That is what makes the falloff follow the hair: distance along the surface is distance along a
    /// strand, so a lock eases off from the point it stops being safe to move and does it in the direction
    /// it was modelled in. See <see cref="PressRamp"/> for why the obvious alternative — fading by height —
    /// does not work.
    /// <para/>
    /// A vertex whose whole connected piece is free never reaches the walk and is absent from the result;
    /// the caller reads that as full press, which is right — it is a strand wholly inside the hat, with no
    /// boundary to ease towards.
    /// </summary>
    private static Dictionary<long, int> PressDepth(
        byte[] mdl, SecondSkinWriter.Source src, IReadOnlyList<MeshVerts> meshes, HashSet<long> free)
    {
        var found = new Dictionary<long, int>();
        foreach (var mv in meshes)
        {
            int mo = src.MeshStart + mv.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;
            uint ic = BitConverter.ToUInt32(mdl, mo + 4), start = BitConverter.ToUInt32(mdl, mo + 16);
            if ((long)src.Ib + (start + ic) * 2 > mdl.Length) continue;

            int n = mv.Positions.Length;
            var adj = new List<int>[n];
            void Link(int a, int b)
            {
                if (a >= n || b >= n || a == b) return;
                (adj[a] ??= []).Add(b);
                (adj[b] ??= []).Add(a);
            }
            for (uint t = 0; t + 3 <= ic; t += 3)
            {
                int a = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t) * 2);
                int b = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t + 1) * 2);
                int c = BitConverter.ToUInt16(mdl, src.Ib + (int)(start + t + 2) * 2);
                Link(a, b);
                Link(b, c);
                Link(c, a);
            }

            var dist = new int[n];
            var q = new Queue<int>();
            for (int v = 0; v < n; v++)
            {
                bool moving = free.Contains(VertexKey(mv.Mesh, v));
                dist[v] = moving ? int.MaxValue : 0;
                if (!moving) q.Enqueue(v);
            }
            while (q.Count > 0)
            {
                int v = q.Dequeue();
                if (adj[v] == null) continue;
                foreach (var w in adj[v])
                    if (dist[w] == int.MaxValue) { dist[w] = dist[v] + 1; q.Enqueue(w); }
            }
            for (int v = 0; v < n; v++)
                if (dist[v] != int.MaxValue && dist[v] > 0) found[VertexKey(mv.Mesh, v)] = dist[v];
        }
        return found;
    }

    /// <summary>One vertex of one mesh, as a single value for a set.</summary>
    private static long VertexKey(int mesh, int vertex) => ((long)mesh << 32) | (uint)vertex;

    /// <summary>
    /// Every vertex of every connected strand that hangs more than <see cref="TailDrop"/> below the hat
    /// line — a ponytail, a side tail, a long fall — which the press must not touch at all.
    /// <para/>
    /// ALL of the strand, including the part above the hat line. That is the entire point: the root is
    /// above the line, and moving only the root is what wrecks the tail.
    /// <para/>
    /// Judged per ISLAND rather than per submesh, because the two are not the same thing here. The
    /// hairstyle that showed this defect keeps its scalp cap and all six of its ponytail strands in one
    /// submesh, so a submesh-level test would either spare the tails or condemn the scalp with them.
    /// </summary>
    /// <summary>
    /// One connected strand, measured the way the press judges it.
    /// </summary>
    /// <param name="Below">Vertices below the hat line — geometry a hat does not cover.</param>
    /// <param name="Reach">The furthest any of its vertices stands off the SCALP in its own direction.
    /// Directional, not against an average radius: a head is nothing like a sphere, and measuring against
    /// one calls the crown a tail.</param>
    /// <param name="Covered">Every measured hat hides all of it. Reported only — what the press may touch is
    /// decided per triangle now, not per strand.</param>
    /// <param name="Tail">It hangs far enough off the head that no hat could cover it — a ponytail, a side
    /// tail. Not the opposite of <paramref name="Covered"/>: most of a hairstyle is neither.</param>
    /// <param name="Hideable">A tail that also REACHES the hat, so hiding it is the only way to deal with
    /// it. Deliberately narrower than <paramref name="Tail"/>, and the two must not be confused: a tail is
    /// never pressed, because pressing one root drags a ponytail flat against the head, but a tail hanging
    /// entirely below the hat line meets no hat at all and hiding it destroys hair a hat was never going to
    /// touch. One measured hairstyle had 125 of its 129 tail strands wholly below the line, the highest of
    /// them 60 mm clear of it — nine tenths of its ponytail, deleted for nothing.</param>
    internal readonly record struct Strand(
        ModelPart Part, int Verts, int Below, float Drop, float Reach, bool Covered, bool Tail, bool Hideable);

    /// <summary>
    /// Every strand of a hairstyle, and whether a hat covers it — which is the only question the press
    /// needs answered.
    /// <para/>
    /// Shared by the solve and by the diagnostics on purpose. Measuring "how far off the scalp" two
    /// slightly different ways in two places is how a gate ends up tuned against numbers it never sees;
    /// that happened twice here before this existed.
    /// </summary>
    internal static List<Strand> Strands(byte[] mdl, ModelParts parts, byte[]? head)
    {
        var meshes = ReadLod0Meshes(mdl);
        if (meshes.Count == 0) return [];
        var frame = head != null ? HeadFrameFrom(head) : null;
        var (centre, radius) = frame ?? HeadFrame(meshes);
        var floor = frame != null
            ? HeadFloor(ReadLod0Meshes(head!), centre, radius, centre.Y + HatLine)
            : ScalpFloor(meshes, centre, radius);
        return Strands(mdl, SecondSkinWriter.Parse(mdl), parts, meshes, centre.Y + HatLine, centre, floor);
    }

    private static List<Strand> Strands(
        byte[] mdl, SecondSkinWriter.Source src, ModelParts parts,
        IReadOnlyList<MeshVerts> meshes, float hatLine, Vector3 centre, float[] floor)
    {
        var found = new List<Strand>();
        var verts = meshes.ToDictionary(m => m.Mesh, m => m.Positions);

        // Islands where a submesh was split into them, the whole submesh where it was not — so every
        // triangle is judged exactly once, at the finest granularity available for it.
        foreach (var part in parts.Parts)
        {
            if (part.Island < 0 && parts.Parts.Any(
                    q => q.Mesh == part.Mesh && q.Submesh == part.Submesh && q.Island >= 0)) continue;
            if (!verts.TryGetValue(part.Mesh, out var pos)) continue;

            float lowest = float.MaxValue, highest = float.MinValue, reach = 0f;
            int n = 0, below = 0;
            foreach (var v in VerticesOf(mdl, src, part))
            {
                if (v >= pos.Length) continue;
                n++;
                var p = pos[v];
                if (p.Y < lowest) lowest = p.Y;
                if (p.Y > highest) highest = p.Y;
                if (p.Y < hatLine) below++;

                var d = p - centre;
                float len = d.Length();
                if (len > 1e-5f) reach = MathF.Max(reach, len - floor[BinOf(d / len)]);
            }
            if (n == 0) continue;

            // COVERED means every hat measured hides the whole strand, so pressing it cannot show anywhere.
            // Reach does not enter into it: a strand wholly above the line is driven INSIDE the skull, so
            // however far out it started it ends up hidden. What matters is only whether the line cuts it.
            float drop = hatLine - lowest;
            bool covered = drop <= PressDip;

            // A TAIL is a much narrower thing, and the two are not complements — the great bulk of a
            // hairstyle is neither pressed nor hidden, but left exactly as its author made it.
            bool tail = drop > TailDrop && reach > TailReach;

            // And hiding is narrower still: only a tail that rises far enough to meet a hat. The boundary is
            // the bottom of the press's own fade — the height below which this decides a hat has no
            // influence at all — so the two answers cannot disagree about where the hat's reach ends.
            //
            // Not the hat line itself, which is 40 mm higher and much too strict. Measured across three
            // hairstyles, tails topping out 2 and 8 mm below the line are at the brim and genuinely clip
            // through it, while another's top out 60 mm below and hang clear of everything. Cutting at the
            // line put the first two on the wrong side and left 168 corners standing up through a brim.
            bool hideable = tail && highest >= hatLine - FanBelow;
            found.Add(new Strand(part, n, below, drop, reach, covered, tail, hideable));
        }
        return found;
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
