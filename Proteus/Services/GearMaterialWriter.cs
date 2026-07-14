using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// One color table row for a gear overlay. Null fields keep the template's value.
/// </summary>
public sealed class GearColorRow
{
    public (float R, float G, float B)? Diffuse { get; init; }
    public (float R, float G, float B)? Emissive { get; init; }
    public (float R, float G, float B)? Specular { get; init; }

    /// <summary>
    /// Slice of the shared array chara/common/texture/sphere_d_array.tex. Needs no material texture.
    /// Both this and <see cref="SphereMapMask"/> must be set — an index with a zero mask does nothing.
    /// </summary>
    public int? SphereMapIndex { get; init; }

    /// <summary>Sphere map intensity (blend strength).</summary>
    public float? SphereMapMask { get; init; }

    public float? Roughness { get; init; }
    public float? Metalness { get; init; }
}

/// <summary>
/// Writes the .mtrl for a second-skin shell by cloning a known-good vanilla material of the target
/// shader, repointing its texture table at our own paths, and patching its color table.
///
/// We clone rather than synthesise because a material's shader keys, constants and sampler table must
/// agree with its .shpk, and Lumina misreads the Dawntrail shader section — so the tail is copied
/// verbatim and only the parts we understand are rewritten.
/// </summary>
public static class GearMaterialWriter
{
    /// <summary>
    /// Game path of the VANILLA material used as a template, or null when we ship our own.
    ///
    /// character.shpk clones a real shipping item (e0041), so it needs nothing installed.
    ///
    /// characterscroll does NOT: every vanilla characterscroll material carries a non-zero colorset
    /// emissive, which renders as a flat white glow that drowns out the scroll map entirely. So for
    /// that shader we ship a known-good material as an embedded resource instead — see
    /// <see cref="EmbeddedTemplate"/>.
    /// </summary>
    public static string? TemplateFor(string shaderPackage) => shaderPackage switch
    {
        "characterscroll.shpk" => null,   // use the embedded one
        _                      => "chara/equipment/e0041/material/v0001/mt_c0201e0041_top_a.mtrl",
    };

