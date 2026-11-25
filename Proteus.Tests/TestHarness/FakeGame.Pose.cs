using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.TestHarness;

namespace Proteus.Tests.TestHarness;

/// <summary>The pose side of <see cref="FakeGame"/> (knee standoff QA): an emote state machine (playing a looping emote
/// sits; playing it again stands), conditions, the Emote sheet's rows, a camera, and a pose dump writer.</summary>
internal sealed partial class FakeGame
{
    /// <summary>The character's mode and emote; null: no character.</summary>
    public EmoteNow? EmoteValue { get; set; } = new(HarnessPose.NormalMode, 0);
    public string? Blocked { get; set; }
    public Dictionary<ushort, string>? Rows { get; set; } = new() { [50] = "/lounge|/sit", [52] = "/groundsit" };
    public HashSet<ushort> Unusable { get; } = [];
    public List<ushort> Played { get; } = [];
    /// <summary>When set, a played emote leaves the character in this mode (a transition) instead of the loop / normal.</summary>
    public string? PlayLeavesMode { get; set; }
    /// <summary>When true, playing the looping emote again does not stand the character up.</summary>
    public bool StandFails { get; set; }

    public CameraNow? CameraValue { get; set; } = new(0.5f, -0.2f, 5f, 1.5f, 20f, -1.4f, 0.8f);
    public List<CameraPose> CameraWrites { get; } = [];
    public float? Rotation { get; set; } = 1.0f;
    public List<string> Dumps { get; } = [];
    public bool DumpFails { get; set; }

    public EmoteNow? EmoteState()
    {
        Fw(nameof(EmoteState));
        return EmoteValue;
    }

    public string? EmoteBlocked()
    {
        Fw(nameof(EmoteBlocked));
        return Blocked;
    }

    public IReadOnlyDictionary<ushort, string>? EmoteRows(IEnumerable<ushort> ids)
    {
        Fw(nameof(EmoteRows));
        return Rows?.Where(kv => ids.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public bool CanUseEmote(ushort id)
    {
        Fw(nameof(CanUseEmote));
        return !Unusable.Contains(id);
    }

    public bool PlayEmote(ushort id)
    {
        Fw(nameof(PlayEmote));
        Played.Add(id);
        if (PlayLeavesMode is { } m) { EmoteValue = new EmoteNow(m, id); return true; }
        if (EmoteValue is { Mode: HarnessPose.LoopMode } now && now.EmoteId == id)
        {
            if (!StandFails) EmoteValue = new EmoteNow(HarnessPose.NormalMode, 0);
        }
        else EmoteValue = new EmoteNow(HarnessPose.LoopMode, id);
        return true;
    }

    public CameraNow? Camera()
    {
        Fw(nameof(Camera));
        return CameraValue;
    }

    public bool SetCamera(CameraPose pose)
    {
        Fw(nameof(SetCamera));
        CameraWrites.Add(pose);
        if (CameraValue is { } c) CameraValue = c with { DirH = pose.DirH, DirV = pose.DirV, Distance = pose.Distance };
        return CameraValue != null;
    }

    public float? PlayerRotation()
    {
        Fw(nameof(PlayerRotation));
        return Rotation;
    }

    public (int Bones, int Models)? PoseDump(string dir)
    {
        Fw(nameof(PoseDump));
        if (DumpFails) return null;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pose.json"), "{}");
        Dumps.Add(dir);
        return (120, 7);
    }
}
