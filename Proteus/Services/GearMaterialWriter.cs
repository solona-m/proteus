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
    /// The Glow dial as the user set it, before it was multiplied into <see cref="Emissive"/>.
    /// <para/>
    /// Kept separately because characterscroll needs the NUMBER, not the colour it produced: there the dial
    /// is the scrolling effect's strength and the emissive is only a gate, so the composed colour cannot
    /// stand in for it. Null on a row whose glow was never set.
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
    /// Slice of the shared array chara/common/texture/tile_norm_array.tex (0–63) — the fabric weave tiled
    /// over this row. Needs no material texture.
    /// <para/>
    /// Null leaves the material's own value, which on a shell is the zeroed weave <see cref="Build"/>
    /// writes, and on an imported pack's material is whatever its author chose.
    /// </summary>
    public int? TileIndex { get; init; }

    /// <summary>How strongly the weave shows. Null means full strength. Like <see cref="TileScaleU"/>, it is
    /// ignored entirely without a <see cref="TileIndex"/> — see the writer for why the three only travel
    /// together.</summary>
    public float? TileStrength { get; init; }

    /// <summary>Weave repeats per UV axis — the tile transform's diagonal. Either one present writes the
    /// whole 2x2 matrix with zero skew, the missing axis falling back to the game default of 16. Ignored
    /// without a <see cref="TileIndex"/>.</summary>
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
    /// <para/>
    /// skin.shpk clones a vanilla BODY material, and it is deliberately narrow: it is for a Skin-mode
    /// overlay auto-promoted to a shell, which should still look like skin. That shader is not a general
    /// shell target, because it declares only THREE samplers — g_SamplerDiffuse, g_SamplerNormal,
    /// g_SamplerMask — and no <c>g_SamplerIndex</c>. The <c>_id</c> row selector every other shell is built
    /// on has nowhere to bind, so the colour table cannot be addressed per texel: no row presets, no
    /// per-row opacity, no mask rows. An overlay that needs any of those must stay on character.shpk (see
    /// <see cref="RenderModeInference.IsClothSub"/> and the caller's choice).
    /// <para/>
    /// What it buys is the thing character.shpk cannot do at all: the wearer's SKIN TONE. skin.shpk reads
    /// the normal map's blue channel as skin-colour influence, which is exactly the channel a gear shell
    /// spends on its per-pixel alpha gate — so the two are mutually exclusive by construction, and a whole
    /// skin (opaque, tinted) wants the tone while a tattoo (sheer, decal) wants the gate.
    /// <para/>
    /// Measured on the shipping materials (mt_c0201b0001_a, c0101, c1401, c1501, f0002_fac) rather than
    /// assumed: 3 textures in base/norm/mask order, one colour set, and CategorySkinType keyed to Body.
    /// <c>g_AlphaThreshold</c> is declared on every one of them (the Hrothgar body and the face ship it
    /// non-zero at 0.5), so it has an alpha path too — but coverage here is triangle-trim, which is what a
    /// near-opaque whole skin needs anyway.
    /// </summary>
    /// <param name="charCode">
    /// The wearer's own race code ("0201", "1501", …), for the skin arm only. Skin is keyed to the REAL race
    /// — a body material carries a CategorySkinType telling the shader which skin path to take, and Hrothgar
    /// (c1501) ships a different value from every other body. Cloning c0201's onto a Hrothgar would light the
    /// shell down the non-fur path while the body beside it takes the other. Null keeps the Midlander
    /// default, and so does a race whose material can't be loaded — see the caller's fallback.
    /// </param>
    /// <param name="faceId">
    /// The face this shell was cut from ("f0001", …), for the skin arm only. A face is skin.shpk like the
    /// body but NOT the same material: the body declares <c>CategorySkinType = Body</c> and ships
    /// <c>g_AlphaThreshold</c> at 0, while a face material declares no shader keys at all and ships the
    /// threshold at 0.5 — so cloning the body onto face geometry renders it down the body's skin path with
    /// the wrong cutoff. It also names a different mask. Null means the body.
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
    /// The texture game paths a material names, in slot order. Used to inherit a slot the overlay does not
    /// supply — a skin shell with no mask of its own wants the one the surface it is copying actually wears,
    /// which differs between the body (a shared skin mask) and a face (its own).
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
        // skin.shpk 3: base, norm, mask — no "id", because it declares no g_SamplerIndex (see TemplateFor).
        "skin.shpk"            => ["base", "norm", "mask"],
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
    /// on character.shpk for a sheerer alpha falloff; we push to 1.5 for a sheerer edge still. Gear
    /// non-scroll only.
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
    private const int HTileIndex = 25, HTileAlpha = 26;
    /// <summary>
    /// The tile transform: a 2x2 UV matrix (UU, UV, VU, VV) whose diagonal is how many times the weave
    /// repeats per axis and whose off-diagonal is skew. Vanilla ships ScaledIdentity(16) — repeat 16 both
    /// ways, no skew. Proteus writes the diagonal from the editor's Scale controls and pins skew to zero:
    /// on a 64px weave skew and rotation are invisible, and composing them would mean porting
    /// Penumbra.GameData's HalfMatrix2x2 (a project Proteus does not reference) for a control nobody moves.
    /// </summary>
    private const int HTileXfUU = 28, HTileXfUV = 29, HTileXfVU = 30, HTileXfVV = 31;
    private const int RowCount = 32, RowBytes = 64;

    /// <summary>Slices in chara/common/texture/tile_norm_array.tex — read out of the .tex header, and the
    /// reason <see cref="HTileIndex"/>'s encoding divides by 64.</summary>
    internal const int TileCount = 64;

    /// <summary>The tile transform vanilla ships on every row, and what an unset Scale axis falls back to.</summary>
    internal const float DefaultTileScale = 16f;

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
        // A SKIN shell keeps the vanilla body material exactly as shipped apart from its texture table.
        // Every rewrite below is a gear-shader fix and none of them means anything here: the transparency
        // flag and g_AlphaThreshold drive an alpha gate skin.shpk reads out of a different channel (its blue
        // is skin-colour influence — the whole reason to use this shader), g_AlphaOffset isn't declared, and
        // the colour table is the SKIN colorset, not a gear one, so both the tile-weave clear and the row
        // patch would be writing gear offsets over skin data.
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
        // Everything from here to the return is gear-shader work. A skin shell wants the vanilla body
        // material verbatim behind its new textures, so it takes none of it.
        if (isSkin) return r;

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
            r = TextureLoader.PatchConstantValues(r, ConstAlphaOffset, 1.5f).data;
        }

        // Gear materials layer a tiling fabric weave over the surface (the colour table's Tile fields),
        // and the templates ship it at full strength. A second skin is SKIN — that weave shows up as a
        // rough, grainy texture the real skin doesn't have. Switch it off on every row.
        //
        // A BASELINE, not a verdict: PatchColorTable runs immediately below and re-writes half 26 on any row
        // whose preset asked for a weave, so an authored tile survives and every other row stays smooth.
        // Keep this BEFORE the patch — swapping the two makes the editor's Tile picker a silent no-op.
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
    /// <summary>
    /// Copy <paramref name="src"/>'s whole 32×64 colour table into <paramref name="dst"/>, each table
    /// located from its OWN header — the same computation <see cref="PatchColorTable"/> does, which is why
    /// this works between two materials of different shaders and different texture counts.
    /// <para/>
    /// This is what lets an imported content pack keep its look when Proteus rebuilds its material onto
    /// <c>characterscroll.shpk</c> for an animated glow. <see cref="Build"/> clones a VANILLA template, so
    /// without this a glowing piercing would silently take e6257's colour table: the author's silver, its
    /// metalness and its roughness gone, and e6257's own non-zero emissives inherited in their place.
    /// <para/>
    /// Grafted AFTER Build deliberately, so the author's tile alpha survives too — Build zeroes it because
    /// a second skin is skin and the weave shows, and a content piece is not skin.
    /// <para/>
    /// Returns the input unchanged when either material lacks a Dawntrail table, on the same reasoning as
    /// <see cref="PatchColorTable"/>: a legacy 16-row layout read at these offsets is shredded, not copied.
    /// </summary>
    /// <summary>
    /// The roughness and metalness already in a material's colour table, one entry per sub-row (0–31), or
    /// null when it carries no Dawntrail table.
    /// <para/>
    /// For the editor, so a panel over an imported pack's OWN material shows the values that material
    /// actually holds. Its grid otherwise falls back to 0 and 0.5 for anything the sidecar has not
    /// overridden — honest for a shell, whose material Proteus builds from a neutral template, and a lie
    /// for a content pack: the piercings arrive at metalness 1.0 while the panel reads 0, so the surface
    /// renders metallic and the control that should fix it looks like it already is.
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

    public static byte[] CopyColorTable(byte[] dst, byte[] src)
    {
        int dstAt = ColorTableStart(dst), srcAt = ColorTableStart(src);
        if (dstAt < 0 || srcAt < 0) return dst;

        var r = (byte[])dst.Clone();
        Buffer.BlockCopy(src, srcAt, r, dstAt, RowCount * RowBytes);
        return r;
    }

    /// <summary>
    /// Byte offset of a material's Dawntrail colour table, or -1 when it has none the writer may touch.
    /// <para/>
    /// The offset is read from the material's own header rather than assumed, which is the whole reason the
    /// row writer works on a pack's authored material as well as on a freshly built one.
    /// </summary>
    /// <summary>
    /// Byte offset of the colour table, or -1 when this material has none that can be written.
    /// <para/>
    /// Internal rather than private because the colour PANEL has to ask the same question, and asking it a
    /// second way was a bug: it tested the DECLARED data-set size out of the header while this tests the
    /// offset against the actual buffer. A material whose header promises a full table but whose file is
    /// short passed there and fails here, so the grid drew live, took edits, and
    /// <see cref="PatchColorTable"/> returned the material untouched with nothing on screen saying why.
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
            // Never on a scrolling material. characterscroll demonstrably reassigns halves in this
            // neighbourhood — 21 is the effect's visibility and 23 its master switch, neither of which means
            // that on character.shpk — so there is no basis for assuming 25/26/28-31 survive it either. A
            // value reaching one of those there would be stale rather than chosen, since the editor hides
            // the Tile block on a glow material for the same reason it hides the sphere.
            //
            // EVERY tile write hangs off the index, strength and scale included. On their own they are not a
            // weaker version of the same request, they are a different one, and both ways of making it are
            // wrong:
            //   - strength alone revives whatever weave the row ALREADY names. On a shell that is the vanilla
            //     template's, which Build only silenced (it zeroes half 26 and leaves half 25 alone), so the
            //     body comes back wearing a pattern nobody picked.
            //   - scale alone re-tiles a weave the user never chose and clears its skew — on an imported pack
            //     that is the author's own material, edited in place, with no second copy to restore from.
            // The editor can reach both: its Strength and Scale controls are dimmed while no pattern is set,
            // and dimmed in this codebase means inert-looking but still draggable. Keeping the trio together
            // here is what makes that safe, and it also protects hand-authored metadata, which no UI guards.
            if (!isScroll && def.TileIndex is { } ti)
            {
                // The index is NOT stored as a number: the shader reads (half * 64), so the row carries
                // (index + 0.5) / 64. The half-step centres the value in its bucket so the truncating read
                // cannot land a slice off — 63 encodes as 0.9921875, where Half's step is a thirtieth of the
                // bucket width, so every slice round-trips exactly.
                WH(HTileIndex, (Math.Clamp(ti, 0, TileCount - 1) + 0.5f) / 64f);

                // Build zeroed half 26 on every row, so an index with no strength would be an invisible
                // tile — the same silent no-op as a sphere index with a zero mask. Full unless told.
                WH(HTileAlpha, def.TileStrength ?? 1f);

                // Either axis writes the whole matrix: the off-diagonal has to be pinned to zero explicitly
                // or a content pack's authored skew would survive under a scale the user thinks is plain.
                if (def.TileScaleU is not null || def.TileScaleV is not null)
                {
                    WH(HTileXfUU, def.TileScaleU ?? DefaultTileScale);
                    WH(HTileXfUV, 0f);
                    WH(HTileXfVU, 0f);
                    WH(HTileXfVV, def.TileScaleV ?? DefaultTileScale);
                }
            }

            // ── the scrolling effect ─────────────────────────────────────────
            // Arm it on rows that actually glow. Field 23 is the master switch — without it nothing renders
            // however right the rest is — and sphere-map opacity doubles as the effect's visibility.
            // SphereIntensity is a Cloth concept with no meaning on a glow row, so only a POSITIVE value
            // overrides; null OR 0 means "fully visible" (else a stray 0 silently kills the glow).
            //
            // The row EMISSIVE is left exactly as written above, because on this shader it is what the
            // effect's brightness scales with. That was measured, not assumed, and the wrong guess cost two
            // rounds: pinning the emissive to a small gate and sending the dial to field 21 instead left the
            // effect barely visible with visibility already at 1.0, which is what proves field 21 is not the
            // brightness. A dial at full does not add white either — a saturated orange scroll map simply
            // blows out to white at that intensity, so the fix for "it looks white" is a lower dial.
            //
            // The DIAL is the condition rather than the composed colour: a glow whose colour is genuinely
            // black is still a glow the user asked for, and the two differ once a colour is picked.
            if (isScroll && def.EmissiveStrength is { } strength && strength > 0f)
            {
                WH(HEffectEnable, 1f);
                WH(HSphereMask, def.SphereMapMask is { } vis && vis > 0f ? vis : 1f);
            }
        }
        return r;
    }
}
