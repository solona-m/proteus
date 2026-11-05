using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.VariantRepointTestKit;

namespace Proteus.Tests;

/// <summary><see cref="PenumbraModMeta.EditContainers"/> and <see cref="PenumbraModMeta.ReadContainerRedirects"/>. SENSITIVE writer.</summary>
public class PenumbraModMetaContainerTests
{
    private static Dictionary<string, string> D(params (string K, string V)[] kv) => kv.ToDictionary(p => p.K, p => p.V);

    private static string Combo(VariantRepointTestKit k)
        => k.Mod("Combo", D(("chara/d.tex", "d.tex")),
                 groups: [("Colour", "Single", [("Red", D(("chara/r.tex", "r.tex")), []), ("Blue", D(("chara/b.tex", "b.tex")), [])]),
                          ("Extras", "Multi", [("Glow", D(("chara/g.tex", "g.tex")), [])]),
                          ("Looks", "Combining", [("A", D(("chara/a.tex", "a.tex")), []), ("B", D(("chara/c.tex", "c.tex")), [])])]);

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-45")]
    public void EditContainers_adds_into_default_option_and_combining_container()
    {
        using var k = new VariantRepointTestKit();
        var mod = Combo(k);
        var before = Manifest(mod);
        PenumbraModMeta.EditContainers(mod,
        [
            new(new(""), D(("chara/x0.tex", "x0.tex")), D()),
            new(new("Colour", "Blue"), D(("chara/x1.tex", "x1.tex")), D()),
            new(new("Extras", "Glow"), D(("chara/x2.tex", "x2.tex")), D()),
            new(new("Looks", null, 1), D(("chara/x3.tex", "x3.tex")), D()),
        ]);
        var reds = PenumbraModMeta.ReadContainerRedirects(mod);
        Assert.Contains(reds, r => r.Container.IsDefault && r.GamePath == "chara/x0.tex");
        Assert.Contains(reds, r => r.Container.Option == "Blue" && r.File == "x1.tex");
        Assert.Contains(reds, r => r.Container.Option == "Glow" && r.File == "x2.tex");
        Assert.Contains(reds, r => r.Container.Index == 1 && r.File == "x3.tex");
        var after = Manifest(mod);
        Assert.Equal(before.Select(p => p.Key), after.Select(p => p.Key));
        Assert.Equal(before["Groups"]!.AsArray().Select(g => g!["Name"]!.GetValue<string>()),
                     after["Groups"]!.AsArray().Select(g => g!["Name"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(before["Groups"]![0]!["Options"]![0], after["Groups"]![0]!["Options"]![0]));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-45")]
    public void EditContainers_never_overwrites_an_existing_key()
    {
        using var k = new VariantRepointTestKit();
        var mod = Combo(k);
        PenumbraModMeta.EditContainers(mod, [new(new("Colour", "Red"), D(("CHARA\\R.TEX", "other.tex")), D())]);
        Assert.Equal("r.tex", FilesOf(mod, new("Colour", "Red")).Single().Value);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-61")]
    public void EditContainers_appends_after_existing_keys()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", D(("chara/z.tex", "z.tex"), ("chara/a.tex", "a.tex")));
        PenumbraModMeta.EditContainers(mod, [new(new(""), D(("chara/m.tex", "m.tex")), D())]);
        Assert.Equal(["chara/z.tex", "chara/a.tex", "chara/m.tex"],
                     Manifest(mod)["DefaultData"]!["Files"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-20")]
    public void EditContainers_missing_container_throws_and_writes_nothing()
    {
        using var k = new VariantRepointTestKit();
        var mod = Combo(k);
        var before = File.ReadAllText(Path.Combine(mod, "meta.json"));
        Assert.Throws<InvalidOperationException>(() => PenumbraModMeta.EditContainers(mod,
            [new(new(""), D(("chara/x.tex", "x.tex")), D()), new(new("Colour", "Green"), D(("chara/y.tex", "y.tex")), D())]));
        Assert.Throws<InvalidOperationException>(() => PenumbraModMeta.EditContainers(mod,
            [new(new("Looks", null, 5), D(("chara/y.tex", "y.tex")), D())]));
        Assert.Throws<InvalidOperationException>(() => PenumbraModMeta.EditContainers(mod,
            [new(new("Looks", "A"), D(("chara/y.tex", "y.tex")), D())]));   // a combining option holds no files
        Assert.Equal(before, File.ReadAllText(Path.Combine(mod, "meta.json")));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-31")]
    public void EditContainers_preserves_non_ascii_and_unknown_keys()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("色", D(("chara/d.tex", "ünï.tex")), groups: [("赤", "Single", [("一", D(("chara/r.tex", "赤.tex")), [])])]);
        var m = Manifest(mod);
        m["Unknown"] = new JsonObject { ["Deep"] = new JsonArray("二") };
        WriteManifest(mod, m);
        PenumbraModMeta.EditContainers(mod, [new(new("赤", "一"), D(("chara/x.tex", "x.tex")), D())]);
        var after = Manifest(mod);
        Assert.True(JsonNode.DeepEquals(m["Unknown"], after["Unknown"]));
        Assert.True(JsonNode.DeepEquals(m["DefaultData"], after["DefaultData"]));
        Assert.Equal("赤.tex", after["Groups"]![0]!["Options"]![0]!["Files"]!["chara/r.tex"]!.GetValue<string>());
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-53")]
    public void EditContainers_removes_only_a_key_still_naming_the_file()
    {
        using var k = new VariantRepointTestKit();
        var mod = Combo(k);
        PenumbraModMeta.EditContainers(mod, [new(new("Colour", "Red"), D(), D(("CHARA/R.TEX", "OTHER.tex")))]);
        Assert.Single(FilesOf(mod, new("Colour", "Red")));
        PenumbraModMeta.EditContainers(mod, [new(new("Colour", "Red"), D(), D(("CHARA/R.TEX", "R.TEX")))]);
        Assert.Empty(FilesOf(mod, new("Colour", "Red")));
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-45")]
    public void EditContainers_removal_takes_out_only_its_key()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.Mod("Dress", D(("chara/m.mdl", "top.mdl"), (MaterialPath(Top, 2), "top_a.mtrl"), (MaterialPath(Top, 3), "top_a.mtrl")));
        PenumbraModMeta.EditContainers(mod, [new(new(""), D(), D((MaterialPath(Top, 2), "top_a.mtrl")))]);
        Assert.Equal(["chara/m.mdl", MaterialPath(Top, 3)], FilesOf(mod, Default).Keys);
    }

    [Fact]
    [Trait("Plan", "EDGE-imc-all-variants-6")]
    public void EditContainers_refuses_a_v3_folder()
    {
        using var k = new VariantRepointTestKit();
        var mod = k.LegacyMod("Old", Top);
        Assert.Throws<PenumbraModMeta.LegacyFolderException>(() =>
            PenumbraModMeta.EditContainers(mod, [new(new(""), D(("chara/x.tex", "x.tex")), D())]));
        Assert.Empty(PenumbraModMeta.ReadContainerRedirects(mod));
    }
}
