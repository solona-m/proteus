using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Proteus.Services.Matcap;
using StbImageWriteSharp;

namespace Proteus.TestHarness;

/// <summary>
/// The Matcaps tool's routes (harness builds only, ADR 0002):
/// <list type="bullet">
/// <item><c>GET /matcaps[?refresh=1][&amp;mod=&amp;model=]</c>: images, libraries with their status, and for a model its parts,
/// siblings, apply group and existing matcaps.</item>
/// <item><c>POST /actions/matcap-image</c>: a generated sphere PNG in the global folder, deleted at put-back if unchanged.</item>
/// <item><c>POST /actions/matcap</c>: apply, change, undo or forget, as a backed-up job.</item>
/// </list>
/// </summary>
internal sealed partial class HarnessRouter
{
    private async Task<HarnessResponse> MatcapsView(HarnessRequest request)
    {
        bool refresh = request.Query.TryGetValue("refresh", out var r) && r == "1";
        request.Query.TryGetValue("mod", out var mod);
        request.Query.TryGetValue("model", out var model);
        if ((mod is { Length: > 0 }) != (model is { Length: > 0 }))
            return HarnessResponse.Error(400, "invalid-field", "give mod and model together, or neither");

        bool retry = request.Query.TryGetValue("retry", out var rt) && rt == "1";
        var port = await OnFramework(() => Game.Studio?.Matcaps());
        if (port == null) return HarnessResponse.Error(503, "studio-unavailable", "the Studio is not available");
        // Asking counts as first use, as showing the pane does; retry=1 also re-arms a finished or failed run (QA only).
        // Before the Penumbra check: Proteus's own matcaps need no Penumbra.
        port.Pack?.EnsureAsync(retryFailed: retry, evenIfReady: retry);
        var packView = port.Pack is { } pk ? PackView(pk) : null;
        if (port.ModsRoot == null || port.Mods == null)
            return Scrubbed(503, new { error = new { code = "penumbra-unavailable", message = "Penumbra is not available" }, pack = packView });
        if (refresh || port.Index?.Current == null)
            await Task.Run(() => port.Index!.Refresh(port.Mods, port.ModsRoot, force: refresh)).ConfigureAwait(false);
        var state = port.Index!.Current ?? MatcapIndex.State.Empty;
        var statuses = await OnFramework(() => state.Libraries.ToDictionary(l => l.Mod + "|" + l.Group, l => MatcapSelection.Read(port.Penumbra, l)));

        object? modelView = null;
        if (mod is { Length: > 0 } && model is { Length: > 0 })
        {
            var (dir, mods, error) = await OnFramework(() => FindModOnFramework(mod));
            if (error != null) return error;
            string root = Path.Combine(mods!.ModsRoot, dir!);
            if (Game.IsLegacyFolder(root)) return HarnessResponse.Error(409, "legacy-mod", "the mod is an old (v3) Penumbra mod");
            modelView = MatcapJob.ReadModel(root, model, port, out var jobError);
            if (jobError != null)
                return HarnessResponse.Error(jobError.Code == "model-ambiguous" ? 409 : 404, jobError.Code, jobError.Message);
        }

        return Scrubbed(200, new
        {
            scanned = port.Index.Current != null, scanning = port.Index.Scanning,
            images = MatcapImages.List(port.ImageFolder, null).Select(i => new { name = i.Name, path = i.Path, source = "global" })
                .Concat((port.Pack?.Available() ?? []).Select(i => new { name = i.Name, path = i.Path, source = "proteus" })),
            pack = packView,
            libraries = state.Libraries.Select(l =>
            {
                var s = statuses[l.Mod + "|" + l.Group];
                return new
                {
                    mod = l.Mod, name = l.Name, website = l.Website, group = l.Group, texture = l.Texture,
                    options = l.Options.Select(o => new { name = o.Name }), selected = s.CurrentOption, enabled = s.Enabled,
                    inherited = s.Inherited, heldBy = s.HeldBy, providers = state.ProvidersOf(l.Texture),
                };
            }),
            model = modelView,
        });
    }

