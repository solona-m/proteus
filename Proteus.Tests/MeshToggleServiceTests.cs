using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The whole write, end to end against a mod folder on disk: the model is edited, the IMC group appears, and
/// revert leaves the folder exactly as it was found.
/// </summary>
public class MeshToggleServiceTests
{
    private const string GamePath = "chara/equipment/e0043/model/c0201e0043_top.mdl";
    private const string ModelRel = "items/top.mdl";

    private static SyntheticModel.Mesh Mesh(params SyntheticModel.Sub[] subs)
        => new("/mt_test.mtrl", subs);

    /// <summary>
    /// A minimal equipment IMC: a header, then one 6-byte entry per declared part, per variant. The Body
    /// entry carries a MaterialId of 3 so the tests can prove it survives the edit — inventing one would
    /// point the item at a different material folder.
    /// </summary>
    private static byte[] Imc(ushort variants = 1, ushort attributeMask = 0x0003, byte materialId = 3)
    {
        const int parts = 5;                       // Head, Body, Hands, Legs, Feet
        var b = new byte[4 + (variants + 1) * parts * 6];
        BitConverter.TryWriteBytes(b.AsSpan(0), variants);
        BitConverter.TryWriteBytes(b.AsSpan(2), (ushort)0x1F);

        for (int set = 0; set <= variants; set++)
        {
            int at = 4 + (set * parts + 1) * 6;    // part 1 = Body
            b[at] = materialId;
            b[at + 1] = 7;                          // DecalId
            BitConverter.TryWriteBytes(b.AsSpan(at + 2), (ushort)(attributeMask | (2 << 10)));   // + SoundId 2
            b[at + 4] = 5;                          // VfxId
            b[at + 5] = 9;                          // MaterialAnimationId
        }
        return b;
    }

