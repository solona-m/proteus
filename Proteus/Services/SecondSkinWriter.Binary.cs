using System;

namespace Proteus.Services;

public static partial class SecondSkinWriter
{
    /// <summary>
    /// Write uv0 at <paramref name="off"/> in the declared type, leaving any third and fourth component (uv1 on
    /// the Half4/Float4 packing) alone. False for a type it will not write; the caller must then refuse the
    /// whole model, not one mesh.
    /// </summary>
    internal static bool WriteUv0(byte[] a, int off, byte type, float u, float v)
    {
        switch (type)
        {
            case 1: case 3:        // Float2 / Float4
                W32(a, off, (uint)BitConverter.SingleToInt32Bits(u));
                W32(a, off + 4, (uint)BitConverter.SingleToInt32Bits(v));
                return true;
            case 13: case 14:      // Half2 / Half4
                W16(a, off, Half(u));
                W16(a, off + 2, Half(v));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Encode a unit normal into a vertex element of the given type, leaving any 4th component intact. False
    /// when the type has no room for three components or no defined scale. Not <see cref="WriteXYZ"/>, which
    /// drops z for a Half2 — a fine partial position write and silent corruption for a normal.
    /// </summary>
    internal static bool WriteNormal(byte[] a, int off, byte type, float x, float y, float z)
    {
        switch (type)
        {
            case 2: case 3:   // Float3 / Float4
                W32(a, off, (uint)BitConverter.SingleToInt32Bits(x));
                W32(a, off + 4, (uint)BitConverter.SingleToInt32Bits(y));
                W32(a, off + 8, (uint)BitConverter.SingleToInt32Bits(z));
                return true;
            case 14:          // Half4
                W16(a, off, Half(x)); W16(a, off + 2, Half(y)); W16(a, off + 4, Half(z));
                return true;
            case 10:          // Short4n
                W16(a, off,     (ushort)(short)Math.Clamp(MathF.Round(x * 32767f), -32767f, 32767f));
                W16(a, off + 2, (ushort)(short)Math.Clamp(MathF.Round(y * 32767f), -32767f, 32767f));
                W16(a, off + 4, (ushort)(short)Math.Clamp(MathF.Round(z * 32767f), -32767f, 32767f));
                return true;
            case 8:           // Ubyte4n — inverts ReadTyped's /255 AND BuildVerbatim's *2-1 unbias
                a[off]     = (byte)Math.Clamp(MathF.Round((x * 0.5f + 0.5f) * 255f), 0f, 255f);
                a[off + 1] = (byte)Math.Clamp(MathF.Round((y * 0.5f + 0.5f) * 255f), 0f, 255f);
                a[off + 2] = (byte)Math.Clamp(MathF.Round((z * 0.5f + 0.5f) * 255f), 0f, 255f);
                return true;
            default:
                return false;   // 9/13 have no z; 5/6/7/16/17 have no normalized scale
        }
    }

    /// <summary>Write a 2-component UV (u,v) at <paramref name="off"/>, as two halves or two floats.</summary>
    private static void WriteUV2(byte[] a, int off, bool half, float u, float v)
    {
        if (half) { W16(a, off, Half(u)); W16(a, off + 2, Half(v)); }
        else
        {
            W32(a, off, (uint)BitConverter.SingleToInt32Bits(u));
            W32(a, off + 4, (uint)BitConverter.SingleToInt32Bits(v));
        }
    }

    /// <summary>Write x,y,z into a position element of the given type, leaving any 4th component intact.</summary>
    internal static void WriteXYZ(byte[] a, int off, byte type, float x, float y, float z)
    {
        switch (type)
        {
            case 2: case 3:   // Float3 / Float4
                W32(a, off, (uint)BitConverter.SingleToInt32Bits(x));
                W32(a, off + 4, (uint)BitConverter.SingleToInt32Bits(y));
                W32(a, off + 8, (uint)BitConverter.SingleToInt32Bits(z));
                break;
            case 14:          // Half4
                W16(a, off, Half(x)); W16(a, off + 2, Half(y)); W16(a, off + 4, Half(z));
                break;
            case 13:          // Half2 (unusual for position)
                W16(a, off, Half(x)); W16(a, off + 2, Half(y));
                break;
        }
    }

    /// <summary>
    /// Decode a vertex attribute of the given FFXIV vertex-declaration <paramref name="type"/> into up to
    /// four floats. Covers the types skin meshes actually use for position/normal/uv (float, half, and
    /// the normalized integer forms); unknown types leave the destination zeroed.
    /// </summary>
    internal static void ReadTyped(byte[] s, int addr, byte type, Span<float> o)
    {
        o.Clear();
        float H(int a) => (float)BitConverter.ToHalf(s, a);
        float F(int a) => BitConverter.ToSingle(s, a);
        short I(int a) => BitConverter.ToInt16(s, a);
        ushort U(int a) => BitConverter.ToUInt16(s, a);
        switch (type)
        {
            case 0:  o[0] = F(addr); break;                                                             // Float1
            case 1:  o[0] = F(addr); o[1] = F(addr + 4); break;                                         // Float2
            case 2:  o[0] = F(addr); o[1] = F(addr + 4); o[2] = F(addr + 8); break;                     // Float3
            case 3:  o[0] = F(addr); o[1] = F(addr + 4); o[2] = F(addr + 8); o[3] = F(addr + 12); break; // Float4
            case 5:  for (int k = 0; k < 4; k++) o[k] = s[addr + k]; break;                             // Ubyte4
            case 8:  for (int k = 0; k < 4; k++) o[k] = s[addr + k] / 255f; break;                      // Ubyte4n
            case 6:  o[0] = I(addr); o[1] = I(addr + 2); break;                                         // Short2
            case 7:  for (int k = 0; k < 4; k++) o[k] = I(addr + k * 2); break;                         // Short4
            case 9:  o[0] = I(addr) / 32767f; o[1] = I(addr + 2) / 32767f; break;                       // Short2n
            case 10: for (int k = 0; k < 4; k++) o[k] = I(addr + k * 2) / 32767f; break;                // Short4n
            case 13: o[0] = H(addr); o[1] = H(addr + 2); break;                                         // Half2
            case 14: o[0] = H(addr); o[1] = H(addr + 2); o[2] = H(addr + 4); o[3] = H(addr + 6); break; // Half4
            case 16: o[0] = U(addr); o[1] = U(addr + 2); break;                                         // Ushort2
            case 17: for (int k = 0; k < 4; k++) o[k] = U(addr + k * 2); break;                         // Ushort4
        }
    }

    private static ushort Half(float f) => BitConverter.HalfToUInt16Bits((Half)f);
    private static void W16(byte[] a, int o, ushort v) => BitConverter.GetBytes(v).CopyTo(a, o);
    private static void W32(byte[] a, int o, uint v) => BitConverter.GetBytes(v).CopyTo(a, o);
}