    /// <summary>The download as the harness reports it.</summary>
    internal static object PackView(IMatcapPack pack)
    {
        var st = pack.Status;
        return new { state = st.State.ToString().ToLowerInvariant(), have = st.Have, total = st.Total, detail = st.Detail };
    }

    /// <summary>A plain test image name: starts "qa-", ends ".png", no folder.</summary>
    internal static bool SeedName(string? name)
        => name is { Length: > 7 } && name.StartsWith("qa-", StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
           && name.IndexOfAny(['/', '\\', ':']) < 0 && Path.GetFileName(name) == name && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>A 256×256 shaded sphere in <paramref name="rgb"/> on black: a matcap that reads as one.</summary>
    internal static byte[] SpherePng((byte R, byte G, byte B) rgb)
    {
        const int N = 256;
        var px = new byte[N * N * 4];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float nx = (x + 0.5f) / N * 2f - 1f, ny = 1f - (y + 0.5f) / N * 2f;
                float d = nx * nx + ny * ny;
                int at = (y * N + x) * 4;
                px[at + 3] = 255;
                if (d > 1f) continue;
                float nz = MathF.Sqrt(1f - d);
                float light = MathF.Max(0f, 0.3f * nx + 0.5f * ny + 0.8f * nz);
                float spec = MathF.Pow(MathF.Max(0f, nz * 0.9f + ny * 0.3f), 40f);
                px[at] = (byte)Math.Min(255f, rgb.R * light + 255f * spec);
                px[at + 1] = (byte)Math.Min(255f, rgb.G * light + 255f * spec);
                px[at + 2] = (byte)Math.Min(255f, rgb.B * light + 255f * spec);
            }
        using var ms = new MemoryStream();
        new ImageWriter().WritePng(px, N, N, ColorComponents.RedGreenBlueAlpha, ms);
        return ms.ToArray();
    }

