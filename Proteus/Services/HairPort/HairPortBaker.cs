using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using CheapLoc;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// Turns one race's hair model into another race's: the geometry half of a hair port, with no file or mod I/O.
/// <para/>
/// The game ships the shared hairstyles once per race, and those per-race copies differ from one another almost exactly
/// by <c>human.pbd</c>'s head-bone matrices: bent through them, a Miqo'te's model lands a few millimetres from the
/// Midlander's own (measured over the shared styles of 14 race pairs, 2026-10-16: 3.5–7 mm on average, against 6–700 mm
/// unbent). So a port is that bend — through the race both fall through to — plus what the bend cannot know: ear fur
/// that only a Miqo'te has ears for, bones the target's skeleton lacks, and material names that carry the old race.
/// </summary>
internal static class HairPortBaker
{
    /// <summary>
    /// Raised whenever a port would come out differently, so ports made by an older build are made again.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// The most of a port's cranium vertices that may sit deeper than <see cref="InsideDepth"/> inside the target's
    /// skull. Measured over the game's own per-race copies of the shared hairstyles (2026-10-16): those reach 10%, ports
    /// 19%, and a hairstyle left unbent on a smaller head 35–99% (see HairPortLocalTests).
    /// </summary>
    public const float SunkLimit = 0.25f;

    /// <summary>
    /// How far, in metres, the port's typical distance from the scalp may move from the source's on its own head. The
    /// game's own copies move it up to 12 mm, ports 10 mm; a hairstyle left unbent sinks or floats 16–45 mm.
    /// </summary>
    public const float GapMargin = 0.014f;

    /// <summary>The share of a hairstyle's triangles a port may turn inside out before it is saved with a warning; see
    /// <see cref="Folded"/>. Taken of its LOD0 triangles, the hidden fur's included. Never a refusal.</summary>
    public const float FoldedShare = 0.005f;

    /// <summary>How deep under the scalp a vertex must be before it counts as sunk into the head, in metres: the scalp
    /// here is the furthest head vertex in each direction, which strands laid on it routinely dip under.</summary>
    private const float InsideDepth = 0.01f;

    /// <summary>The prefix of the bones a hairstyle's own skeleton adds, which only a race the game ships that
    /// skeleton for has.</summary>
    private const string ExtraBonePrefix = "j_ex_";

    private const string EarBonePrefix = "j_mimi";
    private const string HeadBone = "j_kao";

    /// <param name="Model">The port, ready to save at the target race's game path.</param>
    /// <param name="Renamed">Material names changed to the target race, old to new.</param>
    /// <param name="Sunk">Share of the port's cranium vertices sunk into the target's head (see <see cref="SunkLimit"/>).</param>
    /// <param name="GapShift">How far its typical distance from the scalp moved against the source's, in metres.</param>
    /// <param name="FurHidden">Triangles of ear fur taken out, for a Miqo'te hairstyle going to another race.</param>
    /// <param name="Folded">Drawn triangles the bend turned inside out.</param>
    /// <param name="Warning">Said beside the port when it does not sit on the target's head (over <see cref="SunkLimit"/>
    /// or <see cref="GapMargin"/>), or more than <see cref="FoldedShare"/> of it turned inside out: it is saved all the
    /// same, for the player to look at in game. Null otherwise.</param>
    public sealed record Result(byte[] Model, IReadOnlyList<(string From, string To)> Renamed, float Sunk,
                                float GapShift, int FurHidden, long Millis, int Folded = 0, string? Warning = null);

