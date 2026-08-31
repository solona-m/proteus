using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The skin shell's template follows the WEARER's race.
/// <para/>
/// A body material carries a CategorySkinType shader key telling skin.shpk which skin path to take, and
/// Hrothgar (c1501) ships a different value from every other body. Cloning the Midlander material onto a
/// Hrothgar would light the shell down the non-fur path while the body right beside it takes the other, so
/// the race code has to reach the template choice.
/// </summary>
public class SkinTemplateTests
{
    [Fact]
    public void The_skin_template_follows_the_race_code()
    {
        Assert.Equal("chara/human/c1501/obj/body/b0001/material/v0001/mt_c1501b0001_a.mtrl",
            GearMaterialWriter.TemplateFor("skin.shpk", "1501"));
        Assert.Equal("chara/human/c0801/obj/body/b0001/material/v0001/mt_c0801b0001_a.mtrl",
            GearMaterialWriter.TemplateFor("skin.shpk", "0801"));
    }

    /// <summary>Unknown race ⇒ the Midlander default, which the caller also falls back to when a race ships
    /// no such material.</summary>
    [Fact]
    public void An_unknown_race_keeps_the_midlander_default()
    {
        var midlander = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl";
        Assert.Equal(midlander, GearMaterialWriter.TemplateFor("skin.shpk"));
        Assert.Equal(midlander, GearMaterialWriter.TemplateFor("skin.shpk", null));
        Assert.Equal(midlander, GearMaterialWriter.TemplateFor("skin.shpk", "  "));
    }

    /// <summary>The race code is for the SKIN arm only — gear templates are equipment models, which are
    /// keyed to a model race and cloned as-is.</summary>
    [Fact]
    public void The_gear_templates_ignore_the_race_code()
    {
        Assert.Equal(GearMaterialWriter.TemplateFor("character.shpk"),
                     GearMaterialWriter.TemplateFor("character.shpk", "1501"));
        Assert.Equal(GearMaterialWriter.TemplateFor("characterscroll.shpk"),
                     GearMaterialWriter.TemplateFor("characterscroll.shpk", "1501"));
    }

    /// <summary>skin.shpk takes three textures and no "id" slot — it declares no row selector to bind one
    /// to.</summary>
    [Fact]
    public void The_skin_shader_takes_three_textures_and_no_index()
    {
        Assert.Equal(["base", "norm", "mask"], GearMaterialWriter.TextureOrder("skin.shpk"));
        Assert.DoesNotContain("id", GearMaterialWriter.TextureOrder("skin.shpk"));
    }

    /// <summary>
    /// A FACE shell takes a face material, not the body's. Both are skin.shpk, but the body declares
    /// CategorySkinType and ships g_AlphaThreshold at 0 while a face declares no shader keys and ships 0.5 —
    /// so cloning the body onto face geometry lights it down the body's path with the wrong cutoff. Face
    /// materials also carry no version folder, unlike the body's.
    /// </summary>
    [Fact]
    public void A_face_surface_takes_a_face_material()
    {
        Assert.Equal("chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_fac_a.mtrl",
            GearMaterialWriter.TemplateFor("skin.shpk", "0201", "f0001"));
        Assert.Equal("chara/human/c1401/obj/face/f0004/material/mt_c1401f0004_fac_a.mtrl",
            GearMaterialWriter.TemplateFor("skin.shpk", "1401", "f0004"));

        // No face id ⇒ the body, which is what every non-face surface passes.
        Assert.Contains("/obj/body/", GearMaterialWriter.TemplateFor("skin.shpk", "0201"));
        Assert.Contains("/obj/body/", GearMaterialWriter.TemplateFor("skin.shpk", "0201", null));
    }

    /// <summary>
    /// The texture table is read back so a slot the overlay doesn't supply can inherit what the surface it
    /// copies actually wears — the body's mask is shared, a face's is its own, and Hrothgar's body has a
    /// third. Hardcoding any one of them would be wrong for the other two.
    /// </summary>
    [Fact]
    public void TextureNames_reads_the_slots_back()
    {
        var built = GearMaterialWriter.Build(
            SyntheticMtrl(), ["a/base.tex", "a/norm.tex", "a/mask.tex"], null);
        Assert.Equal(["a/base.tex", "a/norm.tex", "a/mask.tex"], GearMaterialWriter.TextureNames(built));
    }

    /// <summary>A minimal 3-texture skin.shpk material, enough for the writer to repoint.</summary>
    private static byte[] SyntheticMtrl()
    {
        var strings = new System.IO.MemoryStream();
        var offs = new System.Collections.Generic.List<int>();
        void Put(string s)
        {
            offs.Add((int)strings.Position);
            strings.Write(System.Text.Encoding.ASCII.GetBytes(s));
            strings.WriteByte(0);
        }
        Put("t0.tex"); Put("t1.tex"); Put("t2.tex");
        Put("map1"); Put("map2");
        Put("colorset");
        Put("skin.shpk");
        while (strings.Position % 4 != 0) strings.WriteByte(0);
        var str = strings.ToArray();

        const int texCount = 3, uvCount = 2, csCount = 1;
        int head = 16 + (texCount + uvCount + csCount) * 4;
        var m = new byte[head + str.Length + 12];
        System.BitConverter.GetBytes((ushort)m.Length).CopyTo(m, 4);
        System.BitConverter.GetBytes((ushort)0).CopyTo(m, 6);              // dataSetSize
        System.BitConverter.GetBytes((ushort)str.Length).CopyTo(m, 8);
        System.BitConverter.GetBytes((ushort)offs[6]).CopyTo(m, 10);       // shpk name
        m[12] = texCount; m[13] = uvCount; m[14] = csCount; m[15] = 0;
        for (int i = 0; i < texCount; i++) System.BitConverter.GetBytes((ushort)offs[i]).CopyTo(m, 16 + i * 4);
        for (int i = 0; i < uvCount; i++) System.BitConverter.GetBytes((ushort)offs[3 + i]).CopyTo(m, 16 + texCount * 4 + i * 4);
        System.BitConverter.GetBytes((ushort)offs[5]).CopyTo(m, 16 + (texCount + uvCount) * 4);
        str.CopyTo(m, head);
        return m;
    }
}
