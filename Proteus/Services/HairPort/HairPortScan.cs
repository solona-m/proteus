using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CheapLoc;
using XivLiveMesh;

namespace Proteus.Services;

/// <summary>
/// A look through the player's hair mods for hairstyles made for another race only, fitting each to the player's race
/// before it is ever worn — the same decision and the same port as on equip (<see cref="HairPortWatcher.Decide.What"/>,
/// <see cref="HairPortBuilder.Build"/>, <see cref="HairPortWriter.Save"/>), for every hairstyle at once. What Penumbra
/// resolves for the player decides, so only mods switched on in the player's collection, with the options chosen there,
/// are ever looked at. A mod is reloaded once however many of its hairstyles were fitted, the player is redrawn only when
/// the hairstyle worn changed, and one chat line tells what was done.
/// </summary>
internal static class HairPortScan
{
    /// <summary>The highest hairstyle number looked for.</summary>
    public const int MaxId = 999;

    public enum RowState { Fitted, AlreadyFitted, Remade, Refused, Undone }

    /// <summary>What a check found for one hairstyle of the player's race.</summary>
    /// <param name="ModRoot">The mod the hairstyle is fitted from, or would be.</param>
    /// <param name="TargetGamePath">The player's race's model of the hairstyle.</param>
    /// <param name="Message">Why it was not fitted, for <see cref="RowState.Refused"/> and <see cref="RowState.Undone"/>.</param>
    public sealed record Row(int Id, string ModRoot, string ModDir, string ModName, string TargetGamePath, RowState State,
                             string Message = "");

    /// <param name="Examined">The hairstyles looked at: their earlier rows are replaced by <paramref name="Rows"/>. One
    /// skipped because nothing it resolves to changed keeps its row.</param>
    /// <param name="Written">Every game path a port was saved for, other races sharing a material included.</param>
    /// <param name="Failed">Penumbra could not be asked what anything resolves to: nothing was looked at, and the check
    /// is still owed.</param>
    public sealed record Result(IReadOnlyList<int> Examined, IReadOnlyList<Row> Rows, bool Cancelled,
                                IReadOnlyList<string> Written, bool Failed = false);

