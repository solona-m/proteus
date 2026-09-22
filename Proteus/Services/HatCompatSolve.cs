using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// Works out how a hairstyle has to change to fit under a hat: which parts to cut and which vertices to press
/// against the skull. Pure geometry; results are MESH-RELATIVE vertex indices, per mesh, as
/// <see cref="ModelAttributeWriter.AddShape"/> wants them.
/// </summary>
public static partial class HatCompatSolve
{
    /// <summary>
    /// Which generation of this solve produced a patch. BUMP IT whenever a constant below changes or the output
    /// geometry changes: the watcher redoes patches stamped with an older version.
    /// </summary>
    public const int Version = 32;

    /// <summary>How far above the head's centre a hat sits on the head, in model units; the lowest a cut may
    /// safely go, measured as visibility from a viewer's line of sight.</summary>
    public const float HatLine = 0.022f;

    /// <summary>
    /// Where covered hair is driven to, as a FRACTION of the scalp's own radius in that direction: inside the
    /// head, since hair left level with the skull shows through the hat.
    /// </summary>
    public const float ScalpFraction = 0.75f;

    /// <summary>
    /// How far below the hat line a strand may dip and still count as wholly covered by a hat. Reported by
    /// diagnostics only, not enforced.
    /// </summary>
    public const float PressDip = 0.005f;


    /// <summary>
    /// How far below the hat line a strand must hang before it counts as a TAIL, offered for hiding. A floor
    /// only; <see cref="TailReach"/> does the separating.
    /// </summary>
    public const float TailDrop = 0.05f;

    /// <summary>How far off the scalp a strand must reach to count as a tail, in model units.</summary>
    public const float TailReach = 0.10f;


    /// <summary>
    /// How far beyond the scalp the cut still reaches, in model units: the thickness of the hat it assumes is
    /// around the hair. Errs small, since uncut hair is merely pressed while hair cut outside a hat is a hole.
    /// </summary>
    public const float CutReach = 0.030f;

    /// <summary>
    /// How far BELOW the hat line the press fades to nothing, in model units. The fade keeps neighbouring
    /// vertices moving by nearly the same amount, so no ribbon forms across a hard edge.
    /// </summary>
    public const float FanBelow = 0.09f;

    /// <summary>
    /// How many edge-rings the press takes to reach full strength, counting in from the last vertex it may not
    /// move. Measured along the mesh's edges so each strand eases off along its own length.
    /// </summary>
    public const int PressRamp = 4;

    /// <summary>
    /// The most shape values one model may carry (<c>ShapeValueCount</c> is a u16). A value is spent per INDEX
    /// SLOT, not per vertex; the solve subtracts the model's existing count separately.
    /// </summary>
    public const int MaxShapeValues = 65000;

    /// <summary>Directional bins over the sphere: 32 around by 16 up, about 11 degrees each.</summary>
    private const int BinsU = 32, BinsV = 16;

    /// <summary>
    /// How far, in bins, the scalp floor looks for the hair's nearest approach to the skull. Too small and the
    /// press does nothing; too large and it flattens the head to a sphere.
    /// </summary>
    private const int MinRadiusBins = 2;

    /// <param name="Moved">Per LOD0 mesh, the mesh-relative vertices to move and where to.</param>
    /// <param name="Centre">The skull centre the press was computed about.</param>
    /// <param name="Radius">Median scalp radius about that centre.</param>
    /// <param name="Dropped">Vertices the press wanted to move and could not afford (see
    /// <see cref="MaxShapeValues"/>); non-zero means the shape is partial.</param>
    public sealed record Result(
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, Vector3>> Moved,
        Vector3 Centre,
        float Radius,
        float MedianPress,
        float MaxPressed,
        int Considered,
        int Dropped = 0)
    {
        /// <summary>The pieces above the hat line, to be dropped outright — see CutAtHatLine.</summary>
        public IReadOnlyList<ModelPart> Cut { get; init; } = [];

        /// <summary>
        /// Of <see cref="Dropped"/>, how many went because the FILE's shape-value count would have overflowed,
        /// and how many because one MESH ran out of room for spare vertices.
        /// </summary>
        public int DroppedForValues { get; init; }

        /// <inheritdoc cref="DroppedForValues"/>
        public int DroppedForSpares { get; init; }

        /// <summary>What the press would have spent if nothing had been refused, and what it could.</summary>
        public int WantedValues { get; init; }

        /// <inheritdoc cref="WantedValues"/>
        public int Budget { get; init; }

        /// <summary>Nothing to do — for a hairstyle whose author already made it hat-compatible.</summary>
        public static Result None { get; } = new(
            new Dictionary<int, IReadOnlyDictionary<int, Vector3>>(), Vector3.Zero, 0, 0, 0, 0);
    }

