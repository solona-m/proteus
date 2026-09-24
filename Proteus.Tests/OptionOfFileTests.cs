using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Which option a refit was cut from, so the save can carry that option's material and textures across.
/// <para/>
/// The whole difficulty is separators. Penumbra writes an author's manifest with backslashes —
/// <c>chest size\small\chara\…</c> — while this writer and the Studio use forward slashes, so a literal comparison
/// matches only the options Proteus wrote itself and never an author's. An author's is the only kind that matters
/// here, so getting this wrong makes the whole feature silently do nothing on real mods while passing any test whose
/// fixture was written with forward slashes.
/// </summary>
public class OptionOfFileTests
{
    private static PenumbraModMeta.Redirect At(string file, string source)
        => new("chara/equipment/e0118/model/c0201e0118_top.mdl", file, source);

    [Fact]
    public void An_author_s_option_is_found_though_its_manifest_uses_backslashes()
    {
        var redirects = new[] { At(@"chest size\small\chara\equipment\e0118\model\c0201e0118_top.mdl", "Size / Small") };

        Assert.Equal("Small", BodyRetargetWriter.OptionOfFile(
            redirects, "chest size/small/chara/equipment/e0118/model/c0201e0118_top.mdl", "Size"));
    }

    [Fact]
    public void One_of_Proteus_s_own_options_is_found_too()
    {
        var redirects = new[] { At("Body Retarget/Rue+ Small/chara/equipment/e0118/model/c0201e0118_top.mdl",
                                   "Size / Rue+ Small") };

        Assert.Equal("Rue+ Small", BodyRetargetWriter.OptionOfFile(
            redirects, "Body Retarget/Rue+ Small/chara/equipment/e0118/model/c0201e0118_top.mdl", "Size"));
    }

    [Fact]
    public void An_option_of_another_group_is_not_offered()
    {
        // Saving into "Size" does not switch "Content Type" off, so there is nothing of its to replace — and copying
        // would duplicate redirects that are still live.
        var redirects = new[] { At(@"content type\sfw\chara\equipment\e0118\model\c0201e0118_top.mdl",
                                   "Content Type / SFW") };

        Assert.Null(BodyRetargetWriter.OptionOfFile(
            redirects, "content type/sfw/chara/equipment/e0118/model/c0201e0118_top.mdl", "Size"));
    }

    [Fact]
    public void A_model_from_the_default_files_has_no_option()
    {
        // A default-data redirect's source is the mod, with no " / option" after it. Nothing switches it off.
        var redirects = new[] { At(@"body\model.mdl", "An Outfit") };

        Assert.Null(BodyRetargetWriter.OptionOfFile(redirects, "body/model.mdl", "Size"));
    }

    [Fact]
    public void A_different_model_in_the_same_group_is_not_mistaken_for_it()
    {
        var redirects = new[]
        {
            At(@"size\large\chara\equipment\e0118\model\c0201e0118_top.mdl",  "Size / Large"),
            At(@"size\medium\chara\equipment\e0118\model\c0201e0118_top.mdl", "Size / Medium"),
        };

        Assert.Equal("Medium", BodyRetargetWriter.OptionOfFile(
            redirects, "size/medium/chara/equipment/e0118/model/c0201e0118_top.mdl", "Size"));
    }
}
