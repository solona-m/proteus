using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services.Matcap;

/// <summary>
/// A glass part's row cut: Proteus's own copy of the part's <c>characterglass.shpk</c> material, moved to
/// <c>character.shpk</c> so the cut can discard on the row. Only the cut copy (published under <c>…_mcut…</c> names and
/// removed by the matcap's own Undo) is converted; the author's glass file is never written.
/// <para/>
/// <b>Why glass can't be cut as it is</b> (measured 2026-11-18, <c>shader/sm5/shpk/characterglass.shpk</c> disassembled
/// with d3dcompiler_47): its 38 pixel shaders all write one render target — glass draws only in a forward pass, never in
/// the G-buffer passes where character.shpk discards by normal blue × vertex alpha &lt; <c>g_AlphaThreshold</c>. It
/// declares <c>g_AlphaThreshold</c> but no pixel shader reads it; its discards are dither fades, dissolve and depth
/// tests. In the main pass normal blue scales the opacity of a refractive blend over the scene behind, and reflections
/// and specular are still added: blue 0 makes clear glass, not a hole.
/// <para/>
/// <b>The graft.</b> Kept from the glass: the string table, texture entries, UV sets, colour and dye tables and the
/// sampler entries (ids, flags, texture indices). Replaced: the shader name (appended, <see cref="CharacterShaderUpgrade.RenameShader"/>),
/// the keys — exactly the pair the user's working character.shpk cut carries (vertex colour <see cref="VertexColourKey"/> =
/// <see cref="VertexColourMask"/>, flow standard); the template's other two keys, texture mode 0xB616DC5A=0x5CC605B5 and
/// 0xD2777173=0x4242B842, are character.shpk's own defaults (measured), while the vertex colour default is 0xDFE74BAC, so
/// it is written — and the constants and value block, taken from a game character.shpk accessory material with the same
/// three samplers (<see cref="TemplatePath"/>). Then both movement constants 0 (the cut must not sway) and the threshold
/// <see cref="MatcapRowCut.CutThreshold"/>. Additional data: the table bits (<see cref="TableBits"/>) from the glass, every
/// other bit from the template. Shader flags: the template's, with the backface bit from the glass.
/// <para/>
/// Penumbra's nearest precedent, <c>ModMigrator.SetGlassReferenceValues</c>, is a same-shader glass → glass migration that
/// copies keys, constants and the whole additional data from a game reference and keeps textures and table; this follows
/// its shape across shaders, taking the additional data bitwise because the table kept is the glass's.
/// <para/>
/// Pure: reads bytes, writes nothing. Every result passes <see cref="IsSound"/> (a malformed .mtrl crashes the game).
/// </summary>
internal static class MatcapGlassCut
{
    public const string GlassShader = "characterglass.shpk";

    /// <summary>A game character.shpk accessory material bound exactly to normal, mask and index (pinned locally).</summary>
    public const string TemplatePath = "chara/accessory/a0170/material/v0001/mt_c0101a0170_ear_a.mtrl";

    public const uint VertexColourKey = 0xF52CCF05, VertexColourMask = 0xA7D2FF60;

    /// <summary>The additional data's table bits: a table (0x4), a dye table (0x8), and the width/height logs (bits 4–11).</summary>
    public const uint TableBits = 0xFFC;

    private const uint FlagHideBackfaces = 0x01;

    public enum Failure { None, Layout, Template, Unsound }

    private static readonly uint[] Samplers = [MatcapMaterial.SamplerNormal, MatcapMaterial.SamplerMask, ShaderUpgradePlan.SamplerIndex];

    private static readonly (uint Key, uint Value)[] Keys =
        [(VertexColourKey, VertexColourMask), (CharacterShaderUpgrade.FlowMapKey, CharacterShaderUpgrade.FlowMapStandard)];

    /// <summary>The offsets of a .mtrl's sections, or null when it can't be walked.</summary>
    internal sealed record Layout(int Tex, int Uv, int Cs, int Add, int DataSet, int StrAt, int StrSize, int AddAt,
                                  int ShaderAt, int ValueSize, int KeyCount, int ConstCount, int SampCount, uint Flags,
                                  int KeysAt, int ConstsAt, int SampsAt, int ValuesAt, int End);

