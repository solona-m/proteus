using System;
using System.Collections.Generic;
using System.IO;
using Proteus.Services;

namespace Proteus.Tests;

/// <summary>
/// Shared inputs for the gear shader upgrade tests: the two real legacy materials (<c>gear_top_a.mtrl</c>, the
/// index/mask form; <c>gear_legacy_diffuse.mtrl</c>, the diffuse form from Solona's Bandana Top, rows zeroed), its grey
/// specular map (<c>legacy_spec_s.tex</c>, 64×64 B8G8R8A8), and a temporary mods folder to put them in.
/// </summary>
internal sealed class ShaderUpgradeTestKit : IDisposable
{
    public const string TopPath = "chara/equipment/e6024/material/v0001/mt_c0201e6024_top_b.mtrl";
    public const string DwnPath = "chara/equipment/e6024/material/v0001/mt_c0201e6024_dwn_a.mtrl";
    public const int Pid = 4242;

    public readonly string Root = Path.Combine(Path.GetTempPath(), "proteus-su-" + Guid.NewGuid().ToString("N")[..8]);
    public string ModsRoot => Path.Combine(Root, "mods");
    public string ManagedRoot => Path.Combine(ModsRoot, SidecarDiscoveryService.ManagedModDir);

    public int Decodes;
    public ShaderUpgradePlan.MaskMemo Memo { get; } = new();

    public ShaderUpgradeTestKit()
    {
        Directory.CreateDirectory(ManagedRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* temp */ }
    }

    public static byte[] FormA() => Fixture("gear_top_a.mtrl");
    public static byte[] FormB() => Fixture("gear_legacy_diffuse.mtrl");
    public static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>The fixture specular map as RGBA8.</summary>
    public static (byte[] Rgba, int W, int H) Spec()
    {
        var b = Fixture("legacy_spec_s.tex");
        int w = BitConverter.ToUInt16(b, 8), h = BitConverter.ToUInt16(b, 10);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = b[80 + i + 2];
            rgba[i + 1] = b[80 + i + 1];
            rgba[i + 2] = b[80 + i];
            rgba[i + 3] = b[80 + i + 3];
        }
        return (rgba, w, h);
    }

    /// <summary>Put bytes into a mod folder; returns the full path.</summary>
    public string Put(string mod, string rel, byte[] bytes)
    {
        var p = Path.Combine(ModsRoot, mod, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>The specular map's resolved file: the fixture, put in the mod as its <c>_s</c>.</summary>
    public string? SpecDisk;

    public Func<string?, string, (byte[] Rgba, int W, int H)?> Decode { get; set; } = (_, _) => Spec();

    public ShaderUpgradePlan.Inputs Inputs(MaskGloss gloss = MaskGloss.InverseSpecular, IReadOnlySet<string>? published = null,
                                          TexEncoding encoding = TexEncoding.Uncompressed)
        => new()
        {
            ModsRoot = ModsRoot,
            ManagedRoot = ManagedRoot,
            Pid = Pid,
            Read = File.ReadAllBytes,
            ResolveTexture = _ => SpecDisk,
            DecodeNative = (disk, g) => { Decodes++; return Decode(disk, g); },
            Stat = p => File.Exists(p) ? (new FileInfo(p).Length, new FileInfo(p).LastWriteTimeUtc.Ticks) : null,
            MaskFileOk = name => TextureLoader.IsCompleteTex(Path.Combine(ManagedRoot, ManagedModPaths.ShaderUpgradeSubdir, name)),
            Memo = Memo,
            Gloss = gloss,
            Encoding = encoding,
            Published = published ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>A B8G8R8A8 .tex writer for tests (TextureLoader's needs the game); same header as WriteTex.</summary>
    public static bool WriteTex(byte[] rgba, int w, int h, string path, TexEncoding _)
    {
        var hd = new byte[80];
        BitConverter.TryWriteBytes(hd.AsSpan(0), 0x00800000u);
        BitConverter.TryWriteBytes(hd.AsSpan(4), 0x1450u);
        BitConverter.TryWriteBytes(hd.AsSpan(8), (ushort)w);
        BitConverter.TryWriteBytes(hd.AsSpan(10), (ushort)h);
        BitConverter.TryWriteBytes(hd.AsSpan(12), (ushort)1);
        hd[14] = 1;
        BitConverter.TryWriteBytes(hd.AsSpan(28), 80u);
        var px = new byte[rgba.Length];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = rgba[i + 2]; px[i + 1] = rgba[i + 1]; px[i + 2] = rgba[i]; px[i + 3] = rgba[i + 3];
        }
        using var f = File.Create(path);
        f.Write(hd);
        f.Write(px);
        return true;
    }

    /// <summary>A copy of a material with one sampler's texture index pointing elsewhere, or other changes.</summary>
    public static void SetKey(byte[] mtrl, uint key, uint value) => TextureLoader.PatchShaderKey(mtrl, key, value).data.CopyTo(mtrl, 0);
}
