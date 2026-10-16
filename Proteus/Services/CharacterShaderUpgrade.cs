using System;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// Move a gear material from <c>characterlegacy.shpk</c> to Dawntrail's <c>character.shpk</c>.
/// <para/>
/// Why it exists: the legacy shader has no wind. Only <c>character.shpk</c> reads a vertex's second colour as how far
/// the wind moves it, so wind painted on a garment whose materials are still legacy — most pre-Dawntrail mods, Iris
/// among them — moves nothing however its movement constants are set.
/// <para/>
/// The recipe is the one modders apply by hand (xivmodding.com, "Updating a mod to DT shaders"), checked against the
/// two legacy materials the game itself moved to <c>character.shpk</c> (e0765 and e0815 <c>_b</c>):
/// <list type="bullet">
/// <item>the shader package becomes <c>character.shpk</c>, and gains the flow-map key every <c>character.shpk</c>
/// gear material the game ships carries (<see cref="FlowMapKey"/> = <see cref="FlowMapStandard"/>);</item>
/// <item>every colour table row: fields 3 and 11 to 1 and field 7 to 0 — the slots the legacy shader read as
/// shininess and specular mask — with the shininess carried into roughness (field 16) by the reflection-mip match
/// Penumbra uses, and the game's default sheen where a row has none;</item>
/// <item>every dye row: a three-digit template to its Dawntrail four-digit twin (the same template with a 1 in
/// front), and the shininess flag to the roughness flag it now feeds.</item>
/// </list>
/// Textures are left alone: the mask's green is gloss to the old shader and roughness to the new, but the game's own
/// upgraded materials keep their legacy <c>_m</c>, and the rows' roughness already carries the shininess.
/// A material whose colour table is not Dawntrail's 32×32 layout is refused: only a pre-Dawntrail tool's output has
/// one, and its rows would need converting first.
/// </summary>
internal static class CharacterShaderUpgrade
{
    public const string Legacy = "characterlegacy.shpk";
    public const string Character = "character.shpk";

    /// <summary>The flow-map mode key, set to its standard (no flow map) value on all the game's character gear.</summary>
    public const uint FlowMapKey = 0x40D1481E, FlowMapStandard = 0x337C6BC4;

    private const int Rows = 32, HalvesPerRow = 32, RowBytes = HalvesPerRow * 2, TableBytes = Rows * RowBytes;
    private const int DyeRowBytes = 4, DyeBytes = Rows * DyeRowBytes;

    // The additional data's table flags: a table, a dye table, and the table's width (in vec4s) and height as logs.
    private const uint HasTable = 0x4, HasDyeTable = 0x8;
    private const int WidthLog = 3, HeightLog = 5;

    // Dye row flags (Penumbra's ColorDyeTableRow).
    private const uint DyeShininess = 0x08, DyeSpecularMask = 0x10, DyeRoughness = 0x20;

    /// <summary>The shader package a material names, or null when the file can't be walked that far.</summary>
    public static string? ShaderOf(byte[] mtrl)
    {
        if (mtrl.Length < 16) return null;
        int strings = 16 + 4 * (mtrl[12] + mtrl[13] + mtrl[14]);
        int size = BitConverter.ToUInt16(mtrl, 8), at = strings + BitConverter.ToUInt16(mtrl, 10);
        if (strings + size > mtrl.Length || at >= strings + size) return null;
        int end = at;
        while (end < strings + size && mtrl[end] != 0) end++;
        return Encoding.UTF8.GetString(mtrl, at, end - at);
    }

