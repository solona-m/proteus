using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A mod that already ships the chosen size is switched to it rather than refitted. Reported: Farfalla, put on with the
/// automatic refit aimed at Rue+, stayed on its default "Neolithe XS" — though it ships a "Rue M" that IS Rue+'s Yiggle
/// Medium. Against the installed mods, so skipped where they are not.
/// </summary>
public class AuthorSizeTests
{
    private const string Farfalla = LocalData.Mods + ":Farfalla - by Solona";
    private const string Rue = LocalData.Mods + ":hs-Rue+-2.2.7-y0f";
    private const string Top = "chara/equipment/e0041/model/c0201e0041_top.mdl";

    private static System.Collections.Generic.List<(string Option, string File)> ShirtSizes()
        => PenumbraModMeta.ReadAllRedirects(LocalData.Path(Farfalla))
                          .Where(r => r.GamePath == Top && r.Source.StartsWith("Shirt Size / "))
                          .Select(r => (r.Source["Shirt Size / ".Length..], r.File))
                          .ToList();

    [LocalDataFact(Farfalla, Rue)]
    public void The_authors_own_size_is_found_when_it_is_the_chosen_one()
    {
        var rue = BodySizeCatalog.Read(LocalData.Path(Rue));
        var yiggleMedium = rue.For("_top", "0201").First(o => o.Label.EndsWith("Yiggle - Medium"));

        Assert.Equal("Rue M", AutoRefitWatcher.AuthorSize(LocalData.Path(Farfalla), ShirtSizes(), rue, yiggleMedium,
                                                          "_top", "0201"));
    }

    [LocalDataFact(Farfalla, Rue)]
    public void A_size_the_author_did_not_make_is_refitted_as_before()
    {
        // Farfalla's Rue sizes are Yiggle ones; a plain Rue+ size is none of them, near as they are.
        var rue = BodySizeCatalog.Read(LocalData.Path(Rue));
        var plain = rue.For("_top", "0201").First(o => !o.Label.Contains("Yiggle"));

        Assert.Null(AutoRefitWatcher.AuthorSize(LocalData.Path(Farfalla), ShirtSizes(), rue, plain, "_top", "0201"));
    }
}