    internal static Layout? Walk(byte[] b)
    {
        if (b.Length < 16) return null;
        int dataSet = U16(b, 6), strSize = U16(b, 8);
        int tex = b[12], uv = b[13], cs = b[14], add = b[15];
        int strAt = 16 + 4 * (tex + uv + cs);
        int addAt = strAt + strSize;
        int shaderAt = addAt + add + dataSet;
        if (shaderAt + 12 > b.Length) return null;
        int valueSize = U16(b, shaderAt), keys = U16(b, shaderAt + 2), consts = U16(b, shaderAt + 4), samps = U16(b, shaderAt + 6);
        uint flags = U32(b, shaderAt + 8);
        int keysAt = shaderAt + 12, constsAt = keysAt + 8 * keys, sampsAt = constsAt + 8 * consts, valuesAt = sampsAt + 12 * samps;
        int end = valuesAt + valueSize;
        if (end > b.Length) return null;
        return new Layout(tex, uv, cs, add, dataSet, strAt, strSize, addAt, shaderAt, valueSize, keys, consts, samps, flags,
                          keysAt, constsAt, sampsAt, valuesAt, end);
    }

    /// <summary>
    /// The glass material moved to character.shpk for a row cut, or why not: <see cref="Failure.Template"/> when the template
    /// is missing or not of the expected shape, <see cref="Failure.Layout"/> when the glass can't be converted safely,
    /// <see cref="Failure.Unsound"/> when the result fails the gate. Deterministic.
    /// </summary>
    public static (byte[]? Bytes, Failure Why) Convert(byte[] glass, byte[]? template)
    {
        try
        {
            if (template == null || TemplateShape(template) is not { } t) return (null, Failure.Template);
            if (GlassShape(glass) is not { } g) return (null, Failure.Layout);

            uint addData = (U32(glass, g.AddAt) & TableBits) | (U32(template, t.AddAt) & ~TableBits);
            uint flags = (t.Flags & ~FlagHideBackfaces) | (g.Flags & FlagHideBackfaces);

            var o = new List<byte>(glass.Length + t.ValueSize + 8 * t.ConstCount);
            o.AddRange(glass.AsSpan(0, g.AddAt).ToArray());                       // header, entries, strings
            o.AddRange(BitConverter.GetBytes(addData));                            // additional data (4 bytes)
            o.AddRange(glass.AsSpan(g.AddAt + g.Add, g.DataSet).ToArray());        // colour (and dye) table
            o.AddRange(BitConverter.GetBytes((ushort)t.ValueSize));
            o.AddRange(BitConverter.GetBytes((ushort)Keys.Length));
            o.AddRange(BitConverter.GetBytes((ushort)t.ConstCount));
            o.AddRange(BitConverter.GetBytes((ushort)g.SampCount));
            o.AddRange(BitConverter.GetBytes(flags));
            foreach (var (k, v) in Keys) { o.AddRange(BitConverter.GetBytes(k)); o.AddRange(BitConverter.GetBytes(v)); }
            o.AddRange(template.AsSpan(t.ConstsAt, 8 * t.ConstCount).ToArray());  // constant entries
            o.AddRange(glass.AsSpan(g.SampsAt, 12 * g.SampCount).ToArray());      // the glass's own sampler entries
            o.AddRange(template.AsSpan(t.ValuesAt, t.ValueSize).ToArray());       // value block
            var r = o.ToArray();
            if (r.Length > ushort.MaxValue) return (null, Failure.Unsound);
            BitConverter.TryWriteBytes(r.AsSpan(4), (ushort)r.Length);

            if (CharacterShaderUpgrade.RenameShader(r, CharacterShaderUpgrade.Character) is not { } named) return (null, Failure.Unsound);
            if (ShaderUpgradePlan.WindNeutral(named) is not { } calm) return (null, Failure.Unsound);
            var (cut, ok) = TextureLoader.EnsureConstantValues(calm, MatcapMaterial.ConstAlphaThreshold, MatcapRowCut.CutThreshold);
            if (!ok || !IsSound(glass, cut, template)) return (null, Failure.Unsound);
            return (cut, Failure.None);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return (null, Failure.Unsound);
        }
    }