    /// <summary>A throwaway mod folder: a v4 manifest publishing one model, and the model itself.</summary>
    private sealed class Mod : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "proteus_parts_" + Path.GetRandomFileName());

        public Mod(byte[] model, string modelRel = ModelRel, string gamePath = GamePath)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "meta.json"),
                "{\"FileVersion\":4,\"Name\":\"Frock\",\"Groups\":[],\"DefaultData\":{\"Files\":{"
                + $"\"{gamePath}\":\"{modelRel}\"" + "}}}");
            var dest = Path.Combine(Root, modelRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, model);
        }

        public byte[] Model(string rel = ModelRel)
            => File.ReadAllBytes(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));

        public JsonElement Group(string name)
            => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "meta.json")))
                .RootElement.GetProperty("Groups").EnumerateArray()
                .Single(g => g.GetProperty("Name").GetString() == name);

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private static List<PenumbraModMeta.Redirect> Redirects(Mod mod)
        => PenumbraModMeta.ReadAllRedirects(mod.Root);

    private static MeshToggleService.Outcome Write(
        Mod mod, ModelParts parts, params MeshToggleService.Plan[] toggles)
    {
        var redirects = Redirects(mod);
        return MeshToggleService.Write(
            mod.Root, redirects.Single(r => r.File == ModelRel), parts, toggles, redirects,
            path => path == "chara/equipment/e0043/e0043.imc" ? Imc() : null);
    }

    // ── the happy path ──────────────────────────────────────────────────────

    [Fact]
    public void Write_TagsTheChosenPart_AndAddsTheGroup()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0))));
        var parts = ModelPartReader.Read(mod.Model())!;

        var result = Write(mod, parts, new MeshToggleService.Plan("Bow", [parts.Parts[1]]));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, result.FilesPatched);

        // The model carries the new attribute, on that submesh only.
        var after = ModelPartReader.Read(mod.Model())!;
        Assert.Equal(["atr_tv_a"], after.AttributeNames);
        Assert.Equal([0u, 1u], after.Parts.Select(p => p.AttributeMask));

        var group = mod.Group(MeshToggleService.GroupNameFor("Body"));
        Assert.Equal("Imc", group.GetProperty("Type").GetString());
        Assert.Equal("Bow", group.GetProperty("Options")[0].GetProperty("Name").GetString());
        Assert.Equal(1, group.GetProperty("Options")[0].GetProperty("AttributeMask").GetInt32());
    }

    /// <summary>
    /// The attribute's name has to carry the SLOT's own letter, because that is how the game finds it: an
    /// IMC bit is matched to a model attribute by the name <c>atr_</c> + slot letter + <c>v_</c> + the bit's
    /// letter. Tagging a pair of trousers with a top's <c>atr_tv_a</c> tags geometry the game never looks
    /// at, and the switch does nothing at all — which is exactly how this shipped and had to be found in
    /// game.
    /// <para/>
    /// The letters are the first of each slot's path suffix (met, top, glv, dwn, sho, ear, nek, wrs, rir),
    /// confirmed against Penumbra's own accessory workaround and a survey of 4,000 installed models.
    /// </summary>
    [Theory]
    [InlineData("top", "Body", "atr_tv_a")]
    [InlineData("dwn", "Legs", "atr_dv_a")]
    [InlineData("glv", "Hands", "atr_gv_a")]
    [InlineData("sho", "Feet", "atr_sv_a")]
    [InlineData("met", "Head", "atr_mv_a")]
    public void Write_NamesTheAttributeAfterTheSlot(string suffix, string _, string expected)
    {
        var gamePath = $"chara/equipment/e0043/model/c0201e0043_{suffix}.mdl";
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))), ModelRel, gamePath);
        var parts = ModelPartReader.Read(mod.Model())!;
        var redirects = Redirects(mod);

        var result = MeshToggleService.Write(
            mod.Root, redirects.Single(), parts, [new MeshToggleService.Plan("Belt", [parts.Parts[0]])],
            redirects, path => path == "chara/equipment/e0043/e0043.imc" ? Imc() : null);

        Assert.True(result.Ok, result.Message);
        Assert.Equal([expected], ModelPartReader.Read(mod.Model())!.AttributeNames);
    }

    /// <summary>
    /// Every field of the entry but the mask has to arrive unchanged, or adding a switch quietly changes
    /// which material variant, decal or sound the item uses.
    /// </summary>
    [Fact]
    public void Write_CarriesTheItemsRealImcEntryThrough()
    {
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));
        var parts = ModelPartReader.Read(mod.Model())!;

        Write(mod, parts, new MeshToggleService.Plan("Hood", [parts.Parts[0]]));

        var entry = mod.Group(MeshToggleService.GroupNameFor("Body")).GetProperty("DefaultEntry");
        Assert.Equal(3, entry.GetProperty("MaterialId").GetInt32());
        Assert.Equal(7, entry.GetProperty("DecalId").GetInt32());
        Assert.Equal(5, entry.GetProperty("VfxId").GetInt32());
        Assert.Equal(9, entry.GetProperty("MaterialAnimationId").GetInt32());
        Assert.Equal(2, entry.GetProperty("SoundId").GetInt32());
    }

    /// <summary>
    /// The switch bit must be CLEAR in the default entry and set by the option, so the group means the same
    /// thing whether Penumbra combines the two by OR or by XOR.
    /// </summary>
    [Fact]
    public void Write_PutsTheSwitchBitOutsideTheDefaultMask()
    {
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));
        var parts = ModelPartReader.Read(mod.Model())!;

        Write(mod, parts, new MeshToggleService.Plan("Hood", [parts.Parts[0]]));

        var group = mod.Group(MeshToggleService.GroupNameFor("Body"));
        int def = group.GetProperty("DefaultEntry").GetProperty("AttributeMask").GetInt32();
        int option = group.GetProperty("Options")[0].GetProperty("AttributeMask").GetInt32();

        Assert.Equal(0, def & option);
        // The vanilla mask was 0b11; bit 0 is ours now, so it is cleared and bit 1 is left alone.
        Assert.Equal(0b10, def);
        Assert.Equal(0b01, option);
        // Ticked by default, so adding a switch changes nothing until the user unticks it.
        Assert.Equal(1, group.GetProperty("DefaultSettings").GetInt32());
    }

    [Fact]
    public void Write_SplitsASubmeshWhenTheSwitchIsOnlyPartOfIt()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 3, TrianglesPerIsland: 2))));
        var parts = ModelPartReader.Read(mod.Model())!;

        var result = Write(mod, parts,
            new MeshToggleService.Plan("Buckle", [parts.Parts.Single(p => p.Label == "1.1.2")]));

        Assert.True(result.Ok, result.Message);
        var after = ModelPartReader.Read(mod.Model())!;

        // One submesh became three records, and only the middle one is tagged.
        var subs = after.Parts.Where(p => p.Island < 0).ToList();
        Assert.Equal(3, subs.Count);
        Assert.Equal([0u, 1u, 0u], subs.Select(p => p.AttributeMask));
        Assert.Equal([2, 2, 2], subs.Select(p => p.TriangleCount));
    }

    [Fact]
    public void Write_GivesEachSwitchItsOwnLetter()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0))));
        var parts = ModelPartReader.Read(mod.Model())!;

        var result = Write(mod, parts,
            new MeshToggleService.Plan("Bow", [parts.Parts[0]]),
            new MeshToggleService.Plan("Belt", [parts.Parts[1]]));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(["atr_tv_a", "atr_tv_b"], ModelPartReader.Read(mod.Model())!.AttributeNames);

        var options = mod.Group(MeshToggleService.GroupNameFor("Body")).GetProperty("Options").EnumerateArray()
            .ToDictionary(o => o.GetProperty("Name").GetString()!, o => o.GetProperty("AttributeMask").GetInt32());
        Assert.Equal(1, options["Bow"]);
        Assert.Equal(2, options["Belt"]);
        Assert.Equal(0b11, mod.Group(MeshToggleService.GroupNameFor("Body")).GetProperty("DefaultSettings").GetInt32());
    }

    /// <summary>Two switches over different islands of ONE submesh — the split has to serve both at once.</summary>
    [Fact]
    public void Write_SplitsOnceForTwoSwitchesSharingASubmesh()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 3, TrianglesPerIsland: 2))));
        var parts = ModelPartReader.Read(mod.Model())!;

        var result = Write(mod, parts,
            new MeshToggleService.Plan("Left", [parts.Parts.Single(p => p.Label == "1.1.1")]),
            new MeshToggleService.Plan("Right", [parts.Parts.Single(p => p.Label == "1.1.3")]));

        Assert.True(result.Ok, result.Message);
        var subs = ModelPartReader.Read(mod.Model())!.Parts.Where(p => p.Island < 0).ToList();
        Assert.Equal(3, subs.Count);
        Assert.Equal([1u, 0u, 2u], subs.Select(p => p.AttributeMask));
    }

    /// <summary>
    /// A whole-submesh switch and an island switch in the SAME mesh, with the island lower down. The split
    /// pushes the whole submesh along by a record, and a claim recorded against its original number would
    /// tag the neighbour instead — the wrong piece would disappear.
    /// </summary>
    [Fact]
    public void Write_RenumbersAWholeSubmeshClaimPastASplitBelowIt()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 2, TrianglesPerIsland: 2),   // 1.1, split below
                 new SyntheticModel.Sub(0, TrianglesPerIsland: 7))));            // 1.2, claimed whole
        var parts = ModelPartReader.Read(mod.Model())!;

        var result = Write(mod, parts,
            new MeshToggleService.Plan("Trim", [parts.Parts.Single(p => p.Label == "1.1.1")]),
            new MeshToggleService.Plan("Skirt", [parts.Parts.Single(p => p.Label == "1.2")]));

        Assert.True(result.Ok, result.Message);
        var subs = ModelPartReader.Read(mod.Model())!.Parts.Where(p => p.Island < 0).ToList();

        // Three records now: the two halves of the old 1.1, then the untouched 1.2.
        Assert.Equal([2, 2, 7], subs.Select(p => p.TriangleCount));
        Assert.Equal(1u, subs[0].AttributeMask);   // "Trim" -> atr_tv_a
        Assert.Equal(0u, subs[1].AttributeMask);
        Assert.Equal(2u, subs[2].AttributeMask);   // "Skirt" -> atr_tv_b, on the SEVEN-triangle record
    }

    // ── refusals: nothing may be half-written ───────────────────────────────

    [Fact]
    public void Write_RefusesAModelItCannotFindAnImcEntryFor()
    {
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));
        var parts = ModelPartReader.Read(mod.Model())!;
        var before = mod.Model();

        var redirects = Redirects(mod);
        var result = MeshToggleService.Write(
            mod.Root, redirects.Single(), parts, [new MeshToggleService.Plan("Bow", [parts.Parts[0]])],
            redirects, _ => null);

        Assert.False(result.Ok);
        Assert.Contains("IMC", result.Message);
        Assert.Equal(before, mod.Model());
        Assert.False(File.Exists(Path.Combine(mod.Root, "Proteus", MeshToggleService.RecordFile)));
    }

    [Fact]
    public void Write_RefusesASlotThatHasNoImcFile()
    {
        const string hair = "chara/human/c0201/obj/hair/h0001/model/c0201h0001_hir.mdl";
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))), "hair.mdl", hair);
        var parts = ModelPartReader.Read(mod.Model("hair.mdl"))!;
        var redirects = Redirects(mod);

        var result = MeshToggleService.Write(
            mod.Root, redirects.Single(), parts, [new MeshToggleService.Plan("Clip", [parts.Parts[0]])],
            redirects, _ => Imc());

        Assert.False(result.Ok);
        Assert.Contains("Hair", result.Message);
    }

    [Fact]
    public void Write_RefusesMoreSwitchesThanTheModelHasRoomFor()
    {
        // Nine of the ten letters already spoken for.
        var used = Enumerable.Range(0, 9).Select(i => $"atr_tv_{(char)('a' + i)}").ToList();
        using var mod = new Mod(SyntheticModel.Build(used,
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0))));
        var parts = ModelPartReader.Read(mod.Model())!;

        var result = Write(mod, parts,
            new MeshToggleService.Plan("A", [parts.Parts[0]]),
            new MeshToggleService.Plan("B", [parts.Parts[1]]));

        Assert.False(result.Ok);
        Assert.Contains("1 switch slot(s) left", result.Message);
    }

    // ── revert ──────────────────────────────────────────────────────────────

    [Fact]
    public void Revert_PutsTheModelBackAndDropsTheGroup()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0))));
        var original = mod.Model();
        var parts = ModelPartReader.Read(original)!;

        Assert.True(Write(mod, parts, new MeshToggleService.Plan("Bow", [parts.Parts[1]])).Ok);
        Assert.NotEqual(original, mod.Model());

        var result = MeshToggleService.Revert(mod.Root);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(original, mod.Model());
        Assert.Empty(JsonDocument.Parse(File.ReadAllText(Path.Combine(mod.Root, "meta.json")))
            .RootElement.GetProperty("Groups").EnumerateArray());
        Assert.False(File.Exists(Path.Combine(mod.Root, "Proteus", MeshToggleService.RecordFile)));
    }

    /// <summary>
    /// A second write must not overwrite the pristine backup with an already-tagged model, or revert would
    /// restore the previous edit instead of the author's file.
    /// </summary>
    [Fact]
    public void Revert_AfterTwoWrites_RestoresTheAuthorsOriginal()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0))));
        var original = mod.Model();

        var parts = ModelPartReader.Read(mod.Model())!;
        Assert.True(Write(mod, parts, new MeshToggleService.Plan("Bow", [parts.Parts[0]])).Ok);

        var parts2 = ModelPartReader.Read(mod.Model())!;
        Assert.True(Write(mod, parts2, new MeshToggleService.Plan("Belt", [parts2.Parts[1]])).Ok);

        Assert.True(MeshToggleService.Revert(mod.Root).Ok);
        Assert.Equal(original, mod.Model());
    }

    [Fact]
    public void Revert_OnAModelWithNoSwitches_SaysSoRatherThanThrowing()
    {
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));
        Assert.False(MeshToggleService.Revert(mod.Root).Ok);
    }

    /// <summary>
    /// The record has to survive a round trip, or the panel cannot show what a mod already carries and
    /// revert has nothing to work from.
    /// </summary>
    [Fact]
    public void Write_LeavesAReadableRecord()
    {
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));
        var parts = ModelPartReader.Read(mod.Model())!;
        Write(mod, parts, new MeshToggleService.Plan("Bow", [parts.Parts[0]]));

        var item = Assert.Single(MeshToggleService.ReadRecord(mod.Root)!.Items);
        Assert.Equal(MeshToggleService.GroupNameFor("Body"), item.GroupName);
        Assert.Equal(43, item.SetId);
        Assert.Equal("Body", item.Slot);
        Assert.Equal([ModelRel], item.Files);
        Assert.Equal("a", item.Toggles["Bow"]);
    }

    /// <summary>
    /// A mod with two garments needs two groups and two independent letter budgets. Folded into one, the
    /// second write replaced the first item's group with one carrying the WRONG identifier, and both
    /// switches ended up driving the same bit.
    /// </summary>
    [Fact]
    public void Write_KeepsTwoItemsInOneModApart()
    {
        const string legsPath = "chara/equipment/e0043/model/c0201e0043_dwn.mdl";
        const string legsRel = "items/dwn.mdl";

        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));
        // A second model, on a different slot of the same set.
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Frock\",\"Groups\":[],\"DefaultData\":{\"Files\":{"
            + $"\"{GamePath}\":\"{ModelRel}\",\"{legsPath}\":\"{legsRel}\"" + "}}}");
        var legsDest = Path.Combine(mod.Root, legsRel.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(legsDest, SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));

        var redirects = Redirects(mod);
        byte[]? Read(string p) => p == "chara/equipment/e0043/e0043.imc" ? Imc() : null;

        var top = redirects.Single(r => r.File == ModelRel);
        var topParts = ModelPartReader.Read(mod.Model())!;
        Assert.True(MeshToggleService.Write(mod.Root, top, topParts,
            [new MeshToggleService.Plan("Collar", [topParts.Parts[0]])], redirects, Read).Ok);

        var legs = redirects.Single(r => r.File == legsRel);
        var legParts = ModelPartReader.Read(mod.Model(legsRel))!;
        Assert.True(MeshToggleService.Write(mod.Root, legs, legParts,
            [new MeshToggleService.Plan("Belt", [legParts.Parts[0]])], redirects, Read).Ok);

        // Two groups, each naming its own slot, each with exactly its own switch.
        var bodyGroup = mod.Group(MeshToggleService.GroupNameFor("Body"));
        var legsGroup = mod.Group(MeshToggleService.GroupNameFor("Legs"));
        Assert.Equal("Body", bodyGroup.GetProperty("Identifier").GetProperty("EquipSlot").GetString());
        Assert.Equal("Legs", legsGroup.GetProperty("Identifier").GetProperty("EquipSlot").GetString());
        Assert.Equal("Collar", Assert.Single(bodyGroup.GetProperty("Options").EnumerateArray())
            .GetProperty("Name").GetString());
        Assert.Equal("Belt", Assert.Single(legsGroup.GetProperty("Options").EnumerateArray())
            .GetProperty("Name").GetString());

        // Each model is tagged with its OWN slot's attribute; both may use letter 'a' without colliding,
        // because they are different items driven by different groups.
        Assert.Equal(["atr_tv_a"], ModelPartReader.Read(mod.Model())!.AttributeNames);
        Assert.Equal(["atr_dv_a"], ModelPartReader.Read(mod.Model(legsRel))!.AttributeNames);

        Assert.Equal(2, MeshToggleService.ReadRecord(mod.Root)!.Items.Count);
    }

    /// <summary>Reusing a name would overwrite the letter the first switch is remembered by, leaving its
    /// attribute tagged on geometry nothing can ever clear.</summary>
    [Fact]
    public void Write_RefusesASwitchNameTheItemAlreadyHas()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0))));
        var parts = ModelPartReader.Read(mod.Model())!;
        Assert.True(Write(mod, parts, new MeshToggleService.Plan("Bow", [parts.Parts[0]])).Ok);

        var again = ModelPartReader.Read(mod.Model())!;
        var result = Write(mod, again, new MeshToggleService.Plan("Bow", [again.Parts[1]]));

        Assert.False(result.Ok);
        Assert.Contains("Bow", result.Message);
        Assert.Equal(["atr_tv_a"], ModelPartReader.Read(mod.Model())!.AttributeNames);
    }

    /// <summary>
    /// One switch taking a submesh whole and another taking an island of it cannot both be honoured — the
    /// island claim used to be dropped silently, writing an option that controlled nothing.
    /// </summary>
    [Fact]
    public void Write_RefusesTwoSwitchesClaimingOneSubmesh()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 3, TrianglesPerIsland: 2))));
        var parts = ModelPartReader.Read(mod.Model())!;

        var result = Write(mod, parts,
            new MeshToggleService.Plan("Skirt", [parts.Parts.Single(p => p.Label == "1.1")]),
            new MeshToggleService.Plan("Belt", [parts.Parts.Single(p => p.Label == "1.1.2")]));

        Assert.False(result.Ok);
        Assert.Contains("1.1", result.Message);
        Assert.Empty(ModelPartReader.Read(mod.Model())!.AttributeNames);
    }

    /// <summary>
    /// A legacy record recorded no set or slot, so it could not be matched — and the next write to the same
    /// garment added a SECOND item and a second group with the same IMC identifier. Penumbra keeps only the
    /// first it reaches, so the new switch would report success and do nothing. The identity is recovered
    /// from the group the legacy record names.
    /// </summary>
    [Fact]
    public void Write_AdoptsALegacyRecordRatherThanAddingASecondGroup()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0))));
        var parts = ModelPartReader.Read(mod.Model())!;
        Assert.True(Write(mod, parts, new MeshToggleService.Plan("Bow", [parts.Parts[0]])).Ok);

        // Rewrite the record in the old shape — no set, no slot — as an older build left it.
        var sidecar = Path.Combine(mod.Root, "Proteus", MeshToggleService.RecordFile);
        File.WriteAllText(sidecar,
            "{\"GroupName\":\"" + MeshToggleService.GroupNameFor("Body") + "\","
            + "\"Files\":[\"" + ModelRel + "\"],\"Toggles\":{\"Bow\":\"a\"}}");

        var again = ModelPartReader.Read(mod.Model())!;
        Assert.True(Write(mod, again, new MeshToggleService.Plan("Belt", [again.Parts[1]])).Ok);

        // ONE group, carrying both switches — not two groups fighting over one identifier.
        var groups = JsonDocument.Parse(File.ReadAllText(Path.Combine(mod.Root, "meta.json")))
            .RootElement.GetProperty("Groups").EnumerateArray()
            .Where(g => g.GetProperty("Type").GetString() == "Imc").ToList();
        Assert.Single(groups);
        Assert.Equal(["Bow", "Belt"],
            groups[0].GetProperty("Options").EnumerateArray()
                .Select(o => o.GetProperty("Name").GetString()));

        var item = Assert.Single(MeshToggleService.ReadRecord(mod.Root)!.Items);
        Assert.Equal(43, item.SetId);
        Assert.Equal("Body", item.Slot);
    }

    /// <summary>
    /// A sibling whose submesh the author already tagged must be skipped: OR-ing our bit onto it would
    /// leave a submesh carrying two attributes, whose combination rule this design does not assume.
    /// </summary>
    [Fact]
    public void Write_SkipsASiblingThatAlreadyTagsASubmesh()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(0))));

        const string otherRel = "items/other.mdl";
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Frock\",\"Groups\":[{\"Type\":\"Multi\",\"Name\":\"Size\",\"Options\":["
            + "{\"Name\":\"A\",\"Files\":{\"" + GamePath + "\":\"" + ModelRel + "\"}},"
            + "{\"Name\":\"B\",\"Files\":{\"" + GamePath + "\":\"" + otherRel + "\"}}]}]}");
        // Same part layout, but its second submesh is already behind the author's own attribute.
        File.WriteAllBytes(Path.Combine(mod.Root, otherRel.Replace('/', Path.DirectorySeparatorChar)),
            SyntheticModel.Build(["atr_tv_j"], Mesh(new SyntheticModel.Sub(0), new SyntheticModel.Sub(1))));

        var redirects = Redirects(mod);
        var parts = ModelPartReader.Read(mod.Model())!;
        var result = MeshToggleService.Write(
            mod.Root, redirects.Single(r => r.File == ModelRel), parts,
            [new MeshToggleService.Plan("Bow", [parts.Parts[1]])],
            redirects, path => path == "chara/equipment/e0043/e0043.imc" ? Imc() : null);

        Assert.True(result.Ok, result.Message);
        Assert.Equal([otherRel], result.Skipped);
        // The sibling is untouched: still one attribute, still the author's own mask.
        var other = ModelPartReader.Read(mod.Model(otherRel))!;
        Assert.Equal(["atr_tv_j"], other.AttributeNames);
        Assert.Equal([0u, 1u], other.Parts.Select(p => p.AttributeMask));
    }

    /// <summary>The record is a file a user may open; the legacy fields exist only to be read from an older
    /// one and must not reappear as nulls beside the real data.</summary>
    [Fact]
    public void Write_LeavesNoNullLegacyFieldsInTheRecord()
    {
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));
        var parts = ModelPartReader.Read(mod.Model())!;
        Write(mod, parts, new MeshToggleService.Plan("Bow", [parts.Parts[0]]));

        var json = File.ReadAllText(Path.Combine(mod.Root, "Proteus", MeshToggleService.RecordFile));
        Assert.DoesNotContain("null", json);
        Assert.Contains("Items", json);
    }

    /// <summary>
    /// Two IMC groups on one item are not merged — Penumbra keeps the first it reaches, ordered by
    /// descending priority. Ours has to outrank an author's own edit for the same item, or it is never
    /// applied at all and every switch is listed but inert.
    /// </summary>
    [Fact]
    public void Write_OutranksTheModsOwnImcGroupForTheSameItem()
    {
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));
        // The author's own IMC edit on the same set and slot, sitting above the default priority.
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Frock\",\"Groups\":[{\"Type\":\"Imc\",\"Name\":\"Straps\",\"Priority\":4,"
            + "\"Identifier\":{\"ObjectType\":\"Equipment\",\"PrimaryId\":43,\"Variant\":1,\"EquipSlot\":\"Body\"},"
            + "\"DefaultEntry\":{\"MaterialId\":1,\"AttributeMask\":1023},\"Options\":[{\"Name\":\"Hide\",\"AttributeMask\":512}]}],"
            + "\"DefaultData\":{\"Files\":{\"" + GamePath + "\":\"" + ModelRel + "\"}}}");

        var parts = ModelPartReader.Read(mod.Model())!;
        Assert.True(Write(mod, parts, new MeshToggleService.Plan("Bow", [parts.Parts[0]])).Ok);

        Assert.True(mod.Group(MeshToggleService.GroupNameFor("Body")).GetProperty("Priority").GetInt32() > 4);
    }

    /// <summary>
    /// A v3 folder has no Groups array to count, and the sentinel that stood in for one overflowed the
    /// legacy writer's ordinal arithmetic into a file called group_-2147483648_….json — which reads back as
    /// an enormous NEGATIVE ordinal, sorting the group first rather than last.
    /// </summary>
    [Fact]
    public void Write_OnALegacyFolder_NumbersTheGroupFileSanely()
    {
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"), "{\"FileVersion\":3,\"Name\":\"Frock\"}");
        File.WriteAllText(Path.Combine(mod.Root, "default_mod.json"),
            "{\"Files\":{\"" + GamePath + "\":\"" + ModelRel + "\"}}");

        var parts = ModelPartReader.Read(mod.Model())!;
        Assert.True(Write(mod, parts, new MeshToggleService.Plan("Bow", [parts.Parts[0]])).Ok);

        var file = Assert.Single(Directory.GetFiles(mod.Root, "group_*.json"));
        var number = Path.GetFileNameWithoutExtension(file).Split('_')[1];
        Assert.True(int.TryParse(number, out var n) && n is > 0 and < 1000, $"nonsense ordinal: {number}");
        Assert.Equal("Imc", JsonDocument.Parse(File.ReadAllText(file)).RootElement
            .GetProperty("Type").GetString());
    }

    /// <summary>
    /// A letter the item already uses must not be handed out again just because the model in front of the
    /// user does not carry it. That happens when a sibling was skipped: the switch was written into the
    /// other file, so this one's attribute table is empty and looks free. Two options on one bit flip each
    /// other, and ticking both XORs it back to nothing.
    /// </summary>
    [Fact]
    public void Write_DoesNotReuseALetterTheItemAlreadyClaimed()
    {
        const string otherRel = "items/other.mdl";
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 2, TrianglesPerIsland: 2))));

        // Two files on one game path whose triangle ordering differs, so only one can ever be patched.
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Frock\",\"Groups\":[{\"Type\":\"Multi\",\"Name\":\"Size\",\"Options\":["
            + "{\"Name\":\"A\",\"Files\":{\"" + GamePath + "\":\"" + ModelRel + "\"}},"
            + "{\"Name\":\"B\",\"Files\":{\"" + GamePath + "\":\"" + otherRel + "\"}}]}]}");
        File.WriteAllBytes(Path.Combine(mod.Root, otherRel.Replace('/', Path.DirectorySeparatorChar)),
            SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: 4))));

        var redirects = Redirects(mod);
        byte[]? Read(string p) => p == "chara/equipment/e0043/e0043.imc" ? Imc() : null;

        var first = ModelPartReader.Read(mod.Model())!;
        Assert.True(MeshToggleService.Write(mod.Root, redirects.Single(r => r.File == ModelRel), first,
            [new MeshToggleService.Plan("Belt", [first.Parts.Single(p => p.Label == "1.1.1")])],
            redirects, Read).Ok);

        // Now add a switch while the SKIPPED model is the one selected. Its table carries no attribute at
        // all, so the letter has to come from the record instead.
        var second = ModelPartReader.Read(mod.Model(otherRel))!;
        Assert.Empty(second.AttributeNames);
        Assert.True(MeshToggleService.Write(mod.Root, redirects.Single(r => r.File == otherRel), second,
            [new MeshToggleService.Plan("Strap", [second.Parts[0]])], redirects, Read).Ok);

        var item = Assert.Single(MeshToggleService.ReadRecord(mod.Root)!.Items);
        Assert.Equal("a", item.Toggles["Belt"]);
        Assert.Equal("b", item.Toggles["Strap"]);

        // Distinct bits, or one switch would drive the other.
        var masks = mod.Group(MeshToggleService.GroupNameFor("Body")).GetProperty("Options").EnumerateArray()
            .Select(o => o.GetProperty("AttributeMask").GetInt32()).ToList();
        Assert.Equal(masks.Count, masks.Distinct().Count());
    }

    /// <summary>A record written before the file became per-item still has to be undoable.</summary>
    [Fact]
    public void Revert_UnderstandsALegacyRecord()
    {
        using var mod = new Mod(SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0))));
        var original = mod.Model();
        var parts = ModelPartReader.Read(mod.Model())!;
        Assert.True(Write(mod, parts, new MeshToggleService.Plan("Bow", [parts.Parts[0]])).Ok);

        // Rewrite the record in the old shape, naming the group that was actually written.
        var sidecar = Path.Combine(mod.Root, "Proteus", MeshToggleService.RecordFile);
        File.WriteAllText(sidecar,
            "{\"GroupName\":\"" + MeshToggleService.GroupNameFor("Body") + "\","
            + "\"Files\":[\"" + ModelRel + "\"],\"Toggles\":{\"Bow\":\"a\"}}");

        Assert.True(MeshToggleService.Revert(mod.Root).Ok);
        Assert.Equal(original, mod.Model());
        Assert.Empty(JsonDocument.Parse(File.ReadAllText(Path.Combine(mod.Root, "meta.json")))
            .RootElement.GetProperty("Groups").EnumerateArray());
    }

    /// <summary>
    /// Sibling files are patched with the reference model's ordinals, so a file that only AGREES ON COUNTS
    /// must be refused — the same ordinals would otherwise tag different triangles.
    /// </summary>
    [Fact]
    public void Write_SkipsASiblingWhoseTrianglesAreOrderedDifferently()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 2, TrianglesPerIsland: 2))));

        // A second file on the same game path, same part counts, but its submesh holds one run instead of
        // two islands — so the ordinals mean something different.
        const string otherRel = "items/other.mdl";
        File.WriteAllText(Path.Combine(mod.Root, "meta.json"),
            "{\"FileVersion\":4,\"Name\":\"Frock\",\"Groups\":[{\"Type\":\"Multi\",\"Name\":\"Size\",\"Options\":["
            + "{\"Name\":\"A\",\"Files\":{\"" + GamePath + "\":\"" + ModelRel + "\"}},"
            + "{\"Name\":\"B\",\"Files\":{\"" + GamePath + "\":\"" + otherRel + "\"}}]}]}");
        File.WriteAllBytes(Path.Combine(mod.Root, otherRel.Replace('/', Path.DirectorySeparatorChar)),
            SyntheticModel.Build([], Mesh(new SyntheticModel.Sub(0, Islands: 1, TrianglesPerIsland: 4))));

        var redirects = Redirects(mod);
        var parts = ModelPartReader.Read(mod.Model())!;
        var result = MeshToggleService.Write(
            mod.Root, redirects.Single(r => r.File == ModelRel), parts,
            [new MeshToggleService.Plan("Belt", [parts.Parts.Single(p => p.Label == "1.1.1")])],
            redirects, path => path == "chara/equipment/e0043/e0043.imc" ? Imc() : null);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, result.FilesPatched);
        Assert.Equal([otherRel], result.Skipped);
    }
}
