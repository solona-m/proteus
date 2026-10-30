using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A mod that already ships the chosen size is switched to it rather than refitted. Reported: Farfalla, put on with the
/// automatic refit aimed at Rue+, stayed on its default "Neolithe XS" — though it ships a "Rue M" that IS Rue+'s Yiggle
/// Medium.
/// <para/>
/// Read against Farfalla itself, the test went with the mod: repacked on 2026-10-13, its "Rue M" became a refit of its
/// own Neolithe size rather than the author's copy of the body, sharing 87% of the body's skin rather than all of it,
/// and AuthorSize rightly stopped calling it an exact copy. So the author's size is made here, as an author makes one:
/// the body's own chest, shipped as one of the garment's sizes beside one that is nothing of the body's. Against the
/// installed Rue+, so skipped where it is not.
/// </summary>
[Trait("Speed", "Slow")]
public class AuthorSizeTests : IDisposable
{
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";

    private readonly string mod = Directory.CreateTempSubdirectory("proteus-author-size").FullName;

    public void Dispose()
    {
        try { Directory.Delete(mod, recursive: true); } catch { /* a test folder */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The garment's sizes: "Rue M", the body's own Yiggle Medium chest copied into the mod, and "Other", a chest of
    /// the body's that is not it — Rue+'s first non-Yiggle chest whose file differs.
    /// </summary>
    private List<(string Option, string File)> Sizes(BodySizeCatalog rue, BodyOption yiggleMedium)
    {
        var other = rue.For("_top", "0201").First(o => !o.Label.Contains("Yiggle")
            && !File.ReadAllBytes(rue.PathOf(o)).AsSpan().SequenceEqual(File.ReadAllBytes(rue.PathOf(yiggleMedium))));

        List<(string, string)> sizes = [];
        foreach (var (name, option) in new[] { ("Rue M", yiggleMedium), ("Other", other) })
        {
            string rel = $"size/shirt/{name.ToLowerInvariant()}/chara/equipment/e0041/model/c0201e0041_top.mdl";
            string full = Path.Combine(mod, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Copy(rue.PathOf(option), full);
            sizes.Add((name, rel));
        }
        return sizes;
    }

    [LocalDataFact(Rue)]
    public void The_authors_own_size_is_found_when_it_is_the_chosen_one()
    {
        var rue = BodySizeCatalog.Read(LocalData.Path(Rue));
        var yiggleMedium = rue.For("_top", "0201").First(o => o.Label.EndsWith("Yiggle - Medium"));

        Assert.Equal("Rue M", AutoRefitWatcher.AuthorSize(mod, Sizes(rue, yiggleMedium), rue, yiggleMedium,
                                                          "_top", "0201"));
    }

    [LocalDataFact(Rue)]
    public void A_size_the_author_did_not_make_is_refitted_as_before()
    {
        // The author made a Yiggle size; a plain Rue+ size is none of the garment's, near as it is.
        var rue = BodySizeCatalog.Read(LocalData.Path(Rue));
        var yiggleMedium = rue.For("_top", "0201").First(o => o.Label.EndsWith("Yiggle - Medium"));
        var sizes = Sizes(rue, yiggleMedium);
        var plain = rue.For("_top", "0201").First(o => !o.Label.Contains("Yiggle")
            && !File.ReadAllBytes(rue.PathOf(o)).AsSpan().SequenceEqual(File.ReadAllBytes(rue.PathOf(yiggleMedium)))
            && !File.ReadAllBytes(rue.PathOf(o)).AsSpan().SequenceEqual(File.ReadAllBytes(
                   Path.Combine(mod, sizes[1].File.Replace('/', Path.DirectorySeparatorChar)))));

        Assert.Null(AutoRefitWatcher.AuthorSize(mod, sizes, rue, plain, "_top", "0201"));
    }
}
