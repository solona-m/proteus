using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

using static Proteus.Services.BodyBridge;

using static Proteus.Services.ToeCapSolver;

public static partial class SecondSkinWriter
{
    /// <summary>
    /// Build the merged shell. <paramref name="sources"/> are the body models the character is currently
    /// drawing (resolved live, never prebuilt); every layer is applied to every source.
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> sources, IReadOnlyList<SecondSkinLayer> layers, out Stats stats)
        => Build(sources, layers, null, false, out stats);

    /// <summary>
    /// Append the shell into a host accessory model: <paramref name="baseModel"/>'s meshes are emitted verbatim
    /// first, then the layers with material indices offset past the host's. Null = a fresh shell-only model.
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> sources, IReadOnlyList<SecondSkinLayer> layers,
        byte[]? baseModel, out Stats stats)
        => Build(sources, layers, baseModel, false, out stats);

    /// <summary>
    /// As above; <paramref name="skipConnectors"/> drops geometry the shell already draws (see
    /// <see cref="PlanConnectorDrops"/>), which on a sheer overlay would show as doubled alpha.
    /// </summary>
    public static byte[] Build(IReadOnlyList<byte[]> sources, IReadOnlyList<SecondSkinLayer> layers,
        byte[]? baseModel, bool skipConnectors, out Stats stats,
        IReadOnlyList<HashSet<string>?>? enabledShapes = null, Action<string>? diag = null,
        IReadOnlyList<AuthoredCapSet>? authoredCaps = null,
        // Per-source UV-space converter, parallel to `sources`; null = already in shell space (Source.UvConv).
        IReadOnlyList<UVRemapService.UvConversion?>? uvConverters = null)
        => Build(
            sources.Select((m, i) => new SourceSpec(
                m,
                KeepMaterial: null,   // body-skin filter, the behaviour every existing caller expects
                EnabledShapes: enabledShapes != null && i < enabledShapes.Count ? enabledShapes[i] : null,
                UvConv: uvConverters != null && i < uvConverters.Count ? uvConverters[i] : null,
                DropConnectors: skipConnectors)).ToList(),
            layers, baseModel, out stats, diag, authoredCaps);

    /// <summary>
    /// Where one or more <see cref="Build"/> calls spent their time — instrumentation only, summed across calls
    /// (one per host). The spans nest: "layers" contains the three solves, which run lazily inside the emit loop.
    /// </summary>
    public sealed class BuildTimings
    {
        public readonly PhaseCounter Prepare   = new();   // parse, redundancy planning, cap selection, bone/attr unions
        public readonly PhaseCounter CapSelect = new();   //   of which: choosing and placing the authored toe cap
        public readonly PhaseCounter Layers    = new();   // host pre-pass + every layer's emit
        public readonly PhaseCounter Bust      = new();   //   of which: bust bridge solves
        public readonly PhaseCounter Cleft     = new();   //   of which: cleft solves
        public readonly PhaseCounter Crotch    = new();   //   of which: crotch flat solves
        public readonly PhaseCounter Serialize = new();   // string block, headers, buffers

        public void Reset()
        {
            Prepare.Reset(); CapSelect.Reset(); Layers.Reset(); Bust.Reset(); Cleft.Reset(); Crotch.Reset();
            Serialize.Reset();
        }

        public string Describe()
            => $"prepare {Prepare.Ms:F0} (caps {CapSelect.Ms:F0}) | layers {Layers.Ms:F0} (bust {Bust.Ms:F0}/{Bust.Calls}"
             + $" + cleft {Cleft.Ms:F0}/{Cleft.Calls} + crotch flat {Crotch.Ms:F0}/{Crotch.Calls}) | serialize {Serialize.Ms:F0}";
    }

    /// <summary>
    /// Build the merged shell from fully-described sources. Every layer is applied to every source, so all
    /// sources must share one UV space and one race space.
    /// </summary>
    public static byte[] Build(IReadOnlyList<SourceSpec> sources, IReadOnlyList<SecondSkinLayer> layers,
        byte[]? baseModel, out Stats stats, Action<string>? diag = null,
        IReadOnlyList<AuthoredCapSet>? authoredCaps = null, PushSweep? pushSweep = null,
        BuildTimings? timings = null)
    {
        return new ShellBuild(sources, layers, baseModel, diag, authoredCaps, pushSweep, timings).Run(out stats);
    }

    /// <summary>
    /// v6 bone tables: a header per table ({u16 offset, u16 size}) followed by the index data. The offset
    /// is in DWORDS and relative to that table's OWN header — not to the section start.
    /// </summary>
    private static void WriteBoneTablesV6(MemoryStream ms, List<ushort[]> tables)
    {
        long start = ms.Position;
        int headerBytes = tables.Count * 4;
        long dataPos = start + headerBytes;

        // Hoisted: a stackalloc inside the loop accumulates a frame per iteration. Every write fills both bytes.
        Span<byte> t = stackalloc byte[2];
        for (int i = 0; i < tables.Count; i++)
        {
            long headerPos = start + i * 4;
            ms.Position = headerPos;

            BitConverter.TryWriteBytes(t, (ushort)((dataPos - headerPos) / 4));
            ms.Write(t);
            BitConverter.TryWriteBytes(t, (ushort)tables[i].Length);
            ms.Write(t);

            ms.Position = dataPos;
            foreach (var b in tables[i])
            {
                BitConverter.TryWriteBytes(t, b);
                ms.Write(t);
            }
            if ((tables[i].Length & 1) == 1) { BitConverter.TryWriteBytes(t, (ushort)0); ms.Write(t); }
            dataPos = ms.Position;
        }
        ms.Position = dataPos;
    }

    /// <summary>The 4 model-level bounding boxes, unioned across every part.</summary>
    private static byte[] UnionModelBBoxes(List<Source> parsed)
    {
        var outBB = new byte[4 * BBoxSize];
        for (int box = 0; box < 4; box++)
        {
            var min = new float[4] { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
            var max = new float[4] { float.MinValue, float.MinValue, float.MinValue, float.MinValue };
            bool any = false;
            foreach (var src in parsed)
            {
                if (src.ModelBBoxes.Length < (box + 1) * BBoxSize) continue;
                any = true;
                for (int c = 0; c < 4; c++)
                {
                    min[c] = MathF.Min(min[c], BitConverter.ToSingle(src.ModelBBoxes, box * BBoxSize + c * 4));
                    max[c] = MathF.Max(max[c], BitConverter.ToSingle(src.ModelBBoxes, box * BBoxSize + 16 + c * 4));
                }
            }
            if (!any) continue;
            for (int c = 0; c < 4; c++)
            {
                BitConverter.GetBytes(min[c]).CopyTo(outBB, box * BBoxSize + c * 4);
                BitConverter.GetBytes(max[c]).CopyTo(outBB, box * BBoxSize + 16 + c * 4);
            }
        }
        return outBB;
    }

    /// <summary>
    /// <c>atr_</c>, a slot letter, <c>v_</c>, then a part letter a–j: the attribute names an IMC attribute
    /// mask switches (see <c>MeshToggleService.AttributeSlotLetter</c>). Everything else — <c>atr_sne</c>,
    /// <c>atr_hij</c> — is driven by what gear is worn.
    /// </summary>
    public static bool IsVariantAttribute(string? name)
        => name is { Length: 8 } n
           && n.StartsWith("atr_", StringComparison.Ordinal)
           && n[5] == 'v' && n[6] == '_'
           && n[7] is >= 'a' and <= 'j';

    /// <summary>The bits of this model's submesh attribute masks that name an IMC variant attribute.</summary>
    internal static uint VariantBits(Source src)
    {
        uint bits = 0;
        for (int i = 0; i < 32 && i < src.AttrNames.Length; i++)
            if (IsVariantAttribute(src.AttrNames[i])) bits |= 1u << i;
        return bits;
    }

    /// <summary>
    /// Are these two submeshes alternatives — each tagged with a variant the other does not carry, so an IMC
    /// option decides which one is drawn? One whose variants are a SUBSET of the other's is not: that is a
    /// piece drawn "only with" the other (see <see cref="IsHidden"/>), and the two can be drawn together.
    /// </summary>
    internal static bool Alternatives(uint a, uint b, uint variantBits)
    {
        uint av = a & variantBits, bv = b & variantBits;
        return av != 0 && bv != 0 && (av & bv) != av && (av & bv) != bv;
    }

    /// <summary>
    /// Is <paramref name="a"/> drawn only with a variant that <paramref name="b"/> does not need — an add-on laid on
    /// the surface <paramref name="b"/> draws regardless? Then <paramref name="b"/> is no stand-in for it.
    /// </summary>
    internal static bool AddOnTo(uint a, uint b, uint variantBits)
    {
        uint av = a & variantBits, bv = b & variantBits;
        return av != 0 && (av & bv) == 0;
    }

    /// <summary>
    /// Is this submesh switched off by the pack's toggles? A submesh draws only when every attribute it names
    /// is on; an untagged submesh (mask 0) always draws.
    /// </summary>
    private static bool IsHidden(Source src, uint mask, IReadOnlySet<string> hidden)
    {
        if (mask == 0) return false;
        for (int bit = 0; bit < 32 && bit < src.AttrNames.Length; bit++)
            if ((mask & (1u << bit)) != 0 && hidden.Contains(src.AttrNames[bit]))
                return true;
        return false;
    }

    /// <summary>First .mdl version with the Dawntrail bone-table layout (a header array plus a shared index
    /// pool). Anything older stores a fixed <see cref="V5BoneTableBytes"/>-byte struct per table.</summary>
    private const uint MdlVersionV6 = 0x01000006;

    /// <summary>
    /// Where a mesh's uv1 lives, shared by <see cref="BuildVerbatim"/> and <see cref="CopyVerbatim"/>: packed in
    /// a Float4/Half4 uv0's <c>.zw</c>, a separate <c>usage 4 index 1</c> element, or APPENDED to uv0's own
    /// stream when there is neither. A model can have both forms at once, so every slot is written.
    /// </summary>
    private readonly record struct Uv1Plan(
        bool ZwValid, int ZwOffset, bool ZwHalf, VElem? Explicit, bool Append, int Stream, int AppendOffset)
    {
        /// <summary>Bytes this adds to <see cref="Stream"/>'s stride. Zero unless a uv1 is appended.</summary>
        public int ExtraBytes => Append ? 8 : 0;
    }

    private static Uv1Plan PlanUv1(VElem? uv0, VElem? uv1El, byte[] bs)
    {
        bool zwValid = uv0 is { } uz && (uz.Type == 3 || uz.Type == 14);
        int  zwOff   = uv0 is { } uo ? uo.Offset + (uo.Type == 3 ? 8 : 4) : 0;
        bool zwHalf  = uv0 is { } uh && uh.Type == 14;
        int  stream  = uv0 is { } us ? us.Stream : 1;
        return new Uv1Plan(zwValid, zwOff, zwHalf, uv1El,
            Append: uv0 is not null && !zwValid && uv1El is null,
            Stream: stream, AppendOffset: bs[stream]);
    }

    /// <summary>Write one vertex's (u, v) into every uv1 slot the plan names.</summary>
    private static void WriteUv1(
        in Uv1Plan p, VElem uv0, byte[][] outStreams, byte[] outStrides, int i, float u, float v)
    {
        if (p.ZwValid)
            WriteUV2(outStreams[uv0.Stream], i * outStrides[uv0.Stream] + p.ZwOffset, p.ZwHalf, u, v);
        if (p.Explicit is { } e1)
            WriteUV2(outStreams[e1.Stream], i * outStrides[e1.Stream] + e1.Offset, e1.Type is 13 or 14, u, v);
        if (p.Append)
            WriteUV2(outStreams[p.Stream], i * outStrides[p.Stream] + p.AppendOffset, false, u, v);
    }

    /// <summary>Splice a Float2 uv1 into a declaration block, when the plan appended one. The .zw and
    /// existing-uidx1 cases already declare theirs, so this no-ops for them.</summary>
    private static void SpliceUv1Decl(byte[] declBlock, in Uv1Plan p)
    {
        if (!p.Append) return;
        for (int e = 0; e < 17; e++)
        {
            int o = e * 8;
            if (declBlock[o] != 0xFF) continue;
            declBlock[o]     = (byte)p.Stream;
            declBlock[o + 1] = (byte)p.AppendOffset;
            declBlock[o + 2] = 1;                         // Float2
            declBlock[o + 3] = UseUV;
            declBlock[o + 4] = 1;                         // usageIndex 1
            if (e + 1 < 17) declBlock[(e + 1) * 8] = 0xFF;
            break;
        }
    }

    private static void CopyVerbatim(
        byte[] s, int vb, int srcDeclOff, ushort vc, VElem[] decl, uint[] vbo, byte[] bs, bool mirrorUv1,
        out byte[][] outStreams, out byte[] outStrides, out byte[] declBlock)
    {
        // Match BuildVerbatim's stream count: every stream carrying data OR named by a decl element.
        int streamCount = bs[2] > 0 ? 3 : (bs[1] > 0 ? 2 : 1);
        foreach (var el in decl) streamCount = Math.Max(streamCount, Math.Min((int)el.Stream, 2) + 1);

        // uv1 is touched ONLY for a glowing content piece (characterscroll samples its scroll map with it);
        // everything else stays byte-for-byte.
        VElem? uv0 = null, uv1El = null;
        if (mirrorUv1)
            foreach (var el in decl)
                if (el.Usage == UseUV)
                {
                    if (el.UsageIndex == 0) uv0 ??= el; else uv1El ??= el;
                }
        var plan = PlanUv1(uv0, uv1El, bs);
        bool doMirror = mirrorUv1 && uv0 is not null;

        outStrides = new byte[streamCount];
        for (int st = 0; st < streamCount; st++) outStrides[st] = bs[st];
        if (doMirror && plan.Append) outStrides[plan.Stream] = (byte)(bs[plan.Stream] + plan.ExtraBytes);

        outStreams = new byte[streamCount][];
        for (int st = 0; st < streamCount; st++)
        {
            outStreams[st] = new byte[vc * outStrides[st]];
            for (int i = 0; i < vc; i++)
                Array.Copy(s, vb + (int)vbo[st] + i * bs[st], outStreams[st], i * outStrides[st], bs[st]);
        }

        declBlock = new byte[DeclSize];
        Array.Copy(s, srcDeclOff, declBlock, 0, DeclSize);

        if (doMirror)
        {
            var u0 = uv0!.Value;
            Span<float> tmp = stackalloc float[4];
            for (int i = 0; i < vc; i++)
            {
                // The AUTHORED uv0, unshifted: a content mesh keeps its own UV island and the material's tiling sets
                // the repeat.
                ReadTyped(s, vb + (int)vbo[u0.Stream] + i * bs[u0.Stream] + u0.Offset, u0.Type, tmp);
                WriteUv1(plan, u0, outStreams, outStrides, i, tmp[0], tmp[1]);
            }
            SpliceUv1Decl(declBlock, plan);
        }
    }

    private static int BuildVerbatim(
        byte[] s, int vb, int srcDeclOff, ushort vc, VElem[] decl, uint[] vbo, byte[] bs, float push,
        out byte[][] outStreams, out byte[] outStrides, out byte[] declBlock, out (float U, float V)[] uvs,
        out (float U, float V)[]? uvsPreConv,
        UVRemapService.UvConversion? uvConv,
        out Vec3[]? capSrcPos, out Vec3[]? capOutPos, out ToeCapPlan? capPlan,
        sbyte[]? sides = null,
        SecondSkinLayer? cap = null, ushort[]? capTris = null, Action<string>? capLog = null,
        bool buildCapGeometry = true,
        Func<Vec3[], Vec3[], ushort[], (float U, float V)[], BustBridgePlan?>? bridge = null,
        PushSweep? pushSweep = null, ushort[]? spanTris = null, NailBedPlan? nailBeds = null)
    {
        return new VerbatimCopy(s, vb, srcDeclOff, vc, decl, vbo, bs, push, uvConv, sides, cap, capTris, capLog, buildCapGeometry, bridge, pushSweep, spanTris, nailBeds).Run(out outStreams, out outStrides, out declBlock, out uvs, out uvsPreConv, out capSrcPos, out capOutPos, out capPlan);
    }

    /// <summary>Texels the toe-cap map is pulled in by before it cuts. Zero: the weld closes the join instead.</summary>
    private const int CapCutErode = 0;

    /// <summary>
    /// How far a boundary vertex may be dragged to meet the cap's rim: past the mismatch the painted map
    /// produces and short of the next open edge, so a vertex of some other hole is left alone.
    /// </summary>
    private const float WeldRadius = 0.012f;

    /// <summary>
    /// How far a vertex the TOE-CAP cut exposed may be dragged to the rim: the map's deliberate over-cut is
    /// closed by this pull. Slivers it leaves are dropped by <see cref="WeldCollapse"/>.
    /// </summary>
    private const float WeldCutReach = 0.04f;

    /// <summary>
    /// Fraction of the shell's median edge whose SQUARE a weld-touched triangle's area must fall under to be
    /// dropped. Tiny, because every triangle dropped here is a hole.
    /// </summary>
    private const float WeldCollapse = 0.02f;

    /// <summary>How far a cap rim vertex may move to land on the shell's rim. Far tighter than
    /// <see cref="WeldRadius"/>: only the chord error is left to correct.</summary>
    private const float CapWeldRadius = 0.002f;

    /// <summary>Width of the band behind the welded lip ramped up to the cap's standoff: several median
    /// edges, so a one-triangle cliff becomes a slope.</summary>
    private const float CapFeather = 0.015f;

    /// <summary>Rings in from the cap's back seam over which its authored skinning is blended toward the
    /// body's. ONE: the two surfaces only have to agree where they meet.</summary>
    private const int CapSeamBlendRings = 1;

    /// <summary>Smoothing rounds over the shell around the toes. Each runs a positive step and a
    /// negative one, so this counts rounds rather than passes.</summary>
    private const int ShellRelaxPasses = 4;

    /// <summary>The smoothing step of Taubin's pair.</summary>
    private const float ShellRelaxLambda = 0.50f;

    /// <summary>...and the un-smoothing step, slightly larger in magnitude, which is what keeps the
    /// surface from shrinking.</summary>
    private const float ShellRelaxMu = -0.53f;

    /// <summary>How near the cap a shell point must be to be relaxed at all.</summary>
    private const float ShellRelaxReach = 0.004f;

    /// <summary>Furthest the relax may carry a point from where it started, once the inward part of the
    /// move has been dropped.</summary>
    private const float ShellRelaxMaxDrift = 0.0010f;

    /// <summary>How far apart two boundary vertices may sit and still be the same point; well under an edge length.</summary>
    private const float CrackWeldTolerance = 0.0010f;

    /// <summary>How far outside the cap's bounding box a boundary vertex may sit and still be one of the cap's trims
    /// for the crack weld. The trims hug the cap; anything past this is some other boundary — a hand's collapsed nail
    /// bed, a garment's hem — and snapping it is damage.</summary>
    private const float CrackWeldReach = 0.03f;

    /// <summary>How far apart in the atlas two coincident boundary vertices may sample and still be welded;
    /// a crack straddling two charts must stay open.</summary>
    private const float CrackWeldUvTolerance = 0.0020f;

    /// <summary>Weld-then-drop rounds: dropping a collapsed triangle exposes vertices the first round never saw.</summary>
    private const int WeldRounds = 3;

    /// <summary>
    /// The cleft's ramp value at which the span reaches full strength: half the hip weight, so the waist and
    /// flank fade below it and the buttocks are not scaled down.
    /// </summary>
    private const float CleftRampFull = 0.5f;

    /// <summary>The cleft's own slope limit, steeper than <see cref="BustMaxSlope"/>: cloth drops from the
    /// cheeks into the crotch far more steeply than the bust's limit allows.</summary>
    private const float CleftMaxSlope = 3f;

    /// <summary>
    /// The chest span's slope limit away from a hem — see the nearHem note in <c>BustBridgeSolve</c>. Within
    /// <see cref="BustHemRings"/> of uncovered cloth the tuned <see cref="BustMaxSlope"/> still holds.
    /// </summary>
    private const float BustOpenSlope = 2.5f;

    /// <summary>Most triangles a connected component may have and still be a toenail patch. Absolute,
    /// because a shell can be nothing but nail patches.</summary>
    private const int NailIslandMaxTris = 600;

    /// <summary>Largest boundary loop, in edges, that FillSmallHoles will close; a toenail socket is larger
    /// and must stay open.</summary>
    private const int SmallHoleEdges = 8;

    /// <summary>How close to the skin a non-skin triangle must sit to count as body for the clearance pass:
    /// a nail lies on the flesh, a strap stands off it.</summary>
    private const float NailHugsSkin = 0.003f;

    /// <summary>
    /// Minimum stand-off from the skin around the toes, as a FRACTION of the layer's push so it only rescues
    /// strays whatever the push is.
    /// </summary>
    private const float MinSkinClearanceOfPush = 0.6f;

    /// <summary>
    /// The floor for a face with a corner the weld dragged onto the cap's rim (0.6 mm): those faces span the
    /// knuckle flat, and the fractional floor is nothing once the toes bend under them.
    /// </summary>
    private const float WeldedSkinClearance = 0.0006f;

    /// <summary>Most a vertex may be lifted to reach that clearance. Past this it is not a straggler and
    /// moving it would distort the surface rather than repair it.</summary>
    private const float MaxSkinLift = 0.0030f;
    /// <summary>Largest a connected component may be, as a fraction of the biggest, and still be a toenail patch.</summary>
    private const float NailIslandMax = 0.05f;

    /// <summary>How close to the cap's own vertices every vertex of a candidate patch must be before it
    /// counts as sitting under the cap and can be dropped.</summary>
    private const float NailUnderCap = 0.010f;

    /// <summary>Resolution the cap's own footprint is rasterised at when the layer's map isn't square.</summary>
    private const int CapFootprintSize = 512;

    /// <summary>Texels the cap's footprint is widened by before it cuts: the hole has to be where the cap
    /// ENDS, not where it IS.</summary>
    private const int CapCutDilate = 0;
}
