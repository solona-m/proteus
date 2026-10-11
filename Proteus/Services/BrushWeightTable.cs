using System;

namespace Proteus.Services;

/// <summary>
/// <see cref="MeshVolumeSolve.BrushWeight"/> sampled once per brush, by squared distance as a share of the squared
/// reach, rather than worked out per point: the grab's Kelvinlet is far dearer than a smoothstep, and a view weighs
/// every pixel or vertex inside the brush each time the cursor moves. Both views read it, so they agree by construction.
/// </summary>
internal sealed class BrushWeightTable
{
    private const int Size = 512;
    private readonly float[] table = new float[Size + 1];
    private float radius = -1f;
    private bool grab;

    /// <summary>How far the brush last <see cref="Ensure"/>d reaches; what <see cref="At"/>'s share is of.</summary>
    public float Reach { get; private set; }

    /// <summary>Sample the brush of <paramref name="brushRadius"/>, unless it is the one already sampled.</summary>
    public void Ensure(float brushRadius, bool isGrab)
    {
        if (brushRadius == radius && isGrab == grab) return;
        radius = brushRadius;
        grab = isGrab;
        Reach = MeshVolumeSolve.BrushReach(brushRadius, isGrab);
        for (int i = 0; i <= Size; i++)
            table[i] = MeshVolumeSolve.BrushWeight(Reach * MathF.Sqrt(i / (float)Size), brushRadius, isGrab);
    }

    /// <summary>The weight at squared distance <paramref name="d2"/>, read off the table between its samples; 0 past the reach.</summary>
    public float At(float d2)
    {
        float r2 = Reach * Reach;
        if (r2 <= 0f || d2 >= r2) return 0f;
        float x = MathF.Max(d2, 0f) / r2 * Size;
        int i = Math.Min((int)x, Size - 1);
        return table[i] + (table[i + 1] - table[i]) * (x - i);
    }
}
