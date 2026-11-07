using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Services;
using Proteus.Services.Matcap;

namespace Proteus.TestHarness;

/// <summary>
/// The Studio's Matcaps tool for QA: what it lists (<see cref="Read"/>), and Apply (new, or a change of parts that have a
/// matcap), Undo and Forget as a job
/// (<see cref="Run"/>) through the same entry points the panel's buttons call (<see cref="MatcapStudio"/>,
/// <see cref="MatcapWriter"/>, <see cref="MatcapSelection"/>), after a whole-mod backup. A library setting the job changes is
/// recorded in the binding session's ledger (Before written first) and put back at put-back. A Studio edit pending is
/// refused (studio-busy) in the request's framework hop, never flushed.
/// </summary>
internal static class MatcapJob
{
    /// <summary>Apply is the panel's one button: a new matcap, or a change when every named part already has one.</summary>
    internal enum Action { Apply, Undo, Forget }

    /// <param name="Model">The model as QA named it: a mod-relative file or a game path the mod publishes.</param>
    internal sealed record Args(string ModDir, string ModRoot, string Model, Action Kind, IReadOnlyList<string> Parts,
                                string? Image, string? Library, string? Group, string? Option, bool ConfirmEnable,
                                MatcapHarnessPort Port, HarnessChanges Changes, bool Pack = false);

    /// <summary>The matcap QA named, found as the panel finds it, or the job error.</summary>
    internal static (MatcapChoice? Choice, JobError? Error) ResolveChoice(MatcapHarnessPort port, string modRoot, string? image, bool pack,
                                                                         string? library, string? group, string? option)
    {
        if (image != null)
        {
            if (MatcapImages.Find(image, port.ImageFolder, MatcapImages.ModFolder(modRoot), port.Pack?.Available(), packOnly: pack) is not { } img)
                return (null, new JobError("unknown-image", $"no matcap image called {image}"));
            return (MatcapChoice.OfImage(img), null);
        }
        if (port.Index?.Current?.Find(library!, group!) is not { } lib)
            return (null, new JobError("unknown-library", $"no matcap library {library} / {group}; GET /matcaps?refresh=1 first"));
        if (!lib.Options.Any(o => o.Name == option))
            return (null, new JobError("unknown-option", $"{lib.Label} has no option {option}"));
        return (MatcapChoice.OfLibrary(lib, option!), null);
    }

    /// <summary>The model file and game path QA named, or the job error refusing it.</summary>
    internal static (string? Rel, string? GamePath, JobError? Error) ResolveModel(string modRoot, string model)
    {
        var want = model.Replace('\\', '/');
        var rows = PenumbraModMeta.ReadAllRedirects(modRoot)
            .Where(r => r.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)).ToList();
        var byFile = rows.Where(r => string.Equals(r.File.Replace('\\', '/'), want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byFile.Count > 0) return (byFile[0].File, byFile[0].GamePath, null);
        var byPath = rows.Where(r => string.Equals(r.GamePath.Replace('\\', '/'), want, StringComparison.OrdinalIgnoreCase))
                         .Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (byPath.Count == 1) return (byPath[0], want, null);
        if (byPath.Count > 1)
            return (null, null, new JobError("model-ambiguous", $"{byPath.Count} files publish that game path; name the file"));
        return (null, null, new JobError("model-not-in-mod", "the mod does not publish that model"));
    }

