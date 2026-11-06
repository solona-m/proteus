using System.Linq;
using Proteus.Services;
using System.Text.Json;
using System.Threading.Tasks;

namespace Proteus.TestHarness;

/// <summary>
/// The Studio routes, for QA of Move, Rotate and Scale in game: read the Studio, open a mod's model, set the controls,
/// run a drag (or undo). Bodies are checked off the framework thread; the gate, the session check and the panel call run
/// in one framework hop. Every write is undone at put-back (<see cref="HarnessChanges.PutBack"/>).
/// </summary>
internal sealed partial class HarnessRouter
{
    private async Task<HarnessResponse> StudioView()
    {
        var result = await OnFramework(() => Game.Studio is { } port
            ? ChangeResult.Ok(changes.WithSession(port.View()))
            : ChangeResult.Fail(503, "studio-unavailable", "the Studio is not available"));
        return StudioAnswer(result);
    }

    private async Task<HarnessResponse> StudioOpen(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "mod", "model", "show");
        if (bad != null) return bad;
        string? mod = OptionalString(body, "mod", out bad);
        if (bad != null) return bad;
        string? model = OptionalString(body, "model", out bad);
        if (bad != null) return bad;
        if (mod == null || model == null) return HarnessResponse.Error(400, "invalid-field", "mod and model are required");
        bool show = false;
        if (body.TryGetProperty("show", out var s))
        {
            if (s.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return HarnessResponse.Error(400, "invalid-field", "show must be true or false");
            show = s.GetBoolean();
        }
        if (!ModBackup.SafeModDir(mod)) return HarnessResponse.Error(400, "invalid-field", "the mod folder name is not a plain folder name");

        var result = await OnFramework(() =>
        {
            if (changes.Gate(jobs.Busy, Game.AutoRefit().Busy) is { } refused) return refused;
            var (dir, _, error) = FindMod(mod);
            if (error != null) return error;
            return changes.StudioOpen(dir!, model, show);
        });
        return StudioAnswer(result);
    }

    private async Task<HarnessResponse> StudioSet(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, HarnessStudio.SetFields);
        if (bad != null) return bad;
        var (set, error) = HarnessStudio.ParseSet(body);
        if (set == null) return HarnessResponse.Error(400, "invalid-field", error!);
        var result = await OnFramework(() => changes.Gate(jobs.Busy, Game.AutoRefit().Busy) ?? changes.StudioSet(set));
        return StudioAnswer(result);
    }

    private async Task<HarnessResponse> StudioDrag(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, HarnessStudio.DragFields);
        if (bad != null) return bad;
        var (drag, error) = HarnessStudio.ParseDrag(body);
        if (drag == null) return HarnessResponse.Error(400, "invalid-field", error!);
        var result = await OnFramework(() => changes.Gate(jobs.Busy, Game.AutoRefit().Busy) ?? changes.StudioDrag(drag));
        return StudioAnswer(result);
    }

    /// <summary>
    /// A Studio answer, scrubbed as <c>/state</c> is: the session's names (the Studio's status line is free text the
    /// plugin wrote) and every path's Windows user name.
    /// </summary>
    private HarnessResponse StudioAnswer(ChangeResult r)
    {
        string Clean(string text) => LogAnonymizer.Scrub(names.Scrub(text));
        if (r.Status != 200) return HarnessResponse.Error(r.Status, r.Code!, Clean(r.Message ?? ""));
        var json = System.Text.Encoding.UTF8.GetString(HarnessJson.Bytes(r.Body));
        return new HarnessResponse(200, "application/json; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(Clean(json)));
    }

    /// <summary>The put-back answer's Studio entries.</summary>
    internal static object StudioPutBackBlock(PutBackReport report)
        => report.Studio.Select(s => new { mod = s.Mod, files = s.Files, panel = s.Panel, code = s.Code, kept = s.Keep }).ToList();
}