    /// <summary>One LOD0 mesh's own vertices, indexed the way the model's index buffer indexes them.</summary>
    internal sealed record MeshVerts(int Mesh, Vector3[] Positions);

    /// <summary>
    /// The head frame and scalp floor the press would use, exposed so diagnostics query the real floor rather
    /// than a copy.
    /// </summary>
    /// <returns><c>Floor</c> is the cranium-only radius used above the hat line; <c>BandFloor</c> is measured
    /// down to the bottom of the hat's band, for use below the line.</returns>
    internal static (Vector3 Centre, float Radius, float[] Floor, float[] BandFloor)? FrameAndFloor(
        byte[] mdl, byte[]? head)
    {
        var meshes = ReadLod0Meshes(mdl);
        if (meshes.Count == 0) return null;
        var frame = head != null ? HeadFrameFrom(head) : null;
        var (centre, radius) = frame ?? HeadFrame(meshes);
        var headMeshes = head != null ? ReadLod0Meshes(head) : [];
        var floor = frame != null
            ? HeadFloor(headMeshes, centre, radius, centre.Y + HatLine)
            : ScalpFloor(meshes, centre, radius);
        var bandFloor = frame != null
            ? HeadFloor(headMeshes, centre, radius, centre.Y + HatLine - HatBandDrop)
            : floor;
        return (centre, radius, floor, bandFloor);
    }

    /// <summary>
    /// How much of a hairstyle's own <c>atr_kam</c> mask, as a share of LOD0 triangles, may hide hair no hat
    /// covers before Proteus treats the mask as inherited and replaces it (see <see cref="MeasureScalpTagging"/>).
    /// Proteus's own patches measure 0% by construction, so it never takes over its own output.
    /// </summary>
    internal const float InheritedTagShare = 0.05f;

    /// <summary>
    /// Whether the whole-hairstyle press runs after the cut. RETIRED (off): it cannot fit the u16 shape-value
    /// budget; only the <see cref="RingHeight"/> press runs. Kept so diagnostics can still measure it.
    /// </summary>
    internal static readonly bool PressWhatTheCutLeaves = false;

    /// <summary>
    /// How tall the flattened ring above the hat's rim is, in metres. Hair in it is pressed flat rather than
    /// cut, so a hat whose rim rides higher than the reference hat shows flattened hair, not bare scalp.
    /// </summary>
    internal const float RingHeight = 0.015f;

    /// <summary>
    /// How far BELOW the rim the ring's press fades out, in metres, so a lock crossing the rim bends in over
    /// its own length instead of forming a shelf. Every millimetre costs shape values.
    /// </summary>
    internal const float RingFade = 0.03f;

    /// <summary>
    /// How far OUTSIDE the measured hat surface hair is still taken, in metres. RETIRED and not applied: any
    /// radius bound severs a strand crossing it, so the cut goes by direction alone.
    /// </summary>
    internal const float HatMargin = 0.04f;

    /// <summary>
    /// How far BELOW the hat line a hat still wraps the head, and how far off the scalp its inner surface sits:
    /// the hat as a band rather than a plane.
    /// </summary>
    internal const float HatBandDrop = 0.025f;

    /// <inheritdoc cref="HatBandDrop"/>
    internal const float HatClearance = 0.015f;

