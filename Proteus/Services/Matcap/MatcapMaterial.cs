using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Proteus.Services.Matcap;

/// <summary>
/// The matcap material: a copy of the game's own <c>characterreflection.shpk</c> material with its three textures
/// pointed at the part's normal map, a white mask and the matcap. The shader samples the third texture by the view-space
/// normal, which is what a matcap is.
/// <para/>
/// The template is the game's own (<see cref="VanillaTemplatePath"/>, a monster reflection material), read from game
/// data, so nothing of a library author's is copied. Measured against the MatCap Library's material (a Dawntrail
/// re-save of the same shader): the same shader and sampler ids, the same alpha threshold, three distinct texture
/// paths; the game's carries the older 16-row colour table and three fewer constants. Every row is set to row 0, so
/// whatever row the part's own normal alpha selects draws the same.
/// </summary>
internal static class MatcapMaterial
{
    /// <summary>A reflection material the game draws with (pinned by <c>MatcapMaterialLocalTests</c>).</summary>
    public const string VanillaTemplatePath = "chara/monster/m0516/obj/body/b0001/material/v0001/mt_m0516b0001_a.mtrl";

    public const string Shader = "characterreflection.shpk";

    public const uint SamplerNormal = 0x0C5EC1F1, SamplerMask = 0x8A4E82B6, SamplerMatcap = 0xFEA0F3D2;

    /// <summary><c>g_AlphaThreshold</c>: below it the normal map's alpha cuts the surface away.</summary>
    public const uint ConstAlphaThreshold = 0x29AC0223;

    /// <summary>A material's shader, textures (in table order), sampler bindings, constants and colour-table size.</summary>
    public sealed record Header(
        string Shader, IReadOnlyList<string> Textures, IReadOnlyList<(uint Id, int Texture)> Samplers,
        IReadOnlyDictionary<uint, float[]> Constants, int ColorTableBytes)
    {
        /// <summary>The texture bound to sampler <paramref name="id"/>, or null.</summary>
        public string? TextureOf(uint id)
        {
            foreach (var (sid, tex) in Samplers)
                if (sid == id && tex >= 0 && tex < Textures.Count) return Textures[tex];
            return null;
        }

        public bool IsReflection => string.Equals(Shader, MatcapMaterial.Shader, StringComparison.OrdinalIgnoreCase);

        public float? AlphaThreshold => Constants.TryGetValue(ConstAlphaThreshold, out var v) && v.Length > 0 ? v[0] : null;
    }

    /// <summary>The header of a .mtrl, or null when it can't be walked. Never throws.</summary>
    public static Header? ReadHeader(byte[] b)
    {
        try
        {
            if (b.Length < 16) return null;
            ushort dataSet = U16(b, 6), strSize = U16(b, 8), shpkOff = U16(b, 10);
            byte tex = b[12], uv = b[13], cs = b[14], add = b[15];
            int strAt = 16 + tex * 4 + uv * 4 + cs * 4;
            if (strAt + strSize > b.Length) return null;
            string Str(int off)
            {
                int a = strAt + off, e = a;
                if (off >= strSize) return "";
                while (e < strAt + strSize && b[e] != 0) e++;
                var s = Encoding.UTF8.GetString(b, a, e - a);
                return s.StartsWith("--", StringComparison.Ordinal) ? s[2..] : s;
            }
            var textures = Enumerable.Range(0, tex).Select(i => Str(U16(b, 16 + i * 4))).ToList();
            int o = strAt + strSize + add + dataSet;
            if (o + 12 > b.Length) return null;
            ushort keys = U16(b, o + 2), consts = U16(b, o + 4), samps = U16(b, o + 6);
            o += 12 + keys * 8;
            int constAt = o;
            int valuesAt = o + consts * 8 + samps * 12;
            if (valuesAt > b.Length) return null;
            var constants = new Dictionary<uint, float[]>();
            for (int i = 0; i < consts; i++)
            {
                uint id = U32(b, constAt + i * 8);
                int off = U16(b, constAt + i * 8 + 4), size = U16(b, constAt + i * 8 + 6);
                if (valuesAt + off + size > b.Length) continue;
                constants[id] = Enumerable.Range(0, size / 4).Select(k => BitConverter.ToSingle(b, valuesAt + off + k * 4)).ToArray();
            }
            int sampAt = constAt + consts * 8;
            var samplers = new List<(uint, int)>();
            for (int i = 0; i < samps; i++) samplers.Add((U32(b, sampAt + i * 12), b[sampAt + i * 12 + 8]));
            return new Header(Str(shpkOff), textures, samplers, constants, cs > 0 ? dataSet : 0);
        }
        catch { return null; }
    }

