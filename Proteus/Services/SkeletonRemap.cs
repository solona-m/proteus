using System;
using System.Collections.Generic;
using System.Linq;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// The bones a garment leans on that the race it is refitted for has no bone of, moved onto ones it has — a women's
/// garment rigged to her custom skeleton (YAS) made a men's, whose skeleton (IVCS, or the game's) lacks her physics bones.
/// <para/>
/// A move is a rename (<see cref="ModelAttributeWriter.RenameBone"/>): the bone keeps its index and every vertex its
/// bytes, slots, shape keys and LODs, and the game binds the new name to the wearer's bone of that name — two entries of
/// one name both follow it, their influences adding up. Never a bone the wearer has: IVCS's own bones stay as they are,
/// so its physics still drive them. Those moved follow their new bone rigidly; every move is said.
/// <para/>
/// Where each goes, the first that applies: its counterpart in the men's skeleton (<see cref="Counterparts"/>); else its
/// nearest ancestor in the drawn race's skeleton the wearer has; else the standard bone of its part of the body
/// (<see cref="StandIn"/>); else the hip (<see cref="Fallback"/>). A breast or thigh bone never lands outside its own
/// part of the body (<see cref="KeepsItsPart"/>): an ancestor that would put it on the hip is passed over.
/// <para/>
/// One exception to "never a bone the wearer has": TBSE's earring nodes on men's legs refitted for women
/// (<see cref="DropsEarrings"/>). Every skeleton has them, hung under the ear, so the crotch they are weighted to would
/// follow her head; they are moved onto the hip (<see cref="EarringTarget"/>).
/// </summary>
internal static class SkeletonRemap
{
    /// <summary>The bone every human skeleton has, for a bone nothing else knows.</summary>
    internal const string Fallback = "j_kosi";

    internal enum How { Counterpart, Ancestor, StandIn, Fallback, Earring }

    /// <summary>
    /// Whether a garment drawn as <paramref name="drawn"/> refitted for <paramref name="to"/> is saved without earring
    /// nodes: men's legs made women's. Pants carry no real earrings — on them the nodes are TBSE's crotch rig.
    /// </summary>
    internal static bool DropsEarrings(ushort drawn, ushort to, string? slot)
        => HairPortPlan.Playable(drawn) && HairPortPlan.Playable(to) && CrossSexRefit.IsMale(drawn) && !CrossSexRefit.IsMale(to)
           && slot == "_dwn";

    /// <summary>
    /// The bone an earring node on men's legs is moved onto: the hip. Measured on TBSE's legs and four men's pants rigged
    /// to them (EarringBonesLocalTests, 2026-10-22): the weight sits on the crotch, at most 3–15% of a vertex, beside
    /// <c>j_kosi</c> 76–100% of the time, where Bibo+ weights the same skin mostly to <c>j_kosi</c> too. The node's
    /// <c>_l</c>/<c>_r</c> says nothing of the leg its weight is on, and its parent is the ear — so never a thigh, never
    /// its parent.
    /// </summary>
    internal static string EarringTarget(string bone) => Fallback;

    /// <summary>A move onto <see cref="EarringTarget"/> for each earring node among <paramref name="bones"/>.</summary>
    internal static IReadOnlyList<Move> EarringMoves(IEnumerable<string> bones)
        => bones.Where(UvBakeBones.IsEarringNode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(b => new Move(b, EarringTarget(b), How.Earring)).ToList();

    internal sealed record Move(string From, string To, How How);

    /// <summary>
    /// A YAS bone and the IVCS bone of the same part of the body, where the two skeletons name it differently. Measured
    /// on Rue+ 2.2.7's yiggle legs, which rig both (SkeletonRemapLocalTests): the weight each of a pair carries centres on
    /// the same buttock, YAS's 5.5 cm under IVCS's (2026-10-21) — the lower buttock follows IVCS's buttock physics rather
    /// than the hip. YAS's thigh and belly physics bones have no IVCS counterpart any installed men's skeleton has, so
    /// their parents take them.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Counterparts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ya_shiri_phys_l"] = "iv_shiri_l",
        ["ya_shiri_phys_r"] = "iv_shiri_r",
    };