    /// <summary>The GET body for one mod's model: what the panel would show for it. Reads files; never the framework thread.</summary>
    internal static object? ReadModel(string modRoot, string model, MatcapHarnessPort port, out JobError? error)
    {
        error = null;
        var (rel, gamePath, err) = ResolveModel(modRoot, model);
        if (err != null) { error = err; return null; }
        var bytes = File.ReadAllBytes(MatcapWriter.Full(modRoot, rel!));
        var read = ModelPartReader.Read(bytes);
        var parts = read?.Parts ?? [];
        var attrs = read?.AttributeNames ?? [];
        var target = MatcapPlan.TargetOf(gamePath!, out _);
        var kind = target?.Kind;
        var redirects = PenumbraModMeta.ReadAllRedirects(modRoot);
        // The panel's alpha-test rule, once per source material.
        var alpha = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (target != null)
            foreach (var m in parts.Select(p => p.Material).Distinct(StringComparer.OrdinalIgnoreCase))
                try { alpha[m] = MatcapStudio.IsAlphaTested(modRoot, target, m, redirects, port.Env, rel); } catch { alpha[m] = false; }
        // Siblings line up for a NEW apply: against the tickable parts not already on a matcap.
        var tickableParts = parts.Where(p => p.Island < 0 && MatcapPlan.Tickable(p, kind, attrs) && !MatcapPlan.IsOnMatcap(p)).ToList();
        var record = MatcapRecord.Read(modRoot);
        var states = MatcapWriter.Detect(modRoot);
        var siblings = HatCompatService.SiblingFiles(modRoot, gamePath!, rel!)
            .Where(s => !PenumbraModMeta.SameFile(s, rel!))
            .Select(s =>
            {
                bool linesUp = false;
                try
                {
                    var sp = ModelPartReader.Read(File.ReadAllBytes(MatcapWriter.Full(modRoot, s)))?.Parts;
                    linesUp = sp != null && MatcapPlan.LinesUp(tickableParts, sp);
                }
                catch { }
                return new { file = s, linesUp };
            }).ToList();
        int room = 0;
        try { room = ModelMaterialWriter.MaterialRoom(bytes); } catch { }
        return new
        {
            model = rel, gamePath,
            images = MatcapImages.List(port.ImageFolder, MatcapImages.ModFolder(modRoot)).Select(i => new { name = i.Name, fromMod = i.FromMod, fromPack = false })
                .Concat((port.Pack?.Available() ?? []).Select(i => new { name = i.Name, fromMod = false, fromPack = true })),
            parts = parts.Select(p => new
            {
                label = p.Label, material = p.Material, skin = MatcapPlan.IsSkin(p), onMatcap = MatcapPlan.IsOnMatcap(p),
                tickable = MatcapPlan.Tickable(p, kind, attrs), horn = MatcapPlan.IsHorn(p, attrs), island = p.Island,
                alphaTested = alpha.TryGetValue(p.Material, out var at) && at,
            }),
            siblings,
            // The apply group: what an Undo of this model restores with it.
            group = record.Model(rel!) != null ? MatcapWriter.ApplyGroupOf(record, rel!).Order(StringComparer.OrdinalIgnoreCase).ToList() : [rel!],
            entries = record.MaterialsOf(rel!).Select(m => new
            {
                name = m.Name, gamePaths = m.GamePaths, meshes = m.Meshes.Select(x => new { model = x.Model, mesh = x.Mesh, from = x.From }),
                source = m.Source.IsLibrary
                    ? (object)new { kind = "library", mod = m.Source.Library?.Mod, group = m.Source.Library?.Group, option = m.Source.Library?.Option }
                    : new { kind = "image", image = m.Source.Image },
                state = states.TryGetValue(m.Name, out var st) ? st.ToString().ToLowerInvariant() : "ok",
            }),
            materialRoom = room,
        };
    }

