using System;
using System.IO;
using System.Linq;

namespace Proteus.TestHarness;

/// <summary>What put-back sent for the pose routes: the emote (stand-sent / changed / none) and the camera (restore-sent /
/// changed / none), with what a later hop needs to confirm them.</summary>
internal sealed record PoseSent(string Emote, string Camera, EmoteRecord? StoodFrom, CameraRecord? RestoredFrom)
{
    public static readonly PoseSent Nothing = new("none", "none", null, null);
    public bool Any => Emote == "stand-sent" || Camera == "restore-sent";
}

/// <summary>
/// The pose routes' session record (knee standoff QA): the emote the harness played, the camera it moved, and their rate
/// limits. Framework thread only, except <see cref="PoseSettling"/>, which the router sets and clears from the request's
/// task around an off-thread wait.
/// </summary>
internal sealed partial class HarnessChanges
{
    private volatile bool poseSettling;
    private EmoteRecord? emote;
    private CameraRecord? camera;
    private DateTime lastEmoteUtc = DateTime.MinValue;
    private DateTime lastDumpUtc = DateTime.MinValue;
    private bool? emoteRowsMatch;

    /// <summary>
    /// An emote settle, a camera settle or a put-back's confirmation is between its hops: emote, camera, equip, hold,
    /// design apply and a second put-back answer 409 busy meanwhile, so nothing lands between the change and its read.
    /// Cleared in a finally, from whichever thread the request ends on.
    /// </summary>
    public bool PoseSettling { get => poseSettling; set => poseSettling = value; }

    internal EmoteRecord? EmoteRecordNow => emote;
    internal CameraRecord? CameraRecordNow => camera;

    private const string SettlingMessage = "a pose change is still settling; wait for its answer";

    /// <summary>
    /// The emote route's first hop: every refusal in the contract's order, then the emote played. Returns the row played,
    /// or the refusal.
    /// </summary>
    public (ushort Id, ChangeResult? Refused) EmoteBegin(string name, TimeSpan interval)
    {
        if (stopping) return (0, ChangeResult.Fail(503, "harness-stopping", "the test harness is stopping"));
        if (applySettling) return (0, ChangeResult.Fail(409, "busy", "a design apply is still settling; wait for its answer"));
        if (poseSettling) return (0, ChangeResult.Fail(409, "busy", SettlingMessage));
        if (!HarnessPose.Allowlist.TryGetValue(name, out var row))
            return (0, ChangeResult.Fail(400, "emote-not-allowed", $"emote must be one of {string.Join(", ", HarnessPose.Allowlist.Keys)}"));
        emoteRowsMatch ??= HarnessPose.RowsMatch(game.EmoteRows(HarnessPose.Allowlist.Values.Select(v => v.Id)));
        if (emoteRowsMatch != true)
            return (0, ChangeResult.Fail(409, "emote-table-changed", "the game's Emote sheet no longer has the pinned rows; the harness plays none"));
        if (game.EmoteState() is not { } now) return (0, ChangeResult.Fail(409, "no-player", "no local character"));
        if (game.EmoteBlocked() is { } blocked) return (0, ChangeResult.Fail(409, $"emote-blocked:{blocked}", $"an emote is not allowed now ({blocked})"));
        if (DateTime.UtcNow - lastEmoteUtc < interval || HarnessPose.InTransition(now, emote))
            return (0, ChangeResult.Fail(429, "emote-settling", "the last emote is still settling; wait a few seconds"));
        if (now.Mode == HarnessPose.LoopMode)
            return (0, ChangeResult.Fail(409, "emote-busy", "the character is already in an emote loop; put back (or stand up) first"));
        if (!game.CanUseEmote(row.Id)) return (0, ChangeResult.Fail(409, "emote-unavailable", $"the game does not allow {name} now"));
        if (!game.PlayEmote(row.Id)) return (0, ChangeResult.Fail(409, "emote-unavailable", "the emote agent is not available"));
        lastEmoteUtc = DateTime.UtcNow;
        poseSettling = true;   // set in this hop, so nothing lands before the router's wait; it clears it in a finally
        log?.Information("[Proteus] test harness: played emote {0}", name);
        return (row.Id, null);
    }