    /// <summary>
    /// The matcap material for one part, built from <paramref name="template"/> (a characterreflection material):
    /// its normal, mask and matcap textures repointed, every colour-table row set to row 0, and — when the part's own
    /// material had one — its alpha threshold carried over.
    /// </summary>
    /// <exception cref="InvalidDataException">The template isn't a usable reflection material.</exception>
    public static byte[] Build(byte[] template, string normal, string mask, string matcap, float? alphaThreshold = null)
    {
        var h = ReadHeader(template) ?? throw new InvalidDataException("the template material could not be read");
        if (!h.IsReflection) throw new InvalidDataException($"the template uses {h.Shader}, not {Shader}");

        int Index(uint id)
        {
            foreach (var (sid, tex) in h.Samplers) if (sid == id) return tex;
            return -1;
        }
        int iN = Index(SamplerNormal), iM = Index(SamplerMask), iC = Index(SamplerMatcap);
        if (iN < 0 || iM < 0 || iC < 0 || iN == iM || iN == iC || iM == iC)
            throw new InvalidDataException("the template does not bind normal, mask and matcap to three textures");

        // By texture index, not by path: correct even where a template named one path for two samplers.
        var b = RetargetByIndex(template, new Dictionary<int, string> { [iN] = normal, [iM] = mask, [iC] = matcap })
                ?? throw new InvalidDataException("the template's texture table could not be rewritten");
        b = FillRowsWithRowZero(b);
        if (alphaThreshold is { } a)
            b = TextureLoader.PatchConstantValues(b, ConstAlphaThreshold, a).data;
        return b;
    }

    /// <summary>
    /// A copy whose texture entries <paramref name="paths"/> names point at new strings, appended to the string table
    /// (no existing offset moves; see <see cref="TextureLoader.RetargetTexturePaths"/>). Null when it can't be walked.
    /// </summary>
    internal static byte[]? RetargetByIndex(byte[] b, IReadOnlyDictionary<int, string> paths)
    {
        if (b.Length < 16 || paths.Count == 0) return null;
        ushort fileSize = U16(b, 4), strSize = U16(b, 8);
        byte tex = b[12], uv = b[13], cs = b[14];
        int strAt = 16 + tex * 4 + uv * 4 + cs * 4, strEnd = strAt + strSize;
        if (strEnd > b.Length) return null;

        var appended = new List<byte>();
        var offsetOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var newOffset = new Dictionary<int, int>();
        foreach (var (i, path) in paths)
        {
            if (i < 0 || i >= tex) return null;
            if (!offsetOf.TryGetValue(path, out var off))
            {
                off = strSize + appended.Count;
                appended.AddRange(Encoding.UTF8.GetBytes(path));
                appended.Add(0);
                offsetOf[path] = off;
            }
            newOffset[i] = off;
        }
        while (appended.Count % 4 != 0) appended.Add(0);
        if (strSize + appended.Count > ushort.MaxValue || fileSize + appended.Count > ushort.MaxValue) return null;

        var r = new byte[b.Length + appended.Count];
        Array.Copy(b, 0, r, 0, strEnd);
        appended.CopyTo(r, strEnd);
        Array.Copy(b, strEnd, r, strEnd + appended.Count, b.Length - strEnd);
        BitConverter.TryWriteBytes(r.AsSpan(4), (ushort)(fileSize + appended.Count));
        BitConverter.TryWriteBytes(r.AsSpan(8), (ushort)(strSize + appended.Count));
        foreach (var (i, off) in newOffset) BitConverter.TryWriteBytes(r.AsSpan(16 + i * 4), (ushort)off);
        return r;
    }

    /// <summary>
    /// Every colour-table row a copy of row 0. Handles both layouts: the older 16 rows of 32 bytes (512) and Dawntrail's
    /// 32 rows of 64 (2048). A material with no table comes back unchanged.
    /// </summary>
    internal static byte[] FillRowsWithRowZero(byte[] b)
    {
        if (b.Length < 16 || b[14] == 0) return b;
        ushort dataSet = U16(b, 6), strSize = U16(b, 8);
        int rowBytes = dataSet >= 2048 ? 64 : dataSet >= 512 ? 32 : 0;
        int rows = dataSet >= 2048 ? 32 : 16;
        if (rowBytes == 0) return b;
        int at = 16 + b[12] * 4 + b[13] * 4 + b[14] * 4 + strSize + b[15];
        if (at + rows * rowBytes > b.Length) return b;
        var r = (byte[])b.Clone();
        for (int row = 1; row < rows; row++) Buffer.BlockCopy(r, at, r, at + row * rowBytes, rowBytes);
        return r;
    }

    /// <summary>The colour of the generated mask: all channels full, as the guide says.</summary>
    public static readonly byte[] WhiteRgba = [255, 255, 255, 255];

    /// <summary>
    /// The colour of the generated flat normal: straight out of the surface, alpha full — opaque wherever alpha gates
    /// opacity (every row is row 0, so the row it selects doesn't matter).
    /// </summary>
    public static readonly byte[] FlatNormalRgba = [128, 128, 255, 255];

    /// <summary>A small solid-colour RGBA buffer, for <see cref="TextureLoader.WriteTex(byte[], int, int, string)"/>.</summary>
    public static byte[] Solid(byte[] rgba, int size = 16)
    {
        var o = new byte[size * size * 4];
        for (int i = 0; i < o.Length; i += 4) Array.Copy(rgba, 0, o, i, 4);
        return o;
    }

    private static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);

    private static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
}