    internal static async Task Run(HarnessJobs jobs, HarnessJob job, Args args)
    {
        var game = jobs.Game;
        var ct = job.Cancel.Token;
        job.Progress("checking the mod");
        if (game.IsLegacyFolder(args.ModRoot))
        {
            job.Fail(new JobError(MatcapWriter.CodeLegacy, "the mod is an old (v3) Penumbra mod; matcaps are never written into those"));
            return;
        }
        var (rel, gamePath, error) = ResolveModel(args.ModRoot, args.Model);
        if (error != null) { job.Fail(error); return; }

        // The matcap chosen, as the panel finds it.
        MatcapChoice? choice = null;
        if (args.Kind is Action.Apply)
        {
            var (found, choiceError) = ResolveChoice(args.Port, args.ModRoot, args.Image, args.Pack, args.Library, args.Group, args.Option);
            if (choiceError != null) { job.Fail(choiceError); return; }
            choice = found;
            if (choice!.Library is { } lib)
            {
                // Switching a library on is never silent: without the confirmation nothing is written.
                var status = await jobs.Dispatcher.RunAsync(() => MatcapSelection.Read(args.Port.Penumbra, lib), jobs.Timings.FrameworkStart)
                                       .ConfigureAwait(false);
                if (status.Known && !status.Enabled && !args.ConfirmEnable)
                {
                    job.Fail(new JobError(MatcapSelection.CodeNeedsConfirm,
                        string.Format(ProteusLoc.Localize("Parts.Matcap.ConfirmEnable.Fmt", "{0} is off in {1}. Switching it on also turns on: {2}"),
                                      lib.Name, status.Collection, MatcapSelection.AlsoText(lib))));
                    return;
                }
            }
        }

        job.Progress("backing up the mod");
        BackupInfo backup;
        try { backup = jobs.Backups.Create(args.ModRoot, args.ModDir, BackupKind.Matcap, jobs.Session, jobs.NextSeq(), job.Id, ct); }
        catch (BackupException e) { job.Fail(new JobError(e.Code, e.Message)); return; }
        jobs.RememberBackup(job.Id, backup);
        job.SetBackup(new BackupRef(backup.Id, backup.Path));
        ct.ThrowIfCancellationRequested();

        job.Progress("writing");
        // Every face game path the model is published at, gathered before the job (an Undo in an edit mod drops them).
        var facePaths = gamePath != null && MatcapPlan.TargetOf(gamePath, out _) is { Kind: MatcapPlan.Kind.Face }
            ? PenumbraModMeta.ReadAllRedirects(args.ModRoot)
                .Where(r => PenumbraModMeta.SameFile(r.File, rel!) && MatcapPlan.TargetOf(r.GamePath, out _) is { Kind: MatcapPlan.Kind.Face })
                .Select(r => r.GamePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        MatcapWriter.Outcome outcome;
        MatcapStudio.Prepared? prepared = null;
        IReadOnlyList<string>? changeNames = null;   // set when the Apply changed existing matcaps
        switch (args.Kind)
        {
            case Action.Apply:
            {
                var bytes = File.ReadAllBytes(MatcapWriter.Full(args.ModRoot, rel!));
                var parts = ModelPartReader.Read(bytes)?.Parts ?? [];
                var chosen = new List<ModelPart>();
                foreach (var label in args.Parts)
                {
                    if (parts.FirstOrDefault(p => p.Label == label) is not { } part)
                    { job.Fail(new JobError("unknown-part", $"the model has no part {label}")); return; }
                    chosen.Add(part);
                }
                // As the panel's Apply: parts that all have a matcap change it in place; a mix is refused.
                switch (MatcapPlan.KindOfApply(chosen))
                {
                    case MatcapPlan.ApplyKind.Change:
                        changeNames = MatcapPlan.ChangeNames(chosen);
                        outcome = MatcapStudio.ChangeAll(args.ModRoot, changeNames, choice!, args.Port.Env);
                        break;
                    case MatcapPlan.ApplyKind.Mixed:
                        outcome = MatcapWriter.Outcome.Refuse("already-matcap", Localization.Strings.Matcap.AlreadyMatcap);
                        break;
                    default:
                        prepared = MatcapStudio.Prepare(args.ModRoot, rel!, gamePath!, bytes, chosen, choice!, args.Port.Env);
                        outcome = prepared.Ok ? MatcapWriter.Apply(args.ModRoot, prepared.Request!) : MatcapWriter.Outcome.Refuse(prepared.Code, prepared.Message);
                        break;
                }
                break;
            }
            case Action.Undo:
                outcome = MatcapWriter.Undo(args.ModRoot, rel!, args.Port.Env.ReadGameFile);
                break;
            default:
                outcome = MatcapWriter.Forget(args.ModRoot, rel!);
                break;
        }

        // The library's option, on the framework thread, its Before recorded first for put-back.
        object? librarySetting = null;
        if (outcome.Ok && choice?.Library is { } chosenLib)
        {
            librarySetting = await jobs.Dispatcher.RunAsync(() => SelectRecorded(args, chosenLib, choice.Option!), jobs.Timings.FrameworkStart)
                                       .ConfigureAwait(false);
        }

        // Every successful job changed the mod (a Forget removes redirects), so every one ends in a reload and redraw.
        bool changed = outcome.Ok || librarySetting != null;
        // A face the face overlay rewrites is read again after our edit, not from its remembered copy.
        bool isChange = changeNames != null;
        if (changed && !isChange && facePaths.Count > 0 && args.Port.ForgetFaceUpstream is { } forget)
            await jobs.Dispatcher.RunAsync(() => { foreach (var p in facePaths) forget(p); return true; }, jobs.Timings.FrameworkStart).ConfigureAwait(false);
        // The panel's drawn-file line after an Apply (EDGE-47/58), on the framework thread.
        string? drawnLine = null;
        if (outcome.Ok && args.Kind == Action.Apply && !isChange && gamePath != null && args.Port.DrawnStatus is { } drawnOf)
            drawnLine = await jobs.Dispatcher.RunAsync(() => drawnOf(args.ModRoot, gamePath, outcome.Files), jobs.Timings.FrameworkStart).ConfigureAwait(false);
        if (changed)
        {
            jobs.PostAfterChange(job, () => job.SetAfterChange(game.AfterModChange(args.ModDir)));
            await jobs.AwaitAfterChange(job).ConfigureAwait(false);
        }
        try { jobs.Backups.WriteAfter(backup, args.ModRoot); }
        catch (Exception e) { jobs.Log?.Warning(e, "[Proteus] test harness: could not record {0}'s state after the matcap", args.ModDir); }

        var record = MatcapRecord.Read(args.ModRoot);
        var entry = prepared?.Request is { } req ? record.Materials.FirstOrDefault(m => m.Name == req.MaterialName)
                  : changeNames is [var firstName, ..] ? record.Materials.FirstOrDefault(m => string.Equals(m.Name, firstName, StringComparison.OrdinalIgnoreCase))
                  : null;
        // nothing-to-undo is Undo's and Forget's: a refused change is "refused".
        bool undoing = args.Kind is Action.Undo or Action.Forget;
        job.SetResult(new
        {
            action = args.Kind.ToString().ToLowerInvariant(),
            summary = !outcome.Ok ? (undoing && outcome.Code == MatcapWriter.CodeNothing ? "nothing-to-undo" : "refused")
                    : args.Kind switch { Action.Apply => isChange ? "changed" : "applied", Action.Undo => "undone", _ => "forgotten" },
            code = outcome.Code.Length > 0 ? outcome.Code : null,
            resultLine = outcome.Message,
            material = entry?.Name, gamePaths = entry?.GamePaths,
            files = prepared?.Request?.Models.Select(m => new { file = m.Rel, meshesAdded = prepared.Request.Materials.SelectMany(p => p.Meshes).Count(x => x.Rel == m.Rel && x.Source != x.Drawn), lod0Cut = m.Lod0Cut }),
            materials = prepared?.Request?.Materials.Select(p => (object)new { name = p.Name, gamePaths = p.GamePaths, normal = p.Normal, alphaThreshold = (float?)p.AlphaThreshold })
                     ?? changeNames?.Select(n => record.Materials.FirstOrDefault(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase)))
                                    .OfType<MatcapRecord.MaterialEntry>()
                                    .Select(m => (object)new { name = m.Name, gamePaths = m.GamePaths }),
            drawn = args.Kind == Action.Apply && !isChange && outcome.Ok
                ? new { line = drawnLine, ours = drawnLine == null, throughFace = drawnLine == Localization.Strings.Matcap.DrawnThroughFace }
                : null,
            siblingsSkipped = prepared?.SiblingsSkipped,
            librarySetting,
        });
        if (changed)
        {
            job.Progress("watching the redraw");
            await jobs.Follow(job).ConfigureAwait(false);
        }
        job.Progress("");
    }

    /// <summary>Select the library option with its Before recorded in the binding ledger. Framework thread.</summary>
    private static object? SelectRecorded(Args args, MatcapLibrary lib, string option)
    {
        if (args.Port.Penumbra.PlayerCollection() is not { } coll) return new { mod = lib.Mod, error = "penumbra-unavailable" };
        if (args.Changes.Bindings.BeginExternalPermanent(coll.Id, lib.Mod, out var before) is { } refused)
            return new { mod = lib.Mod, error = refused.Code };
        var st = MatcapSelection.Read(args.Port.Penumbra, lib);
        var sel = MatcapSelection.Select(args.Port.Penumbra, lib, option, args.ConfirmEnable);
        args.Changes.Bindings.NoteExternalPermanent(coll.Id, lib.Mod, before);
        args.Changes.NoteMatcapMod(lib.Mod);
        return new
        {
            mod = lib.Mod, group = lib.Group, option, before = st.CurrentOption, enabledBefore = st.Enabled,
            inheritedBefore = st.Inherited, winning = sel.Winning, code = sel.Ok ? null : sel.Code,
        };
    }
}
