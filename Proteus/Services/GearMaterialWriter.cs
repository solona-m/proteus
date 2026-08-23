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
/// How a characterscroll material's scroll map flows. Speed and tiling are material constants, and
/// vanilla ships the speeds at ZERO, so a material with no settings sits still.
/// </summary>
public sealed record ScrollSettings(float SpeedX, float SpeedY, float TilingX, float TilingY)
{
    public static readonly ScrollSettings Default = new(0.15f, 0.15f, 5f, 5f);
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
    /// Game path of the VANILLA material used as a template.
    ///
    /// Non-nullable: the switch has a catch-all arm returning a real path, so there is no shader for
    /// which this yields nothing. It was declared string? for a "we ship our own template" case that was
    /// never built, and that lie cost the only caller a nullable warning it could not act on.
    ///
    /// character.shpk clones a real shipping item (e0041), so it needs nothing installed.
    ///
    /// characterscroll clones vanilla e6257. Its rows carry a non-zero emissive, but we always write the
    /// emissive explicitly (see Build), so that no longer leaks through as a flat white glow.
    /// </summary>
    public static string TemplateFor(string shaderPackage) => shaderPackage switch
    {
        "characterscroll.shpk" => "chara/equipment/e6257/material/v0001/mt_c0201e6257_top_a.mtrl",
        _                      => "chara/equipment/e0041/material/v0001/mt_c0201e0041_top_a.mtrl",
    };