    /// <summary>The emote route's read hop: records what the settle read back and answers it.</summary>
    public ChangeResult EmoteFinish(ushort id, string name)
    {
        var now = game.EmoteState();
        bool settled = now is { } n && HarnessPose.InLoop(n, id);
        emote = new EmoteRecord(id, name, now?.Mode ?? "unknown", now?.EmoteId ?? 0, settled, DateTime.UtcNow);
        return ChangeResult.Ok(new { applied = true, emote = name, mode = emote.Mode, settled });
    }

    /// <summary>The camera route's first hop: refusals, the Before kept once per session, the clamped orbit written.</summary>
    public (CameraPose Pose, string[] Clamped, ChangeResult? Refused) CameraBegin(float? yaw, float? pitch, float? distance)
    {
        if (stopping) return (default, [], ChangeResult.Fail(503, "harness-stopping", "the test harness is stopping"));
        if (applySettling) return (default, [], ChangeResult.Fail(409, "busy", "a design apply is still settling; wait for its answer"));
        if (poseSettling) return (default, [], ChangeResult.Fail(409, "busy", SettlingMessage));
        if (game.Camera() is not { } now || game.PlayerRotation() is not { } rotation)
            return (default, [], ChangeResult.Fail(409, "camera-unavailable", "no active camera (GPose, a cutscene, or no character)"));
        var (pose, clamped) = HarnessPose.Target(now, rotation, yaw, pitch, distance);
        var before = camera?.Before ?? new CameraPose(now.DirH, now.DirV, now.Distance);
        if (!game.SetCamera(pose)) return (default, [], ChangeResult.Fail(409, "camera-unavailable", "the camera could not be written"));
        // Recorded at once with what was written, so a put-back racing the settle still has the Before.
        camera = new CameraRecord(before, pose, DateTime.UtcNow);
        poseSettling = true;   // as the emote: the router clears it after its read hop
        log?.Information("[Proteus] test harness: moved the camera");
        return (pose, clamped.ToArray(), null);
    }

    /// <summary>The camera route's read hop: the settled orbit is what the harness left.</summary>
    public ChangeResult CameraFinish(CameraPose applied, string[] clamped)
    {
        var now = game.Camera();
        float rotation = game.PlayerRotation() ?? 0f;
        if (now is { } n && camera is { } rec) camera = rec with { Settled = new CameraPose(n.DirH, n.DirV, n.Distance) };
        return ChangeResult.Ok(new
        {
            applied = Orbit(applied, rotation),
            settled = now is { } s ? Orbit(new CameraPose(s.DirH, s.DirV, s.Distance), rotation) : null,
            clamped,
        });
    }

    private static object Orbit(CameraPose p, float rotation)
        => new { yaw = HarnessPose.YawOf(rotation, p.DirH), pitch = p.DirV * 180f / MathF.PI, distance = p.Distance };

    /// <summary>The posedump route's hop: label and rate limit checked, the dump written under the session's folder.</summary>
    public ChangeResult PoseDump(string label, TimeSpan interval)
    {
        if (!HarnessPose.ValidLabel(label)) return ChangeResult.Fail(400, "bad-label", "label must be 1 to 32 of a-z, 0-9 and -");
        if (DateTime.UtcNow - lastDumpUtc < interval)
            return ChangeResult.Fail(429, "posedump-settling", "one dump every few seconds; wait");
        var relative = Path.Combine("posedumps", session, label);
        var dir = Path.Combine(HarnessDataRoot, relative);
        if (Directory.Exists(dir)) return ChangeResult.Fail(409, "exists", $"a dump labelled {label} exists this session");
        lastDumpUtc = DateTime.UtcNow;
        if (game.PoseDump(dir) is not { } counts) return ChangeResult.Fail(409, "no-player", "no local character or skeleton");
        return ChangeResult.Ok(new { folder = relative.Replace('\\', '/'), bones = counts.Bones, models = counts.Models });
    }

