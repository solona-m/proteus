using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A TexTools copy of the game's own model — re-saved, or an older version — shipped for Midlander men beside the
/// author's women's design is not a men's piece: the size set crosses the design to men instead of sizing the copy onto
/// TBSE. On mods written here, with models whose triangles share no corner, so every share is exact.
/// </summary>
public class SizeSetGameCopyTests
{
    private const string Item = "e6000";

    private static string PathOf(string race, string slot = "_top", string item = Item)
        => $"chara/equipment/{item}/model/c{race}{item}{slot}.mdl";

    /// <summary>
    /// <paramref name="triangles"/> small triangles 2 cm apart, at <paramref name="x0"/> — the same corners for the same
    /// arguments. <paramref name="moved"/> of the corners are lifted 5 mm, off any other; <paramref name="reversed"/>
    /// lists the triangles backwards; <paramref name="duplicated"/> repeats the last ones, as a re-save's split
    /// vertices do.
    /// </summary>
    private static byte[] Tris(int moved = 0, bool reversed = false, int duplicated = 0, float x0 = 0f,
                               int triangles = 20, string slot = "_top")
    {
        var tris = new List<(float, float, float)[]>();
        for (int t = 0; t < triangles; t++)
        {
            float bx = x0 + t % 5 * 0.02f, by = t / 5 * 0.02f;
            tris.Add([(bx, by, 0f), (bx + 0.005f, by, 0f), (bx, by + 0.005f, 0f)]);
        }
        for (int c = 0; c < moved; c++)
        {
            var (x, y, z) = tris[c / 3][c % 3];
            tris[c / 3][c % 3] = (x, y, z + 0.005f);
        }
        for (int d = 0; d < duplicated; d++) tris.Add(tris[triangles - 1 - d]);
        if (reversed) tris.Reverse();
        return SyntheticModel.Build([], new SyntheticModel.Mesh($"/mt_c0101{Item}{slot}_a.mtrl",
                                                                new SyntheticModel.Sub(0, Corners: [.. tris.SelectMany(t => t)])));
    }

    /// <summary>The game's model: the same for every race here, which is all the compare needs.</summary>
    private static readonly byte[] Game = Tris();

    /// <summary>An author's design: nowhere near the game's.</summary>
    private static readonly byte[] Design = Tris(x0: 10f);

    /// <summary>A copy as TexTools packs it: one corner off (59 of 60), backwards, two triangles split twice.</summary>
    private static readonly byte[] Copy = Tris(moved: 1, reversed: true, duplicated: 2);

    private sealed record F(string Option, string GamePath, string File, byte[] Bytes);

