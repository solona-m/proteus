using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A top that ships its own legs clears BodyShowLeg; the pants model still loads, and a second skin cut from it laid a
/// second pair of stockings over the top's own (Sirius over Denim and Lace: dark, z-fighting). The shell must skip
/// every slot the worn gear's EQP hides, read the way Penumbra resolves it.
/// </summary>
public class EqpVisibilityTests
{
    /// <summary>Sirius's own manipulation: EQP Entry 16003 on e0154 Body — BodyShowLeg (0x100) clear, BodyShowHand set.</summary>
    private const ulong SiriusBody = 16003;

    private static Func<int, byte, ulong> Table(Dictionary<(int, byte), ulong> entries, ulong fallback = ulong.MaxValue)
        => (set, slot) => entries.TryGetValue((set, slot), out var e) ? e : fallback;

    [Fact]
    public void A_top_that_hides_the_legs_hides_the_pants()
    {
        var hidden = EqpVisibility.HiddenParts(154, 375, Table(new()
        {
            [(154, EqpVisibility.SlotBody)] = SiriusBody,
        }));
        Assert.Contains("dwn", hidden);
        Assert.DoesNotContain("glv", hidden);
    }

    [Fact]
    public void An_ordinary_top_hides_nothing()
        => Assert.Empty(EqpVisibility.HiddenParts(154, 375, Table(new())));

    /// <summary>With the legs hidden the game reads the feet flag off the BODY item's legs byte, not the pants'.</summary>
    [Fact]
    public void Feet_follow_the_top_when_it_hides_the_legs()
    {
        var hidden = EqpVisibility.HiddenParts(154, 375, Table(new()
        {
            [(154, EqpVisibility.SlotBody)] = SiriusBody,
            [(154, EqpVisibility.SlotLegs)] = 0x01ul << 16,          // LegsEnabled, no LegsShowFoot
            [(375, EqpVisibility.SlotLegs)] = 0x21ul << 16,          // the pants would have shown them
        }));
        Assert.Contains("sho", hidden);
    }

    [Fact]
    public void Penumbra_v1_blob_yields_its_eqp_overrides()
    {
        var overrides = EqpVisibility.ParsePenumbraEqp(Compress(V1(imcCount: 2, (154, EqpVisibility.SlotBody, SiriusBody))));
        Assert.NotNull(overrides);
        Assert.Equal(SiriusBody, overrides![(154, EqpVisibility.SlotBody)]);
        Assert.Equal(SiriusBody, EqpVisibility.Entry(154, EqpVisibility.SlotBody, overrides, vanilla: null));
    }

    [Fact]
    public void Penumbra_v2_blob_yields_its_eqp_overrides()
    {
        var overrides = EqpVisibility.ParsePenumbraEqp(Compress(V2(imcCount: 1, (154, EqpVisibility.SlotBody, SiriusBody))));
        Assert.NotNull(overrides);
        Assert.Equal(SiriusBody, overrides![(154, EqpVisibility.SlotBody)]);
    }

    /// <summary>A layout this cannot trust must read as "unknown", which hides nothing.</summary>
    [Fact]
    public void An_unreadable_blob_is_unknown_not_empty()
    {
        Assert.Null(EqpVisibility.ParsePenumbraEqp("not base64 at all"));
        Assert.Null(EqpVisibility.ParsePenumbraEqp(Compress(V1(imcCount: 0, (154, 42, SiriusBody)))));
    }

    [Fact]
    public void A_collapsed_block_reads_as_the_default_entry()
    {
        // Control word: only block 0 present. Set 200 lives in block 1.
        var file = new byte[160 * 8];
        BitConverter.GetBytes(1ul).CopyTo(file, 0);
        BitConverter.GetBytes(0x1234ul).CopyTo(file, 5 * 8);
        Assert.Equal(0x1234ul, EqpVisibility.VanillaEntry(file, 5));
        Assert.Equal(0x3fe00070603f00ul, EqpVisibility.VanillaEntry(file, 200));
    }

    /// <summary>
    /// The real table, with Sirius's override: legs hidden, hands and feet still drawn (the shoes render in game).
    /// </summary>
    [LocalDataFact(LocalData.GameData)]
    [Trait("Speed", "Slow")]
    public void Sirius_over_the_real_table_hides_only_the_legs()
    {
        var data = new Lumina.GameData(LocalData.Path(LocalData.GameData));
        var vanilla = data.GetFile(EqpVisibility.VanillaEqpPath)!.Data;
        var overrides = new Dictionary<(int Set, byte Slot), ulong> { [(154, EqpVisibility.SlotBody)] = SiriusBody };

        var hidden = EqpVisibility.HiddenParts(154, 375,
            (set, slot) => EqpVisibility.Entry(set, slot, overrides, vanilla)!.Value);
        Assert.Equal(["dwn"], hidden);

        // Without the override, vanilla e0154 hides nothing.
        Assert.Empty(EqpVisibility.HiddenParts(154, 375,
            (set, slot) => EqpVisibility.Entry(set, slot, null, vanilla)!.Value));
    }

    // ── Penumbra's MetaApi encodings ──────────────────────────────────────────────

    private static byte[] V1(int imcCount, params (ushort Set, byte Slot, ulong Entry)[] eqp)
    {
        var w = new BinaryWriter(new MemoryStream());
        w.Write((byte)1);
        w.Write(Encoding.ASCII.GetBytes("META0001"));
        w.Write(imcCount);
        for (int i = 0; i < imcCount; i++) w.Write(new byte[14]);
        WriteEqp(w, eqp);
        for (int i = 0; i < 5; i++) w.Write(0);   // eqdp, est, rsp, gmp, global eqp
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    private static byte[] V2(int imcCount, params (ushort Set, byte Slot, ulong Entry)[] eqp)
    {
        var w = new BinaryWriter(new MemoryStream());
        w.Write((byte)2);
        w.Write(Encoding.ASCII.GetBytes("META0002"));
        w.Write(((uint)'I' << 24) | ((uint)'M' << 16) | ((uint)'C' << 8));
        w.Write(imcCount);
        for (int i = 0; i < imcCount; i++) w.Write(new byte[14]);
        w.Write(((uint)'E' << 24) | ((uint)'Q' << 16) | ((uint)'P' << 8));
        WriteEqp(w, eqp);
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    private static void WriteEqp(BinaryWriter w, (ushort Set, byte Slot, ulong Entry)[] eqp)
    {
        w.Write(eqp.Length);
        foreach (var (set, slot, entry) in eqp)
        {
            w.Write(set);
            w.Write(slot);
            w.Write((byte)0);   // padding to the identifier's 4 bytes
            w.Write(entry);
        }
    }

    private static string Compress(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true)) gz.Write(raw);
        return Convert.ToBase64String(ms.ToArray());
    }
}
