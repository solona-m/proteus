using System;
using System.Numerics;
using Proteus.Gui;
using Proteus.Services;
using Xunit;
using Vec3 = Proteus.Services.SecondSkinWriter.Vec3;

namespace Proteus.Tests;

/// <summary>The normal maps a stretch or a turn gives, which the solve's exact normals are built from.</summary>
public class LinearNormalsTests
{
    private static Vector3 Unit(Vec3 v) => Vector3.Normalize(new Vector3(v.X, v.Y, v.Z));

    [Fact]
    [Trait("Plan", "AC-axis-scale-7")]
    public void NormalMapIsInverseTranspose()
    {
        var pivot = new Vector3(0.3f, -0.2f, 0.1f);
        var stretch = PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f));
        var map = LinearNormals.NormalMap(stretch)!.Value;
        var n = Unit(LinearNormals.Apply(map, new Vec3(1f, 1f, 0f)));
        var expected = Vector3.Normalize(new Vector3(1f, 0.5f, 0f));
        Assert.True(Vector3.Distance(expected, n) < 1e-6f, $"{n}");
        Assert.Equal(0f, map.M41);
        Assert.Equal(0f, map.M42);
        Assert.Equal(0f, map.M43);

        // A turn's normals turn with it.
        var turn = RotateGizmo.About(pivot, Vector3.UnitY, 0.7f);
        var turnMap = LinearNormals.NormalMap(turn)!.Value;
        var v = new Vector3(0.2f, 0.5f, -0.8f);
        Assert.True(Vector3.Distance(Vector3.TransformNormal(v, turn), Vector3.TransformNormal(v, turnMap)) < 1e-5f);
    }

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-20")]
    public void SingularMatrixHasNoNormalMap()
        => Assert.Null(LinearNormals.NormalMap(Matrix4x4.CreateScale(1f, 0f, 1f)));

    [Fact]
    [Trait("Plan", "EDGE-axis-scale-23")]
    public void IsUniformTellsEvenScaleFromStretch()
    {
        var pivot = new Vector3(0.3f, -0.2f, 0.1f);
        Assert.True(LinearNormals.IsUniform(PartScaleDrag.Build(pivot, new Vector3(2f))));
        Assert.True(LinearNormals.IsUniform(Matrix4x4.CreateTranslation(1f, 2f, 3f)));
        Assert.False(LinearNormals.IsUniform(PartScaleDrag.Build(pivot, new Vector3(1f, 2f, 1f))));
        Assert.False(LinearNormals.IsUniform(RotateGizmo.About(pivot, Vector3.UnitY, 0.3f)));
        var shear = Matrix4x4.Identity;
        shear.M21 = 0.5f;
        Assert.False(LinearNormals.IsUniform(shear));
    }

    [Fact]
    [Trait("Plan", "AC-axis-scale-7")]
    public void TurnIsIdentityForEqualFaces()
    {
        var face = new Vec3(0.12f, 0.98f, -0.3f);
        var v = new Vec3(0.3f, -0.4f, 0.7f);
        Assert.Equal(v, LinearNormals.Turn(face, face, v));
    }
}