    /// <summary>
    /// What earlier checks of this session learned: what each hairstyle resolved to, so a check nobody asked for skips
    /// the ones where nothing changed; and why a port was refused, so the same model is not bent again to be refused
    /// again until it changes.
    /// </summary>
    public sealed class Memo
    {
        internal readonly ConcurrentDictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase);
        internal readonly ConcurrentDictionary<string, string> Refusals = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forget every hairstyle's resolution, so the next check looks at all of them.</summary>
        public void ForgetResolutions() => Keys.Clear();
    }

    /// <summary>Every hairstyle the player's race can choose: those the game has the race's own model of.</summary>
    public static List<int> IdsForFull(ushort race, Func<string, bool> gameHas)
        => Enumerable.Range(1, MaxId).Where(id => gameHas(HairPortPlan.HairPath(race, id))).ToList();

    /// <summary>The hairstyles a mod's author made for other races of <paramref name="race"/>'s sex — the ones a check
    /// of that mod looks at. Empty for an unreadable manifest.</summary>
    public static List<int> IdsInMod(IReadOnlyList<HairPortPlan.Owner>? owners, ushort race)
    {
        var ids = new SortedSet<int>();
        foreach (var owner in owners ?? [])
            foreach (var (path, rel) in owner.Files)
                if (!HairPortPlan.IsPortFile(rel) && HairPortPlan.Parse(path) is { } at
                    && at.Race != race && at.Race / 100 % 2 == race / 100 % 2)
                    ids.Add(at.Id);
        return [.. ids];
    }

    /// <summary>Every game path a decision about hairstyle <paramref name="id"/> for <paramref name="race"/> reads: the
    /// race's own, then each same-sex race it could be ported from, best first.</summary>
    public static IEnumerable<string> PathsFor(ushort race, int id, IReadOnlyList<ushort> candidates)
        => candidates.Select(c => HairPortPlan.HairPath(c, id)).Prepend(HairPortPlan.HairPath(race, id));

    /// <summary>
    /// Look at <paramref name="ids"/> for <paramref name="race"/>, the worn one first, and fit every one a mod made for
    /// another race only. Stops before the next hairstyle once <paramref name="ct"/> is cancelled — never in the middle
    /// of a save — and reloads every mod a port was saved in all the same.
    /// </summary>
    /// <param name="worn">The hairstyle the player wears, or null.</param>
    /// <param name="force">Look again at hairstyles whose resolution is unchanged since the last check.</param>
    /// <param name="label">What the check was, for the log: "full", or the mods.</param>
    /// <param name="progress">Called with (done, total) over the ports to make.</param>
    public static Result Run(HairPortWatcher.IHost host, ushort race, IReadOnlyList<int> ids, string? worn, Memo memo,
                             bool force, string label, CancellationToken ct, Action<int, int>? progress = null)
    {
        var modsRoot = host.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot) || !HairPortPlan.Playable(race)) return new Result([], [], false, []);
        var candidates = HairPortPlan.OrderSources(race, HairPortPlan.SameGenderRaces(race)).ToList();
        int? wornId = worn != null && HairPortPlan.Parse(worn) is { } w && w.Race == race ? w.Id : null;
        var order = ids.Distinct().OrderBy(id => id == wornId ? 0 : 1).ToList();

        var clock = Stopwatch.StartNew();
        var asked = order.SelectMany(id => PathsFor(race, id, candidates)).ToList();
        if (host.ResolveAll(asked) is not { } resolved || (asked.Count > 0 && resolved.Count == 0))
        {
            host.Log($"check of c{race:D4} ({label}): Penumbra could not resolve {asked.Count} path(s)");
            return new Result([], [], false, [], Failed: true);
        }
        long resolveMs = clock.ElapsedMilliseconds;

        var examined = new List<int>();
        var rows = new List<Row>();
        var todo = new List<(int Id, HairPortWatcher.Decide.Decision Decision, string Key)>();
        foreach (int id in order)
        {
            string key = string.Join("|", PathsFor(race, id, candidates).Select(p => resolved.GetValueOrDefault(p)));
            string at = $"{race}|{id}";
            if (!force && memo.Keys.TryGetValue(at, out var was) && was == key) continue;
            examined.Add(id);
            string target = HairPortPlan.HairPath(race, id);
            var d = HairPortWatcher.Decide.What(target, resolved, candidates, modsRoot!, HairPortRecord.Read, host.ReadGame);
            switch (d.Kind)
            {
                case HairPortWatcher.Decide.Kind.Port:
                    todo.Add((id, d, key));
                    continue;
                case HairPortWatcher.Decide.Kind.Ported:
                    rows.Add(MakeRow(host, id, d.ModRoot!, target, RowState.AlreadyFitted));
                    break;
                case HairPortWatcher.Decide.Kind.None when d.ModRoot != null:
                    rows.Add(MakeRow(host, id, d.ModRoot, target, RowState.Undone, d.Message));
                    break;
            }
            memo.Keys[at] = key;
        }

        PbdFile? pbd = todo.Count > 0 ? host.Pbd() : null;
        var written = new List<string>();
        bool cancelled = false;
        long bendMs = 0, reloadMs = 0;
        int done = 0;
        progress?.Invoke(0, todo.Count);
        foreach (var mod in todo.GroupBy(t => t.Decision.ModRoot!, StringComparer.OrdinalIgnoreCase))
        {
            string modRoot = mod.Key, modDir = Path.GetFileName(modRoot);
            bool saved = false;
            foreach (var (id, d, key) in mod)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                string target = HairPortPlan.HairPath(race, id);
                if (pbd == null)
                {
                    host.Log($"{target} not ported — {HairPortWatcher.NoPbd}");
                    rows.Add(MakeRow(host, id, modRoot, target, RowState.Refused, HairPortWatcher.NoPbd));
                    continue;
                }
                string refusal = RefusalKey(modRoot, d.Source!, resolved.GetValueOrDefault(d.Source!), race);
                if (memo.Refusals.TryGetValue(refusal, out var known))
                {
                    rows.Add(MakeRow(host, id, modRoot, target, RowState.Refused, known));
                    memo.Keys[$"{race}|{id}"] = key;
                    continue;
                }

                var t0 = clock.ElapsedMilliseconds;
                Ported outcome;
                try { outcome = PortOne(host, modRoot, d, race, pbd, ct); }
                catch (OperationCanceledException) { cancelled = true; break; }
                // One unreadable model refuses its own hairstyle, not the rest of the check.
                catch (Exception ex)
                {
                    host.Log($"{target} in {modDir} not ported — {ex.GetType().Name}: {ex.Message}");
                    outcome = new Ported([], false, ex.Message);
                }
                finally { bendMs += clock.ElapsedMilliseconds - t0; }

                if (outcome.Writes.Count > 0)
                {
                    saved = true;
                    written.AddRange(outcome.Writes.Select(x => x.TargetGamePath));
                    rows.Add(outcome.Stripped
                        ? MakeRow(host, id, modRoot, target, RowState.Refused, outcome.Message)
                        : MakeRow(host, id, modRoot, target, d.Stale ? RowState.Remade : RowState.Fitted));
                }
                else if (outcome.Current) rows.Add(MakeRow(host, id, modRoot, target, RowState.AlreadyFitted));
                else if (outcome.Message.Length == 0) { /* the author covers the race in a place the player has off */ }
                else
                {
                    rows.Add(MakeRow(host, id, modRoot, target, RowState.Refused, outcome.Message));
                    // The bend's own verdict holds until the model changes; a save that failed, an error or a file
                    // that could not be read may not happen next time — so the hairstyle is not counted seen either.
                    if (!outcome.Refusal) { progress?.Invoke(++done, todo.Count); continue; }
                    memo.Refusals[refusal] = outcome.Message;
                }
                memo.Keys[$"{race}|{id}"] = key;
                progress?.Invoke(++done, todo.Count);
            }
            // Whatever stopped the mod's hairstyles — a cancel included — those saved are loaded.
            if (saved)
            {
                var t0 = clock.ElapsedMilliseconds;
                host.Reload(modDir);
                reloadMs += clock.ElapsedMilliseconds - t0;
            }
            if (cancelled) break;
        }

        // Only the hairstyle on the player's head needs drawing again — and not once another went on meanwhile.
        if (worn != null && written.Contains(worn, StringComparer.OrdinalIgnoreCase)
            && (host.WornNow() is not { } now || string.Equals(now, worn, StringComparison.OrdinalIgnoreCase)))
            host.Redraw();

        var fitted = rows.Where(r => r.State is RowState.Fitted or RowState.Remade).ToList();
        if (fitted.Count > 0) host.AnnounceScan(SummaryLine(fitted));
        host.Log($"checked {examined.Count} hairstyle(s) for c{race:D4} ({label}){(cancelled ? ", cancelled" : "")}: "
               + $"fitted {rows.Count(r => r.State == RowState.Fitted)}, already {rows.Count(r => r.State == RowState.AlreadyFitted)}, "
               + $"remade {rows.Count(r => r.State == RowState.Remade)}, refused {rows.Count(r => r.State == RowState.Refused)}, "
               + $"undone {rows.Count(r => r.State == RowState.Undone)} — resolve {resolveMs} ms, bend {bendMs} ms, reload {reloadMs} ms");
        return new Result(examined, rows, cancelled, written);
    }

    /// <summary>What one port came to: the writes saved; or none, with the race already ported, or why not.</summary>
    /// <param name="Refusal">Why not is the port's own verdict on the model — it does not fit, needs bones the race lacks
    /// — which the same model will get again; not a save that failed, an error, or a file that could not be read.</param>
    /// <param name="Stripped">Not ported, but its old port, which could not be made again, was saved without the skeleton
    /// that changed another hairstyle (<see cref="HairPortWriter.PortWrite.Stripped"/>): <paramref name="Writes"/> are
    /// that, and <paramref name="Message"/> says why it was not ported.</param>
    public sealed record Ported(IReadOnlyList<HairPortWriter.PortWrite> Writes, bool Current, string Message,
                                bool Refusal = false, bool Stripped = false);

    /// <summary>
    /// Port <paramref name="decision"/>'s source onto <paramref name="race"/> — and every race that would read the same
    /// material — and save it into the mod, logging what was done and what refused. The one port step of both the
    /// equip path and a check, so the two cannot drift apart.
    /// </summary>
    public static Ported PortOne(HairPortWatcher.IHost host, string modRoot, HairPortWatcher.Decide.Decision decision,
                                 ushort race, PbdFile pbd, CancellationToken ct = default)
    {
        string modDir = Path.GetFileName(modRoot);
        var plan = HairPortBuilder.Build(modRoot, decision.Source!, [race], pbd, host.ReadGame, withSharers: true, ct);
        foreach (var (r, why) in plan.Refused) host.Log($"c{r:D4} not ported — {why}");
        if (plan.Writes.Count == 0)
            return new Ported([], plan.Current.Contains(race), plan.Refused.TryGetValue(race, out var w) ? w : "",
                              Refusal: !plan.Unreadable);

        var outcome = HairPortWriter.Save(modRoot, plan.Writes, auto: true);
        if (!outcome.Ok)
        {
            host.Log($"{modDir} was not saved — {outcome.Message}");
            return new Ported([], false, outcome.Message);
        }
        var saved = outcome.SavedWrites;
        // Every strip skipped — its port undone or remade since the plan: nothing changed.
        if (saved.Count == 0)
            return new Ported([], false, plan.Refused.TryGetValue(race, out var none) ? none : "");
        foreach (var x in saved)
            host.Log(x.Stripped
                ? $"{x.TargetGamePath} in {modDir}: {HairPortWriter.Describe(x.Into)} — its old port's skeleton, which another "
                  + "hairstyle of the race loads, taken out"
                : $"{x.SourceGamePath} → {x.TargetGamePath} in {modDir}: {HairPortWriter.Describe(x.Into)} "
                  + $"({x.Materials.Count} material/skeleton redirect(s), bent in {x.Millis} ms, skeleton entry {x.Est}, "
                  + $"carried from {x.SkeletonFrom})");
        if (saved.All(x => x.Stripped))
            return new Ported(saved, false, plan.Refused.TryGetValue(race, out var why) ? why : "", Stripped: true);
        return new Ported(saved, false, "");
    }

    /// <summary>
    /// The chat line after a check that fitted hairstyles: which numbers, from which mods (three at most, then how many
    /// more), and that wearing one is a matter of choosing that number.
    /// </summary>
    public static string SummaryLine(IReadOnlyList<Row> fitted)
    {
        var named = fitted.Take(3).Select(r => string.Format(Loc.Localize("HairPort.Scan.Entry.Fmt", "hairstyle {0} ({1})"),
                                                             r.Id, r.ModName));
        string list = string.Join(", ", named);
        if (fitted.Count > 3)
            list += string.Format(Loc.Localize("HairPort.Scan.More.Fmt", " and {0} more"), fitted.Count - 3);
        return string.Format(Loc.Localize("Chat.HairPortScan.Fmt",
            "[Proteus] Fitted to your race: {0}. To wear one, choose that hairstyle number for your character. Undo it, "
          + "or turn this off, under /proteus > Settings > Hair."), list);
    }

    private static Row MakeRow(HairPortWatcher.IHost host, int id, string modRoot, string target, RowState state,
                               string message = "")
    {
        string dir = Path.GetFileName(modRoot);
        return new Row(id, modRoot, dir, host.ModName(dir), target, state, message);
    }

    /// <summary>A refusal holds for the same model of the same mod onto the same race by the same bend.</summary>
    private static string RefusalKey(string modRoot, string sourceGamePath, string? sourceFile, ushort race)
    {
        string stamp = "";
        try
        {
            if (sourceFile != null && new FileInfo(sourceFile) is { Exists: true } f)
                stamp = $"{f.Length}:{f.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception) { /* unreadable: the bend will say so */ }
        return $"{modRoot}|{sourceGamePath}|{stamp}|{race}|{HairPortBaker.Version}";
    }
}
