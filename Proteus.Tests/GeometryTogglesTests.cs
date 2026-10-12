using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Proteus.Gui;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Colors window's Geometry ticks as data (<see cref="GeometryToggles"/>) and the service's reading of them:
/// what a click writes into the sidecar, and what strength each shell is built with.
/// </summary>
public class GeometryTogglesTests
{
    /// <summary>
    /// Every geometry property <see cref="RenderModeInference.WantsGeometry"/> reads, spelled out here rather than
    /// derived from the table: a tick added to the table but not to the promotion would otherwise pass.
    /// </summary>
    private static readonly string[] GeometryProperties =
        ["BustBridge", "SmoothNipples", "NavelBridge", "CleftBridge", "SmoothFold"];

    [Fact]
    [Trait("Plan", "AC-navel-bridge-1")]
    public void Ticks_are_drawn_in_body_order()
        => Assert.Equal(["bustbridge", "smoothnipples", "navelbridge", "cleftbridge", "smoothfold"],
                        GeometryToggles.All.Select(t => t.Id));

    [Fact]
    public void Every_tick_has_a_label_and_a_tooltip()
    {
        var cs = new Proteus.Localization.ColorsStrings();
        foreach (var t in GeometryToggles.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Label(cs)), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Tip(cs)), t.Id);
        }
    }

    /// <summary>Each tick alone promotes the mod to a shell, and each property the promotion reads has a tick.</summary>
    [Fact]
    [Trait("Plan", "AC-navel-bridge-3")]
    public void Every_tick_alone_wants_geometry()
    {
        foreach (var t in GeometryToggles.All)
        {
            var md = new ProteusMetadata();
            t.Set(md, true);
            Assert.True(t.IsOn(md), t.Id);
            Assert.True(RenderModeInference.WantsGeometry(md), $"{t.Id} alone must promote to a shell");
        }
        foreach (var name in GeometryProperties)
        {
            var md = new ProteusMetadata();
            typeof(ProteusMetadata).GetProperty(name)!.SetValue(md, true);
            Assert.Single(GeometryToggles.All, t => t.IsOn(md));
        }
    }

    [Fact]
    public void Ticking_writes_on_and_clears_the_authored_strength()
    {
        var md = new ProteusMetadata { CleftBridgeStrength = 0.3f };
        GeometryToggles.All.Single(t => t.Id == "cleftbridge").Set(md, true);
        Assert.True(md.CleftBridge);
        Assert.Null(md.CleftBridgeStrength);
    }

    [Fact]
    public void Unticking_removes_the_key_rather_than_writing_false()
    {
        var md = new ProteusMetadata { CleftBridge = true, CleftBridgeStrength = 0.3f };
        GeometryToggles.All.Single(t => t.Id == "cleftbridge").Set(md, false);
        Assert.Null(md.CleftBridge);
        var json = JsonSerializer.Serialize(md, ProteusJson.MetadataWrite);
        Assert.DoesNotContain("CleftBridge", json);
    }

    [Theory]
    [Trait("Plan", "EDGE-navel-bridge-13")]
    [InlineData(true, null, 1f)]
    [InlineData(true, 0.4f, 0.4f)]
    [InlineData(true, 0f, 0f)]
    [InlineData(true, -1f, 0f)]
    [InlineData(true, 2f, 1f)]
    [InlineData(null, 1f, 0f)]
    [InlineData(false, 1f, 0f)]
    public void GeometryStrength_reads_the_tick_and_clamps(bool? on, float? strength, float want)
        => Assert.Equal(want, SecondSkinService.GeometryStrength(on, strength));

    [Fact]
    [Trait("Plan", "AC-navel-bridge-6")]
    [Trait("Plan", "EDGE-navel-bridge-16")]
    public void SurfaceStrength_is_for_body_surfaces_only()
    {
        var byMod = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["Mod A"] = 0.7f };
        Assert.Equal(0.7f, SecondSkinService.SurfaceStrength(true, byMod, "Mod A"));
        Assert.Equal(0f, SecondSkinService.SurfaceStrength(false, byMod, "Mod A"));
        Assert.Equal(0f, SecondSkinService.SurfaceStrength(true, byMod, "Mod B"));
    }

    /// <summary>
    /// The layer copies a toe cap needs carry every strength the layer has, found by name so a new pass cannot be
    /// left out: one copy that forgot the crotch span turned it off for every capped layer.
    /// </summary>
    [Fact]
    public void A_toe_cap_copy_of_a_layer_keeps_every_strength()
    {
        var strengths = typeof(SecondSkinLayer).GetProperties()
            .Where(p => p.Name.EndsWith("Strength", StringComparison.Ordinal) && p.PropertyType == typeof(float)).ToList();
        Assert.Contains(strengths, p => p.Name == "NavelBridgeStrength");
        Assert.Contains(strengths, p => p.Name == "FoldSmoothStrength");

        var def = new SecondSkinLayer { MaterialName = "/mt_a.mtrl" };
        for (int i = 0; i < strengths.Count; i++)
            typeof(SecondSkinLayer).GetProperty(strengths[i].Name)!.SetValue(def, 0.1f + i * 0.1f);
        var copy = SecondSkinWriter.WithCoverageAndCap(def, null, null, 0, 0);
        foreach (var p in strengths) Assert.Equal(p.GetValue(def), p.GetValue(copy));
    }

    [Fact]
    [Trait("Plan", "EDGE-navel-bridge-15")]
    public void SurfaceStrength_is_per_mod_whatever_the_case()
    {
        var byMod = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["Mod A"] = 0.7f, ["Mod B"] = 0.2f };
        Assert.Equal(0.7f, SecondSkinService.SurfaceStrength(true, byMod, "mod a"));
        Assert.Equal(0.2f, SecondSkinService.SurfaceStrength(true, byMod, "MOD B"));
    }
}
