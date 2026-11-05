using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>
/// The harness's routes. Thin: each one checks its input, runs its game-touching part through the framework dispatcher
/// (abandoned, never run late, if the framework does not start it in time), and shapes the answer off the framework
/// thread. Game reads live in <see cref="IHarnessGame"/>, job logic in <see cref="RefitJob"/>/<see cref="RestoreJob"/>,
/// file work in <see cref="ModBackup"/>.
/// </summary>
internal sealed class HarnessRouter(HarnessJobs jobs, LogRing ring, SessionNames names, CaptureGate capture,
                                    int build, string buildStamp, Dalamud.Plugin.Services.IPluginLog log)
{
    /// <summary>Every route, for the docs test and 405's Allow header.</summary>
    internal static readonly IReadOnlyList<(string Method, string Path)> Routes =
    [
        ("GET", "/health"), ("GET", "/state"), ("GET", "/log"), ("GET", "/screenshot"),
        ("POST", "/actions/refit"), ("POST", "/actions/restore"), ("POST", "/actions/shader-upgrade"), ("GET", "/jobs/{id}"),
    ];

    private IHarnessGame Game => jobs.Game;

    public async Task<HarnessResponse> Handle(HarnessRequest request)
    {
        try
        {
            string path = request.Path.TrimEnd('/');
            if (path.Length == 0) path = "/";
            string? routePath = path.StartsWith("/jobs/", StringComparison.Ordinal) && path.Length > 6 ? "/jobs/{id}" : path;
            var allowed = Routes.Where(r => r.Path == routePath).Select(r => r.Method).ToList();
            if (allowed.Count == 0) return HarnessResponse.Error(404, "not-found", $"no route {path}");
            if (!allowed.Contains(request.Method))
                return HarnessResponse.Error(405, "method-not-allowed", $"{routePath} takes {string.Join(", ", allowed)}",
                                             new Dictionary<string, string> { ["Allow"] = string.Join(", ", allowed) });

            return routePath switch
            {
                "/health" => await Health(),
                "/state" => await State(),
                "/log" => await Log(request),
                "/screenshot" => await Screenshot(request),
                "/actions/refit" => await Refit(request),
                "/actions/restore" => await Restore(request),
                "/actions/shader-upgrade" => await ShaderUpgradeAction(request),
                _ => Job(path["/jobs/".Length..]),
            };
        }
        catch (FrameworkTimeoutException)
        {
            return HarnessResponse.Error(503, "framework-timeout", "the game's framework did not run the request in time (a loading screen?); try again");
        }
        catch (Exception e)
        {
            log.Warning(e, "[Proteus] test harness: {0} {1} failed", request.Method, request.Path);
            return HarnessResponse.Error(500, "internal", e.GetType().Name);
        }
    }

    private Task<T> OnFramework<T>(Func<T> work) => jobs.Dispatcher.RunAsync(work, jobs.Timings.FrameworkStart);

    /// <summary>JSON with every path's Windows user name scrubbed.</summary>
    private static HarnessResponse Scrubbed<T>(int status, T value)
        => new(status, "application/json; charset=utf-8",
               Encoding.UTF8.GetBytes(LogAnonymizer.Scrub(Encoding.UTF8.GetString(HarnessJson.Bytes(value)))));

    private async Task<HarnessResponse> Health()
    {
        var h = await OnFramework(Game.Health);
        return HarnessResponse.Json(200, new
        {
            ok = true, harness = 1, session = jobs.Session, build, buildStamp, penumbra = h.Penumbra,
            loggedIn = h.LoggedIn, pluginEnabled = h.PluginEnabled,
        });
    }

    private async Task<HarnessResponse> State()
    {
        var s = await OnFramework(Game.State);
        var models = ModelJoin.Join(s.Loaded, s.ResourceMap, s.ModsRoot);
        var c = s.Compositor;
        var a = s.AutoRefit;
        return Scrubbed(200, new
        {
            session = jobs.Session,
            character = s.Character,
            collection = s.CollectionId is { } id ? new { id } : null,
            models,
            compositor = new
            {
                phase = CompositorPhase.Of(c), inFlight = c.InFlight, epoch = c.Epoch,
                pendingTriggers = c.PendingTriggers + c.Pending, pendingReasons = c.PendingReasons,
                lastResult = s.LastResult is { } r
                    ? new { success = r.Success, error = r.ErrorMessage is { } err ? names.Scrub(err) : null, texturesPatched = r.TexturesPatched,
                            overlayModsUsed = r.OverlayModsUsed, epoch = r.Epoch, at = r.Timestamp }
                    : null,
                lastRefresh = s.LastRefresh is { } t ? new { outcome = t.Outcome, totalMs = t.TotalMs, spans = t.Spans, at = t.AtUtc } : null,
            },
            redraws = new { count = Game.RedrawCount, lastAtUtc = Game.LastRedrawUtc },
            autoRefit = new
            {
                // Free text the plugin wrote: the session names come out as they do from /log.
                busy = a.Busy, failed = a.View.Failed, message = names.Scrub(a.View.Message),
                pending = new { running = a.Activity.Running, queued = a.Activity.Queued, walkDue = a.Activity.WalkDue,
                                undoing = a.Activity.Undoing },
            },
            shaderUpgrade = ShaderUpgradeBlock(Game.ShaderUpgrade()),
            job = jobs.Current?.View(),
        });
    }

    /// <summary>The gear shader upgrade in <c>/state</c>: read-only, scrubbed with the rest.</summary>
    internal static object ShaderUpgradeBlock(ShaderUpgradeWatcher.View v) => new
    {
        enabled = v.Enabled, busy = v.Busy, waiting = v.Waiting, awaitingRedraw = v.AwaitingRedraw, maskGloss = v.MaskGloss,
        upgraded = v.UpgradedList.Select(u => new
        {
            gamePath = u.GamePath, modDir = u.ModDir, file = u.File, form = u.Form,
            mask = u.MaskGamePath is { } mg ? new { gamePath = mg, file = u.MaskFile } : null,
        }),
        left = v.LeftList.Select(l => new { gamePath = l.GamePath, source = l.Source, reason = l.Reason }),
        masks = new { memoHits = v.MemoHits, decodes = v.Decodes },
        lastRun = v.LastRun is { } r
            ? new { kind = r.Kind, modsChanged = r.ModsChanged, filesChanged = r.FilesChanged, restored = r.Restored,
                    kept = r.Kept, refused = r.Refused, reloaded = r.Reloaded, redrawRequested = r.RedrawRequested,
                    atUtc = r.AtUtc }
            : null,
    };

    /// <summary>
    /// The gear shader upgrade, for QA (which cannot click Settings): switch it, choose this session's mask strategy, or
    /// start "Undo for what I'm wearing" (optionally without declining, so QA leaves nothing behind) or "Upgrade what I'm
    /// wearing now". Every field is validated before anything changes.
    /// </summary>
    private async Task<HarnessResponse> ShaderUpgradeAction(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "enabled", "maskGloss", "undoWorn", "decline", "upgradeWornNow");
        if (bad != null) return bad;
        bool? enabled = null;
        string? gloss = null;
        bool undoWorn = false, upgradeNow = false, decline = true;
        if (body.TryGetProperty("enabled", out var e))
        {
            if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return HarnessResponse.Error(400, "invalid-field", "enabled must be true or false");
            enabled = e.GetBoolean();
        }
        if (body.TryGetProperty("maskGloss", out var g))
        {
            if (g.ValueKind != JsonValueKind.String || ShaderUpgradeWatcher.ParseGloss(g.GetString()) == null)
                return HarnessResponse.Error(400, "invalid-field", "maskGloss must be one of inverse-specular, low, mid");
            gloss = g.GetString();
        }
        if (body.TryGetProperty("undoWorn", out var u))
        {
            if (u.ValueKind != JsonValueKind.True) return HarnessResponse.Error(400, "invalid-field", "undoWorn must be true");
            undoWorn = true;
        }
        if (body.TryGetProperty("upgradeWornNow", out var n))
        {
            if (n.ValueKind != JsonValueKind.True) return HarnessResponse.Error(400, "invalid-field", "upgradeWornNow must be true");
            upgradeNow = true;
        }
        if (body.TryGetProperty("decline", out var d))
        {
            if (!undoWorn) return HarnessResponse.Error(400, "invalid-field", "decline goes with undoWorn");
            if (d.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return HarnessResponse.Error(400, "invalid-field", "decline must be true or false");
            decline = d.GetBoolean();
        }
        if (undoWorn && upgradeNow) return HarnessResponse.Error(400, "invalid-field", "give undoWorn or upgradeWornNow, not both");
        if (enabled == null && gloss == null && !undoWorn && !upgradeNow)
            return HarnessResponse.Error(400, "invalid-field", "give enabled, maskGloss, undoWorn or upgradeWornNow");

        if (undoWorn || upgradeNow)
        {
            var started = await OnFramework(() =>
            {
                if (Game.AutoRefit().Busy) return false;
                if (enabled != null || gloss != null) Game.SetShaderUpgrade(enabled, gloss);
                return Game.StartShaderUpgradeRun(undoWorn, decline);
            });
            return started
                ? HarnessResponse.Json(202, new { started = true })
                : HarnessResponse.Error(409, "busy", "the shader upgrade or auto refit is working; wait for /state shaderUpgrade.busy == false");
        }
        var (on, now) = await OnFramework(() => Game.SetShaderUpgrade(enabled, gloss));
        return HarnessResponse.Json(200, new { enabled = on, maskGloss = now });
    }

    private async Task<HarnessResponse> Log(HarnessRequest request)
    {
        long? since = null;
        int max = LogRing.DefaultMax;
        if (request.Query.TryGetValue("since", out var sinceText))
        {
            if (!long.TryParse(sinceText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var s))
                return HarnessResponse.Error(400, "invalid-field", "since must be a non-negative integer");
            since = s;
        }
        if (request.Query.TryGetValue("max", out var maxText))
        {
            if (!int.TryParse(maxText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out max) || max < 1)
                return HarnessResponse.Error(400, "invalid-field", "max must be a positive integer");
        }
        var (collections, character) = await OnFramework(Game.NamesNow);
        names.AddAll(collections, character);
        var page = ring.Read(since, Math.Min(max, LogRing.MaxMax));
        return HarnessResponse.Json(200, new
        {
            session = jobs.Session, next = page.Next, oldest = page.Oldest, dropped = page.Dropped,
            lines = page.Lines.Select(l => new { seq = l.Seq, text = l.Text }),
        });
    }

    private async Task<HarnessResponse> Screenshot(HarnessRequest request)
    {
        bool ui = true;
        if (request.Query.TryGetValue("ui", out var uiText))
        {
            if (uiText == "true") ui = true;
            else if (uiText == "false") ui = false;
            else return HarnessResponse.Error(400, "invalid-field", "ui must be true or false");
        }
        uint viewport = await OnFramework(Game.ViewportId);
        try
        {
            var frame = await capture.Run(ct => Game.CaptureRaw(viewport, ui, ct));
            return new HarnessResponse(200, "image/png", FramePng.Encode(frame));
        }
        catch (CaptureBusyException) { return HarnessResponse.Error(429, "busy", "a screenshot is already being taken"); }
        catch (CaptureTimeoutException) { return HarnessResponse.Error(503, "capture-timeout", "the screenshot did not complete (is the game minimised?)"); }
        catch (BadFrameException e) { return HarnessResponse.Error(503, "capture-bad-frame", e.Message); }
        catch (Exception e) { return HarnessResponse.Error(503, "capture-failed", LogAnonymizer.Scrub($"{e.GetType().Name}: {e.Message}")); }
    }

    /// <summary>The body as a JSON object with only the allowed fields, or the error to answer with.</summary>
    private static (JsonElement Body, HarnessResponse? Error) JsonBody(HarnessRequest request, params string[] allowed)
    {
        var type = request.Header("Content-Type");
        if (type == null || !type.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            return (default, HarnessResponse.Error(415, "unsupported-media-type", "send Content-Type: application/json"));
        JsonElement body;
        try
        {
            using var doc = JsonDocument.Parse(request.Body, new JsonDocumentOptions { AllowDuplicateProperties = false });
            body = doc.RootElement.Clone();
        }
        catch (JsonException) { return (default, HarnessResponse.Error(400, "bad-json", "the body is not valid JSON or repeats a field")); }
        if (!StringsDecode(body)) return (default, HarnessResponse.Error(400, "bad-json", "the body has text that is not valid UTF-8"));
        if (body.ValueKind != JsonValueKind.Object) return (default, HarnessResponse.Error(400, "bad-json", "the body must be a JSON object"));
        foreach (var p in body.EnumerateObject())
            if (!allowed.Contains(p.Name, StringComparer.Ordinal))
                return (default, HarnessResponse.Error(400, "invalid-field", $"unknown field {p.Name}"));
        return (body, null);
    }

    /// <summary>Every string in the body decodes: invalid UTF-8 or a lone surrogate escape parses, then throws on read.</summary>
    private static bool StringsDecode(JsonElement e)
    {
        try
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String: _ = e.GetString(); return true;
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        _ = p.Name;
                        if (!StringsDecode(p.Value)) return false;
                    }
                    return true;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) if (!StringsDecode(item)) return false;
                    return true;
                default: return true;
            }
        }
        catch (InvalidOperationException) { return false; }
    }

    private static string? OptionalString(JsonElement body, string name, out HarnessResponse? error)
    {
        error = null;
        if (!body.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String || v.GetString() is not { Length: > 0 } s)
        {
            error = HarnessResponse.Error(400, "invalid-field", $"{name} must be a non-empty string");
            return null;
        }
        return s;
    }

    /// <summary>
    /// The body keys, as the size set's table names them, in the order asked: each through
    /// <see cref="SizeSetBatch.ParseKeys"/>, with <c>@women</c> allowed only on a men's body.
    /// </summary>
    internal static (List<string>? Keys, string? Error) ParseBodies(JsonElement bodies)
    {
        if (bodies.ValueKind != JsonValueKind.Array || bodies.GetArrayLength() == 0)
            return (null, "bodies must be a non-empty array of body keys");
        var keys = new List<string>();
        foreach (var item in bodies.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } raw)
                return (null, "every body must be a non-empty string");
            bool across = raw.EndsWith(SizeSetRefit.AcrossSuffix, StringComparison.OrdinalIgnoreCase);
            string baseKey = across ? raw[..^SizeSetRefit.AcrossSuffix.Length] : raw;
            if (baseKey.Equals("all", StringComparison.OrdinalIgnoreCase) || baseKey.Contains(','))
                return (null, $"'{raw}' is not one body key");
            var parsed = SizeSetBatch.ParseKeys(baseKey, out var error);
            if (parsed is not [var key]) return (null, error ?? $"'{raw}' is not one body key");
            if (across && !SupportedBodies.Choices.Any(c => c.Key == key && c.Male))
                return (null, $"'{raw}': {SizeSetRefit.AcrossSuffix} is only for a men's body");
            string full = across ? key + SizeSetRefit.AcrossSuffix : key;
            if (!keys.Contains(full)) keys.Add(full);
        }
        return (keys, null);
    }

    /// <summary>The framework hop both actions share: auto refit quiet, Penumbra there, the folder Penumbra's.</summary>
    private (string? ModDir, ModList? Mods, HarnessResponse? Error) CheckOnFramework(string mod)
    {
        if (Game.AutoRefit().Busy)
            return (null, null, HarnessResponse.Error(409, "auto-refit-busy", "auto refit is working; wait for /state autoRefit.busy == false"));
        if (Game.Mods() is not { } mods)
            return (null, null, HarnessResponse.Error(503, "penumbra-unavailable", "Penumbra is not available"));
        var dir = mods.Mods.Keys.FirstOrDefault(k => string.Equals(k, mod, StringComparison.OrdinalIgnoreCase));
        if (dir == null)
            return (null, null, HarnessResponse.Error(404, "mod-not-found", $"no Penumbra mod folder '{mod}' (folder names only, not display names)"));
        if (string.Equals(dir, Game.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return (null, null, HarnessResponse.Error(400, "managed-mod", "Proteus's own output mod is never refitted or restored"));
        if (!ModBackup.SafeModDir(dir))
            return (null, null, HarnessResponse.Error(400, "invalid-field", "the mod folder name is not a plain folder name"));
        return (dir, mods, null);
    }

    private async Task<HarnessResponse> Refit(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "mod", "bodies", "remake");
        if (bad != null) return bad;
        string? mod = OptionalString(body, "mod", out bad);
        if (bad != null) return bad;
        if (mod == null) return HarnessResponse.Error(400, "invalid-field", "mod is required");
        if (!body.TryGetProperty("bodies", out var bodies)) return HarnessResponse.Error(400, "invalid-field", "bodies is required");
        var (keys, keyError) = ParseBodies(bodies);
        if (keys == null) return HarnessResponse.Error(400, "invalid-field", keyError!);
        bool remake = false;
        if (body.TryGetProperty("remake", out var remakeValue))
        {
            if (remakeValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return HarnessResponse.Error(400, "invalid-field", "remake must be true or false");
            remake = remakeValue.GetBoolean();
        }

        if (!jobs.TryReserve()) return HarnessResponse.Error(409, "busy", "a job is already running");
        bool started = false;
        try
        {
            var (dir, mods, error) = await OnFramework(() => CheckOnFramework(mod));
            if (error != null) return error;
            var job = new HarnessJob(HarnessJobs.NewJobId(), "refit", dir!);
            var args = new RefitJob.Args(dir!, Path.Combine(mods!.ModsRoot, dir!), keys, remake, mods);
            _ = jobs.Start(job, j => RefitJob.Run(jobs, j, args));
            started = true;
            return HarnessResponse.Json(202, new { job = job.Id, modDir = dir, state = "queued" });
        }
        finally
        {
            if (!started) jobs.Release();
        }
    }

    private async Task<HarnessResponse> Restore(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "job", "mod", "backup");
        if (bad != null) return bad;
        string? jobId = OptionalString(body, "job", out bad);
        if (bad != null) return bad;
        string? mod = OptionalString(body, "mod", out bad);
        if (bad != null) return bad;
        string? backupId = OptionalString(body, "backup", out bad);
        if (bad != null) return bad;
        if (jobId != null && (mod != null || backupId != null))
            return HarnessResponse.Error(400, "invalid-field", "give job alone, or mod (with an optional backup)");
        if (jobId == null && mod == null) return HarnessResponse.Error(400, "invalid-field", "give job, or mod");
        if (mod != null && string.Equals(mod, Game.ManagedModDir, StringComparison.OrdinalIgnoreCase))
            return HarnessResponse.Error(400, "managed-mod", "Proteus's own output mod is never refitted or restored");

        if (!jobs.TryReserve()) return HarnessResponse.Error(409, "busy", "a job is already running");
        bool started = false;
        try
        {
            var selected = RestoreJob.Select(jobs, jobId, mod, backupId);
            if (selected.Error != null) return selected.Error;
            var selection = selected.Selection!;
            var (dir, mods, error) = await OnFramework(() => CheckOnFramework(selection.Backup.ModDir));
            if (error != null) return error;
            var job = new HarnessJob(HarnessJobs.NewJobId(), "restore", dir!);
            string modRoot = Path.Combine(mods!.ModsRoot, dir!);
            _ = jobs.Start(job, j => RestoreJob.Run(jobs, j, selection, modRoot));
            started = true;
            return HarnessResponse.Json(202, new { job = job.Id, modDir = dir, state = "queued" });
        }
        finally
        {
            if (!started) jobs.Release();
        }
    }

    private HarnessResponse Job(string id)
        => jobs.Find(id) is { } job ? Scrubbed(200, job.View()) : HarnessResponse.Error(404, "job-not-found", $"no job {id}");
}