    /// <summary>The template's layout when it is a character.shpk material bound exactly to N/M/ID with a threshold.</summary>
    private static Layout? TemplateShape(byte[] t)
    {
        if (Walk(t) is not { } l || l.End != t.Length || l.Add != 4) return null;
        if (!string.Equals(CharacterShaderUpgrade.ShaderOf(t), CharacterShaderUpgrade.Character, StringComparison.OrdinalIgnoreCase)) return null;
        if (!ExactSamplers(t, l)) return null;
        if (MatcapMaterial.ReadHeader(t)?.AlphaThreshold == null) return null;
        if (l.ValueSize % 4 != 0 || !ConstantsInBlock(t, l)) return null;
        return l;
    }

    /// <summary>The glass's layout when it is characterglass bound exactly to N/M/ID on three textures, with a Dawntrail table.</summary>
    private static Layout? GlassShape(byte[] g)
    {
        if (Walk(g) is not { } l || l.End != g.Length || l.Add != 4) return null;
        if (!string.Equals(CharacterShaderUpgrade.ShaderOf(g), GlassShader, StringComparison.OrdinalIgnoreCase)) return null;
        if (!ExactSamplers(g, l)) return null;
        var texIdx = Enumerable.Range(0, l.SampCount).Select(i => (int)g[l.SampsAt + 12 * i + 8]).ToList();
        if (texIdx.Distinct().Count() != 3 || texIdx.Any(x => x >= l.Tex)) return null;
        if (l.DataSet is not (2048 or 2176) || l.Cs == 0) return null;
        uint add = U32(g, l.AddAt);
        if ((add & 0x4) == 0 || ((add >> 4) & 0xF) != 3 || ((add >> 8) & 0xF) != 5) return null;   // 32 rows × 8 vec4s
        return l;
    }

    private static bool ExactSamplers(byte[] b, Layout l)
    {
        if (l.SampCount != Samplers.Length) return false;
        var ids = Enumerable.Range(0, l.SampCount).Select(i => U32(b, l.SampsAt + 12 * i)).ToHashSet();
        return ids.SetEquals(Samplers);
    }

    private static bool ConstantsInBlock(byte[] b, Layout l)
    {
        for (int i = 0; i < l.ConstCount; i++)
        {
            int off = U16(b, l.ConstsAt + 8 * i + 4), size = U16(b, l.ConstsAt + 8 * i + 6);
            if (size % 4 != 0 || off % 4 != 0 || off + size > l.ValueSize) return false;
        }
        return true;
    }

    private static Dictionary<uint, byte[]> ConstantValues(byte[] b, Layout l)
    {
        var d = new Dictionary<uint, byte[]>();
        for (int i = 0; i < l.ConstCount; i++)
        {
            uint id = U32(b, l.ConstsAt + 8 * i);
            int off = U16(b, l.ConstsAt + 8 * i + 4), size = U16(b, l.ConstsAt + 8 * i + 6);
            d[id] = b.AsSpan(l.ValuesAt + off, size).ToArray();
        }
        return d;
    }

    private static string[] Strings(byte[] b, Layout l, int firstEntry, int count)
    {
        var r = new string[count];
        for (int i = 0; i < count; i++)
        {
            int off = U16(b, 16 + 4 * (firstEntry + i));
            int a = l.StrAt + off, e = a;
            if (off >= l.StrSize) { r[i] = "\0bad"; continue; }
            while (e < l.StrAt + l.StrSize && b[e] != 0) e++;
            r[i] = System.Text.Encoding.UTF8.GetString(b, a, e - a);
        }
        return r;
    }

