using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Proteus.Services;

namespace Proteus.TestHarness;

/// <summary>
/// The routes that let QA check Proteus's idle disk I/O in game (ADR 0002, 2026-11-07 amendment): re-arm the boot probe,
/// save a colour edit through Proteus's own sidecar save, and switch auto refit for the session. Bodies are checked off the
/// framework thread; game reads run in one framework hop; file work runs on the request's worker, under
/// <see cref="HarnessSidecar"/>'s lock, with no framework hop inside it.
/// </summary>
internal sealed partial class HarnessRouter
{
    private async Task<HarnessResponse> BootRearm(HarnessRequest request)
    {
        var (_, bad) = JsonBody(request);
        if (bad != null) return bad;
        if (changes.Stopping) return HarnessResponse.Error(503, "harness-stopping", "the test harness is stopping");
        var boot = await OnFramework(() =>
        {
            Game.RearmBoot();
            return Game.Boot();
        });
        return HarnessResponse.Json(200, new { rearmed = true, boot = BootBlock(boot) });
    }

    private static object BootBlock((bool Latched, int Step, long NextDueInMs) b)
        => new { latched = b.Latched, step = b.Step, nextDueInMs = b.NextDueInMs };

    /// <summary>The colour rows a request names: an option's, or the top level's with neither group nor option.</summary>
    internal static List<ColorTableRowPreset>? RowsOf(ProteusMetadata meta, string? group, string? option)
    {
        if (group == null) return meta.ColorTableRows;
        var g = meta.OptionGroups?.FirstOrDefault(x => string.Equals(x.PenumbraGroupName, group, StringComparison.OrdinalIgnoreCase));
        var o = g?.Options.FirstOrDefault(x => string.Equals(x.Name, option, StringComparison.OrdinalIgnoreCase));
        return o?.ColorTableRows;
    }

    private static string? DiffuseOf(ColorTableRowPreset row, bool subB) => (subB ? row.SubRowB : row.SubRowA)?.Diffuse;

    private async Task<HarnessResponse> SidecarEdit(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "mod", "group", "option", "row", "sub", "diffuse", "restoreMtime");
        if (bad != null) return bad;
        string? mod = OptionalString(body, "mod", out bad);
        if (bad != null) return bad;
        string? group = OptionalString(body, "group", out bad);
        if (bad != null) return bad;
        string? option = OptionalString(body, "option", out bad);
        if (bad != null) return bad;
        string? sub = OptionalString(body, "sub", out bad);
        if (bad != null) return bad;
        string? diffuse = OptionalString(body, "diffuse", out bad);
        if (bad != null) return bad;
        if (mod == null) return HarnessResponse.Error(400, "invalid-field", "mod is required");
        if (!ModBackup.SafeModDir(mod)) return HarnessResponse.Error(400, "invalid-field", "the mod folder name is not a plain folder name");
        if ((group == null) != (option == null)) return HarnessResponse.Error(400, "invalid-field", "group and option go together");
        if (!body.TryGetProperty("row", out var rowEl) || !WholeNumber(rowEl, 0, 1023, out var rowNo))
            return HarnessResponse.Error(400, "invalid-field", "row must be a whole number from 0 to 1023");
        if (sub is not (null or "A" or "B")) return HarnessResponse.Error(400, "invalid-field", "sub must be \"A\" or \"B\"");
        bool subB = sub == "B";
        if (diffuse == null || !HexColour.IsMatch(diffuse))
            return HarnessResponse.Error(400, "invalid-field", "diffuse must be a colour like \"#RRGGBB\"");
        bool restoreMtime = false;
        if (body.TryGetProperty("restoreMtime", out var rm))
        {
            if (rm.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return HarnessResponse.Error(400, "invalid-field", "restoreMtime must be true or false");
            restoreMtime = rm.GetBoolean();
        }

        // One hop: the gate, the folder, the entry as discovery sees it, the row. No file work.
        var found = await OnFramework(() =>
        {
            if (changes.Gate(jobs.Busy, Game.AutoRefit().Busy) is { } refused) return (null, null, refused);
            var (dir, _, error) = FindMod(mod);
            if (error != null) return (null, null, error);
            if (Game.DiscoverEntry(dir!) is not { } entry)
                return ((string?)null, (OverlayEntry?)null, ChangeResult.Fail(404, "not-a-proteus-mod", $"{dir} has no Proteus sidecar"));
            if (RowsOf(entry.Metadata, group, option)?.FirstOrDefault(r => r.Row == (int)rowNo) is null)
                return (null, null, ChangeResult.Fail(404, "row-not-found", $"{dir} has no colour row {rowNo} there"));
            return (dir, entry, (ChangeResult?)null);
        });
        if (found.Item3 is { } failed) return Answer(failed);
        string dir = found.Item1!;
        var entry = found.Item2!;
        var row = RowsOf(entry.Metadata, group, option)!.First(r => r.Row == (int)rowNo);
        string? before = DiffuseOf(row, subB);
        string metaPath = Path.Combine(entry.SidecarRoot, SidecarDiscoveryService.MetadataFile);

        // Under the sidecar lock: back up, save through Proteus's own save, record. No framework hop in here.
        SidecarStamp? stampBefore = null, stampAfter = null;
        bool mtimeRestored = false;
        var edit = changes.Sidecar.Edit(dir, metaPath, () => Game.HasStaged(dir), () =>
        {
            stampBefore = SidecarCache.StatFile(metaPath);
            var target = subB ? (row.SubRowB ??= new ColorTableSubRowPreset()) : (row.SubRowA ??= new ColorTableSubRowPreset());
            target.Diffuse = diffuse;
            if (!Game.SaveSidecar(entry)) return false;
            if (restoreMtime && stampBefore is { } sb)
            {
                File.SetLastWriteTimeUtc(metaPath, new DateTime(sb.LastWriteTicksUtc, DateTimeKind.Utc));
                mtimeRestored = true;
            }
            stampAfter = SidecarCache.StatFile(metaPath);
            return true;
        }, backup => backup!);
        if (edit.Status != 200) return Answer(edit);

        // After the lock: read it back through discovery (its cache), then recomposite.
        string? readBack = await OnFramework(() =>
        {
            var again = Game.DiscoverEntry(dir);
            var r = again is null ? null : RowsOf(again.Metadata, group, option)?.FirstOrDefault(x => x.Row == (int)rowNo);
            Game.Recomposite("test harness: sidecar edit");
            return r is null ? null : DiffuseOf(r, subB);
        });
        return Scrubbed(200, new
        {
            mode = "save", mod = dir, before, after = diffuse, readBack,
            stampBefore = StampJson(stampBefore), stampAfter = StampJson(stampAfter),
            sameLength = stampBefore is { } b0 && stampAfter is { } a0 && b0.Length == a0.Length,
            mtimeRestored, backup = edit.Body, recomposite = true,
        });
    }

