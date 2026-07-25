using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace Proteus.Interop;

/// <summary>
/// Low-level, unsafe access to a live material's GPU colour-table texture — the mechanism behind the
/// colorset "glow / target" button. Modelled on Glamourer's DirectXService/MaterialService, but
/// ClientStructs-only (no Vortice, no Penumbra.GameData): the un-highlighted baseline is read straight
/// from the material's own <c>DataSet</c>, and a highlighted table is uploaded by creating a fresh
/// R16G16B16A16 texture and atomically swapping it into the character's colour-table slot.
///
/// Dawntrail colour table: 32 rows × 64 bytes = 2048 bytes. Per row (halves, matching
/// GearMaterialWriter): Diffuse RGB at byte 0/2/4, Emissive RGB at byte 16/18/20.
/// </summary>
internal static unsafe class ColorTableInterop
{
    public const int RowCount   = 32;
    public const int RowBytes   = 64;
    public const int TableBytes = RowCount * RowBytes;   // 2048
    private const int TexWidth  = 8;
    private const int TexHeight = RowCount;

    // Row-local byte offsets of the two HalfColor fields we overwrite.
    public const int OffDiffuse  = 0;    // halves 0,1,2
    public const int OffEmissive = 16;   // halves 8,9,10

    /// <summary>One colour-table material located on the character: its file-name leaf, its texture slot
    /// (a <c>Texture**</c> as an address), and a copy of its baseline table (from the material's DataSet).</summary>
    public readonly record struct Located(string Leaf, nint Slot, byte[] Baseline);

    /// <summary>A colour-table slot WITHOUT copying its baseline: the leaf, the texture slot address, the
    /// texture pointer currently in that slot, and the material's raw DataSet pointer (valid for this frame
    /// only). Lets a caller decide whether an upload is needed before paying for the 2 KB DataSet copy.</summary>
    public readonly record struct SlotRef(string Leaf, nint Slot, nint CurrentTex, nint DataSet);

    /// <summary>
    /// Walk the character's materials ONCE and return every colour-table material whose file-name leaf
    /// satisfies <paramref name="leafWanted"/>. Only materials that actually carry a colour table are
    /// considered (so we never blacken a plain accessory), and a baseline is copied only for wanted ones —
    /// so a highlight can be applied to (and later restored on) all of them in a single pass per frame.
    /// </summary>
    public static List<Located> FindColorTableMaterials(nint characterAddress, Func<string, bool> leafWanted)
    {
        var result = new List<Located>();
        if (characterAddress == 0)
            return result;

        var chara = (Character*)characterAddress;
        var draw  = chara->GameObject.DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase)
            return result;

