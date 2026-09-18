using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// One color table row for a gear overlay. Null fields keep the template's value.
/// </summary>
// A record so callers can merge with `row with { … }`, which copies any field added later instead of dropping it.
public sealed record GearColorRow
{
    public (float R, float G, float B)? Diffuse { get; init; }
    public (float R, float G, float B)? Emissive { get; init; }
    public (float R, float G, float B)? Specular { get; init; }

    /// <summary>
    /// The Glow dial as the user set it, before it was multiplied into <see cref="Emissive"/>. characterscroll
    /// needs the number itself (the effect's strength). Null on a row whose glow was never set.
    /// </summary>
    public float? EmissiveStrength { get; init; }

    /// <summary>
    /// Slice of the shared array chara/common/texture/sphere_d_array.tex. Needs no material texture.
    /// Both this and <see cref="SphereMapMask"/> must be set — an index with a zero mask does nothing.
    /// </summary>
    public int? SphereMapIndex { get; init; }

    /// <summary>Sphere map intensity (blend strength).</summary>
    public float? SphereMapMask { get; init; }

    public float? Roughness { get; init; }
    public float? Metalness { get; init; }

    /// <summary>
    /// Slice of the shared array chara/common/texture/tile_norm_array.tex (0–63): the fabric weave tiled over
    /// this row. Null leaves the material's own value.
    /// </summary>
    public int? TileIndex { get; init; }

    /// <summary>How strongly the weave shows. Null means full strength. Ignored without a
    /// <see cref="TileIndex"/>.</summary>
    public float? TileStrength { get; init; }

    /// <summary>Weave repeats per UV axis (the tile transform's diagonal). Either present writes the whole
    /// matrix with zero skew, a missing axis defaulting to 16. Ignored without a <see cref="TileIndex"/>.</summary>
    public float? TileScaleU { get; init; }

    /// <inheritdoc cref="TileScaleU"/>
    public float? TileScaleV { get; init; }
}

/// <summary>
/// How a characterscroll material's scroll map flows. Speed and tiling are material constants, and
/// vanilla ships the speeds at ZERO, so a material with no settings sits still.
/// </summary>
public sealed record ScrollSettings(float SpeedX, float SpeedY, float TilingX, float TilingY)
{
    public static readonly ScrollSettings Default = new(0.15f, 0.15f, 5f, 5f);
}

/// <summary>
/// Writes the .mtrl for a second-skin shell by cloning a vanilla material of the target shader, repointing its
/// texture table and patching its color table. Cloned, not synthesised: keys, constants and samplers must agree
/// with the .shpk, so the tail is copied verbatim.
/// </summary>
public static class GearMaterialWriter
{
    /// <summary>
    /// Game path of the VANILLA material used as a template. character.shpk clones e0041, characterscroll e6257,
    /// and skin.shpk a vanilla body/face material. skin.shpk declares no <c>g_SamplerIndex</c>, so it has no
    /// per-texel row selection; it is only for skin-mode overlays promoted to a shell, which want the skin tone
    /// (normal blue = skin-colour influence, the channel gear spends on its alpha gate).
    /// </summary>
    /// <param name="charCode">
    /// The wearer's race code ("0201", …), for the skin arm only: body materials carry a race-specific
    /// CategorySkinType. Null keeps the Midlander default.
    /// </param>
    /// <param name="faceId">
    /// The face this shell was cut from ("f0001", …), for the skin arm only: a face material differs from the
    /// body's (shader keys, alpha threshold, mask). Null means the body.
    /// </param>
    public static string TemplateFor(string shaderPackage, string? charCode = null, string? faceId = null)
        => shaderPackage switch
        {
            "characterscroll.shpk" => "chara/equipment/e6257/material/v0001/mt_c0201e6257_top_a.mtrl",
            "skin.shpk"            => SkinTemplate(charCode, faceId),
            _                      => "chara/equipment/e0041/material/v0001/mt_c0201e0041_top_a.mtrl",
        };

    /// <summary>The vanilla skin material for a race code and surface, or the Midlander body when the race
    /// isn't known. Face materials carry no version folder, unlike the body's.</summary>
    public static string SkinTemplate(string? charCode, string? faceId = null)
    {
        var c = string.IsNullOrWhiteSpace(charCode) ? "0201" : charCode;
        return string.IsNullOrWhiteSpace(faceId)
            ? $"chara/human/c{c}/obj/body/b0001/material/v0001/mt_c{c}b0001_a.mtrl"
            : $"chara/human/c{c}/obj/face/{faceId}/material/mt_c{c}{faceId}_fac_a.mtrl";
    }

