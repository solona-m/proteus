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
    /// VANILLA materials used as templates, one per supported gear shader — both are real shipping
    /// items, so they are always in game data and we never depend on the user having a given mod.
    ///
    /// The two shaders take different texture counts AND orders, so cloning the right template is what
    /// keeps the texture table agreeing with the shader. Slot orders are read from the vanilla files
    /// (see <see cref="TextureOrder"/>) — do not assume they match a modded material's layout.
    /// </summary>
    public static string TemplateFor(string shaderPackage) => shaderPackage switch
    {
        "characterscroll.shpk" => "chara/equipment/e6257/material/v0001/mt_c0201e6257_top_a.mtrl",
        _                      => "chara/equipment/e0041/material/v0001/mt_c0201e0041_top_a.mtrl",
    };

    /// <summary>
    /// Texture slot order the given shader's template expects. Both are 4 textures, but neither the
    /// order nor the set matches:
    ///   character.shpk       base, norm, mask, id
    ///   characterscroll.shpk norm, mask, id, catc   -- "catc" is the scrolling map that drives the
    ///                                                  animated emissive (mods often name it "_o");
    ///                                                  vanilla characterscroll has NO base texture.
    /// </summary>
    public static IReadOnlyList<string> TextureOrder(string shaderPackage) => shaderPackage switch
    {
        "characterscroll.shpk" => ["norm", "mask", "id", "catc"],
        _                      => ["base", "norm", "mask", "id"],
    };

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
                if (def.SphereMapIndex is { } si) WH(HSphereIndex, si);
                if (def.SphereMapMask is { } sm) WH(HSphereMask, sm);
                if (def.Roughness is { } ro) WH(HRoughness, ro);
                if (def.Metalness is { } me) WH(HMetalness, me);
            }
            _ = HSpecular;
        }

        return r;
    }
}