    /// <summary>
    /// The standard bone a body mod's own bone stands in for, by the part of the body it names; null when the name says
    /// nothing. The side comes from an <c>_l</c>/<c>_r</c> anywhere in the name.
    /// </summary>
    internal static string? StandIn(string bone)
    {
        string? side = Side(bone);
        string Sided(string stem, string none) => side == null ? none : stem + side;
        if (IsBreast(bone)) return Sided("j_mune", "j_sebo_b");
        if (IsThigh(bone)) return Sided("j_asi_a", "j_kosi");
        if (bone.Contains("fukubu", StringComparison.Ordinal)) return "j_sebo_a";
        if (bone.Contains("shiri", StringComparison.Ordinal) || bone.Contains("kintama", StringComparison.Ordinal)
            || bone.Contains("funyachin", StringComparison.Ordinal) || bone.Contains("ochinko", StringComparison.Ordinal)
            || bone.Contains("koumon", StringComparison.Ordinal))
            return "j_kosi";
        if (bone.StartsWith("iv_asi_", StringComparison.Ordinal)) return Sided("j_asi_e", "j_kosi");
        // IVCS's third finger joints, which a man's game skeleton lacks: the same finger's last joint the game has. A
        // women's glove made a men's carries them, and the hip their fallback would be takes the fingertips with it.
        if (FingerJoint.Match(bone) is { Success: true } finger)
            return $"j_{finger.Groups[1].Value}_b_{finger.Groups[2].Value}";
        if (bone.StartsWith("j_ex_top", StringComparison.Ordinal)) return "j_sebo_c";
        if (bone.StartsWith("j_ex_met", StringComparison.Ordinal)) return "j_kao";
        return null;
    }