    /// <summary>
    /// Texture slot order the shader's template expects — the sets differ, so this drives the table:
    ///   character.shpk       4: base, norm, mask, id
    ///   characterscroll.shpk 4: norm, mask, id, catc   — NO base texture.
    ///
    /// "catc" is the scrolling map that drives the animated emissive (mods name it "_o"); it is the
    /// glow itself — colour, pattern and animation.
    ///
    /// characterscroll having NO base is load-bearing, not an oversight. When a base texture is present
    /// (Solona's modded 5-texture variant), it DRIVES the diffuse and the colour table's diffuse is
    /// ignored — so the surface is stuck at whatever the overlay's art is, and a glow on white art can
    /// never read. Vanilla scrolling materials take their surface from the colour table instead, which
    /// is how they pair a near-black diffuse with a bright emissive and get a vivid effect.
    /// </summary>
    public static IReadOnlyList<string> TextureOrder(string shaderPackage) => shaderPackage switch
    {
        "characterscroll.shpk" => ["norm", "mask", "id", "catc"],
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

    /// <summary>
    /// g_AlphaOffset (CRC of the name under the game's reflected CRC-32). "Enhanced Nylon" raises it to 1
    /// on character.shpk for a sheerer alpha falloff. Gear non-scroll only.
    /// </summary>
    private const uint ConstAlphaOffset = 0xD07A6A65;

    /// <summary>
    /// GetDecalColor = GetDecalColorRGBA.
    ///
    /// This is what makes the scroll map's COLOUR reach the glow. Without it the shader defaults to
    /// GetDecalColorOff and takes only intensity from the map, tinting it with the row's emissive — so a
    /// vivid rainbow effect renders as a flat white glow no matter what the texture holds. Vanilla e6257
    /// doesn't set it; every scrolling-effect mod does.
    /// </summary>
    private const uint KeyGetDecalColor = 0xD2777173;
    private const uint ValDecalColorRGBA = 0xF35F5131;

    /// <summary>
    /// characterscroll needs TWO UV sets declared — "map1" and "map2". The scroll map is sampled with
    /// uv1 (map2); with only map1 declared the shader falls back to uv0 and the effect renders as a
    /// flat, colourless wash however the colour table is set. Vanilla e6257 declares only map1; every
    /// mod with a working scrolling effect declares both.
    /// </summary>
    private const string SecondUvSet = "map2";
    private const ushort SecondUvSetFlags = 0x0001;

    // Dawntrail color table row = 32 halves (64B). Offsets per Penumbra.GameData ColorTableRow.cs.
    private const int HDiffuse = 0, HSpecular = 4, HEmissive = 8;
    private const int HRoughness = 16, HMetalness = 18;
    private const int HSphereMask = 21, HSphereIndex = 27;
    private const int HTileAlpha = 26;
    private const int RowCount = 32, RowBytes = 64;

    /// <summary>
    /// What actually switches the scrolling effect ON — per Bacara's characterscroll guide, and confirmed
    /// against every working effect mod:
    ///
    ///   [23] "Effect Unknown A"  — must be 1 (or 2). REQUIRED, even with the shader keys set. Zero here
    ///                              and the effect never renders, no matter what else is right.
    ///   [21] Sphere Map Opacity  — doubles as the effect's VISIBILITY on this shader (can be negative).
    ///
    /// A row with no emissive isn't a glow row, so we only arm the rows that have one.
    /// </summary>
    private const int HEffectEnable = 23;

    /// <summary>
    /// Scroll speed and tiling live in material constants. Vanilla e6257 ships the speeds at ZERO — so
    /// even a correctly-armed material sits still. Names from the guide; ~0.01 is the usual speed range
    /// and tiling defaults to 1.
    /// </summary>
    private const uint ConstTranslateSpeedX = 0x738A241C;
    private const uint ConstTranslateSpeedY = 0x71CC9A45;
    private const uint ConstTilingX = 0x43345395;
    private const uint ConstTilingY = 0x4172EDCC;

    /// <summary>
    /// Clone <paramref name="template"/>, point it at <paramref name="texturePaths"/> (which must be in
    /// the shader's slot order — see <see cref="TextureOrder"/>), and apply <paramref name="rows"/>
    /// (keyed by 0-based color table row; absent rows keep the template's values).
    /// </summary>
    // sRGB → linear (the standard curve the game applies to an sRGB diffuse TEXTURE). The color table's
    // diffuse half-floats are consumed as LINEAR, so a colour authored in sRGB (as the editor shows it,
    // and as the skin path bakes it into an sRGB texture) must be converted or it renders too bright.
    private static float SrgbToLinear(float c)
        => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static byte[] Build(
        byte[] template,
        IReadOnlyList<string> texturePaths,
        IReadOnlyDictionary<int, GearColorRow>? rows,
        ScrollSettings? scroll = null,
        bool cutoutAlpha = false,
        bool linearizeDiffuse = false)   // convert the colorset diffuse sRGB→linear (mask shells: colour lives
    {                                    // in the colorset over a white base, so it must match the skin bake)
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
        int shaderStart = 16 + texCount * 4 + uvSets.Count * 4 + colorSetCount * 4 + strings.Length
                        + addDataSize + dataSetSize;
        if (shaderStart + 12 <= r.Length)
        {
            uint flags = BitConverter.ToUInt32(r, shaderStart + 8) | FlagTransparency | FlagHideBackfaces;

            BitConverter.GetBytes(flags).CopyTo(r, shaderStart + 8);
        }

        // g_AlphaThreshold 1 turns on real alpha blending (smooth sheer transparency). Left at the template's
        // 0 it's a hard alpha-test cutout — which renders more like opaque geometry, letting sphere/metal
        // survive gpose's transparent pass, at the cost of aliased sheer edges. Opt-in via cutoutAlpha.
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
            // Sheer edge: always raise g_AlphaOffset so character.shpk's alpha falloff reads sheerer — a
            // second skin should never be a hard cutout. Not applicable to characterscroll (handled above);
            // no-ops safely if the template lacks the constant.
            r = TextureLoader.PatchConstantValues(r, ConstAlphaOffset, 1f).data;
        }

        // Gear materials layer a tiling fabric weave over the surface (the colour table's Tile fields),
        // and the templates ship it at full strength. A second skin is SKIN — that weave shows up as a
        // rough, grainy texture the real skin doesn't have. Switch it off on every row.
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
    /// Overwrite colour table rows in an EXISTING material, in place and nothing else — no texture table,
    /// no string block, no shader section. <paramref name="rows"/> is keyed by 0-based row; absent rows and
    /// null fields keep whatever the material already holds.
    /// <para/>
    /// This is how an imported content pack's own .mtrl is coloured: it already names its own textures and
    /// shader, and rebuilding it through <see cref="Build"/> would demand a template with a matching texture
    /// count and throw away everything the author set. It is also the row writer <see cref="Build"/> itself
    /// uses, so shells and content packs can never drift apart on what a row means.
    /// <para/>
    /// The colour table's position is read from the material's OWN header, which is why this works on any
    /// material rather than only on a freshly built one. No-ops (returning the input unchanged) when the
    /// material declares no colour set, or when its data set is too small to hold a Dawntrail 32×64 table —
    /// a legacy 16-row material would otherwise be shredded by rows written at Dawntrail offsets.
    /// </summary>
    public static byte[] PatchColorTable(
        byte[] mtrl, IReadOnlyDictionary<int, GearColorRow>? rows,
        bool linearizeDiffuse = false, bool isScroll = false)
    {
        if (rows is not { Count: > 0 }) return mtrl;
        if (mtrl.Length < 16) return mtrl;

        byte texCount = mtrl[12], uvCount = mtrl[13], colorSetCount = mtrl[14], addDataSize = mtrl[15];
        if (colorSetCount == 0) return mtrl;

        ushort strTableSize = BitConverter.ToUInt16(mtrl, 8);
        int csStart = 16 + texCount * 4 + uvCount * 4 + colorSetCount * 4 + strTableSize + addDataSize;
        if (csStart < 0 || csStart + RowCount * RowBytes > mtrl.Length) return mtrl;

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

            // Arm the scrolling effect on rows that actually glow. Field 23 is the master switch —
            // without it nothing renders — and sphere-map opacity doubles as the effect's visibility.
            // SphereIntensity is a Cloth concept that has no meaning on a glow row, so only a POSITIVE
            // value overrides; null OR 0 means "fully visible" (else a stray 0 silently kills the glow).
            if (isScroll && def.Emissive is { } em && (em.R > 0 || em.G > 0 || em.B > 0))
            {
                WH(HEffectEnable, 1f);
                WH(HSphereMask, def.SphereMapMask is { } vis && vis > 0f ? vis : 1f);
            }
        }
        return r;
    }
}
