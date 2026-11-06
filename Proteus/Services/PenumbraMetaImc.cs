using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Proteus.Services;

/// <summary>
/// The IMC entries in Penumbra's <c>GetPlayerMetaManipulations</c> blob: what the player's collection makes each gear
/// variant's entry, over the game's own. Gear only (equipment and accessories). Reads the same two binary versions as
/// <see cref="EqpVisibility.ParsePenumbraEqp"/>: 1 (<c>META0001</c>, IMC first) and 2 (<c>META0002</c>, labelled
/// sections). Fails closed: anything it cannot read in full gives null, never a partial answer.
/// </summary>
internal static class PenumbraMetaImc
{
    /// <summary>One gear variant: the set's kind letter ('e' or 'a'), set id, <see cref="ContentSlot"/> label, variant.</summary>
    public readonly record struct Key(char Kind, int SetId, string SlotLabel, int Variant);

    // ImcIdentifier: PrimaryId u16, Variant u8, ObjectType u8, SecondaryId u16, EquipSlot u8, BodySlot u8 — then the
    // 6-byte ImcEntry: MaterialId, DecalId, AttributeAndSound u16, VfxId, MaterialAnimationId.
    private const int Record = 8 + 6;
    private const uint ImcKey = ((uint)'I' << 24) | ((uint)'M' << 16) | ((uint)'C' << 8);

    // Penumbra.GameData ObjectType.
    private const byte Accessory = 3, Equipment = 11;

    /// <summary>The collection's gear IMC entries, or null when the blob is missing or in a form this does not read.</summary>
    public static Dictionary<Key, ImcEntry>? Parse(string? blob)
    {
        if (blob == null) return null;
        if (blob.Length == 0) return [];
        byte[] data;
        try
        {
            using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(blob)), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            data = ms.ToArray();
        }
        catch (Exception) { return null; }
        return ParseDecompressed(data);
    }

    internal static Dictionary<Key, ImcEntry>? ParseDecompressed(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9) return null;
        byte version = data[0];
        var magic = Encoding.ASCII.GetString(data.Slice(1, 8));
        var r = data[9..];
        switch (version)
        {
            case 1 when magic == "META0001":
            {
                if (r.Length < 4) return null;
                int count = BinaryPrimitives.ReadInt32LittleEndian(r);
                return count < 0 ? null : Read(r[4..], count);
            }
            case 2 when magic == "META0002":
            {
                while (r.Length >= 8)
                {
                    uint label = BinaryPrimitives.ReadUInt32LittleEndian(r);
                    int count = BinaryPrimitives.ReadInt32LittleEndian(r[4..]);
                    r = r[8..];
                    if (count < 0) return null;
                    if (label == ImcKey) return Read(r, count);
                    // IMC is the first section when it is there at all: any other label means the collection has none.
                    return [];
                }
                return [];
            }
            default:
                return null;
        }
    }

    private static Dictionary<Key, ImcEntry>? Read(ReadOnlySpan<byte> r, int count)
    {
        if ((long)count * Record > r.Length) return null;
        var result = new Dictionary<Key, ImcEntry>();
        for (int i = 0; i < count; i++)
        {
            var rec = r.Slice(i * Record, Record);
            int set = BinaryPrimitives.ReadUInt16LittleEndian(rec);
            int variant = rec[2];
            byte type = rec[3];
            byte slot = rec[6];
            char kind;
            if (type == Equipment) kind = 'e';
            else if (type == Accessory) kind = 'a';
            else continue;   // weapons, monsters, demihumans: not gear this reads
            if (LabelOf(type, slot) is not { } label)
            {
                // A gear slot under the other object type (Penumbra accepts an Equipment identifier with an accessory
                // slot): an odd entry, skipped. A slot number gear does not have: the layout moved, trust none of it.
                if (LabelOf(Equipment, slot) != null || LabelOf(Accessory, slot) != null) continue;
                return null;
            }
            ushort attrSound = BinaryPrimitives.ReadUInt16LittleEndian(rec[10..]);
            result[new Key(kind, set, label, variant)] = new ImcEntry(
                rec[8], rec[9], (ushort)(attrSound & 0x3FF), (byte)(attrSound >> 10), rec[12], rec[13]);
        }
        return result;
    }

    /// <summary>Penumbra.GameData's <c>EquipSlot</c> number as a <see cref="ContentSlot"/> label, for the object type.</summary>
    private static string? LabelOf(byte type, byte slot) => (type, slot) switch
    {
        (Equipment, 3) => "Head",
        (Equipment, 4) => "Body",
        (Equipment, 5) => "Hands",
        (Equipment, 7) => "Legs",
        (Equipment, 8) => "Feet",
        (Accessory, 9) => "Earrings",
        (Accessory, 10) => "Necklace",
        (Accessory, 11) => "Bracelets",
        (Accessory, 12) => "Right ring",
        (Accessory, 14) => "Left ring",
        _ => null,
    };
}