    /// <summary>
    /// The texture game paths a material names, in slot order, for inheriting a slot the overlay does not supply.
    /// </summary>
    public static IReadOnlyList<string> TextureNames(byte[] m)
    {
        var names = new List<string>();
        if (m.Length < 16) return names;
        ushort strTableSize = BitConverter.ToUInt16(m, 8);
        byte texCount = m[12], uvCount = m[13], csCount = m[14];
        int strStart = 16 + (texCount + uvCount + csCount) * 4;
        for (int i = 0; i < texCount; i++)
        {
            int off = strStart + BitConverter.ToUInt16(m, 16 + i * 4);
            if (off < 0 || off >= m.Length || off >= strStart + strTableSize) { names.Add(""); continue; }
            int e = off;
            while (e < m.Length && m[e] != 0) e++;
            names.Add(Encoding.ASCII.GetString(m, off, e - off));
        }
        return names;
    }

    /// <summary>
    /// Texture slot order the shader's template expects:
    ///   character.shpk       4: base, norm, mask, id
    ///   characterscroll.shpk 4: norm, mask, id, catc   — NO base texture.
    /// "catc" is the scroll map (mods' "_o"). No base is load-bearing: a base texture overrides the colour
    /// table's diffuse, and the glow needs the table's surface colour.
    /// </summary>
    public static IReadOnlyList<string> TextureOrder(string shaderPackage) => shaderPackage switch
    {
        "characterscroll.shpk" => ["norm", "mask", "id", "catc"],
        // skin.shpk 3: base, norm, mask — no "id", because it declares no g_SamplerIndex (see TemplateFor).
        "skin.shpk"            => ["base", "norm", "mask"],
        _                      => ["base", "norm", "mask", "id"],
    };

    /// <summary>
    /// Material shader flags ("Enable Transparency" / "Hide Backfaces"). The vanilla template lacks 0x10, which
    /// makes the normal-blue alpha gate ignored, so transparency is forced on.
    /// </summary>
    private const uint FlagHideBackfaces = 0x01;
    private const uint FlagTransparency = 0x10;

    /// <summary>
    /// g_AlphaThreshold. At 0 (vanilla) the normal-blue alpha is a binary cutout; 1 turns on real alpha blending.
    /// </summary>
    private const uint ConstAlphaThreshold = 0x29AC0223;

    /// <summary>
    /// g_AlphaOffset (CRC of the name under the game's reflected CRC-32). Raised to 1.5 for a sheerer alpha
    /// falloff; gear non-scroll only.
    /// </summary>
    private const uint ConstAlphaOffset = 0xD07A6A65;

    /// <summary>
    /// GetDecalColor = GetDecalColorRGBA: lets the scroll map's COLOUR reach the glow. Without it the shader
    /// takes only intensity from the map and tints it with the row's emissive.
    /// </summary>
    private const uint KeyGetDecalColor = 0xD2777173;
    private const uint ValDecalColorRGBA = 0xF35F5131;

    /// <summary>
    /// characterscroll samples its scroll map with uv1, which needs a second UV set ("map2") declared; without
    /// it the shader falls back to uv0.
    /// </summary>
    private const string SecondUvSet = "map2";
    private const ushort SecondUvSetFlags = 0x0001;

    // Dawntrail color table row = 32 halves (64B). Offsets per Penumbra.GameData ColorTableRow.cs.
    private const int HDiffuse = 0, HSpecular = 4, HEmissive = 8;
    private const int HRoughness = 16, HMetalness = 18;
    private const int HSphereMask = 21, HSphereIndex = 27;
    private const int HTileIndex = 25, HTileAlpha = 26;
    /// <summary>
    /// The tile transform: a 2x2 UV matrix (UU, UV, VU, VV); the diagonal is repeats per axis, the off-diagonal
    /// skew. Proteus writes the diagonal and pins skew to zero.
    /// </summary>
    private const int HTileXfUU = 28, HTileXfUV = 29, HTileXfVU = 30, HTileXfVV = 31;
    private const int RowCount = 32, RowBytes = 64;