    /// <summary>
    /// Where each bone sits in a hairstyle's skeleton, for the deformer: a bone it does not list takes its nearest
    /// listed ancestor's matrix. The same for every port, whatever skeleton the wearer has, so a port made in Studio
    /// and one made on equip come out identical. Hair hangs off the head; the head off the spine.
    /// </summary>
    public static string? ParentOf(string bone)
    {
        if (bone.StartsWith("j_kami", StringComparison.Ordinal) || bone.StartsWith(ExtraBonePrefix, StringComparison.Ordinal)
            || bone.StartsWith(EarBonePrefix, StringComparison.Ordinal) || bone.StartsWith("j_f_", StringComparison.Ordinal))
            return HeadBone;
        return bone switch
        {
            HeadBone => "j_kubi",
            "j_kubi" => "j_sebo_c",
            "j_sebo_c" => "j_sebo_b",
            "j_sebo_b" => "j_sebo_a",
            "j_sebo_a" => "j_kosi",
            _ => null,
        };
    }

    /// <summary>Miqo'te, the only race whose hairstyles carry ears.</summary>
    public static bool HasEars(ushort race) => race / 100 is 7 or 8;

    /// <summary>
    /// The port of <paramref name="source"/>, a <paramref name="from"/> model of hairstyle <paramref name="hairId"/>, for
    /// <paramref name="to"/>. Null, with the reason, when it cannot be made; one that does not sit on the target's head
    /// is made all the same, with a warning.
    /// </summary>
    /// <param name="targetExtraBones">The hairstyle-skeleton bones (<c>j_ex_…</c>) the target race's own model of this
    /// hairstyle names — the ones its skeleton has. Empty when the game ships no such model.</param>
    /// <param name="sourceHead">The source race's face model, to measure the source against.</param>
    /// <param name="targetHead">The target race's.</param>
    public static Result? Prepare(byte[] source, ushort from, ushort to, int hairId, PbdFile pbd,
                                  IReadOnlySet<string> targetExtraBones, byte[]? sourceHead, byte[]? targetHead,
                                  out string refusal)
    {
        var clock = Stopwatch.StartNew();
        refusal = "";
        if (sourceHead == null || targetHead == null)
        {
            refusal = ProteusLoc.Localize("HairPort.Refused.NoHead", "the game's head model to fit it to could not be read");
            return null;
        }

        SecondSkinWriter.Source src;
        try { src = SecondSkinWriter.Parse(source); }
        catch (Exception) { src = null!; }
        if (src == null || ModelSkinReader.Read(source, null, null) is not { } skin)
        {
            refusal = ProteusLoc.Localize("Parts.Retarget.Bake.Unreadable", "the model could not be read");
            return null;
        }

        // A bone of the hairstyle's own skeleton the target race does not get would leave its strands at the origin.
        var missing = src.BoneNames
            .Where(b => b.StartsWith(ExtraBonePrefix, StringComparison.Ordinal) && !targetExtraBones.Contains(b))
            .ToList();
        if (missing.Count > 0)
        {
            refusal = string.Format(ProteusLoc.Localize("HairPort.Refused.Bones.Fmt",
                "it moves with bones c{0:D4} does not have for this hairstyle ({1})"), to, string.Join(", ", missing));
            return null;
        }

        // Every weighted bone must be one the deformer knows, or it would stay where the source race had it while the
        // head around it moved: a strand torn off the scalp.
        ushort root = RacialModelBake.CommonAncestor(from, to);
        var down = root == from || root == 0 ? [] : pbd.Chain(root, from);
        var up = root == to || root == 0 ? [] : pbd.Chain(root, to);
        var unknown = Weighted(skin)
            .Where(b => !PbdFile.Lists(down, b, ParentOf) && !PbdFile.Lists(up, b, ParentOf))
            .ToList();
        if (unknown.Count > 0 && (PbdFile.Lists(down, HeadBone, ParentOf) || PbdFile.Lists(up, HeadBone, ParentOf)))
        {
            refusal = string.Format(ProteusLoc.Localize("HairPort.Refused.Unknown.Fmt",
                "the racial deformer does not know how to move {0}"), string.Join(", ", unknown));
            return null;
        }

        var model = (byte[])source.Clone();
        int furHidden = 0;
        if (HasEars(from) && !HasEars(to)) furHidden = HideEars(model, src);
        // What the bend is judged against: the hairstyle as it will be drawn, its fur already gone.
        var shown = ModelPartReader.Read(model);

        model = RacialModelBake.Port(model, from, to, pbd, ParentOf, out refusal)!;
        if (model == null) return null;

        // No ears on the target's skeleton: what is left on an ear bone rides the head instead of the origin.
        if (HasEars(from) && !HasEars(to))
            foreach (var bone in src.BoneNames.Where(b => b.StartsWith(EarBonePrefix, StringComparison.Ordinal)))
                model = ModelAttributeWriter.RenameBone(model, bone, HeadBone);

        var renamed = new List<(string, string)>();
        foreach (var name in SecondSkinWriter.Parse(model).MatNames)
        {
            var next = HairMaterialPath.RenameRace(name, to);
            if (next == name) continue;
            model = ModelAttributeWriter.RenameMaterial(model, name, next);
            renamed.Add((name, next));
        }

        // A bend that turns the surface inside out draws its back face. A few strand-tip slivers do turn on the bigger
        // bends (2 to 6 of thousands, on the game's own Lalafell and Hrothgar hairstyles), unseen on hair that is drawn
        // from both sides; more than that is said, never refused — the player looks at it in game. The fur is not
        // counted: a Lalafell's deformer has ear bones of its own and turns a Miqo'te's fur over (Loosened Bun: 24
        // triangles), but it is not drawn.
        int folded = 0;
        string? warning = null;
        if (shown is { } a && ModelPartReader.Read(model) is { } b)
        {
            folded = Folded(a, b);
            if (folded > FoldedShare * a.Parts.Where(p => p.Island == -1).Sum(p => p.TriangleCount))
                warning = string.Format(ProteusLoc.Localize("HairPort.Folded.Fmt",
                    "bent onto c{0:D4}, {1} of its triangles turn inside out and may show as holes; check it in game"),
                    to, folded);
        }

        var before = Measure(source, sourceHead);
        var after = Measure(model, targetHead);
        if (before == null || after == null)
        {
            refusal = ProteusLoc.Localize("HairPort.Refused.NoHead", "the game's head model to fit it to could not be read");
            return null;
        }
        // Two-sided: sunk into the skull, or standing off it further (or nearer) than the source does on its own head.
        // Said, never refused, as the folds are: the player looks at it in game.
        float shift = after.Value.Gap - before.Value.Gap;
        if (after.Value.Sunk > SunkLimit || MathF.Abs(shift) > GapMargin)
        {
            var fit = string.Format(ProteusLoc.Localize("HairPort.Fit.Fmt",
                    "it does not sit on c{0:D4}'s head: {1:P0} of it sinks into the skull, and its distance from the " +
                    "scalp is {2:F0} mm off the original's; check it in game"),
                to, after.Value.Sunk, MathF.Abs(shift) * 1000f);
            warning = warning == null ? fit : fit + "; " + warning;
        }

        return new Result(model, renamed, after.Value.Sunk, shift, furHidden, clock.ElapsedMilliseconds, folded, warning);
    }