    private static object? StampJson(SidecarStamp? s) => s is { } v ? new { length = v.Length, ticks = v.LastWriteTicksUtc } : null;

    private async Task<HarnessResponse> SidecarView(HarnessRequest request)
    {
        if (!request.Query.TryGetValue("mod", out var mod) || mod is not { Length: > 0 })
            return HarnessResponse.Error(400, "invalid-field", "mod is required");
        request.Query.TryGetValue("group", out var group);
        request.Query.TryGetValue("option", out var option);
        if (string.IsNullOrEmpty(group) != string.IsNullOrEmpty(option))
            return HarnessResponse.Error(400, "invalid-field", "group and option go together");
        int? rowFilter = null;
        if (request.Query.TryGetValue("row", out var rowText) && rowText is { Length: > 0 })
        {
            if (!int.TryParse(rowText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var r))
                return HarnessResponse.Error(400, "invalid-field", "row must be a whole number");
            rowFilter = r;
        }
        string? g = string.IsNullOrEmpty(group) ? null : group, o = string.IsNullOrEmpty(option) ? null : option;

        var result = await OnFramework(() =>
        {
            var (dir, _, error) = FindMod(mod);
            if (error != null) return ((object?)null, error);
            if (Game.DiscoverEntry(dir!) is not { } entry)
                return (null, ChangeResult.Fail(404, "not-a-proteus-mod", $"{dir} has no Proteus sidecar"));
            var rows = RowsOf(entry.Metadata, g, o);
            object view = new
            {
                mod = dir, found = rows != null,
                rows = (rows ?? []).Where(r => rowFilter == null || r.Row == rowFilter)
                                   .Select(r => new { row = r.Row, diffuseA = r.SubRowA?.Diffuse, diffuseB = r.SubRowB?.Diffuse }),
                staged = Game.HasStaged(dir!),
            };
            return (view, (ChangeResult?)null);
        });
        return result.Item2 is { } failed ? Answer(failed) : Scrubbed(200, result.Item1);
    }

    private async Task<HarnessResponse> AutoRefitAction(HarnessRequest request)
    {
        var (body, bad) = JsonBody(request, "enabled");
        if (bad != null) return bad;
        if (!body.TryGetProperty("enabled", out var e) || e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return HarnessResponse.Error(400, "invalid-field", "enabled is required: true or false");
        bool enabled = e.GetBoolean();
        if (changes.Stopping) return HarnessResponse.Error(503, "harness-stopping", "the test harness is stopping");
        if (jobs.Busy) return HarnessResponse.Error(409, "busy", "a harness job is running");

        var answer = await OnFramework(() =>
        {
            var before = Game.AutoRefitEnabled();
            Game.SetAutoRefitOverride(enabled);
            var record = changes.NoteAutoRefitOverride(enabled, before.Effective);
            var now = Game.AutoRefitEnabled();
            return new { config = now.Config, harnessOverride = now.Override, effective = now.Effective, before = record.BeforeEffective };
        });
        return HarnessResponse.Json(200, answer);
    }

    /// <summary>The <c>/state</c> blocks the idle disk I/O checks add.</summary>
    private sealed record QaStateView(object Boot, object? Collections, object AutoRefitEnabled, object BodyIndex, object? Io,
                                      object LeftoverSidecar);

    /// <summary>What <c>/state</c> adds for the idle disk I/O checks, read in the state's framework hop.</summary>
    private QaStateView QaState()
    {
        var bodyIndex = Game.BodyIndex();
        var enabled = Game.AutoRefitEnabled();
        var io = Game.Io();
        return new QaStateView(
            BootBlock(Game.Boot()),
            Game.Collections()?.Select(c => new { id = c.Id, player = c.Player }).ToList(),
            new { config = enabled.Config, harnessOverride = enabled.Override, effective = enabled.Effective },
            new { built = bodyIndex.Built, stale = bodyIndex.Stale, refreshing = bodyIndex.Refreshing, generation = bodyIndex.Generation },
            io is { } v ? new { windowAgeMs = v.WindowAgeMs, counts = v.Counts } : null,
            changes.LeftoverSidecars.Select(l => new { session = l.Session, mod = l.Mod, backup = l.Backup,
                                                       fileMatchesHarnessWrite = l.FileMatchesHarnessWrite }).ToList());
    }
}
