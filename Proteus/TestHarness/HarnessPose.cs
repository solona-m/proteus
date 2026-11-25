using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Proteus.TestHarness;

/// <summary>The character's emote state as read on the framework thread: its mode (CharacterModes, by name) and the emote
/// its controller plays (0: none).</summary>
internal readonly record struct EmoteNow(string Mode, ushort EmoteId);

/// <summary>The active camera's orbit: horizontal and vertical angle (radians, the game's DirH/DirV) and distance (metres),
/// with the game's limits for the clamp.</summary>
internal readonly record struct CameraNow(float DirH, float DirV, float Distance, float MinDistance, float MaxDistance,
                                          float MinDirV, float MaxDirV);

/// <summary>An orbit the harness wrote or read back.</summary>
internal readonly record struct CameraPose(float DirH, float DirV, float Distance);

/// <summary>The emote the harness played this session: its row and allowlisted name, and what the settle read back.</summary>
internal sealed record EmoteRecord(ushort Id, string Name, string Mode, ushort ReadBackId, bool Settled, DateTime AtUtc);

/// <summary>The camera as the harness found it (once per session) and as it settled after the harness's last write.</summary>
internal sealed record CameraRecord(CameraPose Before, CameraPose Settled, DateTime AtUtc);

/// <summary>
/// The pose and camera routes' pure part: the emote allowlist and its pinned Emote-sheet rows, the label rule, the yaw
/// convention, the put-back decisions and the tolerances. No game access; every game read happens in a framework hop.
/// </summary>
internal static class HarnessPose
{
    /// <summary>The character modes, by CharacterModes name: standing, and an emote loop.</summary>
    public const string NormalMode = "Normal";
    public const string LoopMode = "EmoteLoop";

    /// <summary>The emotes the harness may play: looping poses only, untargeted. Row ids pinned and checked against the
    /// game's Emote sheet (their text commands) at harness start; a mismatch refuses every emote (emote-table-changed).</summary>
    public static readonly IReadOnlyDictionary<string, (ushort Id, string Command)> Allowlist = new Dictionary<string, (ushort, string)>(StringComparer.Ordinal)
    {
        ["sit"] = (50, "/sit"),
        ["groundsit"] = (52, "/groundsit"),
    };

    /// <summary>Put-back's camera tolerance: still where the harness left it (the game's interpolation settles inside it).</summary>
    public const float AngleTolerance = 0.02f;
    public const float DistanceTolerance = 0.05f;

    private static readonly Regex LabelRule = new("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant);

    /// <summary>A posedump label: lower-case letters, digits and hyphens, 1 to 32.</summary>
    public static bool ValidLabel(string? label) => label != null && LabelRule.IsMatch(label);

    /// <summary>Whether each allowlisted row answers to its text command (rows: id → every command, alias and short form
    /// the row has, '|'-joined; null when unreadable).</summary>
    public static bool RowsMatch(IReadOnlyDictionary<ushort, string>? rows)
    {
        if (rows == null) return false;
        foreach (var (_, (id, command)) in Allowlist)
            if (!rows.TryGetValue(id, out var c) || !c.Split('|').Contains(command, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>A row's command forms, lower-case, '|'-joined (empty ones dropped).</summary>
    public static string JoinCommands(params string?[] forms)
        => string.Join("|", forms.Where(f => !string.IsNullOrEmpty(f)).Select(f => f!.ToLowerInvariant()).Distinct());

    /// <summary>The camera's DirH for a yaw relative to the character's facing (degrees; 0 = the camera in front, looking
    /// at the face; 180 = behind). <paramref name="rotation"/> is the character's facing (radians). Wrapped to (-π, π].
    /// The convention is pinned by an in-game check (a yaw-180 screenshot shows the back).</summary>
    public static float DirHFor(float rotation, float yawDegrees) => Wrap(rotation + yawDegrees * MathF.PI / 180f);

    /// <summary>The yaw (degrees, (-180, 180]) a DirH stands at relative to the character's facing; the inverse of <see cref="DirHFor"/>.</summary>
    public static float YawOf(float rotation, float dirH) => Wrap(dirH - rotation) * 180f / MathF.PI;

    public static float Wrap(float a)
    {
        a %= 2f * MathF.PI;
        if (a <= -MathF.PI) a += 2f * MathF.PI;
        else if (a > MathF.PI) a -= 2f * MathF.PI;
        return a;
    }

    /// <summary>What to write for a camera request: each given field set (yaw relative to facing, pitch in degrees, distance
    /// in metres), each clamped to the game's limits; the names of the fields clamped.</summary>
    public static (CameraPose Pose, List<string> Clamped) Target(CameraNow now, float rotation, float? yaw, float? pitch, float? distance)
    {
        var clamped = new List<string>();
        float dirH = yaw is { } y ? DirHFor(rotation, y) : now.DirH;
        float dirV = now.DirV;
        if (pitch is { } p)
        {
            dirV = p * MathF.PI / 180f;
            float lo = MathF.Min(now.MinDirV, now.MaxDirV), hi = MathF.Max(now.MinDirV, now.MaxDirV);
            if (dirV < lo || dirV > hi) { dirV = Math.Clamp(dirV, lo, hi); clamped.Add("pitch"); }
        }
        float dist = now.Distance;
        if (distance is { } d)
        {
            dist = d;
            if (dist < now.MinDistance || dist > now.MaxDistance) { dist = Math.Clamp(dist, now.MinDistance, now.MaxDistance); clamped.Add("distance"); }
        }
        return (new CameraPose(dirH, dirV, dist), clamped);
    }

    /// <summary>Whether the camera still stands where the harness left it.</summary>
    public static bool StillLeft(CameraPose left, CameraNow now)
        => MathF.Abs(Wrap(now.DirH - left.DirH)) <= AngleTolerance && MathF.Abs(now.DirV - left.DirV) <= AngleTolerance
        && MathF.Abs(now.Distance - left.Distance) <= DistanceTolerance;

    /// <summary>Whether the camera is back where it was found.</summary>
    public static bool Restored(CameraPose before, CameraNow now) => StillLeft(before, now);

    /// <summary>Put-back for the emote: "send" when the character still plays the harness's emote in its loop (playing it
    /// again stands it up); else "changed" (the player, or the game, moved on: left alone).</summary>
    public static string EmoteDecision(EmoteRecord record, EmoteNow? now)
        => now is { } n && n.Mode == LoopMode && n.EmoteId == record.Id ? "send" : "changed";

    /// <summary>Whether a settled read shows the emote's loop.</summary>
    public static bool InLoop(EmoteNow now, ushort id) => now.Mode == LoopMode && now.EmoteId == id;

    /// <summary>Between modes: neither standing nor in the loop it was asked for (sitting down, standing up).</summary>
    public static bool InTransition(EmoteNow now, EmoteRecord? last)
        => now.Mode != NormalMode && !(last != null && InLoop(now, last.Id)) && now.Mode != LoopMode;
}