    /// <summary>
    /// How many of <paramref name="a"/>'s triangles <paramref name="b"/> — the same model bent — turns over: its face
    /// pointing against what it pointed before. Triangles too small to see (under 0.01 mm²) are left out; a hair tip's
    /// slivers turn on rounding alone.
    /// </summary>
    internal static int Folded(ModelParts a, ModelParts b)
    {
        int folded = 0;
        foreach (var part in a.Parts.Where(p => p.Island == -1))
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int i = part.Triangles[t], j = part.Triangles[t + 1], k = part.Triangles[t + 2];
                var na = Normal(a.Positions, i, j, k);
                var nb = Normal(b.Positions, i, j, k);
                if (na.Length() * 0.5f >= 1e-8f && nb.LengthSquared() > 1e-14f && Vector3.Dot(na, nb) < 0) folded++;
            }
        return folded;

        static Vector3 Normal(float[] p, int i, int j, int k)
        {
            Vector3 At(int v) => new(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
            return Vector3.Cross(At(j) - At(i), At(k) - At(i));
        }
    }

    /// <summary>The bones any vertex actually leans on.</summary>
    private static IEnumerable<string> Weighted(SkinnedMesh skin)
    {
        var used = new HashSet<int>();
        for (int i = 0; i < skin.BoneWeights.Length; i++)
            if (skin.BoneWeights[i] > 0f) used.Add(skin.BoneIndices[i]);
        return used.Where(i => i < skin.BoneNames.Length).Select(i => skin.BoneNames[i]).Distinct();
    }

