using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Proteus.Gui;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The Studio's rules for "Move skin too", which the part list, the model view and the live character all ask. There
/// is no ImGui under test, so what the user sees is pinned here, where the panel reads it from.
/// </summary>
public class SkinMovePolicyTests
{
    private const string Skin = "/mt_c0201b0001_bibo.mtrl";

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-5")]
    public void SkinMovePolicy_DefaultIsOff() => Assert.False(SkinMovePolicy.DefaultOn);

    /// <summary>Session-only: nothing in the saved settings could carry the checkbox to the next start.</summary>
    [Fact]
    [Trait("Plan", "AC-studio-move-skin-5")]
    public void SkinMoveIsNotPersisted()
    {
        var members = typeof(Configuration).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                           .Select(m => m.Name);
        Assert.DoesNotContain(members, n => n.Contains("MoveSkin") || n.Contains("SkinMove"));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]    // bridge or wind: never
    [Trait("Plan", "AC-studio-move-skin-5")]
    [Trait("Plan", "EDGE-studio-move-skin-9")]
    public void SkinMovePolicy_DoesNotApplyToBridgeOrWind(bool on, bool bridgeOrWind, bool ready, bool applies)
        => Assert.Equal(applies, SkinMovePolicy.Applies(on, bridgeOrWind, ready));

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-5")]
    [Trait("Plan", "EDGE-studio-move-skin-24")]
    public void SkinMovePolicy_WaitsForTheBinding() => Assert.False(SkinMovePolicy.Applies(true, false, followReady: false));

    [Theory]
    [InlineData(false, false, "FixedTicked")]
    [InlineData(true, false, "Lockable")]
    [InlineData(false, true, "MoveDisabled")]
    [InlineData(true, true, "MovePickable")]
    [Trait("Plan", "AC-studio-move-skin-6")]
    public void SkinMovePolicy_SkinRowKinds(bool skinMoves, bool moving, string expected)
        => Assert.Equal(expected, SkinMovePolicy.SkinRow(skinMoves, moving).ToString());

    [Theory]
    [InlineData(false, false, false, true)]   // cloth: always
    [InlineData(true, false, false, false)]   // skin, off: refused, as before
    [InlineData(true, true, false, true)]     // skin under Body size
    [InlineData(true, false, true, true)]     // skin, on
    [Trait("Plan", "AC-studio-move-skin-6")]
    [Trait("Plan", "EDGE-studio-move-skin-22")]
    public void SkinMovePolicy_LockAllowed(bool isSkin, bool retarget, bool skinMoves, bool allowed)
        => Assert.Equal(allowed, SkinMovePolicy.LockAllowed(isSkin, retarget, skinMoves));

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, true, false)]    // locked: never
    [InlineData(true, false, false, false)]   // skin, off
    [InlineData(true, false, true, true)]     // skin, on
    [Trait("Plan", "AC-studio-move-skin-3")]
    [Trait("Plan", "EDGE-studio-move-skin-7")]
    [Trait("Plan", "EDGE-studio-move-skin-22")]
    public void SkinMovePolicy_MovePickable(bool isSkin, bool locked, bool skinMoves, bool pickable)
        => Assert.Equal(pickable, SkinMovePolicy.MovePickable(isSkin, locked, skinMoves));

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [Trait("Plan", "AC-studio-move-skin-6")]
    [Trait("Plan", "EDGE-studio-move-skin-18")]
    public void SkinMovePolicy_HitCounts(bool skinTriangle, bool skinMoves, bool counts)
        => Assert.Equal(counts, SkinMovePolicy.HitCounts(skinTriangle, skinMoves));

    private static ModelPart Part(string label, string material) => new()
    {
        Mesh = 0, Submesh = 0, Island = -1, Label = label, Material = material, Triangles = [0, 1, 2], Ordinals = [0],
        AttributeMask = 0, Toggleable = true, Min = Vector3.Zero, Max = Vector3.One,
    };

    private static ModelParts Model(params string[] materials) => new()
    {
        Positions = new float[9], Normals = new float[9], MeshSpans = [], AttributeNames = [], Min = Vector3.Zero,
        Max = Vector3.One, ShatteredSubmeshes = new Dictionary<string, int>(),
        Parts = [.. materials.Select((m, i) => Part($"1.{i + 1}", m))],
    };

    [Fact]
    [Trait("Plan", "EDGE-studio-move-skin-7")]
    public void SkinMovePolicy_DropSkinLocksRemovesOnlySkinLabels()
    {
        var model = Model(Skin, "/mt_c0201e6255_top_a.mtrl");
        var locks = new HashSet<string> { "1.1", "1.2" };
        Assert.Equal(1, SkinMovePolicy.DropSkinLocks(locks, model.Parts));
        Assert.Equal(["1.2"], locks);
    }

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-11")]
    [Trait("Plan", "EDGE-studio-move-skin-4")]
    public void SkinMovePolicy_IsBodyModel()
    {
        Assert.True(SkinMovePolicy.IsBodyModel(Model(Skin)));
        Assert.True(SkinMovePolicy.IsBodyModel(Model(Skin, "/mt_c0201b0001_nails.mtrl", "/mt_c0201e0000_top_a.mtrl")));
        Assert.False(SkinMovePolicy.IsBodyModel(Model(Skin, "/mt_c0201e6255_top_a.mtrl")));
        Assert.False(SkinMovePolicy.IsBodyModel(Model("/mt_c0201e6255_top_a.mtrl")));
    }

    private static readonly string[] Locales = ["en", "de", "es", "fr", "ja", "ko", "ru", "zh"];

    private static readonly string[] NewKeys =
    [
        "Parts.SkinMoves", "Parts.SkinMoves.Tip", "Parts.SkinMoves.NotThisBrush.Tip", "Parts.SkinMoves.Preparing",
        "Parts.SkinMoves.BodyModel.Warn", "Parts.SkinMoves.Failed", "Parts.Brush.Lock.SkinOff.Tip", "Parts.Move.SkinOff.Tip",
    ];

    private static Dictionary<string, string> Messages(string lang)
    {
        using var s = typeof(Plugin).Assembly.GetManifestResourceStream($"Proteus.Localization.{lang}.json")!;
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("message").GetString()!);
    }

    [Fact]
    [Trait("Plan", "AC-studio-move-skin-10")]
    public void SkinMoveStringsAreTranslated()
    {
        var en = Messages("en");
        foreach (var lang in Locales)
        {
            var messages = Messages(lang);
            foreach (var key in NewKeys)
            {
                Assert.True(messages.ContainsKey(key), $"{lang} has no {key}");
                if (lang != "en") Assert.NotEqual(en[key], messages[key]);
            }
            Assert.False(messages.ContainsKey("Parts.Move.SkinTip"), $"{lang} still has the old move tip");
            Assert.False(messages.ContainsKey("Parts.Brush.Lock.Skin.Tip"), $"{lang} still has the old lock tip");
        }
    }
}