    /// <summary>
    /// The upgraded material, or null when <paramref name="mtrl"/> is not a legacy material with a Dawntrail
    /// colour table, or the file can't be walked.
    /// </summary>
    public static byte[]? Upgrade(byte[] mtrl)
    {
        if (!string.Equals(ShaderOf(mtrl), Legacy, StringComparison.OrdinalIgnoreCase)) return null;

        int dataSetSize = BitConverter.ToUInt16(mtrl, 6), additional = mtrl[15];
        int tableAt = GearMaterialWriter.ColorTableStart(mtrl);
        if (tableAt < 0 || additional < 4 || dataSetSize < TableBytes) return null;
        uint flags = BitConverter.ToUInt32(mtrl, tableAt - additional);
        if ((flags & HasTable) == 0 || ((flags >> 4) & 0xF) != WidthLog || ((flags >> 8) & 0xF) != HeightLog)
            return null;

        var r = (byte[])mtrl.Clone();
        for (int row = 0; row < Rows; row++)
            UpgradeRow(r, tableAt + row * RowBytes);
        if ((flags & HasDyeTable) != 0 && dataSetSize >= TableBytes + DyeBytes)
            for (int row = 0; row < Rows; row++)
                UpgradeDyeRow(r.AsSpan(tableAt + TableBytes + row * DyeRowBytes, DyeRowBytes));

        r = TextureLoader.EnsureShaderKey(r, FlowMapKey, FlowMapStandard);
        return RenameShader(r, Character);
    }

    private static void UpgradeRow(byte[] mtrl, int rowAt)
    {
        Half Get(int i) => BitConverter.ToHalf(mtrl, rowAt + i * 2);
        void Set(int i, float v) => BitConverter.TryWriteBytes(mtrl.AsSpan(rowAt + i * 2), (Half)v);

        float shininess = (float)Get(3);
        if (shininess > 0f) Set(16, RoughnessFromShininess(shininess));
        Set(3, 1f);
        Set(7, 0f);
        Set(11, 1f);
        if ((float)Get(12) == 0f && (float)Get(13) == 0f && (float)Get(14) == 0f)
        {
            Set(12, 0.1f);
            Set(13, 0.2f);
            Set(14, 5f);
        }
    }

    private static void UpgradeDyeRow(Span<byte> row)
    {
        uint v = BitConverter.ToUInt32(row);
        uint template = (v >> 16) & 0x7FF;
        if (template is > 0 and < 1000) v = (v & ~0x7FF0000u) | ((template + 1000) << 16);
        if ((v & DyeShininess) != 0) v = (v & ~DyeShininess) | DyeRoughness;
        // The legacy specular mask has no Dawntrail dye of the same meaning: field 7 is cleared above.
        v &= ~DyeSpecularMask;
        BitConverter.TryWriteBytes(row, v);
    }

    /// <summary>
    /// Penumbra's <c>ColorTableRow.RoughnessFromShininess</c>: solves -6 R² + 12 R = 6.25 - 0.75 log2(S), the old
    /// shaders' reflection mip from shininess set equal to the new shaders' reflection mip from roughness.
    /// </summary>
    internal static float RoughnessFromShininess(float shininess)
    {
        var mip = MathF.FusedMultiplyAdd(-0.75f, MathF.Log2(shininess), 6.25f);
        return 1.0f - MathF.Sqrt(MathF.Max(0.0f, MathF.FusedMultiplyAdd(-1.0f / 6.0f, mip, 1.0f)));
    }

    /// <summary>
    /// The material naming another shader package. The name is appended to the string table rather than written over
    /// the old one, as <see cref="TextureLoader.RetargetTexturePaths"/> appends, so no other string's offset moves.
    /// </summary>
    private static byte[]? RenameShader(byte[] b, string shader)
    {
        ushort fileSize = BitConverter.ToUInt16(b, 4), size = BitConverter.ToUInt16(b, 8);
        int stringsEnd = 16 + 4 * (b[12] + b[13] + b[14]) + size;
        if (stringsEnd > b.Length) return null;

        var appended = new System.Collections.Generic.List<byte>(Encoding.UTF8.GetBytes(shader)) { 0 };
        while (appended.Count % 4 != 0) appended.Add(0);
        if (size + appended.Count > ushort.MaxValue || fileSize + appended.Count > ushort.MaxValue) return null;

        var result = new byte[b.Length + appended.Count];
        Array.Copy(b, 0, result, 0, stringsEnd);
        appended.CopyTo(result, stringsEnd);
        Array.Copy(b, stringsEnd, result, stringsEnd + appended.Count, b.Length - stringsEnd);
        BitConverter.TryWriteBytes(result.AsSpan(4), (ushort)(fileSize + appended.Count));
        BitConverter.TryWriteBytes(result.AsSpan(8), (ushort)(size + appended.Count));
        BitConverter.TryWriteBytes(result.AsSpan(10), size);   // the new name starts where the old table ended
        return result;
    }
}
