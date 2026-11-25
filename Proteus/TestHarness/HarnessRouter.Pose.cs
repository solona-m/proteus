using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace Proteus.TestHarness;

/// <summary>
/// The pose routes (knee standoff QA): an allowlisted looping emote, the camera's orbit, and a live pose dump. Every game
/// read or write runs in a framework hop; every settle is one hop to act, one wait off the framework thread, one hop to
/// read, with <see cref="HarnessChanges.PoseSettling"/> set across the wait (by the first hop) and cleared in a finally.
/// </summary>
internal sealed partial class HarnessRouter
{
    private async Task<HarnessResponse> EmoteAction(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "emote");
        if (bad != null) return bad;
        string? name = OptionalString(body, "emote", out bad);
        if (bad != null) return bad;
        if (name == null) return HarnessResponse.Error(400, "invalid-field", "emote is required");

        var (id, refused) = await OnFramework(() => changes.EmoteBegin(name, jobs.Timings.EmoteInterval));
        if (refused != null) return Answer(refused);
        try
        {
            await Task.Delay(jobs.Timings.EmoteSettle);
            if (changes.Stopping)
                return HarnessResponse.Error(503, "harness-stopping", "the test harness stopped while the emote settled (it was played)");
            return Answer(await OnFramework(() => changes.EmoteFinish(id, name)));
        }
        finally { changes.PoseSettling = false; }
    }

    private async Task<HarnessResponse> CameraAction(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "yaw", "pitch", "distance");
        if (bad != null) return bad;
        float? yaw = null, pitch = null, distance = null;
        foreach (var (field, set) in new (string, Action<float>)[] { ("yaw", v => yaw = v), ("pitch", v => pitch = v), ("distance", v => distance = v) })
        {
            if (!body.TryGetProperty(field, out var el)) continue;
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var d) || !double.IsFinite(d) || !float.IsFinite((float)d))
                return HarnessResponse.Error(400, "bad-value", $"{field} must be a finite number");
            set((float)d);
        }
        if (yaw == null && pitch == null && distance == null)
            return HarnessResponse.Error(400, "invalid-field", "give yaw, pitch, distance or a combination");

        var (pose, clamped, refused) = await OnFramework(() => changes.CameraBegin(yaw, pitch, distance));
        if (refused != null) return Answer(refused);
        try
        {
            await Task.Delay(jobs.Timings.CameraSettle);
            if (changes.Stopping)
                return HarnessResponse.Error(503, "harness-stopping", "the test harness stopped while the camera settled (it was moved)");
            return Answer(await OnFramework(() => changes.CameraFinish(pose, clamped)));
        }
        finally { changes.PoseSettling = false; }
    }

    private async Task<HarnessResponse> PoseDumpAction(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "label");
        if (bad != null) return bad;
        string? label = body.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        if (!HarnessPose.ValidLabel(label)) return HarnessResponse.Error(400, "bad-label", "label must be 1 to 32 of a-z, 0-9 and -");
        return Answer(await OnFramework(() => changes.PoseDump(label!, jobs.Timings.PoseDumpInterval)));
    }
}
