using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Proteus.Services;
using XivLiveMesh;

namespace Proteus.Tests;

/// <summary>
/// What the hair port tests build on without game data: a racial deformer of a few races, spheres for heads and hair,
/// and throwaway mod folders.
/// <para/>
/// The deformer puts a Miqo'te woman's (c0801) head 10 cm above a Midlander woman's (c0201), and a Lalafell woman's
/// (c1201, which falls through to c1101 and then c0101) 60 cm below — so a port that bends nothing lands visibly wrong.
/// </summary>
internal static class HairPortFixtures
{
    public const int Id = 130;

    public static readonly Vector3 Head = new(0f, 1.5f, 0f);

    /// <summary>How far each race's head sits from c0201's.</summary>
    public static Vector3 Offset(ushort race) => race switch
    {
        801 => new Vector3(0f, 0.1f, 0f),
        1201 or 1101 => new Vector3(0f, -0.6f, 0f),
        _ => Vector3.Zero,
    };

    /// <summary>The deformer: each race's head bone moved by <see cref="Offset"/> against its parent's.</summary>
    public static PbdFile Pbd() => new(BuildPbd(
    [
        (101, ushort.MaxValue, new() { ["j_kao"] = Matrix4x4.Identity }),
        (201, 101, new() { ["j_kao"] = Matrix4x4.Identity }),
        (401, 201, new() { ["j_kao"] = Matrix4x4.Identity }),
        (601, 201, new() { ["j_kao"] = Matrix4x4.Identity }),
        (1001, 201, new() { ["j_kao"] = Matrix4x4.Identity }),
        (1401, 201, new() { ["j_kao"] = Matrix4x4.Identity }),
        (1601, 201, new() { ["j_kao"] = Matrix4x4.Identity }),
        (1801, 201, new() { ["j_kao"] = Matrix4x4.Identity }),
        (801, 201, new() { ["j_kao"] = Matrix4x4.CreateTranslation(Offset(801)) }),
        (1101, 101, new() { ["j_kao"] = Matrix4x4.CreateTranslation(Offset(1101)) }),
        (1201, 1101, new() { ["j_kao"] = Matrix4x4.Identity }),
    ]));

    /// <summary>
    /// A human.pbd: headers, the link tree, then one deformer per race — bone names and 3×4 column-vector matrices,
    /// the layout <see cref="PbdFile"/> reads.
    /// </summary>
    public static byte[] BuildPbd(IReadOnlyList<(ushort Race, ushort Parent, Dictionary<string, Matrix4x4> Bones)> races)
    {
        int n = races.Count;
        var deformers = new List<byte[]>();
        foreach (var (_, _, bones) in races)
        {
            var names = bones.Keys.ToList();
            int pad = names.Count * 2 % 4;
            int namesAt = 4 + names.Count * 2 + pad + names.Count * 48;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(names.Count);
            int at = namesAt;
            foreach (var name in names) { w.Write((short)at); at += name.Length + 1; }
            for (int i = 0; i < pad; i++) w.Write((byte)0);
            foreach (var name in names)
            {
                var m = bones[name];
                // Row vectors here; the file stores the column-vector form, three rows of four.
                foreach (var f in new[] { m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43 })
                    w.Write(f);
            }
            foreach (var name in names) { w.Write(Encoding.ASCII.GetBytes(name)); w.Write((byte)0); }
            while (ms.Length % 4 != 0) w.Write((byte)0);
            deformers.Add(ms.ToArray());
        }

        var o = new MemoryStream();
        var bw = new BinaryWriter(o);
        bw.Write(n);
        int offset = 4 + n * 12 + n * 8;
        for (int i = 0; i < n; i++)
        {
            bw.Write(races[i].Race);
            bw.Write((ushort)i);
            bw.Write(offset);
            bw.Write(1f);   // the header's last four bytes, which PbdFile skips
            offset += deformers[i].Length;
        }
        for (int i = 0; i < n; i++)
        {
            int parent = races[i].Parent == ushort.MaxValue ? -1 : races.ToList().FindIndex(r => r.Race == races[i].Parent);
            bw.Write(parent < 0 ? ushort.MaxValue : (ushort)parent);
            bw.Write(ushort.MaxValue);
            bw.Write(ushort.MaxValue);
            bw.Write((ushort)i);
        }
        foreach (var d in deformers) bw.Write(d);
        return o.ToArray();
    }

