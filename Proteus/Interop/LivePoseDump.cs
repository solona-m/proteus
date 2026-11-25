using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using XivLiveMesh;

namespace Proteus.Interop;

/// <summary>
/// <c>/proteus posedump [dir]</c>: the local player's skeleton as it stands this frame, the racial deformer and every
/// model the character draws, written to a folder — so a refit can be posed exactly as the character is standing and
/// looked at offline, rather than only in its bind pose. Main thread only.
/// </summary>
public static unsafe class LivePoseDump
{
    /// <summary>One bone: its name, its parent's (null for a root), and its bind and current model-space transforms
    /// as 16 floats, row-major.</summary>
    public sealed record Bone(string Name, string? Parent, float[] Bind, float[] Pose);

    /// <summary>What <c>pose.json</c> holds.</summary>
    public sealed record Snapshot(ushort GenderRace, float[] Root, List<Bone> Bones, List<string> Models);

    public static string Dump(IObjectTable objects, PenumbraBridge penumbra, IDataManager data, IPluginLog log, string dir,
                              bool logFolder = true)
        => DumpCounted(objects, penumbra, data, log, dir, logFolder).Line;

    /// <summary>What <see cref="DumpCounted"/> did: its log line, and the bones and models written (none when it wrote
    /// nothing: no player, no skeleton).</summary>
    public readonly record struct DumpResult(string Line, int Bones, int Models, bool Written);

    /// <summary>
    /// <see cref="Dump"/>, answering the counts. <paramref name="logFolder"/> false keeps the absolute folder (it carries the
    /// Windows user name) out of the log line: the test harness's <c>/log</c> returns plugin log lines.
    /// </summary>
    public static DumpResult DumpCounted(IObjectTable objects, PenumbraBridge penumbra, IDataManager data, IPluginLog log, string dir,
                                         bool logFolder)
    {
        var cb = LiveCharacter.Player(objects);
        if (cb == null) return new DumpResult("posedump: no local player", 0, 0, false);
        var pose = new LivePose();
        if (!pose.Read(cb)) return new DumpResult("posedump: could not read the skeleton", 0, 0, false);

        Directory.CreateDirectory(dir);
        var bones = new List<Bone>(pose.BoneCount);
        for (int b = 0; b < pose.BoneCount; b++)
        {
            var name = pose.BoneName(b);
            bones.Add(new Bone(name, pose.ParentOf(name), Floats(pose.Bind(b)), Floats(pose.Pose(b))));
        }

        // Every model drawn, as the renderer loaded it: the bytes are what is on screen, whatever the files say now.
        var models = new List<string>();
        int i = -1;
        foreach (var modelPtr in cb->ModelsSpan)
        {
            i++;
            var model = modelPtr.Value;
            if (model == null || model->ModelResourceHandle == null) continue;
            var name = model->ModelResourceHandle->FileName.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            var bytes = LiveCharacter.ModelBytes(model->ModelResourceHandle, name);
            var gamePath = LiveCharacter.GamePathOf(penumbra, name) ?? "";
            var file = $"slot{i:D2}.mdl";
            if (bytes != null) File.WriteAllBytes(Path.Combine(dir, file), bytes);
            models.Add($"{file}\t{gamePath}\t{LiveCharacter.FilePath(name)}");
        }

        var snapshot = new Snapshot(pose.GenderRace, Floats(pose.Root), bones, models);
        File.WriteAllText(Path.Combine(dir, "pose.json"), JsonSerializer.Serialize(snapshot));

        var resolved = penumbra.ResolvePlayer(LiveCharacter.PbdGamePath);
        var pbd = resolved != null && Path.IsPathRooted(resolved) && File.Exists(resolved)
            ? File.ReadAllBytes(resolved)
            : data.GetFile(LiveCharacter.PbdGamePath)?.Data;
        if (pbd != null) File.WriteAllBytes(Path.Combine(dir, "human.pbd"), pbd);

        var line = Line(bones.Count, pose.GenderRace, models.Count, dir, logFolder);
        log.Information("[Proteus] {0}", line);
        return new DumpResult(line, bones.Count, models.Count, true);
    }

    /// <summary>The dump's log line; without <paramref name="logFolder"/> the folder is named, never written out.</summary>
    internal static string Line(int bones, ushort genderRace, int models, string dir, bool logFolder)
        => $"posedump: {bones} bones, race c{genderRace:D4}, {models} models -> {(logFolder ? dir : "harness posedump folder")}";

    /// <summary>A snapshot read back into a pose, for posing a mesh offline.</summary>
    public static LivePose Load(Snapshot s)
    {
        var index = s.Bones.Select((b, n) => (b.Name, n)).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().n);
        var pose = new LivePose();
        // Duplicate names (a partial skeleton repeating the body's) are dropped: LivePose needs unique names, and
        // the first of each is the body's, the one gear is weighted to.
        var keep = s.Bones.Select((b, n) => (b, n)).Where(x => index[x.b.Name] == x.n && x.b.Name.Length > 0).ToList();
        var flat = keep.Select((x, k) => (x.b.Name, k)).ToDictionary(x => x.Name, x => x.k);
        pose.Set(keep.Select(x => x.b.Name).ToList(),
                 keep.Select(x => x.b.Parent != null && flat.TryGetValue(x.b.Parent, out var p) ? p : -1).ToList(),
                 keep.Select(x => Matrix(x.b.Bind)).ToList(),
                 keep.Select(x => Matrix(x.b.Pose)).ToList(),
                 Matrix(s.Root), s.GenderRace);
        return pose;
    }

    private static float[] Floats(in Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    private static Matrix4x4 Matrix(float[] f) =>
        new(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
}