    /// <summary>Slices in chara/common/texture/tile_norm_array.tex — read out of the .tex header, and the
    /// reason <see cref="HTileIndex"/>'s encoding divides by 64.</summary>
    internal const int TileCount = 64;

    /// <summary>The tile transform vanilla ships on every row, and what an unset Scale axis falls back to.</summary>
    internal const float DefaultTileScale = 16f;

    /// <summary>
    /// What switches the scrolling effect ON:
    ///   [23] "Effect Unknown A"  — must be 1 (or 2), or the effect never renders.
    ///   [21] Sphere Map Opacity  — doubles as the effect's VISIBILITY on this shader.
    /// Only rows with an emissive are armed.
    /// </summary>
    private const int HEffectEnable = 23;

    /// <summary>
    /// Scroll speed and tiling material constants. Vanilla e6257 ships the speeds at zero.
    /// </summary>
    private const uint ConstTranslateSpeedX = 0x738A241C;
    private const uint ConstTranslateSpeedY = 0x71CC9A45;
    private const uint ConstTilingX = 0x43345395;
    private const uint ConstTilingY = 0x4172EDCC;

    // sRGB → linear. The color table's diffuse is consumed as LINEAR, so an sRGB-authored colour must be converted.
    private static float SrgbToLinear(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    /// <summary>
    /// Clone <paramref name="template"/>, point it at <paramref name="texturePaths"/> (which must be in
    /// the shader's slot order — see <see cref="TextureOrder"/>), and apply <paramref name="rows"/>
    /// (keyed by 0-based color table row; absent rows keep the template's values).
    /// </summary>
    public static byte[] Build(
        byte[] template,
        IReadOnlyList<string> texturePaths,
        IReadOnlyDictionary<int, GearColorRow>? rows,
        ScrollSettings? scroll = null,
        bool cutoutAlpha = false,
        bool linearizeDiffuse = false,   // convert the colorset diffuse sRGB→linear (mask shells: colour lives
                                         // in the colorset over a white base, so it must match the skin bake)
        bool showBackfaces = false)
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

        string csName = StrAt(U16(csTbl)), shpkName = StrAt(U16(10));

        // UV sets, as declared by the template.
        var uvSets = new List<(string Name, ushort Flags)>();
        for (int i = 0; i < uvCount; i++)
            uvSets.Add((StrAt(U16(uvTbl + i * 4)), U16(uvTbl + i * 4 + 2)));

        // characterscroll samples its scroll map with uv1, which only exists if map2 is declared.
        bool isScroll = string.Equals(shpkName, "characterscroll.shpk", StringComparison.OrdinalIgnoreCase);
        // A SKIN shell keeps the vanilla body material as shipped apart from its texture table: every rewrite
        // below is a gear-shader fix, and the colour table is a skin colorset.
        bool isSkin = string.Equals(shpkName, "skin.shpk", StringComparison.OrdinalIgnoreCase);
        if (isScroll && uvSets.Count < 2)
            uvSets.Add((SecondUvSet, SecondUvSetFlags));

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
        var uvOffs = new List<int>();
        foreach (var (name, _) in uvSets) { uvOffs.Add((int)sb.Position); Put(name); }
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
        OW16((ushort)(fileSize + (strings.Length - strTableSize) + (uvSets.Count - uvCount) * 4));
        OW16(dataSetSize);
        OW16((ushort)strings.Length);
        OW16((ushort)shpkOff);
        // counts — uvCount may have grown (see uvSets above)
        outMs.WriteByte(texCount);
        outMs.WriteByte((byte)uvSets.Count);
        outMs.WriteByte(colorSetCount);
        outMs.WriteByte(addDataSize);
        for (int i = 0; i < texCount; i++) { OW16((ushort)offs[i]); OW16(U16(texTbl + i * 4 + 2)); }
        for (int i = 0; i < uvSets.Count; i++) { OW16((ushort)uvOffs[i]); OW16(uvSets[i].Flags); }
        for (int i = 0; i < colorSetCount; i++) { OW16((ushort)csOff); OW16(U16(csTbl + i * 4 + 2)); }
        outMs.Write(strings);
        int afterStrings = strStart + strTableSize;
        outMs.Write(m, afterStrings, m.Length - afterStrings);           // additional data + color table + shader section, verbatim
        byte[] r = outMs.ToArray();

        // Shader section sits right after the data set. Its layout is
        // { u16 valueListSize, u16 keyCount, u16 constCount, u16 samplerCount, u32 flags }.
        // Everything below is gear-shader work, which a skin shell skips.
        if (isSkin) return r;

        int shaderStart = 16 + texCount * 4 + uvSets.Count * 4 + colorSetCount * 4 + strings.Length
                        + addDataSize + dataSetSize;
        if (shaderStart + 12 <= r.Length)
        {
            uint flags = BitConverter.ToUInt32(r, shaderStart + 8) | FlagTransparency;
            // Backfaces hidden by default (a hugging shell's inside is never seen); a spanning shell lifts off
            // the body, and then a hidden inside reads as a hole.
            if (showBackfaces) flags &= ~FlagHideBackfaces;
            else               flags |= FlagHideBackfaces;

            BitConverter.GetBytes(flags).CopyTo(r, shaderStart + 8);
        }

        // g_AlphaThreshold 1 = real alpha blending. Left at 0 it's a hard cutout, which keeps sphere/metal
        // through gpose's transparent pass at the cost of aliased edges. Opt-in via cutoutAlpha.
        if (!cutoutAlpha)
        {
            var (withAlpha, found) = TextureLoader.PatchConstantValues(r, ConstAlphaThreshold, 1f);
            if (found) r = withAlpha;
        }

        // Let the scroll map's own colour through, instead of a flat emissive-tinted white.
        if (isScroll)
        {
            r = TextureLoader.EnsureShaderKey(r, KeyGetDecalColor, ValDecalColorRGBA);

            // Vanilla ships the scroll speeds at zero, so the pattern would sit still.
            var sc = scroll ?? ScrollSettings.Default;
            r = TextureLoader.PatchConstantValues(r, ConstTranslateSpeedX, sc.SpeedX).data;
            r = TextureLoader.PatchConstantValues(r, ConstTranslateSpeedY, sc.SpeedY).data;
            r = TextureLoader.PatchConstantValues(r, ConstTilingX, sc.TilingX).data;
            r = TextureLoader.PatchConstantValues(r, ConstTilingY, sc.TilingY).data;
        }
        else
        {
            // Sheer edge: raise g_AlphaOffset; no-ops if the template lacks the constant.
            r = TextureLoader.PatchConstantValues(r, ConstAlphaOffset, 1.5f).data;
        }

        // Switch the templates' fabric weave off on every row: a second skin is skin. A baseline only;
        // PatchColorTable below re-writes authored tiles, so keep this BEFORE it.
        {
            int cs = 16 + texCount * 4 + uvSets.Count * 4 + colorSetCount * 4 + strings.Length + addDataSize;
            for (int row = 0; row < RowCount; row++)
            {
                int at = cs + row * RowBytes + HTileAlpha * 2;
                if (at + 2 <= r.Length)
                    BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)0f)).CopyTo(r, at);
            }
        }

        return PatchColorTable(r, rows, linearizeDiffuse, isScroll);
    }

    /// <summary>
    /// The roughness and metalness already in a material's colour table, one entry per sub-row (0–31), or null
    /// when it carries no Dawntrail table. For the editor to show a pack material's real values.
    /// </summary>
    public static IReadOnlyList<(float Roughness, float Metalness)>? ReadPhysical(byte[] mtrl)
    {
        int at = ColorTableStart(mtrl);
        if (at < 0) return null;

        var rows = new (float, float)[RowCount];
        for (int i = 0; i < RowCount; i++)
        {
            int b = at + i * RowBytes;
            rows[i] = ((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(mtrl, b + HRoughness * 2)),
                       (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(mtrl, b + HMetalness * 2)));
        }
        return rows;
    }

    /// <summary>
    /// Copy <paramref name="src"/>'s whole 32×64 colour table into <paramref name="dst"/>, each located from its
    /// OWN header, so a pack rebuilt onto characterscroll keeps its look. Graft AFTER Build. No-op without a
    /// Dawntrail table on either side.
    /// </summary>
    public static byte[] CopyColorTable(byte[] dst, byte[] src)
    {
        int dstAt = ColorTableStart(dst), srcAt = ColorTableStart(src);
        if (dstAt < 0 || srcAt < 0) return dst;

        var r = (byte[])dst.Clone();
        Buffer.BlockCopy(src, srcAt, r, dstAt, RowCount * RowBytes);
        return r;
    }

    /// <summary>
    /// Byte offset of a material's Dawntrail colour table from its own header, or -1 when it has none that can
    /// be written. The colour panel must ask this same question, not re-derive it from the header.
    /// </summary>
    internal static int ColorTableStart(byte[] mtrl)
    {
        if (mtrl.Length < 16) return -1;
        byte texCount = mtrl[12], uvCount = mtrl[13], colorSetCount = mtrl[14], addDataSize = mtrl[15];
        if (colorSetCount == 0) return -1;

        ushort strTableSize = BitConverter.ToUInt16(mtrl, 8);
        int at = 16 + texCount * 4 + uvCount * 4 + colorSetCount * 4 + strTableSize + addDataSize;
        return at < 0 || at + RowCount * RowBytes > mtrl.Length ? -1 : at;
    }

    /// <summary>
    /// Overwrite colour table rows in an EXISTING material in place, nothing else; absent rows and null fields
    /// keep the material's values. Used for imported packs' own materials and by <see cref="Build"/>. No-op
    /// without a Dawntrail 32×64 table.
    /// </summary>
    public static byte[] PatchColorTable(
        byte[] mtrl, IReadOnlyDictionary<int, GearColorRow>? rows,
        bool linearizeDiffuse = false, bool isScroll = false)
    {
        if (rows is not { Count: > 0 }) return mtrl;

        int csStart = ColorTableStart(mtrl);
        if (csStart < 0) return mtrl;

        var r = (byte[])mtrl.Clone();
        foreach (var (row, def) in rows)
        {
            if (row < 0 || row >= RowCount) continue;
            int b = csStart + row * RowBytes;
            void WH(int half, float v) => BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)v)).CopyTo(r, b + half * 2);

            if (def.Diffuse is { } d)
            {
                float dr = linearizeDiffuse ? SrgbToLinear(d.R) : d.R;
                float dg = linearizeDiffuse ? SrgbToLinear(d.G) : d.G;
                float db = linearizeDiffuse ? SrgbToLinear(d.B) : d.B;
                WH(HDiffuse, dr); WH(HDiffuse + 1, dg); WH(HDiffuse + 2, db);
            }
            if (def.Emissive is { } e) { WH(HEmissive, e.R); WH(HEmissive + 1, e.G); WH(HEmissive + 2, e.B); }
            if (def.Specular is { } sp) { WH(HSpecular, sp.R); WH(HSpecular + 1, sp.G); WH(HSpecular + 2, sp.B); }
            if (def.SphereMapIndex is { } si) WH(HSphereIndex, si);
            if (def.SphereMapMask is { } sm) WH(HSphereMask, sm);
            if (def.Roughness is { } ro) WH(HRoughness, ro);
            if (def.Metalness is { } me) WH(HMetalness, me);

            // ── the fabric weave ─────────────────────────────────────────────
            // Never on a scrolling material: characterscroll reassigns halves in this neighbourhood.
            // Every tile write hangs off the index: strength or scale alone would revive or re-tile a weave
            // nobody picked.
            if (!isScroll && def.TileIndex is { } ti)
            {
                // Stored as (index + 0.5) / 64: the shader reads (half * 64) and truncates.
                WH(HTileIndex, (Math.Clamp(ti, 0, TileCount - 1) + 0.5f) / 64f);

                // Build zeroed half 26, so an index with no strength would be invisible. Full unless told.
                WH(HTileAlpha, def.TileStrength ?? 1f);

                // Either axis writes the whole matrix, pinning skew to zero.
                if (def.TileScaleU is not null || def.TileScaleV is not null)
                {
                    WH(HTileXfUU, def.TileScaleU ?? DefaultTileScale);
                    WH(HTileXfUV, 0f);
                    WH(HTileXfVU, 0f);
                    WH(HTileXfVV, def.TileScaleV ?? DefaultTileScale);
                }
            }

            // ── the scrolling effect ─────────────────────────────────────────
            // Arm it on rows the user set to glow (by the DIAL, not the colour). Field 23 is the master switch and
            // sphere-map opacity the visibility: only a POSITIVE value overrides, else fully visible. The row
            // emissive stays as written: on this shader it sets the effect's brightness.
            if (isScroll && def.EmissiveStrength is { } strength && strength > 0f)
            {
                WH(HEffectEnable, 1f);
                WH(HSphereMask, def.SphereMapMask is { } vis && vis > 0f ? vis : 1f);
            }
        }
        return r;
    }
}
