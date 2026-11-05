using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Proteus.Services;

namespace Proteus.Tests;

/// <summary>
/// Shared inputs for the gear shader upgrade tests: the two real legacy materials (<c>gear_top_a.mtrl</c>, the
/// index/mask form; <c>gear_legacy_diffuse.mtrl</c>, the diffuse form from Solona's Bandana Top, rows zeroed), its grey
/// specular map (<c>legacy_spec_s.tex</c>, 64×64 B8G8R8A8), and a temporary mods folder holding real v4 mods built the
/// way Penumbra writes them.
/// </summary>
internal sealed class ShaderUpgradeTestKit : IDisposable
{
    public const string TopPath = "chara/equipment/e6024/material/v0001/mt_c0201e6024_top_b.mtrl";
    public const string DwnPath = "chara/equipment/e6024/material/v0001/mt_c0201e6024_dwn_a.mtrl";

    public readonly string ModsRoot = Path.Combine(Path.GetTempPath(), "proteus-su-" + Guid.NewGuid().ToString("N")[..8]);
    public string ManagedRoot => Path.Combine(ModsRoot, SidecarDiscoveryService.ManagedModDir);

    public int Decodes;
    public ShaderUpgradePlan.MaskMemo Memo { get; } = new();

    public ShaderUpgradeTestKit() => Directory.CreateDirectory(ModsRoot);

    public void Dispose()
    {
        ShaderUpgradeWriter.AfterStep = null;
        try { Directory.Delete(ModsRoot, recursive: true); } catch { /* temp */ }
    }

    public static byte[] FormA() => Fixture("gear_top_a.mtrl");
    public static byte[] FormB() => Fixture("gear_legacy_diffuse.mtrl");
    public static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    public static string SpecPath => ShaderUpgradePlan.MaskSlotPath(FormB())!;

