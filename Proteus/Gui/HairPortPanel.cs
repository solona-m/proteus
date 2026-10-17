using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;
using XivLiveMesh;

namespace Proteus.Gui;

/// <summary>
/// The Body size tool for a hairstyle: "Port to all races". Lists every other race of the hairstyle's sex and what the
/// mod has for it, and ports the ticked ones into the same options of the mod (<see cref="HairPortBuilder"/>,
/// <see cref="HairPortWriter"/>). The work runs on a worker; this starts it, shows how far it got, and reloads the mod.
/// </summary>
/// <param name="readGameFile">Reads a game path from the game's own data. Called from workers.</param>
internal sealed class HairPortPanel(Func<string, byte[]?> readGameFile, IPluginLog log)
{
    /// <summary>What the mod has of one race.</summary>
    public enum State { Open, Covered, Ported, Stale, NoStyle }

    public sealed record Row(ushort Race, State State);

    /// <summary>Whether the open model is a hairstyle, whose Body size tool is this panel instead of the garment refit.</summary>
    public static bool Shows(string? gamePath) => gamePath != null && HairPortPlan.Parse(gamePath) != null;

    /// <summary>
    /// One row per other race of the hairstyle's sex, in race order: made by the author, ported (current or out of
    /// date), one the game offers no such hairstyle to, or open to port.
    /// </summary>
    /// <param name="gameHas">Whether the game has its own model of this hairstyle for a race.</param>
    /// <param name="isStale">Whether an existing port is out of date.</param>
    public static List<Row> Rows(string sourceGamePath, IReadOnlyList<HairPortPlan.Owner> owners, HairPortRecord record,
                                 Func<ushort, bool> gameHas, Func<HairPortRecord.Entry, bool> isStale)
    {
        var rows = new List<Row>();
        if (HairPortPlan.Parse(sourceGamePath) is not { } at) return rows;
        var authored = HairPortPlan.AuthoredRaces(owners, at.Id).ToHashSet();
        foreach (var race in HairPortPlan.SameGenderRaces(at.Race))
        {
            string target = HairPortPlan.HairPath(race, at.Id);
            var ports = record.Ports.Where(e => e.TargetGamePath.Equals(target, StringComparison.OrdinalIgnoreCase)
                                                && e.SourceGamePath.Equals(sourceGamePath, StringComparison.OrdinalIgnoreCase))
                              .ToList();
            var state = authored.Contains(race) ? State.Covered
                      : ports.Count > 0 ? (ports.Any(isStale) ? State.Stale : State.Ported)
                      : !gameHas(race) ? State.NoStyle
                      : State.Open;
            rows.Add(new Row(race, state));
        }
        return rows;
    }

    /// <summary>The races ticked on opening: every one there is something to do for.</summary>
    public static IEnumerable<ushort> DefaultTicks(IEnumerable<Row> rows)
        => rows.Where(r => r.State is State.Open or State.Stale).Select(r => r.Race);

    private string? openKey;
    private Task<List<Row>>? reading;
    private List<Row>? rows;
    private readonly HashSet<ushort> ticked = [];
    private Task<(HairPortBuilder.Plan Plan, HairPortWriter.Outcome Outcome)>? running;
    private CancellationTokenSource? cancel;
    private volatile string progress = "";
    private string result = "";
    private bool resultFailed;
    private IReadOnlyDictionary<ushort, string> refused = new Dictionary<ushort, string>();
    private PbdFile? pbd;