    /// <summary>A sphere of latitude/longitude rings, every vertex following <paramref name="bones"/>.</summary>
    public static SyntheticModel.SurfaceMesh Sphere(string material, Vector3 centre, float radius,
                                                    (string Bone, float W)[] bones, int rings = 14, int segments = 20,
                                                    float minY = -1f)
    {
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        for (int r = 0; r <= rings; r++)
        {
            float phi = MathF.PI * r / rings;
            for (int s = 0; s < segments; s++)
            {
                float th = 2 * MathF.PI * s / segments;
                var d = new Vector3(MathF.Sin(phi) * MathF.Cos(th), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(th));
                pos.Add(centre + d * radius);
                nrm.Add(d);
            }
        }
        var tris = new List<int>();
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                int a = r * segments + s, b = r * segments + (s + 1) % segments;
                int c = a + segments, d = b + segments;
                if (pos[a].Y - centre.Y < minY * radius && pos[c].Y - centre.Y < minY * radius) continue;
                tris.AddRange([a, c, b, b, c, d]);
            }
        return new SyntheticModel.SurfaceMesh(material, [.. pos], [.. nrm], new Vector2[pos.Count],
            Enumerable.Repeat(bones, pos.Count).ToArray()!, [(0u, tris.ToArray())]);
    }

    /// <summary>The game's table of hairstyle skeletons, as the game lays it out: which skeleton each race's hairstyle
    /// loads.</summary>
    public static byte[] EstBytes(params (ushort Race, int Set, int Skeleton)[] entries)
    {
        var sorted = entries.OrderBy(e => e.Race).ThenBy(e => e.Set).ToList();
        var bytes = new byte[4 + sorted.Count * 6];
        BitConverter.GetBytes(sorted.Count).CopyTo(bytes, 0);
        for (int i = 0; i < sorted.Count; i++)
        {
            BitConverter.GetBytes((ushort)sorted[i].Set).CopyTo(bytes, 4 + i * 4);
            BitConverter.GetBytes(sorted[i].Race).CopyTo(bytes, 4 + i * 4 + 2);
            BitConverter.GetBytes((ushort)sorted[i].Skeleton).CopyTo(bytes, 4 + sorted.Count * 4 + i * 2);
        }
        return bytes;
    }

    /// <summary>A race's face model: a 9 cm head on its head bone.</summary>
    public static byte[] HeadModel(ushort race, float radius = 0.09f)
        => SyntheticModel.BuildSurface([], Sphere($"/mt_c{race:D4}f0001_fac_a.mtrl", Head + Offset(race), radius,
                                                  [("j_kao", 1f)]));

    /// <summary>A race's hairstyle: a 10 cm cap over its head, plus any extra meshes.</summary>
    public static byte[] HairModel(ushort race, string? material = null, (string Bone, float W)[]? bones = null,
                                   params SyntheticModel.SurfaceMesh[] extra)
        => SyntheticModel.BuildSurface([],
            [Sphere(material ?? $"/mt_c{race:D4}h{Id:D4}_hir_a.mtrl", Head + Offset(race), 0.1f,
                    bones ?? [("j_kao", 1f)], minY: -0.2f), .. extra]);

    /// <summary>The game's own files the builder reads: each race's face, and its own model of the hairstyle.</summary>
    public static Func<string, byte[]?> Game(IEnumerable<ushort> races, IDictionary<string, byte[]>? more = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var race in races)
        {
            files[HairPortPlan.HeadPaths(race).First()] = HeadModel(race);
            files[HairPortPlan.HairPath(race, Id)] = HairModel(race);
        }
        if (more != null) foreach (var (k, v) in more) files[k] = v;
        return path => files.TryGetValue(path, out var b) ? b : null;
    }

    /// <summary>A race's model of hairstyle <paramref name="id"/>: the same cap, its material named for that race and id.
    /// <paramref name="radius"/> changes the bytes without moving it off the head.</summary>
    public static byte[] HairModelOf(ushort race, int id, float radius = 0.1f)
        => SyntheticModel.BuildSurface([],
            [Sphere($"/mt_c{race:D4}h{id:D4}_hir_a.mtrl", Head + Offset(race), radius, [("j_kao", 1f)], minY: -0.2f)]);

    /// <summary>The game's own files for several hairstyles: each race's face, its model of each id, and the material
    /// that model names — which a port copies when the mod has none of its own.</summary>
    public static Func<string, byte[]?> GameOf(IEnumerable<ushort> races, IEnumerable<int> ids,
                                               IDictionary<string, byte[]>? more = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var race in races)
        {
            files[HairPortPlan.HeadPaths(race).First()] = HeadModel(race);
            foreach (int id in ids)
            {
                files[HairPortPlan.HairPath(race, id)] = HairModelOf(race, id);
                if (HairMaterialPath.GamePath(race, id, $"/mt_c{race:D4}h{id:D4}_hir_a.mtrl") is { } mtrl)
                    files[mtrl] = [1, 2, 3, (byte)(race / 100), (byte)id];
            }
        }
        if (more != null) foreach (var (k, v) in more) files[k] = v;
        return path => files.TryGetValue(path, out var b) ? b : null;
    }

    /// <summary>A throwaway mod folder: a manifest and the files it names.</summary>
    public sealed class Mod : IDisposable
    {
        public string Root { get; }

        /// <param name="groups">The manifest's Groups array, as JSON.</param>
        /// <param name="defaults">The default data's Files, game path to file.</param>
        public Mod(string groups = "[]", IDictionary<string, string>? defaults = null, int fileVersion = 4)
        {
            Root = Path.Combine(Path.GetTempPath(), "proteus_hairport_" + Path.GetRandomFileName());
            Directory.CreateDirectory(Root);
            var dd = JsonSerializer.Serialize(new { Files = defaults ?? new Dictionary<string, string>(),
                                                    FileSwaps = new Dictionary<string, string>(), Manipulations = Array.Empty<object>() });
            File.WriteAllText(Path.Combine(Root, "meta.json"),
                $"{{\"FileVersion\":{fileVersion},\"Name\":\"Hair\",\"Identifier\":\"id-1\",\"Author\":\"Someone\"," +
                $"\"Groups\":{groups},\"DefaultData\":{dd}}}");
        }

        public void Put(string rel, byte[] bytes)
        {
            var path = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public byte[] Get(string rel) => File.ReadAllBytes(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));

        public bool Has(string rel) => File.Exists(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));

        public string Manifest => File.ReadAllText(Path.Combine(Root, "meta.json"));

        public List<HairPortPlan.Owner> Owners => HairPortPlan.ReadOwners(Root)!;

        public HairPortPlan.Owner Owner(string group, string option)
            => Owners.First(o => o.Group == group && o.Option == option);

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    /// <summary>A single-select group of options, each publishing its files.</summary>
    public static string Group(string name, string type, params (string Option, Dictionary<string, string> Files)[] options)
        => JsonSerializer.Serialize(new
        {
            Name = name, Description = "", Priority = 0, Type = type, DefaultSettings = 0,
            Options = options.Select(o => new { Name = o.Option, Description = "", Files = o.Files,
                                                FileSwaps = new Dictionary<string, string>(),
                                                Manipulations = Array.Empty<object>() }),
        });
}