    /// <summary>The material we ship for a shader, or null when a vanilla template is used instead.</summary>
    public static byte[]? EmbeddedTemplate(string shaderPackage)
    {
        if (!string.Equals(shaderPackage, "characterscroll.shpk", StringComparison.OrdinalIgnoreCase))
            return null;

        using var s = typeof(GearMaterialWriter).Assembly
            .GetManifestResourceStream("Proteus.Resources.characterscroll_template.mtrl");
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Texture slot order the shader's template expects — neither the count nor the order matches
    /// between them, so this drives the texture table:
    ///   character.shpk       4: base, norm, mask, id
    ///   characterscroll.shpk 5: norm, mask, id, catc, base
    /// "catc" is the scrolling map that drives the animated emissive (mods often name it "_o"); it is
    /// the glow itself — its colour and intensity — not a mask on the colorset's emissive.
    /// </summary>
    public static IReadOnlyList<string> TextureOrder(string shaderPackage) => shaderPackage switch
    {
        "characterscroll.shpk" => ["norm", "mask", "id", "catc", "base"],
        _                      => ["base", "norm", "mask", "id"],
    };

    /// <summary>
    /// Material shader flags (the "Enable Transparency" / "Hide Backfaces" toggles in a material editor).
    ///
    /// TRANSPARENCY IS NOT ON BY DEFAULT: the vanilla character.shpk template ships flags 0x0D — no
    /// 0x10 — so the normal map's blue channel (the gear alpha gate) is simply IGNORED and the shell
    /// renders fully opaque. A second skin is always a transparent surface, so we force both bits on.
    /// </summary>
    private const uint FlagHideBackfaces = 0x01;
    private const uint FlagTransparency = 0x10;

    /// <summary>
    /// g_AlphaThreshold. The vanilla templates ship this at 0, which makes the shader treat the normal
    /// map's blue channel as a binary cutout — every pixel is either fully opaque or discarded, so a
    /// sheer overlay renders solid. Setting it to 1 turns on real alpha blending, which is what a second
    /// skin always wants.
    /// </summary>
    private const uint ConstAlphaThreshold = 0x29AC0223;

    // Dawntrail color table row = 32 halves (64B). Offsets per Penumbra.GameData ColorTableRow.cs.
    private const int HDiffuse = 0, HSpecular = 4, HEmissive = 8;
    private const int HRoughness = 16, HMetalness = 18;
    private const int HSphereMask = 21, HSphereIndex = 27;
    private const int RowCount = 32, RowBytes = 64;

    /// <summary>
    /// Clone <paramref name="template"/>, point it at <paramref name="texturePaths"/> (which must be in
    /// the shader's slot order — see <see cref="TextureOrder"/>), and apply <paramref name="rows"/>
    /// (keyed by 0-based color table row; absent rows keep the template's values).
    /// </summary>
    public static byte[] Build(byte[] template, IReadOnlyList<string> texturePaths, IReadOnlyDictionary<int, GearColorRow>? rows)
    {
        var m = template;
        ushort U16(int o) => BitConverter.ToUInt16(m, o);

        ushort fileSize = U16(4), dataSetSize = U16(6), strTableSize = U16(8);
        byte texCount = m[12], uvCount = m[13], colorSetCount = m[14], addDataSize = m[15];

        int texTbl = 16, uvTbl = texTbl + texCount * 4, csTbl = uvTbl + uvCount * 4;
        int strStart = csTbl + colorSetCount * 4;

        string StrAt(int rel)
        {
            int o = strStart + rel, e = o;
            while (m[e] != 0) e++;
            return Encoding.ASCII.GetString(m, o, e - o);
        }

        if (texturePaths.Count != texCount)
            throw new ArgumentException($"template wants {texCount} textures, got {texturePaths.Count}", nameof(texturePaths));

        string uvName = StrAt(U16(uvTbl)), csName = StrAt(U16(csTbl)), shpkName = StrAt(U16(10));

        // Rebuild the string table with our texture paths.
        var sb = new MemoryStream();
        var offs = new List<int>();
        void Put(string x)
        {
            offs.Add((int)sb.Position);
            sb.Write(Encoding.ASCII.GetBytes(x));
            sb.WriteByte(0);
        }
        foreach (var tp in texturePaths) Put(tp);
        int uvOff = (int)sb.Position; Put(uvName);
        int csOff = (int)sb.Position; Put(csName);
        int shpkOff = (int)sb.Position; Put(shpkName);
        while (sb.Position % 4 != 0) sb.WriteByte(0);
        byte[] strings = sb.ToArray();

        var outMs = new MemoryStream();
        void OW16(ushort v)
        {
            Span<byte> t = stackalloc byte[2];
            BitConverter.TryWriteBytes(t, v);
            outMs.Write(t);
        }

        outMs.Write(m, 0, 4);                                            // version
        OW16((ushort)(fileSize + (strings.Length - strTableSize)));
        OW16(dataSetSize);
        OW16((ushort)strings.Length);
        OW16((ushort)shpkOff);
        outMs.Write(m, 12, 4);                                           // counts
        for (int i = 0; i < texCount; i++) { OW16((ushort)offs[i]); OW16(U16(texTbl + i * 4 + 2)); }
        for (int i = 0; i < uvCount; i++) { OW16((ushort)uvOff); OW16(U16(uvTbl + i * 4 + 2)); }
        for (int i = 0; i < colorSetCount; i++) { OW16((ushort)csOff); OW16(U16(csTbl + i * 4 + 2)); }
        outMs.Write(strings);
        int afterStrings = strStart + strTableSize;
        outMs.Write(m, afterStrings, m.Length - afterStrings);           // additional data + color table + shader section, verbatim
        byte[] r = outMs.ToArray();

        // Shader section sits right after the data set. Its layout is
        // { u16 valueListSize, u16 keyCount, u16 constCount, u16 samplerCount, u32 flags }.
        int shaderStart = 16 + texCount * 4 + uvCount * 4 + colorSetCount * 4 + strings.Length
                        + addDataSize + dataSetSize;
        if (shaderStart + 12 <= r.Length)
        {
            uint flags = BitConverter.ToUInt32(r, shaderStart + 8) | FlagTransparency | FlagHideBackfaces;
            BitConverter.GetBytes(flags).CopyTo(r, shaderStart + 8);
        }

        // Without this the blue-channel alpha is a binary cutout and a sheer overlay renders solid.
        var (withAlpha, found) = TextureLoader.PatchConstantValues(r, ConstAlphaThreshold, 1f);
        if (found) r = withAlpha;

        if (rows is { Count: > 0 })
        {
            int csStart = 16 + texCount * 4 + uvCount * 4 + colorSetCount * 4 + strings.Length + addDataSize;
            foreach (var (row, def) in rows)
            {
                if (row < 0 || row >= RowCount) continue;
                int b = csStart + row * RowBytes;
                void WH(int half, float v) => BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)v)).CopyTo(r, b + half * 2);

                if (def.Diffuse is { } d) { WH(HDiffuse, d.R); WH(HDiffuse + 1, d.G); WH(HDiffuse + 2, d.B); }
                if (def.Emissive is { } e) { WH(HEmissive, e.R); WH(HEmissive + 1, e.G); WH(HEmissive + 2, e.B); }
                if (def.Specular is { } sp) { WH(HSpecular, sp.R); WH(HSpecular + 1, sp.G); WH(HSpecular + 2, sp.B); }
                if (def.SphereMapIndex is { } si) WH(HSphereIndex, si);
                if (def.SphereMapMask is { } sm) WH(HSphereMask, sm);
                if (def.Roughness is { } ro) WH(HRoughness, ro);
                if (def.Metalness is { } me) WH(HMetalness, me);
            }
        }

        return r;
    }
}