    /// <summary>Which directional bin a direction falls in — for diagnostics reading <see cref="FrameAndFloor"/>.</summary>
    internal static int BinFor(Vector3 dir) => BinOf(dir);

    /// <param name="Tagged">LOD0 triangles already carrying <c>atr_kam</c>.</param>
    /// <param name="Harmful">Of those, the ones no hat covers but which hug the head — see
    /// <see cref="MeasureScalpTagging"/>.</param>
    /// <param name="Triangles">LOD0 triangles in the model.</param>
    /// <param name="DeepestHarmful">How far the lowest harmful triangle sits BELOW the hat line, in metres,
    /// or 0 when there are none. Reporting only.</param>
    internal readonly record struct ScalpTagging(int Tagged, int Harmful, int Triangles, float DeepestHarmful)
    {
        /// <summary>Harmful triangles as a share of the model's LOD0 triangles.</summary>
        public float HarmfulShare => Triangles > 0 ? Harmful / (float)Triangles : 0f;
    }

    /// <summary>
    /// What a hairstyle's EXISTING <c>atr_kam</c> tagging would cost it under a hat, to detect masks inherited
    /// from vanilla hair. Harmful = tagged, below the rim, and inside the hat shell (hugging the head); tails
    /// outside the shell are legitimate. Uses <see cref="Outside"/>, shared with the cut.
    /// </summary>
    /// <returns>Null when the model or the head cannot be read, which is not a verdict of any kind.</returns>
    internal static ScalpTagging? MeasureScalpTagging(byte[] mdl, byte[]? head, string? raceCode)
    {
        if (head == null) return null;
        SecondSkinWriter.Source src;
        try { src = SecondSkinWriter.Parse(mdl); } catch { return null; }

        int bit = Array.IndexOf(src.AttrNames, HatCompatService.ScalpAttribute);
        var meshes = ReadLod0Meshes(mdl);
        if (meshes.Count == 0) return null;
        if (FrameAndFloor(mdl, head) is not { } ff) return null;
        var (centre, _, floor, _) = ff;
        float hatLine = centre.Y + HatLine;

        // No mask is a sound answer; still measured so the triangle count is reported.
        uint mask = bit >= 0 ? 1u << bit : 0u;

        int tagged = 0, harmful = 0, triangles = 0;
        float deepest = 0f;

        foreach (var mv in meshes)
        {
            int mo = src.MeshStart + mv.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;
            ushort subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);
            var pos = mv.Positions;

            for (int s = 0; s < subCount; s++)
            {
                int ss = src.SubmeshStart + (subIdx + s) * 16;
                if (ss + 16 > mdl.Length) break;
                uint io = BitConverter.ToUInt32(mdl, ss), ic = BitConverter.ToUInt32(mdl, ss + 4);
                if ((long)src.Ib + (io + ic) * 2 > mdl.Length) break;

                bool isTagged = mask != 0 && (BitConverter.ToUInt32(mdl, ss + 8) & mask) != 0;

                for (uint t = 0; t + 3 <= ic; t += 3)
                {
                    int a = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t) * 2);
                    int b = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t + 1) * 2);
                    int c = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t + 2) * 2);
                    if (a >= pos.Length || b >= pos.Length || c >= pos.Length) continue;
                    triangles++;
                    if (!isTagged) continue;
                    tagged++;

                    // Harmful only if EVERY corner is below the rim and NO corner is outside the shell. Tested
                    // against the rim because the cut is, or this would condemn Proteus's own output.
                    if (HatProfile.AboveRim(raceCode, pos[a], centre) >= 0f
                     || HatProfile.AboveRim(raceCode, pos[b], centre) >= 0f
                     || HatProfile.AboveRim(raceCode, pos[c], centre) >= 0f) continue;
                    if (Outside(pos[a], centre, floor) || Outside(pos[b], centre, floor)
                     || Outside(pos[c], centre, floor)) continue;

                    harmful++;
                    float below = hatLine - MathF.Min(pos[a].Y, MathF.Min(pos[b].Y, pos[c].Y));
                    if (below > deepest) deepest = below;
                }
            }
        }

        return new ScalpTagging(tagged, harmful, triangles, deepest);
    }

    /// <summary>
    /// Whether a point sits beyond the hat around it, judged against the scalp's radius in that direction.
    /// Shared by the cut and <see cref="MeasureScalpTagging"/> so both agree on where the hat is.
    /// </summary>
    private static bool Outside(Vector3 p, Vector3 centre, float[] floor)
    {
        var d = p - centre;
        float len = d.Length();
        return len > 1e-5f && len > floor[BinOf(d / len)] + CutReach;
    }

    /// <summary>
    /// Read every LOD0 mesh's positions separately, un-rebased, so indices match what the index buffer holds
    /// (unlike <see cref="ModelPartReader"/> and <see cref="SecondSkinWriter.TryReadLod0Geometry"/>).
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
    /// Where the head is, judged from the hair alone: the extent of the top of the model, since long hair
    /// drags a centroid down the neck.
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

    /// <summary>The head's own centre and radius, from a face model.</summary>
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
        // The UPPER half only: a hat sits on the cranium, not the jaw.
        var up = new List<float>();
        foreach (var mv in meshes)
            foreach (var p in mv.Positions)
                if (p.Y > centre.Y) up.Add((p - centre).Length());
        if (up.Count == 0) return null;
        up.Sort();
        return (centre, up[up.Count / 2]);
    }

    /// <summary>
    /// The scalp per direction: the FURTHEST head vertex, since the nearest may be eyes, teeth or mouth.
    /// Directions the head does not reach inherit the median.
    /// </summary>
    private static float[] HeadFloor(IReadOnlyList<MeshVerts> head, Vector3 centre, float radius, float minY)
    {
        var ceil = new float[BinsU * BinsV];
        foreach (var mv in head)
            foreach (var p in mv.Positions)
            {
                // THE CRANIUM ONLY: the nose would otherwise bulge the scalp forward past the forehead.
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
    /// The hair's own scalp, for when no head model is available: per direction, the CLOSEST the hair comes to
    /// the head centre, smoothed so one stray vertex cannot dent the result.
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

        // An empty bin gets the median, so a gap cannot read as "the scalp is at the centre".
        var seen = floor.Where(f => f < float.MaxValue).OrderBy(f => f).ToArray();
        float median = seen.Length > 0 ? seen[seen.Length / 2] : fallback;
        for (int i = 0; i < floor.Length; i++) if (floor[i] == float.MaxValue) floor[i] = median;

        // A wide MIN filter first: hair touches the scalp somewhere nearby, and this carries that contact
        // radius across directions where the hair's inner surface stands off the skull.
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

    /// <summary>Work out the cut and press for a hairstyle.</summary>
    /// <param name="parts">Read once by the caller, so the confirmation UI and the solve see the same island
    /// split.</param>
    /// <param name="head">The wearer's face model (<c>..._fac.mdl</c>), which carries the whole cranium.
    /// Without it the skull is guessed from the hair's inner surface.</param>
    /// <param name="fan">How far below the hat line the press fades out; a test seam. See
    /// <see cref="FanBelow"/>.</param>
    /// <param name="raceCode">The wearer's model code ("0801"), for picking the baked hat profile. Null
    /// means no profile and therefore no cut — see <see cref="HatProfile"/>.</param>
    public static Result Solve(byte[] mdl, ModelParts parts, byte[]? head = null,
                               float depth = ScalpFraction, float fan = FanBelow,
                               string? raceCode = null)
    {
        return new HatFit(mdl, head, raceCode).Run();
    }

    /// <summary>
    /// Extend the cut over the hair the press could not afford. Additive only: never recomputes the press.
    /// </summary>
    /// <param name="gone">Vertices the first cut already removed; a corner on one does not spare a
    /// triangle.</param>
    /// <param name="unaffordable">Vertices the press wanted to move, could not, and which sit above the
    /// hat line.</param>
    private static void CutUnaffordable(
        byte[] mdl, SecondSkinWriter.Source src, IReadOnlyList<MeshVerts> meshes,
        HashSet<long> gone, HashSet<long> unaffordable, List<ModelPart> drop)
    {
        foreach (var mv in meshes)
        {
            int mo = src.MeshStart + mv.Mesh * 36;
            if (mo + 36 > mdl.Length) continue;
            ushort subIdx = BitConverter.ToUInt16(mdl, mo + 10), subCount = BitConverter.ToUInt16(mdl, mo + 12);
            var pos = mv.Positions;

            for (int s = 0; s < subCount; s++)
            {
                int ss = src.SubmeshStart + (subIdx + s) * 16;
                if (ss + 16 > mdl.Length) break;
                uint io = BitConverter.ToUInt32(mdl, ss), ic = BitConverter.ToUInt32(mdl, ss + 4);
                if ((long)src.Ib + (io + ic) * 2 > mdl.Length) break;

                int mesh = mv.Mesh, submesh = s;
                var existing = drop.FirstOrDefault(p => p.Mesh == mesh && p.Submesh == submesh);
                if (existing is { Island: < 0 }) continue;        // already claimed whole

                var already = existing?.Ordinals is { } o ? new HashSet<int>(o) : [];
                var extra = new List<int>();

                for (uint t = 0; t + 3 <= ic; t += 3)
                {
                    int ord = (int)(t / 3);
                    if (already.Contains(ord)) continue;

                    int a = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t) * 2);
                    int b = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t + 1) * 2);
                    int c = BitConverter.ToUInt16(mdl, src.Ib + (int)(io + t + 2) * 2);
                    if (a >= pos.Length || b >= pos.Length || c >= pos.Length) continue;

                    if (Abandoned(mesh, a) && Abandoned(mesh, b) && Abandoned(mesh, c)) extra.Add(ord);
                }

                if (extra.Count == 0) continue;

                var ordinals = already.Concat(extra).Order().ToArray();
                bool whole = ordinals.Length == ic / 3;
                var part = new ModelPart
                {
                    Mesh = mesh,
                    Submesh = submesh,
                    Island = whole ? -1 : 0,
                    Label = $"{mesh}.{submesh}",
                    Material = "",
                    Triangles = [],
                    Ordinals = whole ? [] : ordinals,
                    AttributeMask = 0,
                    Min = Vector3.Zero,
                    Max = Vector3.Zero,
                    Toggleable = false,
                };
                if (existing != null) drop[drop.IndexOf(existing)] = part;
                else drop.Add(part);
            }
        }

        bool Abandoned(int mesh, int v)
        {
            var key = VertexKey(mesh, v);
            return unaffordable.Contains(key) || gone.Contains(key);
        }
    }

    /// <summary>
    /// Cut the hairstyle under the hat: what to drop (tagged <c>atr_kam</c>, which costs no shape values) and
    /// which vertices go with it. Follows whole triangles, not a geometric slice.
    /// </summary>
    /// <returns>The submesh pieces to tag, and the vertices no surviving triangle draws, which need no shape
    /// value.</returns>
    /// <param name="centre">The head's centre.</param>
    /// <param name="ears">Ear fur, which is never cut however far above the rim it stands — see
    /// <see cref="ReadEarGeometry"/>.</param>
    private static (List<ModelPart> Drop, HashSet<long> Gone) CutAtHatLine(
        byte[] mdl, SecondSkinWriter.Source src, IReadOnlyList<MeshVerts> meshes, float hatLine,
        Vector3 centre, float radius, string? raceCode, EarGeometry ears)
    {
        var drop = new List<ModelPart>();
        var gone = new HashSet<long>();

        // Above the reference hat's measured RIM plus the flattened ring (see HatProfile); the rim, not the
        // hat's surface, decides what a hat hides.
        bool UnderTheHat(Vector3 p)
            => HatProfile.AboveRim(raceCode, p, centre) > RingHeight;

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

                    // EAR FUR IS NOT SCALP. A Miqo'te's ears come out through the hat, so fur tagged away here
                    // leaves them bald. Counted in uses above first, so sparing a triangle also keeps its
                    // vertices out of `gone`. ANY corner spares it, for the same reason any corner cuts one.
                    if (ears.Fur.Count > 0
                     && (ears.Fur.Contains(VertexKey(mv.Mesh, a))
                      || ears.Fur.Contains(VertexKey(mv.Mesh, b))
                      || ears.Fur.Contains(VertexKey(mv.Mesh, c)))) continue;

                    // ANY corner under the hat takes the whole triangle: a sliver cut below the rim is hidden,
                    // where a fringe left standing pokes through. There is no radius bound; the cut goes by
                    // direction alone.
                    if (!UnderTheHat(pos[a]) && !UnderTheHat(pos[b]) && !UnderTheHat(pos[c])) continue;

                    lost[a]++; lost[b]++; lost[c]++;
                    above.Add((int)(t / 3));
                }

                // A submesh entirely cut is claimed whole; Island < 0 tells IsolateParts there is nothing to split.
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
    /// <param name="Need">How far out of the scalp it started, BEFORE the ramp. This, not
    /// <paramref name="Press"/>, orders the budget.</param>
    /// <param name="Cost">Shape values it would spend: one per index slot naming it.</param>
    private readonly record struct Candidate(
        int Mesh, int Vertex, Vector3 From, Vector3 To, float Need, float Press, int Cost);

    /// <summary>
    /// How far each pressed vertex sits, in mesh edges, from the nearest vertex the press may not move
    /// (breadth-first over the mesh's edges). A vertex whose whole piece is free is absent: full press.
    /// </summary>
    /// <param name="ignore">Geometry the cut removed: seeds nothing, but the walk passes through it.</param>
    private static Dictionary<long, int> PressDepth(
        byte[] mdl, SecondSkinWriter.Source src, IReadOnlyList<MeshVerts> meshes,
        HashSet<long> free, HashSet<long> ignore)
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
                var key = VertexKey(mv.Mesh, v);
                bool seeds = !free.Contains(key) && !ignore.Contains(key);
                dist[v] = seeds ? 0 : int.MaxValue;
                if (seeds) q.Enqueue(v);
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

    /// <summary>One connected strand (per island, not per submesh), measured the way the press judges it.</summary>
    /// <param name="Below">Vertices below the hat line.</param>
    /// <param name="Reach">The furthest any vertex stands off the SCALP in its own direction.</param>
    /// <param name="Covered">Every measured hat hides all of it. Reporting only.</param>
    /// <param name="Tail">Hangs far enough off the head that no hat could cover it; never pressed. Not the
    /// opposite of <paramref name="Covered"/>.</param>
    /// <param name="Hideable">A tail that also REACHES the hat, so hiding is the only remedy. Narrower than
    /// <paramref name="Tail"/>: a tail wholly below the hat meets no hat.</param>
    internal readonly record struct Strand(
        ModelPart Part, int Verts, int Below, float Drop, float Reach, bool Covered, bool Tail, bool Hideable);

    /// <summary>
    /// Every strand of a hairstyle and whether a hat covers it. Shared by the solve and the diagnostics so
    /// both measure the same way.
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

        // Islands where a submesh was split, else the whole submesh, so every triangle is judged exactly once.
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

            // COVERED depends only on whether the line cuts the strand; reach does not enter into it.
            float drop = hatLine - lowest;
            bool covered = drop <= PressDip;

            bool tail = drop > TailDrop && reach > TailReach;

            // Hideable: a tail reaching above the bottom of the press's fade, the same boundary the press uses.
            bool hideable = tail && highest >= hatLine - FanBelow;
            found.Add(new Strand(part, n, below, drop, reach, covered, tail, hideable));
        }
        return found;
    }

    /// <summary>
    /// How many index slots name each vertex, per mesh: the shape-value price of moving it, since a shape
    /// value rewires ONE slot.
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
    /// The MESH-RELATIVE vertices a part draws, via <see cref="ModelPart.Ordinals"/> and the submesh's index
    /// range; never <see cref="ModelPart.Triangles"/>, which are rebased across meshes.
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
