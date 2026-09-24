using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;

namespace Proteus.Tests;

/// <summary>
/// What a saved refit option has to carry besides the model it refitted.
/// <para/>
/// Reported as a gen3 outfit swapped onto Rue+ keeping its new shape but losing its textures and materials. The solve
/// was not at fault: that pair's correspondence matches 99.7% of the body, both transfer maps load, the swapped skin
/// takes the target body's material, and a pre-Dawntrail v5 model survives the re-emit with its materials and texture
/// coordinates untouched. The save was.
/// <para/>
/// Mods split two ways. Most keep the material and textures in the default files or one required option and give a
/// size option nothing but a model — there, a refit carrying only a model is exactly right. Some give every size its
/// own material and textures, and there a size group is single-select: switching the refit ON switches the option it
/// was cut from OFF, and everything drawn on the garment goes with it.
/// </summary>
public class RefitOptionCarriesTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("proteus-refit-carry").FullName;

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* a test folder */ }
        GC.SuppressFinalize(this);
    }

    private const string Model = "chara/equipment/e0118/model/c0201e0118_top.mdl";
    private const string Material = "chara/equipment/e0118/material/v0001/mt_c0201e0118_top_a.mtrl";
    private const string Texture = "chara/equipment/e0118/texture/v01_c0201e0118_top_d.tex";
    private const string OtherRace = "chara/equipment/e0118/model/c0101e0118_top.mdl";

    /// <param name="sizeCarriesEverything">
    /// true: each size ships its own material and textures. false: the size ships a model and the mod's default files
    /// ship the material and textures, which is the common shape.
    /// </param>
    private void WriteMod(bool sizeCarriesEverything)
    {
        string sizeFiles = sizeCarriesEverything
            ? $$"""
                "{{Model}}": "size/medium/model.mdl",
                "{{Material}}": "size/medium/mat.mtrl",
                "{{Texture}}": "size/medium/tex.tex",
                "{{OtherRace}}": "size/medium/other.mdl"
                """
            : $$""""{{Model}}": "size/medium/model.mdl"""";
        string defaults = sizeCarriesEverything
            ? ""
            : $$"""
                "{{Material}}": "shared/mat.mtrl",
                "{{Texture}}": "shared/tex.tex"
                """;

        File.WriteAllText(Path.Combine(root, "meta.json"), $$"""
            {
              "FileVersion": 4,
              "Name": "An Outfit",
              "Author": "somebody",
              "Version": "1.0",
              "DefaultData": { "Files": { {{defaults}} }, "Manipulations": [] },
              "Groups": [
                {
                  "Name": "Size",
                  "Type": "Single",
                  "Priority": 0,
                  "Options": [ { "Name": "Medium", "Files": { {{sizeFiles}} } } ]
                }
              ]
            }
            """);

        foreach (string rel in new[] { "size/medium/model.mdl", "size/medium/mat.mtrl", "size/medium/tex.tex",
                                       "size/medium/other.mdl", "shared/mat.mtrl", "shared/tex.tex" })
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, [0]);
        }
    }

    private List<string> FilesOf(string group, string option)
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "meta.json"))).RootElement;
        var g = manifest.GetProperty("Groups").EnumerateArray()
                        .First(x => x.GetProperty("Name").GetString() == group);
        var o = g.GetProperty("Options").EnumerateArray()
                 .First(x => x.GetProperty("Name").GetString() == option);
        return o.TryGetProperty("Files", out var f) && f.ValueKind == JsonValueKind.Object
            ? f.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToList()
            : [];
    }

    private void Save(string? fromOption)
    {
        var outcome = BodyRetargetWriter.Save(root, "Size", Model, "Rue+", "The Body / A",
                                              [new BodyRetargetWriter.Refit("Rue+ · Small", [1, 2, 3], "Small")],
                                              fromOption);
        Assert.True(outcome.Ok, outcome.Message);
        output.WriteLine($"refit carries: {string.Join("  ", FilesOf("Size", "Rue+ · Small").Select(Path.GetExtension))}");
    }

    [Fact]
    public void Undoing_a_refit_never_deletes_the_author_s_files()
    {
        // The refit carries the author's material and textures so the garment keeps them when it is switched on.
        // Undo removes the option and deletes what it published — which must mean what PROTEUS wrote, and nothing
        // else. Deleting the author's files here would break their mod for every size, permanently.
        WriteMod(sizeCarriesEverything: true);
        Save(fromOption: "Medium");

        string refitModel = Path.Combine(root, JsonDocument
            .Parse(File.ReadAllText(Path.Combine(root, "meta.json"))).RootElement
            .GetProperty("Groups").EnumerateArray().First(g => g.GetProperty("Name").GetString() == "Size")
            .GetProperty("Options").EnumerateArray().First(o => o.GetProperty("Name").GetString() == "Rue+ · Small")
            .GetProperty("Files").GetProperty(Model).GetString()!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(refitModel), "the refit's own model was not written");

        var outcome = BodyRetargetWriter.Undo(root, "Size", "Rue+ · Small");
        Assert.True(outcome.Ok, outcome.Message);

        foreach (string rel in new[] { "size/medium/model.mdl", "size/medium/mat.mtrl",
                                       "size/medium/tex.tex", "size/medium/other.mdl" })
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"undo deleted the author's {Path.GetFileName(rel)}");
        }

        Assert.False(File.Exists(refitModel), "undo left the refit's own model behind");
    }

    [Fact]
    public void A_size_that_ships_its_own_material_and_textures_hands_them_to_the_refit()
    {
        WriteMod(sizeCarriesEverything: true);
        Save(fromOption: "Medium");

        var carried = FilesOf("Size", "Rue+ · Small");

        // The refitted model is the refit's own; everything else the size carried comes across, including the other
        // race's model, which this refit did not touch and the author still has to supply.
        Assert.Contains(Model, carried);
        Assert.Contains(Material, carried);
        Assert.Contains(Texture, carried);
        Assert.Contains(OtherRace, carried);

        // And the refitted model is NOT the author's file.
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "meta.json"))).RootElement;
        string wrote = manifest.GetProperty("Groups").EnumerateArray()
                               .First(g => g.GetProperty("Name").GetString() == "Size")
                               .GetProperty("Options").EnumerateArray()
                               .First(o => o.GetProperty("Name").GetString() == "Rue+ · Small")
                               .GetProperty("Files").GetProperty(Model).GetString()!;
        Assert.DoesNotContain("size/medium/model.mdl", wrote);
    }

    [Fact]
    public void A_size_that_ships_only_a_model_hands_over_only_a_model()
    {
        // The common shape: the material and textures are in the default files, which no option switches off.
        WriteMod(sizeCarriesEverything: false);
        Save(fromOption: "Medium");

        Assert.Equal([Model], FilesOf("Size", "Rue+ · Small"));
    }

    [Fact]
    public void A_model_that_came_from_somewhere_else_hands_over_nothing()
    {
        // No option of this group was switched off by saving here, so there is nothing to replace. Copying anyway
        // would duplicate live redirects and then override the author's if the user changed size.
        WriteMod(sizeCarriesEverything: true);
        Save(fromOption: null);

        Assert.Equal([Model], FilesOf("Size", "Rue+ · Small"));
    }

    [Fact]
    public void Refitting_a_second_slot_keeps_both_the_first_slot_and_the_copied_files()
    {
        WriteMod(sizeCarriesEverything: true);
        Save(fromOption: "Medium");

        const string legs = "chara/equipment/e0118/model/c0201e0118_dwn.mdl";
        var outcome = BodyRetargetWriter.Save(root, "Size", legs, "Rue+", "The Body / A",
                                              [new BodyRetargetWriter.Refit("Rue+ · Small", [4, 5, 6], "Small")],
                                              "Medium");
        Assert.True(outcome.Ok, outcome.Message);

        var carried = FilesOf("Size", "Rue+ · Small");
        Assert.Contains(Model, carried);      // the chest refit from the first save
        Assert.Contains(legs, carried);       // and the legs from this one
        Assert.Contains(Material, carried);
        Assert.Contains(Texture, carried);
    }
}
