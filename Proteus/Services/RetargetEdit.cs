using System;
using System.Collections.Generic;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Services;

/// <summary>
/// The body retarget's answer, in the shape <see cref="MeshVolumeService.Inflate"/> reads it.
/// <para/>
/// A plain carrier and nothing more. Every decision — which nodes move, how far, which were snapped to the target
/// body, which were pushed out of it — was made in <see cref="BodyRetarget"/> over WELDED NODES, and spread to
/// vertices before it got here. Nothing in this class decides anything per vertex, and deliberately so: a second
/// place that reasons about the edit is a second place that can disagree with the first.
/// <para/>
/// Note what is absent. There is no displacement cap: <c>MeshVolumeSolve.MaxDisplacement</c> exists because a held
/// brush button applies hundreds of dabs a second, and a size change is a single computed answer that may legitimately
/// exceed 100 mm. There is no wind, no undo, and no notion of skin — see <see cref="IMeshEdit"/> for why this is not a
/// mode of the brush solve.
/// </summary>
internal sealed class RetargetEdit : IMeshEdit
{
    private readonly Vec3[] delta;
    private readonly Vec3[] normal;

    /// <param name="spans">The garment's spans, straight from <see cref="ModelParts.MeshSpans"/>.</param>
    /// <param name="delta">Per vertex, indexed like <see cref="ModelParts.Positions"/>.</param>
    /// <param name="normal">Per vertex, the normal after the move.</param>
    public RetargetEdit(IReadOnlyList<MeshSpan> spans, Vec3[] delta, Vec3[] normal)
    {
        if (delta.Length != normal.Length)
            throw new ArgumentException($"delta has {delta.Length} vertices and normal {normal.Length}.");

        Spans = spans;
        this.delta = delta;
        this.normal = normal;

        float worst = 0f;
        foreach (var d in delta)
        {
            float len = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            if (len > worst) worst = len;
        }
        Worst = worst;
    }

    public IReadOnlyList<MeshSpan> Spans { get; }

    public float Worst { get; }

    /// <summary>Nothing moved at all, so there is nothing to write.</summary>
    public bool Dirty => Worst > 0f;

    /// <summary>A retarget never touches the wind channel: it moves geometry, and wind is the author's.</summary>
    public bool WindEdited => false;

    public Vec3 DeltaAt(int vertex) => delta[vertex];

    public Vec3 NormalAt(int vertex) => normal[vertex];

    /// <summary>Never read — <see cref="WindEdited"/> is false.</summary>
    public float WindAt(int vertex) => 0f;
}