    /// <summary>The harness data folder (<c>…/test-harness</c>): the backups' parent.</summary>
    private string HarnessDataRoot
        => recordRoot != null && Path.GetDirectoryName(recordRoot) is { Length: > 0 } parent
            ? parent : Path.Combine(Path.GetTempPath(), "proteus-harness-records");

    /// <summary>
    /// Put-back's pose part, inside the single put-back call: the camera back to its Before while it still stands where
    /// the harness left it, and the emote played again (standing up) while the character still plays it. Sends only;
    /// a later hop confirms (HTTP), or nothing does (a stop on the framework thread). Records go with the send.
    /// </summary>
    internal PoseSent PutBackPose()
    {
        string cam = "none", emo = "none";
        CameraRecord? camFrom = null;
        EmoteRecord? emoFrom = null;
        if (camera is { } c)
        {
            try
            {
                if (game.Camera() is { } now && HarnessPose.StillLeft(c.Settled, now) && game.SetCamera(c.Before))
                {
                    cam = "restore-sent";
                    camFrom = c;
                }
                else cam = "changed";
            }
            catch (Exception ex)
            {
                log?.Warning(ex, "[Proteus] test harness: putting the camera back failed");
                cam = "changed";
            }
            camera = null;
        }
        if (emote is { } e)
        {
            try
            {
                if (HarnessPose.EmoteDecision(e, game.EmoteState()) == "send" && game.PlayEmote(e.Id))
                {
                    emo = "stand-sent";
                    emoFrom = e;
                }
                else emo = "changed";
            }
            catch (Exception ex)
            {
                log?.Warning(ex, "[Proteus] test harness: standing the character up failed");
                emo = "changed";
            }
            emote = null;
        }
        return new PoseSent(emo, cam, emoFrom, camFrom);
    }

    /// <summary>
    /// The HTTP put-back's confirming hop: stood (back to normal) or failed, restored (at its Before) or changed. A failed
    /// stand-up is recorded again, so the next put-back retries it (only failures stay recorded).
    /// </summary>
    internal (string Emote, string Camera) ConfirmPose(PoseSent sent)
    {
        string emo = sent.Emote, cam = sent.Camera;
        if (sent.StoodFrom is { } e)
        {
            var now = game.EmoteState();
            if (now is { Mode: HarnessPose.NormalMode }) emo = "stood";
            else
            {
                emo = "failed";
                emote ??= e;
            }
        }
        if (sent.RestoredFrom is { } c)
            cam = game.Camera() is { } now && HarnessPose.Restored(c.Before, now) ? "restored" : "changed";
        return (emo, cam);
    }

    /// <summary>The <c>/state</c> view of the pose: the character's emote and mode, and the camera's orbit.</summary>
    internal (object Pose, object? Camera) PoseView()
    {
        var now = game.EmoteState();
        var cam = game.Camera();
        float rotation = game.PlayerRotation() ?? 0f;
        string? name = now is { } n && n.EmoteId != 0
            ? HarnessPose.Allowlist.FirstOrDefault(kv => kv.Value.Id == n.EmoteId).Key ?? $"#{n.EmoteId}"
            : null;
        return (new { emote = name, mode = now?.Mode ?? "unknown" },
                cam is { } c ? Orbit(new CameraPose(c.DirH, c.DirV, c.Distance), rotation) : null);
    }

    private string[] PoseLeftovers()
        => [.. (emote is { } e ? new[] { $"emote:{e.Name}" } : []), .. (camera != null ? new[] { "camera" } : [])];

    private bool PoseEmpty => emote == null && camera == null;
}
