using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Proteus.Services.Matcap;

namespace Proteus.Tests;

/// <summary>
/// Generated matcap fixtures: a <c>characterreflection</c>-style material in code, and mod folders laid out like a
/// matcap library (Single groups switching one shared texture path). Built here so the tests run anywhere — never
/// from the reference library's files, whose textures may not be redistributed.
/// </summary>
internal static class SyntheticMatcapMod
{
    /// <summary>
    /// A .mtrl in the v1.3 layout: <paramref name="textures"/> in table order, samplers binding ids to texture indices,
    /// float constants, and an optional legacy (512 B) or Dawntrail (2048 B) colour table whose row k is filled with k.
    /// </summary>
    internal static byte[] Material(string shader, string[] textures, (uint Id, int Tex)[] samplers,
                                    (uint Id, float[] Values)[]? constants = null, int colorTableBytes = 512)
    {
        var strings = new MemoryStream();
        var texOffsets = new List<ushort>();
        foreach (var t in textures)
        {
            texOffsets.Add((ushort)strings.Position);
            strings.Write(Encoding.ASCII.GetBytes(t)); strings.WriteByte(0);
        }
        ushort uvOff = (ushort)strings.Position;
        strings.Write("map1\0"u8);
        ushort csOff = (ushort)strings.Position;
        strings.Write("colorSet1\0"u8);
        ushort shpkOff = (ushort)strings.Position;
        strings.Write(Encoding.ASCII.GetBytes(shader)); strings.WriteByte(0);
        while (strings.Length % 4 != 0) strings.WriteByte(0);
        var str = strings.ToArray();

        var table = new byte[colorTableBytes];
        int rowBytes = colorTableBytes >= 2048 ? 64 : 32;
        for (int row = 0; row < colorTableBytes / Math.Max(1, rowBytes); row++)
            for (int k = 0; k < rowBytes / 2; k++)
                BitConverter.TryWriteBytes(table.AsSpan(row * rowBytes + k * 2), (Half)(row + k * 0.01f));

        var consts = constants ?? [(MatcapMaterial.ConstAlphaThreshold, [0.5f]), (0x59BDA0B1, [0f])];
        var values = new MemoryStream();
        var constRecs = new MemoryStream();
        foreach (var (id, vals) in consts)
        {
            constRecs.Write(BitConverter.GetBytes(id));
            constRecs.Write(BitConverter.GetBytes((ushort)values.Position));
            constRecs.Write(BitConverter.GetBytes((ushort)(vals.Length * 4)));
            foreach (var v in vals) values.Write(BitConverter.GetBytes(v));
        }

        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(0x01030000u));
        ms.Write(new byte[2]);                                                     // file size, patched
        ms.Write(BitConverter.GetBytes((ushort)colorTableBytes));
        ms.Write(BitConverter.GetBytes((ushort)str.Length));
        ms.Write(BitConverter.GetBytes(shpkOff));
        ms.WriteByte((byte)textures.Length);
        ms.WriteByte(1);
        ms.WriteByte((byte)(colorTableBytes > 0 ? 1 : 0));
        ms.WriteByte(4);                                                           // additional data size
        foreach (var off in texOffsets) { ms.Write(BitConverter.GetBytes(off)); ms.Write(new byte[2]); }
        ms.Write(BitConverter.GetBytes(uvOff)); ms.Write(new byte[2]);
        if (colorTableBytes > 0) { ms.Write(BitConverter.GetBytes(csOff)); ms.Write(new byte[2]); }
        ms.Write(str);
        ms.Write(colorTableBytes > 0 ? new byte[] { 4, 0, 0, 0 } : new byte[4]);
        ms.Write(table);
        ms.Write(BitConverter.GetBytes((ushort)values.Length));
        ms.Write(BitConverter.GetBytes((ushort)0));                                // shader keys
        ms.Write(BitConverter.GetBytes((ushort)consts.Length));
        ms.Write(BitConverter.GetBytes((ushort)samplers.Length));
        ms.Write(BitConverter.GetBytes(0xDu));                                    // flags
        ms.Write(constRecs.ToArray());
        foreach (var (id, tex) in samplers)
        {
            ms.Write(BitConverter.GetBytes(id));
            ms.Write(BitConverter.GetBytes(0x000F8340u));
            ms.WriteByte((byte)tex); ms.Write(new byte[3]);
        }
        ms.Write(values.ToArray());
        var o = ms.ToArray();
        BitConverter.TryWriteBytes(o.AsSpan(4), (ushort)o.Length);
        return o;
    }

    /// <summary>A reflection material like the game's: normal, mask, matcap, in that order.</summary>
    internal static byte[] Reflection(string normal = "chara/x/n.tex", string mask = "chara/x/s.tex",
                                      string matcap = "chara/x/d.tex", int colorTableBytes = 512)
        => Material(MatcapMaterial.Shader, [normal, mask, matcap],
            [(MatcapMaterial.SamplerNormal, 0), (MatcapMaterial.SamplerMask, 1), (MatcapMaterial.SamplerMatcap, 2)],
            colorTableBytes: colorTableBytes);

    /// <summary>A gear material on the character shader, binding a normal at index 1 (and maybe an alpha threshold).</summary>
    internal static byte[] Gear(string normal, float? alphaThreshold = null, string shader = "characterlegacy.shpk")
        => Material(shader, ["chara/x/gear_d.tex", normal],
            [(0x115306BE, 0), (MatcapMaterial.SamplerNormal, 1)],
            alphaThreshold is { } a ? [(MatcapMaterial.ConstAlphaThreshold, [a])] : [(0x59BDA0B1, [0f])]);

    /// <summary>A tiny .tex stand-in: content only matters as bytes the tests can tell apart.</summary>
    internal static byte[] Tex(byte seed) => Enumerable.Range(0, 96).Select(i => (byte)(i * 7 + seed)).ToArray();

    /// <summary>
    /// A library mod folder in the v4 layout: <c>meta.json</c>, <c>default_mod.json</c> and one Single group per slot,
    /// each option redirecting <paramref name="slotPaths"/>' path to its own .tex, plus a reflection material reading it
    /// in an option of a Multi group that is off by default.
    /// </summary>
    internal static string Library(string root, string folder, string name, string website,
                                   IReadOnlyList<string> slotPaths, IReadOnlyList<string> options,
                                   bool v3 = false, bool materialInDefault = false, string? extraDefaultRedirect = null)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 3, ["Name"] = name, ["Author"] = "Test", ["Website"] = website,
        }.ToJsonString());

        var groups = new List<JsonObject>();
        var matFiles = new JsonObject();
        for (int s = 0; s < slotPaths.Count; s++)
        {
            var mtrl = Reflection(matcap: slotPaths[s]);
            var rel = $"mat{s}.mtrl";
            File.WriteAllBytes(Path.Combine(dir, rel), mtrl);
            matFiles[$"chara/monster/m8058/obj/body/b0001/material/v0001/mt_m8058b0001_{(char)('b' + s)}.mtrl"] = rel;
        }
        var meshGroupOptions = new JsonArray(new JsonObject { ["Name"] = "Materials", ["Files"] = matFiles });
        var defaultFiles = new JsonObject();
        if (materialInDefault) foreach (var kv in matFiles) defaultFiles[kv.Key] = kv.Value!.GetValue<string>();
        if (extraDefaultRedirect != null)
        {
            File.WriteAllBytes(Path.Combine(dir, "extra.tex"), Tex(9));
            defaultFiles[extraDefaultRedirect] = "extra.tex";
        }
        groups.Add(new JsonObject
        {
            ["Name"] = "Mesh placeholder", ["Type"] = "Multi", ["DefaultSettings"] = 0, ["Options"] = meshGroupOptions,
        });
        for (int s = 0; s < slotPaths.Count; s++)
        {
            var opts = new JsonArray();
            for (int i = 0; i < options.Count; i++)
            {
                var rel = $"common/{s}/{i}.tex";
                Directory.CreateDirectory(Path.Combine(dir, "common", s.ToString()));
                File.WriteAllBytes(Path.Combine(dir, rel), Tex((byte)(s * 50 + i)));
                opts.Add(new JsonObject { ["Name"] = options[i], ["Files"] = new JsonObject { [slotPaths[s]] = rel.Replace('/', '\\') } });
            }
            opts.Add(new JsonObject { ["Name"] = "None", ["Files"] = new JsonObject() });
            groups.Add(new JsonObject
            {
                ["Name"] = $"Texture Slot {(char)('A' + s)}", ["Type"] = "Single", ["DefaultSettings"] = 0, ["Options"] = opts,
            });
        }

        if (v3)
        {
            File.WriteAllText(Path.Combine(dir, "default_mod.json"), new JsonObject { ["Files"] = defaultFiles }.ToJsonString());
            for (int g = 0; g < groups.Count; g++)
                File.WriteAllText(Path.Combine(dir, $"group_{g + 1:D3}_{groups[g]["Name"]!.GetValue<string>().ToLowerInvariant()}.json"),
                    groups[g].ToJsonString());
        }
        else
        {
            // v4: groups and the default data live in meta.json.
            var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")))!.AsObject();
            meta["FileVersion"] = 4;
            meta["DefaultData"] = new JsonObject { ["Files"] = defaultFiles, ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray() };
            meta["Groups"] = new JsonArray(groups.Select(g => (JsonNode)g).ToArray());
            File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToJsonString());
        }
        return dir;
    }

    /// <summary>
    /// A minimal v4 target mod: <paramref name="files"/> (relative path → bytes) on disk, published in the default data
    /// at the given game paths.
    /// </summary>
    internal static string TargetMod(string root, string folder, IReadOnlyDictionary<string, (string Rel, byte[] Bytes)> published)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        var files = new JsonObject();
        foreach (var (gamePath, (rel, bytes)) in published)
        {
            var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            files[gamePath] = rel.Replace('/', '\\');
        }
        File.WriteAllText(Path.Combine(dir, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4, ["Name"] = folder, ["Author"] = "Test",
            ["DefaultData"] = new JsonObject { ["Files"] = files, ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray() },
            ["Groups"] = new JsonArray(),
        }.ToJsonString());
        return dir;
    }
}
