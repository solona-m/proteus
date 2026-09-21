using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Proteus.Services;

/// <summary>
/// The correspondence between two sizes of ONE body: the same vertex, moved.
/// <para/>
/// Every size option in a body mod's family is the same mesh re-sculpted, so vertex <c>i</c> of one size is vertex
/// <c>i</c> of the next and the displacement field is free. What has to happen is PROVING that before relying on it,
/// which is what the guard below is.
/// <para/>
/// The proof is the UVs, and getting there cost a wrong guess worth recording. The obvious check — "the two models
/// have the same index buffer" — is FALSE of real data: across Neolithe's chest and legs families, one or two
/// submeshes out of ten consistently reference a slightly different set of vertex indices for the same triangle
/// count, because each size was exported separately and the exporter chose differently among duplicate vertices.
/// Demanding identical triangles refuses every real pair. Nothing in the retarget needs them to match, either: the
/// field is per vertex, and each body's surface is built from its own triangles.
/// <para/>
/// What the retarget does need is that the two models NUMBER their vertices the same way, and uv is what proves it.
/// A size change moves positions; it must not move uv, or the body's textures would no longer line up. So uv agreeing
/// index for index says the numbering is shared, and it says it about the exact array the field is indexed by. It is
/// not required to agree perfectly: an author may nudge a handful (Neolithe's XS-to-L chest moves six of 10,518), so
/// the bar is a share, not equality.
/// </summary>
internal sealed class IdentityCorrespondence : IBodyCorrespondence
{
    private IdentityCorrespondence(ModelParts source, IReadOnlyList<Vector3?> field, string what)
    {
        Source = source;
        Field = field;
        description = what;
    }

    private readonly string description;

    public ModelParts Source { get; }

    public IReadOnlyList<Vector3?> Field { get; }

    public string Describe() => description;

    /// <summary>
    /// The share of vertices whose uv must agree index for index before two models are treated as the same mesh
    /// (90%). A different numbering agrees on almost nothing by accident, so the bar only has to clear noise — while
    /// authors DO re-map uvs on a variant without renumbering it: Neolithe's NSFW chests differ from their siblings on
    /// 1.2% of uvs and are the same mesh vertex for vertex. It was 99% once, and that refused them. A pair below the
    /// bar is not refused either: <see cref="BodyCorrespondence"/> maps it by texture coordinate instead.
    /// </summary>
    private const float UvAgreement = 0.90f;

    /// <summary>Two uvs this close count as the same, which absorbs a re-export through half-precision.</summary>
    private const float UvEpsilon = 1e-5f;

    /// <summary>
    /// The field from <paramref name="source"/> to <paramref name="target"/>, or false with a reason the user can act
    /// on. <paramref name="what"/> names the slot for the message ("chest", "legs").
    /// </summary>
    /// <param name="sourceUv">
    /// The source's uv0, two floats per vertex in the same order as <see cref="ModelParts.Positions"/>, as
    /// <c>SecondSkinWriter.TryReadLod0Geometry</c> returns it. Together with <paramref name="targetUv"/> this is what
    /// PROVES the two models share a vertex numbering — see the type's remarks. Both may be left null, and then only
    /// the structural skeleton is checked; the production path always supplies them, and only tests over geometry that
    /// has no uv at all leave them out.
    /// </param>
    public static bool TryBuild(ModelParts source, ModelParts target, string what,
                                out IdentityCorrespondence? correspondence, out string refusal,
                                float[]? sourceUv = null, float[]? targetUv = null)
    {
        correspondence = null;
        if (!SameSkeleton(source, target, what, out refusal)) return false;

        int vc = source.Positions.Length / 3;
        string proof = "structure only";
        if (sourceUv is { Length: > 0 } || targetUv is { Length: > 0 })
        {
            if (!SameNumbering(sourceUv, targetUv, vc, what, out float share, out refusal)) return false;
            proof = $"{share:P1} of uv identical";
        }

        var field = new Vector3?[vc];
        for (int i = 0; i < vc; i++)
            field[i] = new Vector3(target.Positions[i * 3]     - source.Positions[i * 3],
                                   target.Positions[i * 3 + 1] - source.Positions[i * 3 + 1],
                                   target.Positions[i * 3 + 2] - source.Positions[i * 3 + 2]);

        correspondence = new IdentityCorrespondence(source, field, $"{what}: {vc:N0} vertices, {proof}");
        refusal = "";
        return true;
    }

    /// <summary>Whether the two models number their vertices the same way. See the type's remarks.</summary>
    private static bool SameNumbering(float[]? a, float[]? b, int vertexCount, string what,
                                      out float share, out string refusal)
    {
        share = 0f;
        if (a is null || b is null || a.Length != vertexCount * 2 || b.Length != vertexCount * 2)
        {
            refusal = $"The {what} models' texture coordinates could not be read, so there is no way to tell whether " +
                      "they are two sizes of one body. Refusing rather than guessing.";
            return false;
        }

        int same = 0;
        for (int i = 0; i < vertexCount; i++)
            if (MathF.Abs(a[i * 2] - b[i * 2]) <= UvEpsilon && MathF.Abs(a[i * 2 + 1] - b[i * 2 + 1]) <= UvEpsilon)
                same++;

        share = vertexCount > 0 ? (float)same / vertexCount : 0f;
        if (share >= UvAgreement)
        {
            refusal = "";
            return true;
        }

        refusal = $"The two {what} models have the same vertex count but only {share:P0} of their texture " +
                  "coordinates line up, so they do not number their vertices the same way. They are different " +
                  "meshes, not two sizes of one.";
        return false;
    }

