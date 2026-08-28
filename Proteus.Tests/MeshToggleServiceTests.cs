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

        var group = mod.Group("Parts");
        Assert.Equal("Imc", group.GetProperty("Type").GetString());
        Assert.Equal("Bow", group.GetProperty("Options")[0].GetProperty("Name").GetString());
        Assert.Equal(1, group.GetProperty("Options")[0].GetProperty("AttributeMask").GetInt32());
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

        var entry = mod.Group("Parts").GetProperty("DefaultEntry");
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

        var group = mod.Group("Parts");
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
            new MeshToggleService.Plan("Buckle", [parts.Parts.Single(p => p.Label == "1.1b")]));

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

        var options = mod.Group("Parts").GetProperty("Options").EnumerateArray()
            .ToDictionary(o => o.GetProperty("Name").GetString()!, o => o.GetProperty("AttributeMask").GetInt32());
        Assert.Equal(1, options["Bow"]);
        Assert.Equal(2, options["Belt"]);
        Assert.Equal(0b11, mod.Group("Parts").GetProperty("DefaultSettings").GetInt32());
    }

    /// <summary>Two switches over different islands of ONE submesh — the split has to serve both at once.</summary>
    [Fact]
    public void Write_SplitsOnceForTwoSwitchesSharingASubmesh()
    {
        using var mod = new Mod(SyntheticModel.Build([],
            Mesh(new SyntheticModel.Sub(0, Islands: 3, TrianglesPerIsland: 2))));
        var parts = ModelPartReader.Read(mod.Model())!;

        var result = Write(mod, parts,
            new MeshToggleService.Plan("Left", [parts.Parts.Single(p => p.Label == "1.1a")]),
            new MeshToggleService.Plan("Right", [parts.Parts.Single(p => p.Label == "1.1c")]));

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
            new MeshToggleService.Plan("Trim", [parts.Parts.Single(p => p.Label == "1.1a")]),
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

        var record = MeshToggleService.ReadRecord(mod.Root)!;
        Assert.Equal("Parts", record.GroupName);
        Assert.Equal([ModelRel], record.Files);
        Assert.Equal("a", record.Toggles["Bow"]);
    }
}