    /// <summary>The fixture specular map as RGBA8.</summary>
    public static (byte[] Rgba, int W, int H) Spec()
    {
        var b = Fixture("legacy_spec_s.tex");
        int w = BitConverter.ToUInt16(b, 8), h = BitConverter.ToUInt16(b, 10);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = b[80 + i + 2]; rgba[i + 1] = b[80 + i + 1]; rgba[i + 2] = b[80 + i]; rgba[i + 3] = b[80 + i + 3];
        }
        return (rgba, w, h);
    }

    public static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    /// <summary>A v4 mod: default data and groups (name, type, options of game path → file).</summary>
    public string Mod(string name, Dictionary<string, string>? defaults = null,
                      params (string Group, string Type, (string Option, Dictionary<string, string> Files)[] Options)[] groups)
    {
        var root = Path.Combine(ModsRoot, name);
        Directory.CreateDirectory(root);
        var gs = groups.Select(g => g.Type == "Combining"
            ? (object)new
            {
                Name = g.Group, Description = "", Priority = 0, Type = "Combining", DefaultSettings = 0,
                Options = g.Options.Select(o => new { Name = o.Option, Description = "" }),
                Containers = g.Options.Select(o => new { Files = o.Files, FileSwaps = new Dictionary<string, string>(),
                                                         Manipulations = Array.Empty<object>() }),
            }
            : new
            {
                Name = g.Group, Description = "", Priority = 0, Type = g.Type, DefaultSettings = 0,
                Options = g.Options.Select(o => new { Name = o.Option, Description = "", Files = o.Files,
                                                      FileSwaps = new Dictionary<string, string>(),
                                                      Manipulations = Array.Empty<object>() }),
            }).ToList();
        var meta = new Dictionary<string, object>
        {
            ["FileVersion"] = 4, ["Name"] = name, ["Identifier"] = name, ["Author"] = "Someone",
            ["Groups"] = gs,
            ["DefaultData"] = new { Files = defaults ?? new Dictionary<string, string>(), FileSwaps = new Dictionary<string, string>(),
                                    Manipulations = Array.Empty<object>() },
        };
        File.WriteAllText(Path.Combine(root, "meta.json"), JsonSerializer.Serialize(meta));
        return root;
    }

    /// <summary>A pre-v4 folder: meta.json at FileVersion 3 and a default_mod.json.</summary>
    public string LegacyMod(string name, Dictionary<string, string> files)
    {
        var root = Path.Combine(ModsRoot, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "meta.json"), "{\"FileVersion\":3,\"Name\":\"" + name + "\"}");
        File.WriteAllText(Path.Combine(root, "default_mod.json"),
                          JsonSerializer.Serialize(new { Files = files, FileSwaps = new Dictionary<string, string>(), Manipulations = Array.Empty<object>() }));
        return root;
    }

    public static string Put(string modRoot, string rel, byte[] bytes)
    {
        var p = Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    public static byte[] Get(string modRoot, string rel) => File.ReadAllBytes(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Every file of a mod (but its manifest) → hash.</summary>
    public static Dictionary<string, string> Hashes(string modRoot)
        => Directory.GetFiles(modRoot, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith("meta.json", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(f => Path.GetRelativePath(modRoot, f).Replace('\\', '/'), f => Sha(File.ReadAllBytes(f)));

    /// <summary>A standard dress: a "Colour" pick-one group whose two options ship the top material (and its _s for B).</summary>
    public string Dress(string name, byte[] material, bool withSpec = false)
    {
        var red = new Dictionary<string, string> { [TopPath] = "red/top.mtrl" };
        var blue = new Dictionary<string, string> { [TopPath] = "blue/top.mtrl" };
        if (withSpec) { red[SpecPath] = "tex/top_s.tex"; blue[SpecPath] = "tex/top_s.tex"; }
        var root = Mod(name, null, ("Colour", "Single", [("Red", red), ("Blue", blue)]));
        Put(root, "red/top.mtrl", material);
        Put(root, "blue/top.mtrl", material);
        if (withSpec) Put(root, "tex/top_s.tex", Fixture("legacy_spec_s.tex"));
        return root;
    }

    public Func<string?, string, (byte[] Rgba, int W, int H)?> Decode { get; set; } = (_, _) => Spec();

    public ShaderUpgradePlan.Inputs Inputs(MaskGloss gloss = MaskGloss.InverseSpecular,
                                          TexEncoding encoding = TexEncoding.Uncompressed)
        => new()
        {
            ModsRoot = ModsRoot,
            ManagedRoot = ManagedRoot,
            Read = File.ReadAllBytes,
            DecodeNative = (disk, g) => { Decodes++; return Decode(disk, g); },
            Stat = p => File.Exists(p) ? (new FileInfo(p).Length, new FileInfo(p).LastWriteTimeUtc.Ticks) : null,
            ReadOwners = HairPortPlan.ReadOwners,
            IsDeclined = ShaderUpgradeWriter.IsDeclined,
            MaskFileOk = (modRoot, rel) => TextureLoader.IsCompleteTex(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar))),
            Memo = Memo,
            Gloss = gloss,
            Encoding = encoding,
        };

    /// <summary>Plan the worn materials, resolving each specular game path to <paramref name="resolveS"/>.</summary>
    public ShaderUpgradePlan.Plan Plan(IEnumerable<ShaderUpgradePlan.Worn> worn, Func<string, string?>? resolveS = null,
                                       MaskGloss gloss = MaskGloss.InverseSpecular)
    {
        var inp = Inputs(gloss);
        var (candidates, left) = ShaderUpgradePlan.Candidates(worn, inp);
        var resolved = candidates.Where(c => c.SpecularGamePath != null)
                                 .Select(c => c.SpecularGamePath!).Distinct()
                                 .ToDictionary(s => s, s => resolveS?.Invoke(s));
        return ShaderUpgradePlan.Build(candidates, resolved, inp, left);
    }

    /// <summary>Plan and write one mod.</summary>
    public ShaderUpgradeWriter.Outcome Upgrade(string modRoot, IEnumerable<ShaderUpgradePlan.Worn> worn,
                                               Func<string, string?>? resolveS = null, Func<bool>? mayWrite = null)
    {
        var plan = Plan(worn, resolveS);
        var mod = plan.PerMod.SingleOrDefault(m => m.ModRoot == modRoot);
        return mod == null
            ? new ShaderUpgradeWriter.Outcome([], [], false)
            : ShaderUpgradeWriter.Apply(modRoot, mod.Writes, mayWrite ?? (() => true), WriteTex, TexEncoding.Uncompressed);
    }

    /// <summary>A B8G8R8A8 .tex writer for tests (TextureLoader's needs the game); the same header as WriteTex.</summary>
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
        for (int i = 0; i < px.Length; i += 4) { px[i] = rgba[i + 2]; px[i + 1] = rgba[i + 1]; px[i + 2] = rgba[i]; px[i + 3] = rgba[i + 3]; }
        using var f = File.Create(path);
        f.Write(hd);
        f.Write(px);
        return true;
    }

    public static ShaderUpgradePlan.Worn Worn(string gamePath, string modRoot, string rel)
        => new(gamePath, Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
}