    /// <summary>An IVCS finger joint past the game's own two: thumb, index, middle, ring or little finger, and side.</summary>
    private static readonly System.Text.RegularExpressions.Regex FingerJoint =
        new(@"^iv_(oya|hito|naka|kusu|ko)_[c-z]_(l|r)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsBreast(string bone)
        => bone.Contains("mune", StringComparison.Ordinal) || bone.Contains("kyokin", StringComparison.Ordinal);

    private static bool IsThigh(string bone) => bone.Contains("daitai", StringComparison.Ordinal);

    private static string? Side(string bone)
        => bone.EndsWith("_l", StringComparison.Ordinal) || bone.Contains("_l_", StringComparison.Ordinal) ? "_l"
         : bone.EndsWith("_r", StringComparison.Ordinal) || bone.Contains("_r_", StringComparison.Ordinal) ? "_r"
         : null;

    /// <summary>
    /// Whether moving <paramref name="from"/> onto <paramref name="to"/> keeps it in its own part of the body: a breast
    /// bone on the torso, a thigh bone on its own thigh. Every other bone may go anywhere its rules send it.
    /// </summary>
    internal static bool KeepsItsPart(string from, string to)
    {
        var region = UvBakeBones.RegionOf(to);
        if (IsBreast(from)) return region == UvBakeBones.Region.Torso;
        if (IsThigh(from))
            return Side(from) switch
            {
                "_l" => region == UvBakeBones.Region.ThighL,
                "_r" => region == UvBakeBones.Region.ThighR,
                _ => region is UvBakeBones.Region.ThighL or UvBakeBones.Region.ThighR or UvBakeBones.Region.Pelvis,
            };
        return true;
    }

    /// <summary>Plans the moves for one wearer: its bones, and the parents the drawn race's skeleton gives a bone.</summary>
    /// <param name="dropEarrings">Earring nodes are moved too, though the wearer has them, and nothing is moved onto
    /// one (<see cref="DropsEarrings"/>).</param>
    internal sealed class Planner(IReadOnlySet<string> wearerBones, Func<string, string?> drawnParentOf,
                                  bool dropEarrings = false)
    {
        internal IReadOnlySet<string> WearerBones => wearerBones;

        /// <summary>A move for each of <paramref name="bones"/> the wearer lacks; none for those it has. Dropping
        /// earrings, the earring nodes' moves come first.</summary>
        internal IReadOnlyList<Move> MovesFor(IEnumerable<string> bones)
        {
            var distinct = bones.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var moves = dropEarrings ? EarringMoves(distinct).ToList() : [];
            foreach (string bone in distinct)
                if (!wearerBones.Contains(bone) && !(dropEarrings && UvBakeBones.IsEarringNode(bone))) moves.Add(MoveFor(bone));
            return moves;
        }

        private bool Lands(string bone) => wearerBones.Contains(bone) && !(dropEarrings && UvBakeBones.IsEarringNode(bone));

        private Move MoveFor(string bone)
        {
            if (Counterparts.TryGetValue(bone, out var twin) && Lands(twin) && KeepsItsPart(bone, twin))
                return new Move(bone, twin, How.Counterpart);

            // Up the drawn skeleton to the first bone the wearer has — or has the counterpart of — passing earring nodes
            // being dropped. One that would take a breast or thigh bone out of its part of the body ends the walk:
            // everything above it is further away still.
            var seen = new HashSet<string>(StringComparer.Ordinal) { bone };
            for (string? at = drawnParentOf(bone); at != null && seen.Add(at) && seen.Count <= 256; at = drawnParentOf(at))
            {
                string? hit = Lands(at) ? at
                            : Counterparts.TryGetValue(at, out var other) && Lands(other) ? other : null;
                if (hit == null) continue;
                if (KeepsItsPart(bone, hit)) return new Move(bone, hit, How.Ancestor);
                break;
            }

            if (StandIn(bone) is { } stand && Lands(stand) && KeepsItsPart(bone, stand))
                return new Move(bone, stand, How.StandIn);
            return new Move(bone, Fallback, How.Fallback);
        }
    }

    /// <summary>Influences as they are after <paramref name="moves"/>: renamed, those of one name added up, heaviest first.</summary>
    internal static List<(string Bone, float W)> Apply(IReadOnlyList<(string Bone, float W)> influences, IReadOnlyList<Move> moves)
    {
        var to = moves.ToDictionary(m => m.From, m => m.To, StringComparer.Ordinal);
        var sum = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var (bone, w) in influences)
        {
            if (w <= 0f) continue;
            string name = to.TryGetValue(bone, out var moved) ? moved : bone;
            sum[name] = sum.GetValueOrDefault(name) + w;
        }
        return sum.Select(p => (p.Key, p.Value)).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Bone names as they are after <paramref name="moves"/>.</summary>
    internal static IEnumerable<string> Apply(IEnumerable<string> bones, IReadOnlyList<Move> moves)
    {
        var to = moves.ToDictionary(m => m.From, m => m.To, StringComparer.Ordinal);
        return bones.Select(b => to.TryGetValue(b, out var moved) ? moved : b).Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// <paramref name="model"/> with each move's bone renamed, checked against what the moves should give
    /// (<see cref="Verify"/>); null with why when it is not — nothing is ever saved from a rename that went wrong.
    /// </summary>
    internal static byte[]? Rename(byte[] model, IReadOnlyList<Move> moves, out string refusal)
    {
        refusal = "";
        if (moves.Count == 0) return model;
        byte[] renamed;
        try
        {
            renamed = model;
            // Every entry of the name: RenameBone renames the first, and a model may already name a bone twice.
            foreach (var move in moves)
                for (int guard = 0; guard < 64 && Array.IndexOf(SecondSkinWriter.Parse(renamed).BoneNames, move.From) >= 0; guard++)
                    renamed = ModelAttributeWriter.RenameBone(renamed, move.From, move.To);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            refusal = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        if (Verify(model, renamed, moves, out string why)) return renamed;
        refusal = why;
        return null;
    }

    /// <summary>
    /// Whether <paramref name="after"/> is <paramref name="before"/> with <paramref name="moves"/> and nothing else: the
    /// same vertices where they were, the same shapes and LODs, every vertex's influences — added up by name — what
    /// <see cref="Apply(IReadOnlyList{ValueTuple{string, float}}, IReadOnlyList{Move})"/> gives, and no moved bone left
    /// weighted.
    /// </summary>
    internal static bool Verify(byte[] before, byte[] after, IReadOnlyList<Move> moves, out string why)
    {
        why = "";
        var a = ModelSkinReader.Read(before, null, null);
        var b = ModelSkinReader.Read(after, null, null);
        if (a == null || b == null) { why = "the renamed model could not be read"; return false; }
        if (a.VertexCount != b.VertexCount) { why = $"{a.VertexCount} vertices became {b.VertexCount}"; return false; }
        try
        {
            if (SecondSkinWriter.Parse(before).Shapes.Count != SecondSkinWriter.Parse(after).Shapes.Count)
            { why = "the shape keys changed"; return false; }
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            why = $"the renamed model could not be parsed ({ex.Message})";
            return false;
        }
        if (ModelLodTrimmer.LodCount(before) != ModelLodTrimmer.LodCount(after)) { why = "the LODs changed"; return false; }

        var moved = moves.Select(m => m.From).ToHashSet(StringComparer.Ordinal);
        for (int v = 0; v < a.VertexCount; v++)
        {
            if (a.Positions[v] != b.Positions[v]) { why = $"vertex {v} moved"; return false; }
            var want = Apply(Influences(a, v), moves);
            var got = Apply(Influences(b, v), []);
            if (got.FirstOrDefault(i => moved.Contains(i.Bone)).Bone is { } left)
            { why = $"vertex {v} still leans on {left}"; return false; }
            if (want.Count != got.Count) { why = $"vertex {v} has {got.Count} bones, not {want.Count}"; return false; }
            var wanted = want.ToDictionary(i => i.Bone, i => i.W, StringComparer.Ordinal);
            foreach (var (bone, w) in got)
                if (!wanted.TryGetValue(bone, out float x) || MathF.Abs(x - w) > 1e-6f)
                { why = $"vertex {v} leans on {bone} by {w:F4}, not {x:F4}"; return false; }
        }
        return true;
    }

    private static List<(string Bone, float W)> Influences(SkinnedMesh skin, int v)
    {
        var list = new List<(string, float)>(SkinnedMesh.MaxInfluences);
        for (int k = 0; k < SkinnedMesh.MaxInfluences; k++)
        {
            int at = v * SkinnedMesh.MaxInfluences + k;
            float w = skin.BoneWeights[at];
            if (w > 0f && skin.BoneIndices[at] < skin.BoneNames.Length) list.Add((skin.BoneNames[skin.BoneIndices[at]], w));
        }
        return list;
    }

    /// <summary>The moves as the panel and the log say them: "ya_shiri_phys_l → iv_shiri_l, …".</summary>
    internal static string Describe(IReadOnlyList<Move> moves) => string.Join(", ", moves.Select(m => $"{m.From} → {m.To}"));

    /// <summary>The bone names a model has more than one entry of; empty when it cannot be read.</summary>
    internal static IReadOnlyList<string> DuplicateBones(byte[] model)
    {
        try
        {
            return SecondSkinWriter.Parse(model).BoneNames.GroupBy(b => b, StringComparer.Ordinal)
                                   .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The moves with how each was found, for the log.</summary>
    internal static string DescribeHow(IReadOnlyList<Move> moves)
        => string.Join(", ", moves.Select(m => $"{m.From} → {m.To} ({m.How.ToString().ToLowerInvariant()})"));

    /// <summary>Why a refit whose bones could not be renamed was refused (<see cref="Rename"/>).</summary>
    internal static string RenameRefusal(string why)
        => string.Format(ProteusLoc.Localize("Parts.Retarget.Race.RenameFailed.Fmt", "the bones could not be renamed ({0})"), why);
}