    /// <param name="modRoot">The open mod's folder.</param>
    /// <param name="modDir">Its folder name, as Penumbra knows it.</param>
    /// <param name="sourceGamePath">The open hairstyle's game path.</param>
    /// <param name="afterChange">The mod's files changed: reload it in Penumbra and redraw. Framework thread.</param>
    public void Draw(string modRoot, string modDir, string sourceGamePath, Action<string> afterChange)
    {
        var s = Strings.HairPort;
        string key = $"{modRoot}|{sourceGamePath}";
        if (key != openKey)
        {
            openKey = key;
            rows = null;
            result = "";
            refused = new Dictionary<ushort, string>();
            Read(modRoot, sourceGamePath);
        }
        Consume(modDir, afterChange, modRoot, sourceGamePath);
        if (reading is { IsCompleted: true } r)
        {
            reading = null;
            rows = r.IsCompletedSuccessfully ? r.Result : [];
            ticked.Clear();
            ticked.UnionWith(DefaultTicks(rows));
        }

        ImGui.TextUnformatted(s.Header.Split("###")[0]);
        ImGui.TextWrapped(s.Intro);
        ImGui.Spacing();
        if (rows == null) { ImGui.TextDisabled(s.Reading); return; }

        bool busy = running != null;
        using (ImRaii.Disabled(busy))
            foreach (var row in rows)
            {
                bool can = row.State is State.Open or State.Stale;
                bool tick = ticked.Contains(row.Race);
                using (ImRaii.Disabled(!can))
                    if (ImGui.Checkbox($"{ModelRace.Describe($"{row.Race:D4}")}###hairPortRace{row.Race}", ref tick))
                    {
                        if (tick) ticked.Add(row.Race); else ticked.Remove(row.Race);
                    }
                ImGui.SameLine();
                ImGui.TextDisabled(row.State switch
                {
                    State.Covered => s.RaceCovered,
                    State.Ported => s.RacePorted,
                    State.Stale => s.RaceStale,
                    State.NoStyle => s.RaceNoStyle,
                    _ => s.RaceOpen,
                });
                if (refused.TryGetValue(row.Race, out var why))
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), $"    {why}");
            }

        ImGui.Spacing();
        if (busy)
        {
            ImGui.TextWrapped(progress);
            if (ImGui.Button(s.Cancel)) cancel?.Cancel();
            return;
        }

        using (ImRaii.Disabled(ticked.Count == 0))
            if (ImGui.Button(s.Port, new Vector2(-1, 0))) Start(modRoot, modDir, sourceGamePath, afterChange);

        if (rows.Any(row => row.State is State.Ported or State.Stale))
            if (ImGui.Button(s.Remove)) Remove(modRoot, modDir, sourceGamePath, afterChange);

        if (result.Length > 0)
        {
            if (resultFailed) ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), result);
            else ImGui.TextWrapped(result);
        }
    }

    private void Read(string modRoot, string sourceGamePath)
    {
        reading = Task.Run(() =>
        {
            var owners = HairPortPlan.ReadOwners(modRoot) ?? [];
            var record = HairPortRecord.Read(modRoot);
            var id = HairPortPlan.Parse(sourceGamePath)!.Value.Id;
            return Rows(sourceGamePath, owners, record, race => readGameFile(HairPortPlan.HairPath(race, id)) != null,
                        e => ReadMod(modRoot, e.SourceFile) is { } b && HairPortPlan.IsStale(e, b));
        });
    }

    private void Start(string modRoot, string modDir, string sourceGamePath, Action<string> afterChange)
    {
        var targets = ticked.ToList();
        cancel = new CancellationTokenSource();
        var ct = cancel.Token;
        progress = string.Format(Strings.HairPort.PortingFmt, 0, targets.Count);
        result = "";
        running = Task.Run(() =>
        {
            pbd ??= new PbdFile(readGameFile(LiveCharacter.PbdGamePath)!);
            var plan = HairPortBuilder.Build(modRoot, sourceGamePath, targets, pbd, readGameFile, withSharers: false, ct,
                                             (done, total) => progress = string.Format(Strings.HairPort.PortingFmt, done, total));
            // Nothing is written once cancelled: the bake is the slow part, the save is one step.
            ct.ThrowIfCancellationRequested();
            var outcome = HairPortWriter.Save(modRoot, plan.Writes, auto: false);
            foreach (var w in plan.Writes)
                log.Information("hair port: {0} → {1} in {2} / {3}", w.SourceGamePath, w.TargetGamePath, w.Into.Group, w.Into.Option);
            foreach (var (race, why) in plan.Refused) log.Information("hair port: c{0:D4} not ported — {1}", race, why);
            return (plan, outcome);
        }, ct);
    }

    private void Remove(string modRoot, string modDir, string sourceGamePath, Action<string> afterChange)
    {
        var outcome = HairPortWriter.Undo(modRoot, e => e.SourceGamePath.Equals(sourceGamePath, StringComparison.OrdinalIgnoreCase),
                                          decline: false);
        result = outcome.Ok ? string.Format(Strings.HairPort.RemovedFmt, outcome.Count) : outcome.Message;
        resultFailed = !outcome.Ok;
        if (outcome.Count > 0) afterChange(modDir);
        refused = new Dictionary<ushort, string>();
        Read(modRoot, sourceGamePath);
    }

    /// <summary>A finished run: say what it came to, reload the mod, and read the rows again.</summary>
    private void Consume(string modDir, Action<string> afterChange, string modRoot, string sourceGamePath)
    {
        if (running is not { IsCompleted: true } task) return;
        running = null;
        cancel?.Dispose();
        cancel = null;
        resultFailed = false;
        if (task.IsCanceled || task.Exception?.InnerException is OperationCanceledException)
        {
            result = Strings.HairPort.Cancelled;
            return;
        }
        if (task.IsFaulted)
        {
            log.Error(task.Exception!, "hair port: porting in the Studio failed");
            result = string.Format(Strings.HairPort.FailedFmt, task.Exception!.InnerException?.Message);
            resultFailed = true;
            return;
        }

        var (plan, outcome) = task.Result;
        refused = plan.Refused;
        if (!outcome.Ok)
        {
            result = string.Format(Strings.HairPort.FailedFmt, outcome.Message);
            resultFailed = true;
        }
        else result = outcome.Count > 0 ? string.Format(Strings.HairPort.DoneFmt, outcome.Count) : Strings.HairPort.NothingToDo;
        if (outcome.Count > 0) afterChange(modDir);
        Read(modRoot, sourceGamePath);
    }

    private static byte[]? ReadMod(string modRoot, string rel)
    {
        try { return File.ReadAllBytes(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception) { return null; }
    }
}