    /// <summary>A mod of one Single group per entry of <paramref name="groups"/>, its files written.</summary>
    private static string Mod(params (string Group, F[] Files)[] groups)
    {
        string root = Path.Combine(Path.GetTempPath(), "proteus-gamecopy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manifest = new Dictionary<string, object>
        {
            ["FileVersion"] = 4,
            ["Name"] = "Copies",
            ["DefaultData"] = new Dictionary<string, object> { ["Files"] = new Dictionary<string, string>(),
                                                               ["FileSwaps"] = new Dictionary<string, string>(),
                                                               ["Manipulations"] = Array.Empty<object>() },
            ["Groups"] = groups.Select(g => new Dictionary<string, object>
            {
                ["Version"] = 0, ["Name"] = g.Group, ["Type"] = "Single", ["Priority"] = 0, ["DefaultSettings"] = 0,
                ["Options"] = g.Files.Select(f => f.Option).Distinct().Select(o => new Dictionary<string, object>
                {
                    ["Name"] = o,
                    ["Files"] = g.Files.Where(f => f.Option == o)
                                 .ToDictionary(f => f.GamePath, f => f.File.Replace('/', '\\')),
                }).ToList(),
            }).ToList(),
        };
        File.WriteAllText(Path.Combine(root, PenumbraModMeta.MetaFile), JsonSerializer.Serialize(manifest));
        foreach (var f in groups.SelectMany(g => g.Files))
        {
            string path = Path.Combine(root, f.File.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, f.Bytes);
        }
        return root;
    }

    /// <summary>The game's data: <see cref="Game"/> at every path given.</summary>
    private static Func<string, byte[]?> Reader(params string[] paths)
    {
        var known = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return p => known.Contains(p) ? Game : null;
    }

    /// <summary>The game's model for the three races of the usual mod.</summary>
    private static Func<string, byte[]?> Reader3(string slot = "_top")
        => Reader(PathOf("0101", slot), PathOf("0201", slot), PathOf("0901", slot));

    /// <summary>
    /// BOObies' shape: the author's women's design, the game's men's model packed by TexTools for Midlander men, and its
    /// copy for Highlander men too — all in the one option.
    /// </summary>
    private static string Boobies(byte[]? mens = null, string slot = "_top")
        => Mod(("Halter", [new F("Everything", PathOf("0201", slot), $"halter/c0201{slot}.mdl", Design),
                           new F("Everything", PathOf("0101", slot), $"halter/c0101{slot}.mdl", mens ?? Copy),
                           new F("Everything", PathOf("0901", slot), $"halter/c0901{slot}.mdl", Copy)]));

    private static IEnumerable<string> Names(IEnumerable<SizeSetRefit.Piece> pieces)
        => pieces.Select(p => Path.GetFileNameWithoutExtension(p.GamePath));

    private static void Delete(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    [Trait("Plan", "AC-old-game-copies-4")]
    [Trait("Plan", "AC-old-game-copies-1")]
    public void Panel_and_cli_offer_men_the_design_not_the_copy()   // [AC-old-game-copies-4] [AC-old-game-copies-1]
    {
        string root = Boobies();
        try
        {
            var pieces = SizeSetRefit.PiecesOf(root, Reader3());
            Assert.Equal(["c0201e6000_top"], Names(pieces));

            // The panel's bodies.
            var offered = SizeSetRefit.Offered(pieces).Select(c => c.Key).ToList();
            Assert.Contains("tbse@women", offered);
            Assert.Contains("tbse-x@women", offered);
            Assert.DoesNotContain("tbse", offered);
            Assert.DoesNotContain("tbse-x", offered);

            // The CLI's, asked for every body and the men's.
            var keys = SizeSetBatch.RunKeys(pieces, SizeSetBatch.ParseKeys("all", out _)!, men: true);
            Assert.Equal(offered.Where(k => k.EndsWith("@women", StringComparison.Ordinal)),
                         keys.Where(k => k.EndsWith("@women", StringComparison.Ordinal)));
            Assert.DoesNotContain("tbse", keys);

            Assert.Equal(["c0201e6000_top"], Names(SizeSetRefit.AcrossPieces(pieces)));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "AC-old-game-copies-4")]
    [Trait("Plan", "AC-old-game-copies-2")]
    public void Headless_run_keeps_the_copy_and_its_redirect()   // [AC-old-game-copies-4] [AC-old-game-copies-2]
    {
        string root = Boobies();
        string mods = Path.Combine(Path.GetTempPath(), "proteus-gamecopy-mods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mods);
        try
        {
            var log = Substitute.For<IPluginLog>();
            var outcome = SizeSetBatch.Run(new SizeSetBatch.Inputs(root, mods, SizeSetBatch.ParseKeys("all", out _)!, Men: true,
                                                                   Ends: false, Reader3(), null, log),
                                           new SizeSetRefit.RunLog(), _ => { }, CancellationToken.None);
            Assert.Null(outcome.RefusalCode);
            Assert.Contains("tbse@women", outcome.Keys);
            Assert.DoesNotContain("tbse", outcome.Keys);

            // The copy is the author's file still, given as before: men with no TBSE pick keep the mod's textures on it.
            Assert.Contains(PenumbraModMeta.ReadAllRedirects(root),
                            r => r.GamePath == PathOf("0101") && RefitCore.Normal(r.File) == "halter/c0101_top.mdl");
            Assert.Equal(Copy, File.ReadAllBytes(Path.Combine(root, "halter", "c0101_top.mdl")));
        }
        finally
        {
            Delete(root);
            Delete(mods);
        }
    }

    [Fact]
    [Trait("Plan", "AC-old-game-copies-3")]
    [Trait("Plan", "EDGE-old-game-copies-4")]
    public void A_vanilla_edit_fit_with_no_racial_copies_is_kept()   // [AC-old-game-copies-3] [EDGE-old-game-copies-4]
    {
        // A men's fit edited from the game's model, 58 of 60 still on it: with no other race shipped, the author's.
        string root = Mod(("Fit", [new F("Fit", PathOf("0201"), "fit/w.mdl", Design),
                                   new F("Fit", PathOf("0101"), "fit/m.mdl", Tris(moved: 2))]));
        try
        {
            var pieces = SizeSetRefit.PiecesOf(root, Reader3());
            Assert.Equal(["c0201e6000_top", "c0101e6000_top"], Names(pieces));
            Assert.Contains("tbse", SizeSetRefit.Offered(pieces).Select(c => c.Key));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "AC-old-game-copies-3")]
    [Trait("Plan", "EDGE-old-game-copies-8")]
    public void Per_race_fits_are_not_racial_copies()   // [AC-old-game-copies-3] [EDGE-old-game-copies-8]
    {
        // Other races' models that are the author's own fits — 60% and none of the game's — and a race the game has no
        // model for: none is a TexTools copy.
        string root = Mod(("Hands", [new F("Fit", PathOf("0201"), "h/w.mdl", Design),
                                     new F("Fit", PathOf("0101"), "h/m.mdl", Tris(moved: 2)),
                                     new F("Fit", PathOf("0901"), "h/hi.mdl", Tris(moved: 24)),
                                     new F("Fit", PathOf("1101"), "h/lala.mdl", Design),
                                     new F("Fit", PathOf("1301"), "h/au.mdl", Copy)]));
        try
        {
            var pieces = SizeSetRefit.PiecesOf(root, Reader(PathOf("0101"), PathOf("0201"), PathOf("0901"), PathOf("1101")));
            Assert.Contains("c0101e6000_top", Names(pieces));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "AC-old-game-copies-3")]
    [Trait("Plan", "EDGE-old-game-copies-5")]
    public void Two_different_fits_at_one_path_are_kept()   // [AC-old-game-copies-3] [EDGE-old-game-copies-5]
    {
        // Two fits near the game's, one per option: a copy is one model, an author's fits come per body.
        string root = Mod(("Body", [new F("TBSE", PathOf("0201"), "b/w.mdl", Design),
                                    new F("TBSE", PathOf("0101"), "b/tbse.mdl", Tris(moved: 1)),
                                    new F("TBSE-X", PathOf("0101"), "b/tbsex.mdl", Tris(moved: 2)),
                                    new F("TBSE", PathOf("0901"), "b/hi.mdl", Copy)]));
        try
        {
            var men = SizeSetRefit.PiecesOf(root, Reader3()).Single(p => p.Race == "0101");
            Assert.Equal(2, men.Options.Count);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-5")]
    public void One_copy_in_several_options_is_dropped()   // [EDGE-old-game-copies-5]
    {
        // The same copy, byte for byte, given by two options from two files.
        string root = Mod(("Colour", [new F("Red", PathOf("0201"), "red/w.mdl", Design),
                                      new F("Red", PathOf("0101"), "red/m.mdl", Copy),
                                      new F("Blue", PathOf("0101"), "blue/m.mdl", Copy),
                                      new F("Red", PathOf("0901"), "red/hi.mdl", Copy)]));
        try
        {
            Assert.Equal(["c0201e6000_top"], Names(SizeSetRefit.PiecesOf(root, Reader3())));
        }
        finally
        {
            Delete(root);
        }
    }

    [Theory]
    [Trait("Plan", "EDGE-old-game-copies-3")]
    [InlineData(3, false)]   // 57 of 60: 95%, a copy
    [InlineData(4, true)]    // 56 of 60: under the bar, kept
    public void The_bar_is_ninety_five(int moved, bool kept)   // [EDGE-old-game-copies-3]
    {
        string root = Boobies(Tris(moved: moved));
        try
        {
            Assert.Equal(kept, Names(SizeSetRefit.PiecesOf(root, Reader3())).Contains("c0101e6000_top"));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-6")]
    public void A_recolour_keeps_both_sexes()   // [EDGE-old-game-copies-6]
    {
        // Both sexes' models the game's, re-saved: nothing of the author's beside either, so neither goes.
        string root = Mod(("Colour", [new F("Red", PathOf("0201"), "w.mdl", Tris(reversed: true)),
                                      new F("Red", PathOf("0101"), "m.mdl", Copy),
                                      new F("Red", PathOf("0901"), "hi.mdl", Copy)]));
        try
        {
            Assert.Equal(["c0201e6000_top", "c0101e6000_top"], Names(SizeSetRefit.PiecesOf(root, Reader3())));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-6")]
    public void An_older_womens_copy_is_not_the_authors()   // [EDGE-old-game-copies-6]
    {
        // A women's model 70% the game's — an older version of it — is no design of the author's.
        string root = Boobies();
        File.WriteAllBytes(Path.Combine(root, "halter", "c0201_top.mdl"), Tris(moved: 18));
        try
        {
            Assert.Equal(["c0201e6000_top", "c0101e6000_top"], Names(SizeSetRefit.PiecesOf(root, Reader3())));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-7")]
    public void A_copy_with_no_womens_design_beside_it_is_kept()   // [EDGE-old-game-copies-7]
    {
        string root = Mod(("Fit", [new F("Fit", PathOf("0101"), "m.mdl", Copy),
                                   new F("Fit", PathOf("0901"), "hi.mdl", Copy)]));
        try
        {
            Assert.Equal(["c0101e6000_top"], Names(SizeSetRefit.PiecesOf(root, Reader3())));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-9")]
    public void No_game_model_means_the_authors()   // [EDGE-old-game-copies-9]
    {
        string root = Boobies();
        try
        {
            // None at the men's path: nothing of the game's to be.
            Assert.Contains("c0101e6000_top", Names(SizeSetRefit.PiecesOf(root, Reader(PathOf("0201"), PathOf("0901")))));
            // None at the women's: theirs is the author's, and the copy beside it goes.
            Assert.Equal(["c0201e6000_top"], Names(SizeSetRefit.PiecesOf(root, Reader(PathOf("0101"), PathOf("0901")))));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-10")]
    public void Without_game_data_nothing_changes()   // [EDGE-old-game-copies-10]
    {
        string root = Boobies();
        try
        {
            Assert.Equal(["c0201e6000_top", "c0101e6000_top"], Names(SizeSetRefit.PiecesOf(root)));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-11")]
    public void Unreadable_models_count_as_the_authors()   // [EDGE-old-game-copies-11]
    {
        byte[] garbage = [.. Enumerable.Range(0, 300).Select(i => (byte)(i * 7))];

        // The copy unreadable.
        string root = Boobies(garbage);
        try
        {
            Assert.Contains("c0101e6000_top", Names(SizeSetRefit.PiecesOf(root, Reader3())));
        }
        finally
        {
            Delete(root);
        }

        // The racial copy unreadable, the game's model unreadable, and a reader that throws.
        root = Boobies();
        File.WriteAllBytes(Path.Combine(root, "halter", "c0901_top.mdl"), garbage);
        try
        {
            Assert.Contains("c0101e6000_top", Names(SizeSetRefit.PiecesOf(root, Reader3())));
            Assert.Contains("c0101e6000_top", Names(SizeSetRefit.PiecesOf(root, p => p.Contains("c0101") ? garbage : Game)));
            Assert.Contains("c0101e6000_top",
                            Names(SizeSetRefit.PiecesOf(root, p => p.Contains("c0901") ? throw new IOException("gone") : Game)));
        }
        finally
        {
            Delete(root);
        }

        // On the exact rule's own path — a men's model alone, no women's beside it — an unreadable model is the
        // author's: it no longer stops the whole read.
        root = Mod(("Fit", [new F("Fit", PathOf("0101"), "m.mdl", garbage)]));
        try
        {
            Assert.Equal(["c0101e6000_top"], Names(SizeSetRefit.PiecesOf(root, Reader3())));
            Assert.Equal(["c0101e6000_top"], Names(SizeSetRefit.PiecesOf(root, _ => throw new IOException("gone"))));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-12")]
    public void A_mens_mod_drops_the_womens_copy()   // [EDGE-old-game-copies-12]
    {
        string root = Mod(("Suit", [new F("Suit", PathOf("0101"), "m.mdl", Design),
                                    new F("Suit", PathOf("0201"), "w.mdl", Copy),
                                    new F("Suit", PathOf("0901"), "hi.mdl", Copy)]));
        try
        {
            var pieces = SizeSetRefit.PiecesOf(root, Reader3());
            Assert.Equal(["c0101e6000_top"], Names(pieces));
            var offered = SizeSetRefit.Offered(pieces);
            Assert.Contains("tbse", offered.Select(c => c.Key));
            Assert.All(offered, c => Assert.True(c.Male && !c.ForMen, c.Key));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-13")]
    public void A_glove_copy_is_dropped_and_the_womens_glove_crossed()   // [EDGE-old-game-copies-13]
    {
        string root = Boobies(Tris(moved: 1, reversed: true, slot: "_glv"), "_glv");
        try
        {
            var pieces = SizeSetRefit.PiecesOf(root, Reader3("_glv"), includeEnds: true);
            Assert.Equal(["c0201e6000_glv"], Names(pieces));
            Assert.Equal(["c0201e6000_glv"], Names(SizeSetRefit.AcrossPieces(pieces)));
            Assert.Empty(SizeSetRefit.PiecesOf(root, Reader3("_glv"), includeEnds: false));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-14")]
    public void Our_own_outputs_are_not_racial_copies()   // [EDGE-old-game-copies-14]
    {
        // The only other race's model is one a refit of ours saved: not one TexTools packed.
        string root = Mod(("Halter", [new F("Everything", PathOf("0201"), "halter/w.mdl", Design),
                                      new F("Everything", PathOf("0101"), "halter/m.mdl", Copy),
                                      new F("Everything", PathOf("0901"), BodyRetargetWriter.Subfolder + "/hi.mdl", Copy)]));
        try
        {
            Assert.Contains("c0101e6000_top", Names(SizeSetRefit.PiecesOf(root, Reader3())));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-15")]
    public void Mixed_options_keep_the_piece()   // [EDGE-old-game-copies-15]
    {
        string root = Mod(("Style", [new F("Plain", PathOf("0201"), "w.mdl", Design),
                                     new F("Plain", PathOf("0101"), "plain/m.mdl", Copy),
                                     new F("Fancy", PathOf("0101"), "fancy/m.mdl", Tris(x0: 20f)),
                                     new F("Plain", PathOf("0901"), "hi.mdl", Copy)]));
        try
        {
            var men = SizeSetRefit.PiecesOf(root, Reader3()).Single(p => p.Race == "0101");
            Assert.Equal(["Plain", "Fancy"], men.Options.Select(o => o.Option));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-16")]
    public void A_rerun_leaves_earlier_men_options_alone()   // [EDGE-old-game-copies-16]
    {
        // An earlier run sized the copy onto TBSE, into a group of ours: those stay as they are.
        string root = Mod(("Halter", [new F("Everything", PathOf("0201"), "halter/w.mdl", Design),
                                      new F("Everything", PathOf("0101"), "halter/m.mdl", Copy),
                                      new F("Everything", PathOf("0901"), "halter/hi.mdl", Copy)]),
                          ("Body — Halter", [new F("TBSE", PathOf("0101"), BodyRetargetWriter.Subfolder + "/tbse.mdl", Tris(x0: 30f))]));
        try
        {
            byte[] manifest = File.ReadAllBytes(Path.Combine(root, PenumbraModMeta.MetaFile));
            byte[] earlier = File.ReadAllBytes(Path.Combine(root, BodyRetargetWriter.Subfolder, "tbse.mdl"));
            SizeSetRefit.Run(new SizeSetRefit.Request(root, ["tbse"], Yas: false, Remake: false, [], Reader3(), Ends: false),
                             null, Substitute.For<IPluginLog>(), _ => { }, CancellationToken.None);
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(root, PenumbraModMeta.MetaFile)));
            Assert.Equal(earlier, File.ReadAllBytes(Path.Combine(root, BodyRetargetWriter.Subfolder, "tbse.mdl")));
            Assert.Equal(["c0201e6000_top"], Names(SizeSetRefit.PiecesOf(root, Reader3())));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-18")]
    public void A_racial_copy_of_another_item_does_not_count()   // [EDGE-old-game-copies-18]
    {
        // The other race's copy is of another item: no copy of this one.
        string other = PathOf("0901", item: "e6001");
        string root = Mod(("Halter", [new F("Everything", PathOf("0201"), "halter/w.mdl", Design),
                                      new F("Everything", PathOf("0101"), "halter/m.mdl", Copy),
                                      new F("Everything", other, "halter/hi.mdl", Copy)]));
        try
        {
            Assert.Contains("c0101e6000_top", Names(SizeSetRefit.PiecesOf(root, Reader(PathOf("0101"), PathOf("0201"), other))));
        }
        finally
        {
            Delete(root);
        }

        // Of this item, named in capitals: it counts.
        root = Mod(("Halter", [new F("Everything", PathOf("0201"), "halter/w.mdl", Design),
                               new F("Everything", PathOf("0101"), "halter/m.mdl", Copy),
                               new F("Everything", PathOf("0901").ToUpperInvariant(), "halter/hi.mdl", Copy)]));
        try
        {
            Assert.Equal(["c0201e6000_top"], Names(SizeSetRefit.PiecesOf(root, Reader3())));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-20")]
    public void A_dropped_copy_is_logged_by_path()   // [EDGE-old-game-copies-20]
    {
        string root = Boobies();
        try
        {
            var dropped = new List<string>();
            SizeSetRefit.PiecesOf(root, Reader3(), droppedCopy: dropped.Add);
            Assert.Equal([PathOf("0101")], dropped);

            var log = Substitute.For<IPluginLog>();
            SizeSetRefit.Run(new SizeSetRefit.Request(root, [], Yas: false, Remake: false, [], Reader3(), Ends: false),
                             null, log, _ => { }, CancellationToken.None);
            bool Mentions(NSubstitute.Core.ICall call, string path)
                => call.GetArguments().SelectMany(a => a is object?[] list ? list : [a]).Any(a => a as string == path);
            var debug = log.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IPluginLog.Debug)).ToList();
            Assert.Single(debug, c => Mentions(c, PathOf("0101")));
            Assert.DoesNotContain(debug, c => Mentions(c, PathOf("0201")) || Mentions(c, PathOf("0901")));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    [Trait("Plan", "EDGE-old-game-copies-21")]
    public void A_follower_of_a_dropped_copy_is_recomputed()   // [EDGE-old-game-copies-21]
    {
        // The men's glove followed the men's top — the copy — in their size group. The copy gone, it follows nothing.
        string root = Mod(("Size", [new F("S", PathOf("0201"), "s/w.mdl", Design),
                                    new F("S", PathOf("0101"), "s/m.mdl", Copy),
                                    new F("S", PathOf("0101", "_glv"), "s/mglv.mdl", Tris(x0: 40f, slot: "_glv")),
                                    new F("S", PathOf("0901"), "s/hi.mdl", Copy)]));
        try
        {
            Assert.True(SizeSetRefit.PiecesOf(root).Single(p => p.Slot == "_glv").FollowsChest);

            var pieces = SizeSetRefit.PiecesOf(root, Reader3());
            Assert.Equal(["c0201e6000_top", "c0101e6000_glv"], Names(pieces));
            Assert.False(pieces.Single(p => p.Slot == "_glv").FollowsChest);
        }
        finally
        {
            Delete(root);
        }
    }
}