    private async Task<HarnessResponse> MatcapImageAction(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "name", "color");
        if (bad != null) return bad;
        var name = OptionalString(body, "name", out bad);
        if (bad != null) return bad;
        if (!SeedName(name)) return HarnessResponse.Error(400, "invalid-field", "name must be a plain file name starting qa- and ending .png");
        var color = OptionalString(body, "color", out bad) ?? "#3080ff";
        if (bad != null) return bad;
        if (color.Length != 7 || color[0] != '#' || !int.TryParse(color[1..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
            return HarnessResponse.Error(400, "invalid-field", "color must be #RRGGBB");
        if (changes.Stopping) return HarnessResponse.Error(503, "harness-stopping", "the test harness is stopping");

        var folder = await OnFramework(() => Game.Studio?.Matcaps().ImageFolder);
        if (folder == null) return HarnessResponse.Error(503, "penumbra-unavailable", "the matcap folder is not available");
        var path = Path.Combine(folder, name!);
        if (File.Exists(path)) return HarnessResponse.Error(409, "exists", $"{name} is already in the matcap folder");
        var bytes = SpherePng(((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex));
        File.WriteAllBytes(path, bytes);
        await OnFramework(() => { changes.NoteSeededImage(path, bytes); return true; });
        return Scrubbed(200, new { name, path });
    }

    private async Task<HarnessResponse> MatcapAction(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "mod", "model", "parts", "image", "library", "group", "option", "confirmEnable",
                                   "entry", "undo", "forget", "pack");
        if (bad != null) return bad;
        string? mod = OptionalString(body, "mod", out bad); if (bad != null) return bad;
        string? model = OptionalString(body, "model", out bad); if (bad != null) return bad;
        string? image = OptionalString(body, "image", out bad); if (bad != null) return bad;
        string? library = OptionalString(body, "library", out bad); if (bad != null) return bad;
        string? group = OptionalString(body, "group", out bad); if (bad != null) return bad;
        string? option = OptionalString(body, "option", out bad); if (bad != null) return bad;
        string? entry = OptionalString(body, "entry", out bad); if (bad != null) return bad;
        if (mod == null || model == null) return HarnessResponse.Error(400, "invalid-field", "mod and model are required");
        bool Flag(string field) => body.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.True;
        foreach (var f in new[] { "confirmEnable", "undo", "forget", "pack" })
            if (body.TryGetProperty(f, out var v) && v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return HarnessResponse.Error(400, "invalid-field", $"{f} must be true or false");
        bool undo = Flag("undo"), forget = Flag("forget");
        // pack: true picks Proteus's own matcap of that name over a user image of the same name.
        if (body.TryGetProperty("pack", out _) && image == null)
            return HarnessResponse.Error(400, "invalid-field", "pack goes with image");

        var labels = new List<string>();
        if (body.TryGetProperty("parts", out var parts))
        {
            if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
                return HarnessResponse.Error(400, "invalid-field", "parts must be a non-empty array of part labels");
            foreach (var p in parts.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 } label)
                    return HarnessResponse.Error(400, "invalid-field", "every part must be a label like \"1.2\"");
                labels.Add(label);
            }
        }
        bool hasSource = image != null || library != null;
        if (image != null && library != null) return HarnessResponse.Error(400, "invalid-field", "give image or library, not both");
        if (library != null && (group == null || option == null))
            return HarnessResponse.Error(400, "invalid-field", "a library needs group and option");
        MatcapJob.Action kind;
        if (undo || forget)
        {
            if (undo && forget || hasSource || labels.Count > 0 || entry != null)
                return HarnessResponse.Error(400, "invalid-field", "undo and forget take mod and model only");
            kind = undo ? MatcapJob.Action.Undo : MatcapJob.Action.Forget;
        }
        else if (entry != null)
        {
            if (!hasSource || labels.Count > 0) return HarnessResponse.Error(400, "invalid-field", "a change takes entry and a source, no parts");
            kind = MatcapJob.Action.Change;
        }
        else
        {
            if (!hasSource || labels.Count == 0) return HarnessResponse.Error(400, "invalid-field", "an apply takes parts and an image or library");
            kind = MatcapJob.Action.Apply;
        }
        if (changes.Stopping) return HarnessResponse.Error(503, "harness-stopping", "the test harness is stopping");

        if (!jobs.TryReserve()) return HarnessResponse.Error(409, "busy", "a job is already running");
        bool started = false;
        try
        {
            var (dir, mods, error, port) = await OnFramework<(string?, ModList?, HarnessResponse?, MatcapHarnessPort?)>(() =>
            {
                var (d, m, e) = CheckOnFramework(mod);
                if (e != null) return (d, m, e, null);
                if (Game.Studio is not { } studio)
                    return (d, m, HarnessResponse.Error(503, "studio-unavailable", "the Studio is not available"), null);
                // Never flushed: the user's own unsaved edit is theirs to save.
                if (studio.EditPending())
                    return (d, m, HarnessResponse.Error(409, "studio-busy", "the Studio has an edit under way or not saved yet"), null);
                return (d, m, null, studio.Matcaps());
            });
            if (error != null) return error;
            string modRoot = Path.Combine(mods!.ModsRoot, dir!);
            var job = new HarnessJob(HarnessJobs.NewJobId(), kind switch
            {
                MatcapJob.Action.Undo => "matcap-undo", MatcapJob.Action.Forget => "matcap-forget",
                MatcapJob.Action.Change => "matcap-change", _ => "matcap",
            }, dir!);
            var args = new MatcapJob.Args(dir!, modRoot, model, kind, labels, image, library, group, option, Flag("confirmEnable"),
                                          entry, port!, changes, Pack: Flag("pack"));
            _ = jobs.Start(job, j => MatcapJob.Run(jobs, j, args));
            started = true;
            return HarnessResponse.Json(202, new { job = job.Id, modDir = dir, state = "queued" });
        }
        finally
        {
            if (!started) jobs.Release();
        }
    }
}