    /// <summary>
    /// Whether the two models have the same shape of mesh: vertex count, mesh layout, and the same submeshes drawing
    /// with the same materials and the same number of triangles.
    /// <para/>
    /// Compares STRUCTURE ONLY and never positions, which are the thing that is supposed to differ. Two things are
    /// deliberately left out:
    /// <list type="bullet">
    /// <item>ISLANDS. <see cref="ModelPartReader"/> welds those from positions at 1e-4, so two sizes of one body can
    /// legitimately split into different island counts where a gap closes as the body grows.</item>
    /// <item>The TRIANGLES themselves. Real size pairs disagree about them and are still two sizes of one body — see
    /// the type's remarks. The vertex numbering is proved from uv instead.</item>
    /// </list>
    /// </summary>
    private static bool SameSkeleton(ModelParts a, ModelParts b, string what, out string refusal)
    {
        if (Fingerprint(a) == Fingerprint(b)) { refusal = ""; return true; }

        string where = $"The two {what} models do not match";

        if (a.Positions.Length != b.Positions.Length)
        {
            refusal = $"{where}: {a.Positions.Length / 3:N0} vertices here, {b.Positions.Length / 3:N0} there. " +
                      "They are different meshes, not two sizes of one.";
            return false;
        }

        if (a.MeshSpans.Count != b.MeshSpans.Count)
        {
            refusal = $"{where}: {a.MeshSpans.Count} meshes here, {b.MeshSpans.Count} there.";
            return false;
        }

        for (int i = 0; i < a.MeshSpans.Count; i++)
        {
            if (a.MeshSpans[i] == b.MeshSpans[i]) continue;
            refusal = $"{where}: mesh {i} covers {a.MeshSpans[i].Count:N0} vertices here and " +
                      $"{b.MeshSpans[i].Count:N0} there.";
            return false;
        }

        var pa = Submeshes(a);
        var pb = Submeshes(b);
        if (pa.Count != pb.Count)
        {
            refusal = $"{where}: {pa.Count} submeshes here, {pb.Count} there.";
            return false;
        }

        for (int i = 0; i < pa.Count; i++)
        {
            if (pa[i].Mesh != pb[i].Mesh || pa[i].Submesh != pb[i].Submesh)
            {
                refusal = $"{where}: submesh {i} is {pa[i].Label} here and {pb[i].Label} there.";
                return false;
            }
            if (!string.Equals(pa[i].Material, pb[i].Material, StringComparison.OrdinalIgnoreCase))
            {
                refusal = $"{where}: submesh {pa[i].Label} draws with {pa[i].Material} here and " +
                          $"{pb[i].Material} there.";
                return false;
            }
            if (pa[i].Triangles.Length != pb[i].Triangles.Length)
            {
                refusal = $"{where}: submesh {pa[i].Label} has {pa[i].TriangleCount:N0} triangles here and " +
                          $"{pb[i].TriangleCount:N0} there.";
                return false;
            }
            if (!pa[i].Ordinals.SequenceEqual(pb[i].Ordinals))
            {
                refusal = $"{where}: submesh {pa[i].Label} lays its triangles out differently within the mesh.";
                return false;
            }
        }

        // Structure agreed everywhere the detailed walk looks, so the fingerprint disagreed over something the walk
        // does not cover. Refuse rather than guess: silently retargeting off a mismatch is the worst outcome here.
        refusal = $"{where}, though every part of them that was checked agrees. Refusing rather than guessing.";
        return false;
    }

    /// <summary>Whole submeshes in model order — the parts an island would otherwise duplicate.</summary>
    private static List<ModelPart> Submeshes(ModelParts m)
        => m.Parts.Where(p => p.Island < 0).ToList();

    /// <summary>
    /// A cheap structural digest, so the common case (they match) costs one pass instead of a full walk. FNV-1a over
    /// the vertex count, the spans, and each submesh's mesh, submesh, material, triangle count and ordinals.
    /// </summary>
    private static ulong Fingerprint(ModelParts m)
    {
        ulong h = 14695981039346656037UL;
        void Mix(ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                h ^= (v >> (i * 8)) & 0xFF;
                h *= 1099511628211UL;
            }
        }

        Mix((ulong)m.Positions.Length);
        Mix((ulong)m.MeshSpans.Count);
        foreach (var s in m.MeshSpans)
        {
            Mix((ulong)s.Mesh);
            Mix((ulong)s.BaseVertex);
            Mix((ulong)s.Count);
        }

        var subs = Submeshes(m);
        Mix((ulong)subs.Count);
        foreach (var p in subs)
        {
            Mix((ulong)p.Mesh);
            Mix((ulong)p.Submesh);
            Mix((ulong)p.Triangles.Length);
            foreach (char c in p.Material) Mix(char.ToLowerInvariant(c));
            foreach (int o in p.Ordinals) Mix((ulong)o);
        }

        return h;
    }
}
