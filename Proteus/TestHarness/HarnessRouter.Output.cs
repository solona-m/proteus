using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>
/// The routes that let QA drive the shell output failure flows in game (issue sweep D, ADR 0002 2026-11-08 amendment):
/// hold Proteus's own output files or fail their next moves (#91's write retry and latch), make one published shell
/// material fail to load (the automatic redraw and the named message), and switch auto redraw for the session. Each is
/// in memory or read-only, undone by put-back, <c>off</c> and unload, and never touches a user's mod.
/// </summary>
internal sealed partial class HarnessRouter
{
    private static readonly string[] ShellFaultSlots = ["norm", "mask", "id", "base", "catc"];

    private HarnessResponse OutputLockAction(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "files", "ms", "failMoves", "release");
        if (bad != null) return bad;
        if (changes.Stopping) return HarnessResponse.Error(503, "harness-stopping", "the test harness is stopping");

        if (body.TryGetProperty("release", out var rel))
        {
            if (rel.ValueKind != JsonValueKind.True || body.EnumerateObject().Count() != 1)
                return HarnessResponse.Error(400, "invalid-field", "release takes {\"release\":true} alone");
            var (released, left, _) = changes.OutputLocks.ReleaseAll();
            return HarnessResponse.Json(200, new { released = released.Select(r => r.File).ToList(), movesLeft = left });
        }

        if (!body.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array
            || filesEl.GetArrayLength() is 0 or > HarnessOutputLocks.MaxFiles)
            return HarnessResponse.Error(400, "invalid-field", $"files is required: 1 to {HarnessOutputLocks.MaxFiles} patterns");
        var files = new List<string>();
        foreach (var f in filesEl.EnumerateArray())
        {
            var p = f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            if (HarnessOutputLocks.Validate(p) is { } why) return HarnessResponse.Error(400, "invalid-field", why);
            files.Add(p!);
        }

        bool hasMs = body.TryGetProperty("ms", out var msEl), hasFail = body.TryGetProperty("failMoves", out var failEl);
        if (hasMs == hasFail)
            return HarnessResponse.Error(400, "invalid-field", "give exactly one of ms (hold the files) or failMoves (fail their next moves)");

        try
        {
            if (hasMs)
            {
                if (msEl.ValueKind != JsonValueKind.Number || !msEl.TryGetInt32(out int ms) || ms is < 1 or > HarnessOutputLocks.MaxMs)
                    return HarnessResponse.Error(400, "invalid-field", $"ms must be 1 to {HarnessOutputLocks.MaxMs}");
                var locked = changes.OutputLocks.Lock(Game.ManagedOutputDir(), files, ms);
                if (locked is not { } l)
                    return HarnessResponse.Error(404, "no-match", "no existing output file matches those patterns");
                return HarnessResponse.Json(200, new
                {
                    mode = "lock", held = l.Held.Select(h => new { file = h.File, bytes = h.Bytes }).ToList(),
                    failMoves = (int?)null, untilUtc = l.UntilUtc,
                });
            }

            if (failEl.ValueKind != JsonValueKind.Number || !failEl.TryGetInt32(out int n) || n is < 1 or > HarnessOutputLocks.MaxFailMoves)
                return HarnessResponse.Error(400, "invalid-field", $"failMoves must be 1 to {HarnessOutputLocks.MaxFailMoves}");
            var until = changes.OutputLocks.ArmMoves(Game.ManagedOutputDir(), files, n);
            return HarnessResponse.Json(200, new { mode = "fail-moves", patterns = files, failMoves = n, untilUtc = until });
        }
        catch (InvalidOperationException)
        {
            return HarnessResponse.Error(409, "locks-held", "an output lock or move fault stands; send {\"release\":true} first");
        }
    }

    private async Task<HarnessResponse> ShellFaultAction(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "shell", "slot", "until", "clear");
        if (bad != null) return bad;
        if (changes.Stopping) return HarnessResponse.Error(503, "harness-stopping", "the test harness is stopping");

        if (body.TryGetProperty("clear", out var clear))
        {
            if (clear.ValueKind != JsonValueKind.True || body.EnumerateObject().Count() != 1)
                return HarnessResponse.Error(400, "invalid-field", "clear takes {\"clear\":true} alone");
            bool had = Game.ClearShellFault();
            changes.NoteShellFault(false);
            if (had) await OnFramework(() => { Game.Recomposite("test harness: shell fault cleared"); return true; });
            return HarnessResponse.Json(200, new { cleared = had, recomposite = had });
        }

        var shell = body.TryGetProperty("shell", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (shell == null || !(shell == "any" || System.Text.RegularExpressions.Regex.IsMatch(shell, "^ss_[0-9a-z]{1,2}$")))
            return HarnessResponse.Error(400, "invalid-field", "shell is required: \"any\" or a shell such as \"ss_1\"");
        string slot = "norm";
        if (body.TryGetProperty("slot", out var sl))
        {
            slot = sl.ValueKind == JsonValueKind.String ? sl.GetString()! : "";
            if (!ShellFaultSlots.Contains(slot, StringComparer.Ordinal))
                return HarnessResponse.Error(400, "invalid-field", "slot must be norm, mask, id, base or catc");
        }
        var until = body.TryGetProperty("until", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (until is not ("auto-redraw" or "put-back"))
            return HarnessResponse.Error(400, "invalid-field", "until is required: \"auto-redraw\" or \"put-back\"");

        if (!Game.ArmShellFault(shell, slot, until))
            return HarnessResponse.Error(409, "fault-armed", "a shell fault is armed; send {\"clear\":true} first");
        changes.NoteShellFault(true);
        return HarnessResponse.Json(200, new { armed = true, shell, slot, until });
    }

    private async Task<HarnessResponse> AutoRedrawAction(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "enabled");
        if (bad != null) return bad;
        if (!body.TryGetProperty("enabled", out var e) || e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return HarnessResponse.Error(400, "invalid-field", "enabled is required: true or false");
        bool enabled = e.GetBoolean();
        if (changes.Stopping) return HarnessResponse.Error(503, "harness-stopping", "the test harness is stopping");

        var answer = await OnFramework(() =>
        {
            var before = Game.AutoRedrawEnabled();
            Game.SetAutoRedrawOverride(enabled);
            var record = changes.NoteAutoRedrawOverride(enabled, before.Effective);
            var now = Game.AutoRedrawEnabled();
            // Switching it back on recomposites, as the Settings checkbox does: a layer left out while it was off returns.
            bool recomposite = now.Effective && !before.Effective;
            if (recomposite) Game.Recomposite("test harness: auto redraw back on");
            return new { config = now.Config, harnessOverride = now.Override, effective = now.Effective, before = record.BeforeEffective,
                         recomposite };
        });
        return HarnessResponse.Json(200, answer);
    }
}