    /// <summary>
    /// The gate every converted (and final cut) copy must pass. Structure: the header's file size and the exact computed
    /// end equal the length; the value block a multiple of 4 with every constant inside it; every sampler's texture inside
    /// the texture table. Kept from the glass: additional-data and data-set sizes, texture strings (but the normal's, which
    /// must be <paramref name="normalPath"/> when given), UV sets, sampler entries, colour and dye tables. Parameters:
    /// character.shpk, exactly <see cref="Keys"/>, the template's constant ids with its values (the movement constants 0,
    /// the threshold <see cref="MatcapRowCut.CutThreshold"/>), the flags and additional data by rule. Never throws.
    /// </summary>
    public static bool IsSound(byte[] glass, byte[] converted, byte[] template, string? normalPath = null)
    {
        try
        {
            if (Walk(converted) is not { } c || Walk(glass) is not { } g || Walk(template) is not { } t) return false;
            if (c.End != converted.Length || U16(converted, 4) != converted.Length) return false;
            if (c.ValueSize % 4 != 0 || !ConstantsInBlock(converted, c)) return false;
            for (int i = 0; i < c.SampCount; i++)
                if (converted[c.SampsAt + 12 * i + 8] >= c.Tex) return false;

            if (c.Add != g.Add || c.Add != 4 || c.DataSet != g.DataSet || c.Tex != g.Tex || c.Uv != g.Uv || c.Cs != g.Cs) return false;
            if (!string.Equals(CharacterShaderUpgrade.ShaderOf(converted), CharacterShaderUpgrade.Character, StringComparison.Ordinal)) return false;

            // Textures and UV sets as the glass's (the normal may be retargeted to the cut's bake).
            var gt = Strings(glass, g, 0, g.Tex);
            var ct = Strings(converted, c, 0, c.Tex);
            int normalTex = -1;
            for (int i = 0; i < g.SampCount; i++)
                if (U32(glass, g.SampsAt + 12 * i) == MatcapMaterial.SamplerNormal) normalTex = glass[g.SampsAt + 12 * i + 8];
            for (int i = 0; i < gt.Length; i++)
            {
                if (i == normalTex && normalPath != null) { if (ct[i] != normalPath) return false; }
                else if (ct[i] != gt[i]) return false;
            }
            if (!Strings(glass, g, g.Tex, g.Uv).SequenceEqual(Strings(converted, c, c.Tex, c.Uv))) return false;
            if (!glass.AsSpan(g.SampsAt, 12 * g.SampCount).SequenceEqual(converted.AsSpan(c.SampsAt, 12 * c.SampCount))) return false;
            if (!glass.AsSpan(g.AddAt + g.Add, g.DataSet).SequenceEqual(converted.AsSpan(c.AddAt + c.Add, c.DataSet))) return false;

            // Additional data and flags by rule.
            if (U32(converted, c.AddAt) != ((U32(glass, g.AddAt) & TableBits) | (U32(template, t.AddAt) & ~TableBits))) return false;
            if (c.Flags != ((t.Flags & ~FlagHideBackfaces) | (g.Flags & FlagHideBackfaces))) return false;

            // Keys: exactly the pair.
            if (c.KeyCount != Keys.Length) return false;
            for (int i = 0; i < Keys.Length; i++)
                if (U32(converted, c.KeysAt + 8 * i) != Keys[i].Key || U32(converted, c.KeysAt + 8 * i + 4) != Keys[i].Value) return false;

            // Constants: the template's ids and values, but the movement constants 0 and the threshold the cut's.
            var cv = ConstantValues(converted, c);
            var tv = ConstantValues(template, t);
            if (cv.Count != c.ConstCount) return false;   // a duplicate constant id
            if (!cv.Keys.ToHashSet().SetEquals(tv.Keys.Append(MeshVolumeService.VertexMovementScaleId)
                                                      .Append(MeshVolumeService.VertexMovementMaxLengthId)
                                                      .Append(MatcapMaterial.ConstAlphaThreshold))) return false;
            foreach (var (id, bytes) in cv)
            {
                if (id is MeshVolumeService.VertexMovementScaleId or MeshVolumeService.VertexMovementMaxLengthId)
                { if (bytes.Length < 4 || BitConverter.ToSingle(bytes, 0) != 0f) return false; continue; }
                if (id == MatcapMaterial.ConstAlphaThreshold)
                { if (bytes.Length < 4 || BitConverter.ToSingle(bytes, 0) != MatcapRowCut.CutThreshold) return false; continue; }
                if (!tv.TryGetValue(id, out var want) || !want.AsSpan().SequenceEqual(bytes)) return false;
            }
            return MatcapMaterial.ReadHeader(converted) != null;
        }
        catch { return false; }
    }

    private static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);

    private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
}
