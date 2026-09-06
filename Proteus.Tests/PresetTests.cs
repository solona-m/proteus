using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for the preset pieces that need no Dalamud and no game: the model's deep clone, the store
/// round-trip, the share-code / file codec, the override bag's editing rules, and the compositor's
/// per-mod precedence merge.
/// </summary>
public class PresetTests
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private static ColorTableRowPreset Row(int row, string diffuse) => new()
    {
        Row = row,
        SubRowA = new ColorTableSubRowPreset { Diffuse = diffuse },
    };

    private static ModPreset Sample(string name = "Sheer") => new()
    {
        Name       = name,
        ModName    = "My Stockings",
        ModAuthor  = "Solona",
        Options    = new Dictionary<string, List<string>>
        {
            ["Style"] = ["Roses"],
            ["Legs"]  = ["Left", "Right"],
        },
        Colors = new OverlayColorOverride
        {
            Top     = [Row(1, "#FFFFFF")],
            Mask    = [Row(2, "#101010")],
            Options = new Dictionary<string, Dictionary<string, List<ColorTableRowPreset>>>
            {
                ["Style"] = new() { ["Roses"] = [Row(16, "#FF00FF")] },
            },
        },
        Gear = new OverlayGearOverride
        {
            Top     = new GearSettingsPreset { Layer = OverlayLayer.Gear, Shader = "character.shpk" },
            Content = new GearSettingsPreset { Scroll = "starfield", ScrollSpeedX = 0.01f },
            Options = new Dictionary<string, Dictionary<string, GearSettingsPreset>>
            {
                ["Style"] = new() { ["Roses"] = new GearSettingsPreset { SkinToneMask = 0.4f } },
            },
        },
        StackOrder = ["Style\0Roses", "Legs\0Left"],
    };

    // ── Model ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Clone_IsDeep_SoEditingTheLiveLookNeverWritesIntoTheSavedPreset()
    {
        var saved = Sample();
        var live  = saved.Clone();

        live.Options["Style"][0]                    = "Stripes";
        live.Colors.Top![0].SubRowA!.Diffuse        = "#000000";
        live.Colors.Options!["Style"]["Roses"][0].Row = 5;
        live.Colors.Mask![0].SubRowA!.Diffuse       = "#ABCDEF";
        live.Gear.Top!.Shader                       = "skin.shpk";
        live.Gear.Options!["Style"]["Roses"].SkinToneMask = 1f;
        live.Gear.Content!.ScrollSpeedX             = 9f;
        live.StackOrder[0]                          = "changed";

        Assert.Equal("Roses",           saved.Options["Style"][0]);
        Assert.Equal("#FFFFFF",         saved.Colors.Top![0].SubRowA!.Diffuse);
        Assert.Equal(16,                saved.Colors.Options!["Style"]["Roses"][0].Row);
        Assert.Equal("#101010",         saved.Colors.Mask![0].SubRowA!.Diffuse);
        Assert.Equal("character.shpk",  saved.Gear.Top!.Shader);
        Assert.Equal(0.4f,              saved.Gear.Options!["Style"]["Roses"].SkinToneMask);
        Assert.Equal(0.01f,             saved.Gear.Content!.ScrollSpeedX);
        Assert.Equal("Style\0Roses",    saved.StackOrder[0]);
    }

    [Fact]
    public void Store_RoundTrips_ThroughJson()
    {
        var store = new ModPresetStore
        {
            Presets = new Dictionary<string, List<ModPreset>>(StringComparer.OrdinalIgnoreCase)
            {
                ["my-stockings"] = [Sample(), Sample("Full coverage")],
            },
            Applied = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            {
                ["my-stockings"] = Guid.NewGuid(),
            },
        };

        var back = JsonSerializer.Deserialize<ModPresetStore>(
            JsonSerializer.Serialize(store, JsonOpts), JsonOpts)!;

        Assert.Equal(ModPresetStore.CurrentVersion, back.Version);
        Assert.Equal(2, back.Presets["my-stockings"].Count);
        Assert.Equal(store.Applied["my-stockings"], back.Applied["my-stockings"]);

        var p = back.Presets["my-stockings"][0];
        Assert.Equal("Sheer", p.Name);
        Assert.Equal(["Left", "Right"], p.Options["Legs"]);
        Assert.Equal("#FF00FF", p.Colors.Options!["Style"]["Roses"][0].SubRowA!.Diffuse);
        Assert.Equal(0.4f, p.Gear.Options!["Style"]["Roses"].SkinToneMask);
        Assert.Equal("starfield", p.Gear.Content!.Scroll);
        Assert.Equal(["Style\0Roses", "Legs\0Left"], p.StackOrder);
    }

    [Fact]
    public void Store_LooksUpAMod_CaseInsensitively_LikeEveryOtherModDirectoryKey()
    {
        var store = new ModPresetStore
        {
            Presets = new Dictionary<string, List<ModPreset>>(StringComparer.OrdinalIgnoreCase)
            {
                ["My-Stockings"] = [Sample()],
            },
        };

        // Deserialization hands back an ordinal dictionary whatever the property initializer said, so a
        // load that skipped Normalized() would lose every preset for a mod looked up under other casing.
        var raw = JsonSerializer.Deserialize<ModPresetStore>(
            JsonSerializer.Serialize(store, JsonOpts), JsonOpts)!;
        Assert.False(raw.Presets.ContainsKey("my-stockings"));

        Assert.True(raw.Normalized().Presets.ContainsKey("my-stockings"));
    }

    [Fact]
    public void Store_NormalizesTheAppliedPinsToo()
    {
        var store = new ModPresetStore
        {
            Applied = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase) { ["My-Mod"] = Guid.NewGuid() },
        };

        var back = JsonSerializer.Deserialize<ModPresetStore>(
            JsonSerializer.Serialize(store, JsonOpts), JsonOpts)!.Normalized();

        Assert.True(back.Applied.ContainsKey("my-mod"));
    }

    [Fact]
    public void Store_MintsAnIdForAHandWrittenPreset_SoTwoOfThemAreNotTheSamePin()
    {
        // Two presets typed into presets.json by hand, neither with an id. Left empty they would be one
        // preset as far as pinning is concerned — pin either and both light up.
        var raw = JsonSerializer.Deserialize<ModPresetStore>(
            """{"Version":1,"Presets":{"my-mod":[{"Name":"One"},{"Name":"Two"}]}}""", JsonOpts)!.Normalized();

        var ids = raw.Presets["my-mod"].Select(p => p.Id).ToList();
        Assert.DoesNotContain(Guid.Empty, ids);
        Assert.Equal(2, ids.Distinct().Count());
    }

    // ── Pack preset identity ────────────────────────────────────────────────────

    private const string PackJson =
        """{"FormatVersion":1,"Name":"My Stockings","Presets":[{"Name":"Sheer"},{"Name":"Opaque"}]}""";

    [Fact]
    public void PackPresetId_IsTheSame_EveryTimeTheMetadataIsRead()
    {
        // The pin is written from one read of metadata.json and looked up against the next one — a
        // recomposite re-reads the sidecar within a frame of applying. An id minted per deserialization
        // (which a `= Guid.NewGuid()` initializer does) dangled the pin instantly, and the picker fell
        // back to "No preset" over a look that had applied perfectly well.
        var first  = JsonSerializer.Deserialize<ProteusMetadata>(PackJson, JsonOpts)!.Presets!;
        var second = JsonSerializer.Deserialize<ProteusMetadata>(PackJson, JsonOpts)!.Presets!;

        Assert.Equal(PresetService.PackId("my-stockings", first[0]),
                     PresetService.PackId("my-stockings", second[0]));
        Assert.NotEqual(Guid.Empty, PresetService.PackId("my-stockings", first[0]));
    }

    [Fact]
    public void PackPresetId_IsPerModAndPerName_SoTwoPacksSheerNeverCollide()
    {
        var presets = JsonSerializer.Deserialize<ProteusMetadata>(PackJson, JsonOpts)!.Presets!;

        Assert.NotEqual(PresetService.PackId("my-stockings", presets[0]),
                        PresetService.PackId("my-stockings", presets[1]));
        Assert.NotEqual(PresetService.PackId("my-stockings",    presets[0]),
                        PresetService.PackId("other-stockings", presets[0]));
    }

    [Fact]
    public void PackPresetId_HonoursAnIdTheAuthorDidWrite()
    {
        var authored = Guid.NewGuid();
        var presets  = JsonSerializer.Deserialize<ProteusMetadata>(
            $$"""{"FormatVersion":1,"Presets":[{"Id":"{{authored}}","Name":"Sheer"}]}""", JsonOpts)!.Presets!;

        // An authored id survives a rename, which is the whole reason to allow one.
        Assert.Equal(authored, PresetService.PackId("my-stockings", presets[0]));
    }

    [Fact]
    public void SameLook_IgnoresNameAndTimestamps_ButNotTheCapturedFields()
    {
        var a = Sample();
        var b = a.Clone();
        b.Name        = "Something else";
        b.Id          = Guid.NewGuid();
        b.LastEditUtc = DateTime.UtcNow.AddDays(1);
        Assert.True(PresetService.SameLook(a, b));

        b.Colors.Top![0].SubRowA!.Diffuse = "#000000";
        Assert.False(PresetService.SameLook(a, b));
    }

    [Fact]
    public void SameLook_IsNotFooledByOptionOrdering()
    {
        var a = Sample();
        var b = a.Clone();
        b.Options["Legs"] = ["Right", "Left"];   // same ticks, other order
        Assert.True(PresetService.SameLook(a, b));

        b.Options["Legs"] = ["Right"];           // genuinely different
        Assert.False(PresetService.SameLook(a, b));
    }

    [Fact]
    public void SameLook_IgnoresAGroupTheModGainedAfterThePresetWasSaved()
    {
        var saved = Sample();
        var live  = saved.Clone();
        live.Options["Sleeves"] = ["Long"];      // the author shipped a new group

        // Otherwise every preset in the pack sprouts a ● the moment its author updates the mod, and the
        // only way to clear it is an Update that bakes in whatever that new group happens to be set to.
        Assert.True(PresetService.SameLook(saved, live));
    }

    [Fact]
    public void SameLook_IgnoresAGroupTheModHasSinceDropped()
    {
        var saved = Sample();
        var live  = saved.Clone();
        live.Options.Remove("Legs");             // the group is gone from the mod

        // Nothing the wearer can act on: Update would just quietly drop it from the preset, losing the
        // selection for anyone whose copy of the mod still has the group.
        Assert.True(PresetService.SameLook(saved, live));
    }

    [Fact]
    public void SameLook_StillCatchesAnEditToAGroupThePresetNames()
    {
        var saved = Sample();
        var live  = saved.Clone();
        live.Options["Style"] = ["Stripes"];

        Assert.False(PresetService.SameLook(saved, live));
    }

    [Fact]
    public void SameLook_CatchesAnUntickInAMultiSelectGroup()
    {
        var saved = Sample();
        var live  = saved.Clone();
        live.Options["Legs"] = ["Left"];         // "Right" unticked

        Assert.False(PresetService.SameLook(saved, live));
    }

    // ── The shape a mod author writes ───────────────────────────────────────────

    /// <summary>
    /// The exact JSON shape documented in "For Creators.md". If this stops deserializing, the guide is
    /// telling authors to write something the plugin ignores — which is silent, and which they would
    /// diagnose as "presets don't work".
    /// </summary>
    [Fact]
    public void PackPresets_ParseFromTheMetadataShapeTheCreatorGuideDocuments()
    {
        const string json = """
        {
          "FormatVersion": 1,
          "Name": "My Stockings",
          "Presets": [
            {
              "Name": "Sheer",
              "Description": "Barely-there, for a lighter skin tone.",
              "Options": { "Style": ["Roses"], "Welt": ["None"] },
              "Colors": {
                "Options": {
                  "Style": { "Roses": [ { "Row": 16, "SubRowA": { "Diffuse": "#F3E2DA", "Opacity": -45 } } ] }
                }
              }
            },
            {
              "Name": "Full coverage",
              "Options": { "Style": ["Roses"], "Welt": ["Wide"] }
            }
          ]
        }
        """;

        var meta = JsonSerializer.Deserialize<ProteusMetadata>(json, JsonOpts)!;

        Assert.Equal(2, meta.Presets!.Count);

        var sheer = meta.Presets[0];
        Assert.Equal("Sheer", sheer.Name);
        Assert.Equal(["Roses"], sheer.Options["Style"]);
        Assert.Equal(["None"], sheer.Options["Welt"]);
        Assert.Equal("#F3E2DA", sheer.Colors.Options!["Style"]["Roses"][0].SubRowA!.Diffuse);
        // Opacity is an ADJUSTMENT in −100…100, not a 0–1 alpha. Asserted with its real type because a
        // fractional value here does not merely round — it fails the whole parse, taking every other
        // preset in the file with it.
        Assert.Equal(-45, sheer.Colors.Options!["Style"]["Roses"][0].SubRowA!.Opacity);

        // A preset that only sets Options is valid and is the easiest kind to hand-write — the guide
        // says so, so the omitted Colors/Gear/StackOrder must arrive as usable empties, not nulls.
        var full = meta.Presets[1];
        Assert.NotNull(full.Colors);
        Assert.NotNull(full.Gear);
        Assert.Empty(full.StackOrder);
    }

    [Fact]
    public void PackPresets_AreAbsentRatherThanEmptyOnAModThatShipsNone()
    {
        var meta = JsonSerializer.Deserialize<ProteusMetadata>(
            """{ "FormatVersion": 1, "Name": "Plain" }""", JsonOpts)!;

        Assert.Null(meta.Presets);
    }

    // ── Codec ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ShareCode_RoundTrips()
    {
        var original = Sample();
        var decoded  = PresetCodec.FromShareCode(PresetCodec.ToShareCode(original));

        Assert.Null(decoded.Error);
        var p = decoded.Preset!;
        Assert.Equal("Sheer", p.Name);
        Assert.Equal("My Stockings", p.ModName);
        Assert.Equal("Solona", p.ModAuthor);
        Assert.True(PresetService.SameLook(original, p));
    }

    [Fact]
    public void ShareCode_MintsAFreshId_SoASharedPresetCannotCollideWithALocalOne()
    {
        var original = Sample();
        var decoded  = PresetCodec.FromShareCode(PresetCodec.ToShareCode(original)).Preset!;

        Assert.NotEqual(Guid.Empty, decoded.Id);
        Assert.NotEqual(original.Id, decoded.Id);
    }

    [Fact]
    public void ShareCode_SurvivesTheNewlinesAChatClientAdds()
    {
        var code    = PresetCodec.ToShareCode(Sample());
        var wrapped = string.Join("\n", Chunk(code, 40)) + "  ";

        Assert.Null(PresetCodec.FromShareCode(wrapped).Error);
    }

    private static IEnumerable<string> Chunk(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size)
            yield return s.Substring(i, Math.Min(size, s.Length - i));
    }

    [Fact]
    public void ShareCode_RejectsAFutureVersion_WithAMessageRatherThanAThrow()
    {
        var raw = Convert.FromBase64String(PresetCodec.ToShareCode(Sample()));
        raw[0] = 99;
        var result = PresetCodec.FromShareCode(Convert.ToBase64String(raw));

        Assert.Null(result.Preset);
        Assert.Contains("99", result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all !!!")]
    [InlineData("AAAA")]                       // valid base64, right version byte, garbage payload
    public void ShareCode_RejectsRubbish_WithAMessageRatherThanAThrow(string code)
    {
        var result = PresetCodec.FromShareCode(code);
        Assert.Null(result.Preset);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void ShareCode_RejectsAWellFormedButEmptyPreset()
    {
        var empty  = new ModPreset { Name = "Nothing" };
        var result = PresetCodec.FromShareCode(PresetCodec.ToShareCode(empty));

        Assert.Null(result.Preset);
        Assert.Contains("empty", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void File_RoundTrips_AndSuggestsASafeName()
    {
        var original = Sample("Sheer / dark");
        var path     = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            Guid.NewGuid().ToString("N") + PresetCodec.FileExtension);

        try
        {
            PresetCodec.ToFile(original, path);
            var decoded = PresetCodec.FromFile(path);

            Assert.Null(decoded.Error);
            Assert.True(PresetService.SameLook(original, decoded.Preset!));
            Assert.DoesNotContain('/', PresetCodec.SuggestedFileName(original));
            Assert.EndsWith(PresetCodec.FileExtension, PresetCodec.SuggestedFileName(original));
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void File_RejectsSomethingThatIsNotAPreset()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            Guid.NewGuid().ToString("N") + PresetCodec.FileExtension);
        System.IO.File.WriteAllText(path, "{ \"hello\": true }");

        try
        {
            var result = PresetCodec.FromFile(path);
            Assert.Null(result.Preset);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ── Applying against a mod that has moved on ────────────────────────────────

    private static Dictionary<string, HashSet<string>> Catalogue(params (string Group, string[] Options)[] groups)
        => groups.ToDictionary(g => g.Group, g => g.Options.ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, List<string>> Wanted(params (string Group, string[] Options)[] groups)
        => groups.ToDictionary(g => g.Group, g => g.Options.ToList());

    [Fact]
    public void Apply_WritesEverythingWhenTheModStillHasItAll()
    {
        var plan = PresetService.PlanOptionWrites(
            Wanted(("Style", ["Roses"]), ("Legs", ["Left", "Right"])),
            Catalogue(("Style", ["Roses", "Stripes"]), ("Legs", ["Left", "Right"])));

        Assert.Equal(2, plan.Writes.Count);
        Assert.Empty(plan.MissingGroups);
        Assert.Empty(plan.MissingOptions);
    }

    [Fact]
    public void Apply_SkipsARenamedGroupAndStillWritesTheRest()
    {
        var plan = PresetService.PlanOptionWrites(
            Wanted(("Style", ["Roses"]), ("Legs", ["Left"])),
            Catalogue(("Style", ["Roses"])));          // "Legs" was renamed away

        Assert.Equal(["Legs"], plan.MissingGroups);
        Assert.Single(plan.Writes);
        Assert.Equal("Style", plan.Writes[0].Group);
    }

    [Fact]
    public void Apply_DropsAMissingOptionButKeepsItsSiblings()
    {
        var plan = PresetService.PlanOptionWrites(
            Wanted(("Legs", ["Left", "Right"])),
            Catalogue(("Legs", ["Left"])));            // "Right" is gone

        Assert.Equal([("Legs", "Right")], plan.MissingOptions);
        Assert.Equal(["Left"], plan.Writes[0].Selection);
    }

    [Fact]
    public void Apply_LeavesAGroupAloneRatherThanClearingItWhenEveryOptionIsGone()
    {
        var plan = PresetService.PlanOptionWrites(
            Wanted(("Legs", ["Left", "Right"])),
            Catalogue(("Legs", ["Something else"])));

        // Writing an empty selection would CLEAR the group — "wear none of these" — which the preset
        // never asked for. Nothing is written, and both losses are reported.
        Assert.Empty(plan.Writes);
        Assert.Equal(2, plan.MissingOptions.Count);
    }

    [Fact]
    public void Apply_StillClearsAGroupThePresetDeliberatelySavedAsEmpty()
    {
        var plan = PresetService.PlanOptionWrites(
            Wanted(("Legs", [])),
            Catalogue(("Legs", ["Left", "Right"])));

        Assert.Single(plan.Writes);
        Assert.Empty(plan.Writes[0].Selection);
        Assert.Empty(plan.MissingOptions);
    }

    [Fact]
    public void Apply_WritesVerbatimWhenTheManifestCannotBeRead()
    {
        // Null catalogue means "we know nothing", not "the mod has no groups" — reporting every group as
        // missing on an unreadable manifest would be a lie, and would refuse a perfectly good preset.
        var plan = PresetService.PlanOptionWrites(Wanted(("Style", ["Roses"]), ("Legs", ["Left"])), null);

        Assert.Equal(2, plan.Writes.Count);
        Assert.Empty(plan.MissingGroups);
        Assert.Empty(plan.MissingOptions);
    }

    [Fact]
    public void Apply_MatchesGroupAndOptionNamesCaseInsensitively_AsPenumbraDoes()
    {
        var plan = PresetService.PlanOptionWrites(
            Wanted(("style", ["roses"])),
            Catalogue(("Style", ["Roses"])));

        Assert.Single(plan.Writes);
        Assert.Empty(plan.MissingGroups);
        Assert.Empty(plan.MissingOptions);
    }

    // ── Naming ──────────────────────────────────────────────────────────────────

    [Fact]
    public void UniqueName_DeduplicatesRatherThanRefusingTheSave()
    {
        Assert.Equal("Sheer", PresetService.UniqueName(["Full"], "Sheer"));
        Assert.Equal("Sheer (2)", PresetService.UniqueName(["Sheer"], "Sheer"));
        Assert.Equal("Sheer (3)", PresetService.UniqueName(["Sheer", "Sheer (2)"], "Sheer"));
        Assert.Equal("Sheer (2)", PresetService.UniqueName(["SHEER"], "Sheer"));
    }

    // ── Precedence merge ────────────────────────────────────────────────────────

    [Fact]
    public void MergeByMod_LetsThePresetWinForItsOwnModAndLeavesEveryOtherModToTheBinding()
    {
        var design = new Dictionary<string, string> { ["a"] = "design-a", ["b"] = "design-b" };
        var preset = new Dictionary<string, string> { ["a"] = "preset-a" };

        var merged = CompositorService.MergeByMod<string>(preset, design)!;

        Assert.Equal("preset-a", merged["a"]);
        Assert.Equal("design-b", merged["b"]);
    }

    [Fact]
    public void MergeByMod_MatchesModDirectoriesCaseInsensitively()
    {
        var design = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["My-Mod"] = "design" };
        var preset = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["my-mod"] = "preset" };

        var merged = CompositorService.MergeByMod<string>(preset, design)!;

        Assert.Single(merged);
        Assert.Equal("preset", merged["MY-MOD"]);
    }

    [Fact]
    public void MergeByMod_ReturnsTheOtherSideUntouchedWhenOneIsEmpty()
    {
        var design = new Dictionary<string, string> { ["a"] = "design-a" };

        Assert.Same(design, CompositorService.MergeByMod<string>(null, design));
        Assert.Same(design, CompositorService.MergeByMod<string>(new Dictionary<string, string>(), design));
        Assert.Same(design, CompositorService.MergeByMod<string>(design, null));
        Assert.Null(CompositorService.MergeByMod<string>(null, null));
    }

    // ── Override bag ────────────────────────────────────────────────────────────

    private static OverlayOverrideBag BagGoverning(string modDir)
    {
        var bag = OverlayOverrideBag.Detached();
        bag.SetMod(modDir, new OverlayColorOverride(), new OverlayGearOverride(), null);
        return bag;
    }

    [Fact]
    public void Bag_PeekNeverCreates_SoLookingAtALookCannotChangeIt()
    {
        var bag = BagGoverning("mod");

        Assert.Null(bag.PeekRows("mod", "Style", "Roses"));
        Assert.Null(bag.PeekMaskRows("mod"));
        Assert.Null(bag.PeekGear("mod", "Style", "Roses"));

        // Still nothing stored: a peek must leave the override exactly as it found it.
        Assert.Null(bag.Colors!["mod"].Options);
        Assert.Null(bag.Colors!["mod"].Mask);
        Assert.Null(bag.Gear!["mod"].Options);
        Assert.Null(bag.Gear!["mod"].Top);
    }

    [Fact]
    public void Bag_SetThenPeek_RoundTripsEveryScope()
    {
        var bag = BagGoverning("mod");

        Assert.True(bag.SetRows("mod", null, null, [Row(1, "#111111")]));
        Assert.True(bag.SetRows("mod", "Style", "Roses", [Row(2, "#222222")]));
        Assert.True(bag.SetMaskRows("mod", [Row(3, "#333333")]));

        Assert.Equal("#111111", bag.PeekRows("mod", null, null)![0].SubRowA!.Diffuse);
        Assert.Equal("#222222", bag.PeekRows("mod", "Style", "Roses")![0].SubRowA!.Diffuse);
        Assert.Equal("#333333", bag.PeekMaskRows("mod")![0].SubRowA!.Diffuse);
    }

    [Fact]
    public void Bag_RefusesAModItDoesNotGovern_SoTheCallerWritesMetadataInstead()
    {
        var bag = BagGoverning("mine");

        Assert.False(bag.Governs("other"));
        Assert.False(bag.SetRows("other", null, null, [Row(1, "#111111")]));
        Assert.False(bag.SetMaskRows("other", [Row(1, "#111111")]));
        Assert.Null(bag.PeekRows("other", null, null));
        Assert.Null(bag.GetEditableGear("other", null, null, new OverlayDescriptor()));
    }

    [Fact]
    public void Bag_GetEditableGear_SeedsFromTheDescriptorAndThenKeepsTheSameInstance()
    {
        var bag  = BagGoverning("mod");
        var seed = new OverlayDescriptor { Layer = OverlayLayer.Gear, Shader = "character.shpk" };

        var first = bag.GetEditableGear("mod", "Style", "Roses", seed)!;
        Assert.Equal("character.shpk", first.Shader);

        first.Shader = "skin.shpk";
        var second = bag.GetEditableGear("mod", "Style", "Roses", seed)!;

        Assert.Same(first, second);
        Assert.Equal("skin.shpk", second.Shader);   // re-seeding would have thrown the edit away
    }

    [Fact]
    public void Bag_ContentGearClonesItsSeed_SoTheSidecarsOwnSettingsAreNeverWrittenBackTo()
    {
        var bag  = BagGoverning("mod");
        var seed = new GearSettingsPreset { Scroll = "starfield", ScrollSpeedX = 0.01f };

        var editable = bag.GetEditableContentGear("mod", null, null, seed)!;
        editable.ScrollSpeedX = 5f;

        Assert.NotSame(seed, editable);
        Assert.Equal(0.01f, seed.ScrollSpeedX);
    }

    [Fact]
    public void Bag_ContentGearUsesItsOwnSlot_NotTop()
    {
        var bag = BagGoverning("mod");
        bag.GetEditableContentGear("mod", null, null, new GearSettingsPreset { Scroll = "starfield" });

        Assert.Null(bag.Gear!["mod"].Top);
        Assert.Equal("starfield", bag.Gear!["mod"].Content!.Scroll);
    }

    [Fact]
    public void Bag_SetMod_AddsAndRemovesWithoutMutatingThePublishedDictionary()
    {
        var bag = OverlayOverrideBag.Detached();
        bag.SetMod("a", new OverlayColorOverride(), new OverlayGearOverride(), null);
        var published = bag.Colors!;

        bag.SetMod("b", new OverlayColorOverride(), new OverlayGearOverride(), null);

        // Copy-on-write: the dictionary the compositor already snapshotted must not gain a key behind it.
        Assert.Single(published);
        Assert.Equal(2, bag.Colors!.Count);
    }

    [Fact]
    public void Bag_RemovingTheLastMod_GoesFullyNull_SoTheCompositorKeepsItsCheapPath()
    {
        var bag = BagGoverning("mod");
        Assert.True(bag.Active);

        Assert.True(bag.RemoveMod("mod"));
        Assert.False(bag.Active);
        Assert.Null(bag.Colors);
        Assert.Null(bag.Gear);
        Assert.Null(bag.Stack);

        Assert.False(bag.RemoveMod("mod"));   // idempotent
    }

    [Fact]
    public void Bag_StackOrder_IsStoredOnlyWhenThePresetActuallyRestacked()
    {
        var bag = OverlayOverrideBag.Detached();

        bag.SetMod("mod", new OverlayColorOverride(), new OverlayGearOverride(), null);
        Assert.Null(bag.StackOrderFor("mod"));   // null ⇒ fall back to the global config order

        bag.SetMod("mod", new OverlayColorOverride(), new OverlayGearOverride(), ["Style\0Roses"]);
        Assert.Equal(["Style\0Roses"], bag.StackOrderFor("mod"));

        bag.SetMod("mod", new OverlayColorOverride(), new OverlayGearOverride(), []);
        Assert.Null(bag.StackOrderFor("mod"));   // an empty capture is "nothing to say", not "no overlays"
    }

    [Fact]
    public void Bag_SetStackOrder_WritesTheSameKeysTheConfigUses()
    {
        var bag = BagGoverning("mod");

        Assert.True(bag.SetStackOrder("mod", [("Style", "Roses"), ("Legs", "Left")]));
        Assert.Equal(
            [Configuration.ModStackEntry("Style", "Roses"), Configuration.ModStackEntry("Legs", "Left")],
            bag.StackOrderFor("mod"));
    }

    [Fact]
    public void Bag_ClearOption_DropsOneOptionAndLeavesTheRest()
    {
        var bag = BagGoverning("mod");
        bag.SetRows("mod", "Style", "Roses",   [Row(1, "#111111")]);
        bag.SetRows("mod", "Style", "Stripes", [Row(2, "#222222")]);
        bag.GetEditableGear("mod", "Style", "Roses", new OverlayDescriptor { Shader = "character.shpk" });

        Assert.True(bag.ClearOption("mod", "Style", "Roses"));

        Assert.Null(bag.PeekRows("mod", "Style", "Roses"));
        Assert.Equal("#222222", bag.PeekRows("mod", "Style", "Stripes")![0].SubRowA!.Diffuse);
        Assert.False(bag.ClearOption("mod", "Style", "Roses"));   // nothing left to clear
    }

    [Fact]
    public void Bag_ClearOption_WithNoScope_ClearsTopAndContentTogether()
    {
        var bag = BagGoverning("mod");
        bag.SetRows("mod", null, null, [Row(1, "#111111")]);
        bag.GetEditableGear("mod", null, null, new OverlayDescriptor { Shader = "character.shpk" });
        bag.GetEditableContentGear("mod", null, null, new GearSettingsPreset { Scroll = "starfield" });

        Assert.True(bag.ClearOption("mod", null, null));

        Assert.Null(bag.PeekRows("mod", null, null));
        Assert.Null(bag.Gear!["mod"].Top);
        Assert.Null(bag.Gear!["mod"].Content);   // a reset that left the glow behind would be a lie
    }

    [Fact]
    public void Bag_ClearOption_RefusesAHalfSpecifiedScopeRatherThanWipingTop()
    {
        var bag = BagGoverning("mod");
        bag.SetRows("mod", null, null, [Row(1, "#111111")]);

        Assert.False(bag.ClearOption("mod", "Style", null));
        Assert.False(bag.ClearOption("mod", null, "Roses"));
        Assert.NotNull(bag.PeekRows("mod", null, null));
    }

    [Fact]
    public void Bag_Adopt_ReplacesAllThreeChannelsTogether()
    {
        var bag = BagGoverning("mod");
        bag.GetEditableGear("mod", null, null, new OverlayDescriptor { Shader = "character.shpk" });

        // Colours and gear must move as one. Adopting colours while leaving gear behind is what made
        // "Update binding" store a preset's glow and then stop showing it.
        bag.Adopt(
            new Dictionary<string, OverlayColorOverride>(StringComparer.OrdinalIgnoreCase) { ["mod"] = new() },
            new Dictionary<string, OverlayGearOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["mod"] = new() { Top = new GearSettingsPreset { Shader = "skin.shpk" } },
            },
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("skin.shpk", bag.PeekGear("mod", "any", "option")!.Shader);
    }
}