    /// <summary>
    /// Take the ears out of a Miqo'te hairstyle, in place: every triangle with a corner on the fur or the ear's own skin
    /// (<see cref="HatCompatSolve.ReadEarGeometry"/>) collapses, so nothing is drawn where the target has no ears.
    /// Also how a Miqo'te's own hairstyle is made earless (<see cref="EarlessHair.Make"/>).
    /// </summary>
    internal static int HideEars(byte[] model, SecondSkinWriter.Source src)
    {
        var meshes = HatCompatSolve.ReadLod0Meshes(model);
        var ears = HatCompatSolve.ReadEarGeometry(model, src, meshes);
        if (ears.Fur.Count == 0 || ModelPartReader.Read(model) is not { } parts) return 0;

        var fur = new HashSet<int>();
        foreach (var span in parts.MeshSpans)
            for (int v = 0; v < span.Count; v++)
                if (ears.Fur.Contains(((long)span.Mesh << 32) | (uint)v)) fur.Add(span.BaseVertex + v);

        var hidden = new HashSet<PolygonSelection.Key>();
        foreach (var part in parts.Parts)
            for (int t = 0; t + 3 <= part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (fur.Contains(a) || fur.Contains(b) || fur.Contains(c)) hidden.Add(PolygonSelection.Key.Of(a, b, c));
            }
        return ModelPartHider.Hide(model, parts.MeshSpans, hidden);
    }

    /// <summary>
    /// How a hairstyle sits on a head, over its vertices around the cranium: the share sunk into the skull, and the
    /// median distance from the scalp (negative inside). Null when either model has nothing to measure; a hairstyle with
    /// no vertex around that cranium at all is nowhere near it, and measures as entirely sunk.
    /// </summary>
    /// <remarks>The scalp is <see cref="HatCompatSolve.HeadFloor"/>'s: per direction from the face model's centre, its
    /// furthest vertex above that centre.</remarks>
    internal static (float Sunk, float Gap)? Measure(byte[] hair, byte[] head)
    {
        var frame = HatCompatSolve.HeadFrameFrom(head);
        var meshes = HatCompatSolve.ReadLod0Meshes(hair);
        if (frame is not { } f || meshes.Count == 0) return null;

        var floor = HatCompatSolve.HeadFloor(HatCompatSolve.ReadLod0Meshes(head), f.Centre, f.Radius, f.Centre.Y);
        int considered = 0, sunk = 0;
        var gaps = new List<float>();
        foreach (var mv in meshes)
            foreach (var p in mv.Positions)
            {
                // The cranium only: below it the face model is jaw and neck, which hair hangs past, not into.
                if (p.Y < f.Centre.Y) continue;
                var d = p - f.Centre;
                float len = d.Length();
                if (len < 1e-5f) continue;
                considered++;
                float gap = len - floor[HatCompatSolve.BinFor(d / len)];
                gaps.Add(gap);
                if (gap < -InsideDepth) sunk++;
            }

        if (considered == 0) return (1f, float.NegativeInfinity);
        gaps.Sort();
        return (sunk / (float)considered, gaps[gaps.Count / 2]);
    }
}