        var cb = (CharacterBase*)draw;
        int slots = cb->SlotCount;
        for (int s = 0; s < slots; s++)
        {
            for (int j = 0; j < 10; j++)
            {
                int idx = s * 10 + j;
                var mat = (MaterialResourceHandle*)cb->Materials[idx];
                // Cheap pointer/field checks first — only materialise the (allocating) file name for a
                // material that actually has a colour table and whose texture slot exists.
                if (mat == null || !mat->HasColorTable || mat->DataSet == null || mat->DataSetSize < TableBytes
                    || idx >= cb->ColorTableTexturesSpan.Length)
                    continue;

                var leaf = Path.GetFileName(mat->FileName.ToString());
                if (string.IsNullOrEmpty(leaf) || !leafWanted(leaf))
                    continue;

                var slot = (nint)Unsafe.AsPointer(ref cb->ColorTableTexturesSpan[idx]);
                var baseline = new byte[TableBytes];
                fixed (byte* dst = baseline)
                    Buffer.MemoryCopy(mat->DataSet, dst, TableBytes, TableBytes);
                result.Add(new Located(leaf, slot, baseline));
            }
        }
        return result;
    }

    /// <summary>
    /// Like <see cref="FindColorTableMaterials"/> but returns each slot's current texture pointer and raw
    /// DataSet pointer WITHOUT copying the baseline — so a caller that usually skips (its texture is already
    /// current) doesn't allocate a 2 KB table every frame. Read the table lazily via <see cref="ReadTable"/>
    /// using the returned <c>DataSet</c> (same-frame only). Framework thread.
    /// </summary>
    public static List<SlotRef> FindColorTableSlots(nint characterAddress, Func<string, bool> leafWanted)
    {
        var result = new List<SlotRef>();
        if (characterAddress == 0)
            return result;

        var chara = (Character*)characterAddress;
        var draw  = chara->GameObject.DrawObject;
        if (draw == null || draw->GetObjectType() != ObjectType.CharacterBase)
            return result;

        var cb = (CharacterBase*)draw;
        int slots = cb->SlotCount;
        for (int s = 0; s < slots; s++)
        {
            for (int j = 0; j < 10; j++)
            {
                int idx = s * 10 + j;
                var mat = (MaterialResourceHandle*)cb->Materials[idx];
                if (mat == null || !mat->HasColorTable || mat->DataSet == null || mat->DataSetSize < TableBytes
                    || idx >= cb->ColorTableTexturesSpan.Length)
                    continue;

                var leaf = Path.GetFileName(mat->FileName.ToString());
                if (string.IsNullOrEmpty(leaf) || !leafWanted(leaf))
                    continue;

                var slot = (nint)Unsafe.AsPointer(ref cb->ColorTableTexturesSpan[idx]);
                result.Add(new SlotRef(leaf, slot, *(nint*)slot, (nint)mat->DataSet));
            }
        }
        return result;
    }

    /// <summary>Copy a material's 2048-byte colour table out of its raw DataSet pointer (same-frame only).</summary>
    public static byte[] ReadTable(nint dataSet)
    {
        var table = new byte[TableBytes];
        if (dataSet != 0)
            fixed (byte* dst = table)
                Buffer.MemoryCopy((void*)dataSet, dst, TableBytes, TableBytes);
        return table;
    }

    /// <summary>
    /// Upload a 2048-byte colour table into the slot returned by <see cref="TryFind"/>, swapping in a new
    /// GPU texture and releasing the old one. Must run on the framework thread.
    /// </summary>
    public static bool Upload(nint texSlot, byte[] table)
    {
        if (texSlot == 0 || table.Length < TableBytes)
            return false;

        var size = stackalloc int[2];
        size[0] = TexWidth;
        size[1] = TexHeight;
        var tex = Device.Instance()->CreateTexture2D(size, 1, TextureFormat.R16G16B16A16_FLOAT,
            TextureFlags.TextureType2D | TextureFlags.Managed | TextureFlags.Immutable, 7);
        if (tex == null)
            return false;

        bool ok;
        fixed (byte* ptr = table)
            ok = tex->InitializeContents(ptr);
        if (!ok)
        {
            tex->DecRef();
            return false;
        }

        var slot = (nint*)texSlot;
        var old  = (Texture*)Interlocked.Exchange(ref *slot, (nint)tex);
        if (old != null)
            old->DecRef();
        return true;
    }

    /// <summary>Write an RGB HalfColor (3 packed halves) at <paramref name="byteOffset"/> in a row.</summary>
    public static void WriteHalfColor(byte[] table, int row, int byteOffset, float r, float g, float b)
    {
        int o = row * RowBytes + byteOffset;
        BitConverter.TryWriteBytes(table.AsSpan(o),     (ushort)BitConverter.HalfToInt16Bits((Half)r));
        BitConverter.TryWriteBytes(table.AsSpan(o + 2), (ushort)BitConverter.HalfToInt16Bits((Half)g));
        BitConverter.TryWriteBytes(table.AsSpan(o + 4), (ushort)BitConverter.HalfToInt16Bits((Half)b));
    }
}
