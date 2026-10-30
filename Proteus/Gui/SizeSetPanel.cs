using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Proteus.Interop;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The Body size tool's "every size for other bodies": the author ticks bodies, and the open mod gets every size of
/// each — see <see cref="SizeSetRefit"/>. The same thing the mod packer's <c>--refit-to</c> does at pack time, for a mod
/// already installed.
/// <para/>
/// The work runs on a worker; this only starts it, shows how far it has got, and tells Penumbra once it is done.
/// </summary>
/// <param name="readGameFile">Reads a game path from the game's own data — the game's models a mod ships are not the
/// author's pieces (see <see cref="SizeSetRefit.PiecesOf"/>). Called from workers.</param>
internal sealed class SizeSetPanel(PenumbraBridge penumbra, UVRemapService uvRemap, Func<string, byte[]?> readGameFile,
                                   Configuration config, IPluginLog log)
{
    /// <summary>Bodies the ranking may keep cached between runs — as the automatic refit keeps them.</summary>
    private const int CachedBodiesKept = 400;

    /// <summary>
    /// The installed body mods, re-read (only where they changed) on opening and at the start of every run. The automatic
    /// refit's own once the plugin hands it over (<see cref="UseBodyIndex"/>): it is kept current as mods change, so a
    /// look here costs a directory listing per mod, where an index of its own parsed every manifest a second time and
    /// held a second copy of every body.
    /// </summary>
    private BodyModIndex index = new();

    /// <summary>Share an index kept elsewhere. Before any run starts.</summary>
    internal void UseBodyIndex(BodyModIndex shared) => index = shared;

    /// <summary>
    /// The choices whose bodies are installed, as the last look found them; null until one has come back. Looked for
    /// each time the section is opened — a directory listing per mod, too much for every frame — so a body installed
    /// with the section open shows once it is closed and opened again.
    /// </summary>
    private HashSet<string>? installed;
    private Task<(HashSet<string> Installed, List<(string Dir, string Name)> Bodies)>? scanning;
    private bool wasOpen;

    /// <summary>The installed body mods, as the last look found them: what "Made on" offers.</summary>
    private List<(string Dir, string Name)> bodyMods = [];
    private readonly SizeSetMadeOn madeOn = new();

    /// <summary>
    /// The open mod's pieces, and the mod they were read for; null until read. Only their races' bodies are offered,
    /// and the men's again "for men" when it has women's chest or legs pieces to cross (<see cref="SizeSetRefit.Offered"/>):
    /// ticked apart, so a tick on TBSE kept from a men's mod never writes men's versions into a women's one.
    /// </summary>
    private List<SizeSetRefit.Piece>? pieces;
    private string? piecesFor;
    private Task<List<SizeSetRefit.Piece>>? readingPieces;

    /// <summary>When the last look was started, and how soon one that came back with nothing is tried again.</summary>
    private long scanTriedAt;
    private const long ScanRetryMs = 3000;

    private bool remake;
    private Task<SizeSetRefit.Result>? running;
    private CancellationTokenSource? cancel;

    /// <summary>The mod being worked on, and what the worker last said it was doing.</summary>
    private string? runningFor;
    private volatile string progress = "";

    /// <summary>What the last run in this mod came to, shown until another mod is opened.</summary>
    private string? resultFor;
    private string result = "";
    private bool resultFailed;
    private IReadOnlyList<string> notes = [];
    private IReadOnlyList<string> warnings = [];

    /// <param name="modRoot">The open mod's folder.</param>
    /// <param name="modDir">Its folder name, as Penumbra knows it.</param>
    /// <param name="afterChange">The mod's files changed: reload it in Penumbra and redraw. Framework thread.</param>
    public void Draw(string modRoot, string modDir, Action<string> afterChange)
    {
        var s = Strings.SizeSet;
        Consume();

        bool open = ImGui.CollapsingHeader(s.Header);
        // On opening; and again while there is still no answer — Penumbra not ready yet, or a look that failed — so the
        // bodies are not left unchoosable until the section is closed and opened again.
        if (open && (!wasOpen || (installed == null && Environment.TickCount64 - scanTriedAt > ScanRetryMs))) Scan();
        wasOpen = open;
        if (!open) return;
        if (scanning is { IsCompleted: true } scan)
        {
            scanning = null;
            if (scan.IsCompletedSuccessfully)
            {
                (installed, bodyMods) = scan.Result;
                madeOn.Prune(bodyMods.Select(b => b.Dir));
            }
            else log.Warning(scan.Exception!, "[Proteus] size set: reading the body mods failed");
        }

        madeOn.Open(modDir);
        ReadPieces(modRoot, modDir);
        ImGui.TextWrapped(s.Intro);
        ImGui.Spacing();
        if (installed == null || pieces == null) ImGui.TextDisabled(Strings.AutoRefit.FindingBodies);
        else if (pieces.Count == 0) ImGui.TextDisabled(s.NoPieces);

        bool busy = running != null;
        using (ImRaii.Disabled(busy))
        {
            DrawMadeOn(modDir);
            bool shownAny = false, men = false, forMen = false;
            foreach (var (key, body, label, male, crosses, tip) in Offered())
            {
                // Men's bodies below a rule, when a unisex mod offers both; the men's versions of its women's pieces
                // below another.
                if ((male && !men) || (crosses && !forMen))
                {
                    men |= male;
                    forMen |= crosses;
                    if (shownAny) ImGui.Separator();
                }
                shownAny = true;
                // Not installed: shown, but not choosable. A tick from when it was installed is kept for when it is
                // again, and left out of the run meanwhile.
                bool missing = installed != null && !installed.Contains(body);
                bool ticked = !missing && config.SizeSetBodies.Contains(key);
                using (ImRaii.Disabled(missing || installed == null))
                {
                    if (ImGui.Checkbox(label + "###sizeSet_" + key, ref ticked))
                    {
                        if (ticked) config.SizeSetBodies.Add(key);
                        else config.SizeSetBodies.Remove(key);
                        config.Save();
                    }
                }
                if (missing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(string.Format(s.NotInstalledTipFmt, BodyLabel(body)));
                else if (tip && ImGui.IsItemHovered())
                    ImGui.SetTooltip(string.Format(s.ForMenTipFmt, BodyLabel(body), s.MensGroup));
            }

            ImGui.Spacing();
            bool yas = config.SizeSetYas;
            if (ImGui.Checkbox(s.Yas, ref yas))
            {
                config.SizeSetYas = yas;
                config.Save();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.YasTip);

            bool ends = config.SizeSetEnds;
            if (ImGui.Checkbox(s.Ends, ref ends))
            {
                config.SizeSetEnds = ends;
                config.Save();
                // The bodies offered come from the pieces, and a mod of shoes alone has none without them.
                piecesFor = null;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.EndsTip);

            bool piercings = config.SizeSetPiercings;
            if (ImGui.Checkbox(s.Piercings, ref piercings))
            {
                config.SizeSetPiercings = piercings;
                config.Save();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.PiercingsTip);
            ImGui.Checkbox(s.Remake, ref remake);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.RemakeTip);
        }

        ImGui.Spacing();
        // Only what is offered: a tick on TBSE kept from a men's mod does nothing in a women's one, whose men's
        // versions are ticked apart ("tbse@women").
        var keys = Offered().Where(c => config.SizeSetBodies.Contains(c.Key) && installed?.Contains(c.Body) == true)
                            .Select(c => c.Key)
                            .ToList();
        if (busy)
        {
            ImGui.TextWrapped(progress);
            if (ImGui.Button(s.Cancel)) cancel?.Cancel();
            return;
        }

        using (ImRaii.Disabled(keys.Count == 0))
        {
            if (ImGui.Button(s.Run, new System.Numerics.Vector2(-1, 0))) Start(modRoot, modDir, keys, afterChange);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(keys.Count == 0 ? s.PickBody : s.RunTip);

        if (resultFor != modDir || result.Length == 0) return;
        if (resultFailed) ImGui.TextColored(ProteusStyle.Bad, result);
        else ImGui.TextWrapped(result);
        if (notes.Count > 0)
        {
            ImGui.TextDisabled(s.LeftOut);
            foreach (string note in notes) ImGui.BulletText(note);
        }
        if (warnings.Count == 0) return;
        using (ImRaii.PushColor(ImGuiCol.Text, ProteusStyle.Warn))
        {
            ImGui.TextUnformatted(s.CheckInGame);
            foreach (string warning in warnings) ImGui.BulletText(warning);
        }
    }

    /// <summary>"Made on": find it automatically, or the body mod the author says the chest and legs were made on.</summary>
    private void DrawMadeOn(string modDir)
    {
        var s = Strings.SizeSet;
        var choices = SizeSetMadeOn.Choices(bodyMods, modDir);
        string shown = madeOn.Dir is { } dir ? choices.FirstOrDefault(c => c.Dir == dir).Name ?? dir : s.MadeOnAuto;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
        using (var combo = ImRaii.Combo(s.MadeOn, shown))
        {
            if (combo)
            {
                if (ImGui.Selectable(s.MadeOnAuto + "###sizeSetMadeOnAuto", madeOn.Dir == null)) madeOn.Choose(null);
                foreach (var (choice, name) in choices)
                    if (ImGui.Selectable(name + "###sizeSetMadeOn_" + choice, madeOn.Dir == choice)) madeOn.Choose(choice);
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.MadeOnTip);
    }

    /// <summary>A body as the table names it ("TBSE"), not as its row does ("TBSE (for men)").</summary>
    private static string BodyLabel(string body) => SupportedBodies.Choices.First(c => c.Key == body).Label;

    /// <summary>The bodies for the open mod's pieces; none until they are read.</summary>
    private IReadOnlyList<SizeSetRefit.Choice> Offered() => pieces == null ? [] : SizeSetRefit.Offered(pieces);

    /// <summary>Read the open mod's pieces on a worker, once per mod: its manifest, which can be large.</summary>
    private void ReadPieces(string modRoot, string modDir)
    {
        if (readingPieces is { IsCompleted: true } done)
        {
            readingPieces = null;
            if (done.IsCompletedSuccessfully) pieces = done.Result;
            else
            {
                log.Warning(done.Exception!, "[Proteus] size set: reading {0}'s pieces failed", piecesFor ?? "");
                pieces = [];
            }
        }
        if (piecesFor == modDir || readingPieces != null) return;
        piecesFor = modDir;
        pieces = null;
        bool ends = config.SizeSetEnds;
        readingPieces = Task.Run(() => SizeSetRefit.PiecesOf(modRoot, readGameFile, ends));
    }

    /// <summary>Find which bodies are installed, on a worker. Framework thread: Penumbra's mod list is asked for here.</summary>
    private void Scan()
    {
        if (scanning != null) return;
        scanTriedAt = Environment.TickCount64;
        if (penumbra.GetAllMods() is not { } mods || penumbra.GetModDirectory() is not { } modsRoot) return;
        scanning = Task.Run(() =>
        {
            var bodies = index.RefreshState(mods, modsRoot).List;
            return (SupportedBodies.Choices.Select(c => c.Key).Where(k => SupportedBodies.Installed(k, bodies))
                                   .ToHashSet(StringComparer.Ordinal),
                    bodies.Select(b => (b.Dir, b.Name)).ToList());
        });
    }

    /// <param name="afterChange">Told on the framework thread once the run ends, however it ends — see below.</param>
    private void Start(string modRoot, string modDir, List<string> keys, Action<string> afterChange)
    {
        // Penumbra's mod list is IPC: asked here, on the framework thread, and handed to the worker.
        if (penumbra.GetAllMods() is not { } mods || penumbra.GetModDirectory() is not { } modsRoot) return;

        // The game's own body, which a shoe or glove may have been made on, read here: the game's data is read on this
        // thread, and a read that failed on the worker would leave the automatic refit without the game's body too.
        var gameBodies = SizeSetRefit.GameBodies(keys, readGameFile, config.SizeSetEnds);
        // A women's mod's men's versions are made for the men's skeleton the player's collection gives (IVCS when
        // installed), asked for here — IPC — once; the game's own when Penumbra gives nothing.
        SizeSetRefit.AcrossInputs? across = null;
        if (SizeSetRefit.MensKeys(keys).Count > 0)
        {
            var skeletons = new[] { OtherRaceWearer.BaseSkeletonPath(CrossSexRefit.Men),
                                    OtherRaceWearer.BaseSkeletonPath(CrossSexRefit.Women) };
            var resolved = penumbra.ResolvePlayerPaths(skeletons);
            if (resolved == null) log.Information("[Proteus] size set: the collection's skeletons could not be asked for; the game's are used");
            across = new SizeSetRefit.AcrossInputs(path => resolved?.GetValueOrDefault(path));
        }
        var request = new SizeSetRefit.Request(modRoot, keys, config.SizeSetYas, remake, [], readGameFile,
                                               config.SizeSetEnds, race => gameBodies.GetValueOrDefault(race),
                                               config.SizeSetPiercings, across, madeOn.Dir);
        cancel = new CancellationTokenSource();
        var ct = cancel.Token;
        runningFor = modDir;
        progress = Strings.AutoRefit.FindingBodies;
        resultFor = null;
        running = Task.Run(() =>
        {
            // A run reads hundreds of megabytes of models: a blocking full collection partway would stop the game too.
            var latency = System.Runtime.GCSettings.LatencyMode;
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            try
            {
                var bodies = index.RefreshState(mods, modsRoot).List;
                return SizeSetRefit.Run(request with { Bodies = bodies }, uvRemap, log, p => progress = p, ct);
            }
            finally
            {
                System.Runtime.GCSettings.LatencyMode = latency;
                BodySizeMatch.ForgetCandidatesBeyond(CachedBodiesKept);
                Reload(modDir, afterChange);
            }
        }, ct);
    }

    /// <summary>
    /// Tell Penumbra the mod changed, from the worker as the run ends — stopped, failed or done. Not left to the panel:
    /// it is only drawn while the Body size tool is open on a model, so a run that finished with the Studio closed or
    /// another tool chosen left Penumbra on the old manifest, and one that threw after its first save never told it at all.
    /// Reloading a mod nothing was saved into is harmless.
    /// </summary>
    private void Reload(string modDir, Action<string> afterChange)
    {
        try
        {
            _ = Plugin.Framework.RunOnFrameworkThread(() =>
            {
                try { afterChange(modDir); }
                catch (Exception e) { log.Warning(e, "[Proteus] size set: could not reload {0}", modDir); }
            });
        }
        catch (Exception e)
        {
            // The plugin unloading under a run: nothing left to tell.
            log.Warning(e, "[Proteus] size set: could not reach the framework to reload {0}", modDir);
        }
    }

    /// <summary>A finished run: keep what it came to for the panel. Penumbra was told by the run itself.</summary>
    private void Consume()
    {
        if (running is not { IsCompleted: true } task) return;
        running = null;
        cancel?.Dispose();
        cancel = null;
        string dir = runningFor!;
        resultFor = dir;
        notes = [];
        warnings = [];
        resultFailed = false;

        if (task.IsCanceled || task.Exception?.InnerException is OperationCanceledException)
        {
            result = Strings.SizeSet.Cancelled;
            return;
        }
        if (task.IsFaulted)
        {
            log.Error(task.Exception!, "[Proteus] size set failed");
            result = string.Format(Strings.SizeSet.FailedFmt, task.Exception!.InnerException?.Message);
            resultFailed = true;
            return;
        }

        var r = task.Result;
        notes = r.Notes;
        warnings = r.Warnings ?? [];
        result = SizeSetRefit.ResultLine(r);
        log.Information("[Proteus] size set: {0} sizes added to {1}; {2} left out", r.Made, dir, r.Notes.Count);
    }
}
