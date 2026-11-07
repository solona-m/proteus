using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Proteus.Services;
using Proteus.Services.Matcap;

namespace Proteus.TestHarness;

/// <summary>
/// The Studio's Matcaps tool for QA: what it lists (<see cref="Read"/>), and Apply, Change, Undo and Forget as a job
/// (<see cref="Run"/>) through the same entry points the panel's buttons call (<see cref="MatcapStudio"/>,
/// <see cref="MatcapWriter"/>, <see cref="MatcapSelection"/>), after a whole-mod backup. A library setting the job changes is
/// recorded in the binding session's ledger (Before written first) and put back at put-back. A Studio edit pending is
/// refused (studio-busy) in the request's framework hop, never flushed.
/// </summary>
internal static class MatcapJob
{
    internal enum Action { Apply, Change, Undo, Forget }

    /// <param name="Model">The model as QA named it: a mod-relative file or a game path the mod publishes.</param>
    internal sealed record Args(string ModDir, string ModRoot, string Model, Action Kind, IReadOnlyList<string> Parts,
                                string? Image, string? Library, string? Group, string? Option, bool ConfirmEnable,
                                string? Entry, MatcapHarnessPort Port, HarnessChanges Changes);

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
        var kind = MatcapPlan.TargetOf(gamePath!, out _)?.Kind;
        var record = MatcapRecord.Read(modRoot);
        var states = MatcapWriter.Detect(modRoot);
        var siblings = HatCompatService.SiblingFiles(modRoot, gamePath!, rel!)
            .Where(s => !PenumbraModMeta.SameFile(s, rel!)).ToList();
        int room = 0;
        try { room = ModelMaterialWriter.MaterialRoom(bytes); } catch { }
        return new
        {
            model = rel, gamePath,
            images = MatcapImages.List(port.ImageFolder, MatcapImages.ModFolder(modRoot)).Select(i => new { name = i.Name, fromMod = i.FromMod }),
            parts = parts.Select(p => new
            {
                label = p.Label, material = p.Material, skin = MatcapPlan.IsSkin(p), onMatcap = MatcapPlan.IsOnMatcap(p),
                tickable = MatcapPlan.Tickable(p, kind, attrs), horn = MatcapPlan.IsHorn(p, attrs), island = p.Island,
            }),
            siblings,
            group = record.Models.Select(m => m.File),
            entries = record.Materials.Select(m => new
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
        if (args.Kind is Action.Apply or Action.Change)
        {
            if (args.Image != null)
            {
                if (MatcapImages.Find(args.Image, args.Port.ImageFolder, MatcapImages.ModFolder(args.ModRoot)) is not { } img)
                { job.Fail(new JobError("unknown-image", $"no matcap image called {args.Image}")); return; }
                choice = MatcapChoice.OfImage(img);
            }
            else
            {
                if (args.Port.Index?.Current?.Find(args.Library!, args.Group!) is not { } lib)
                { job.Fail(new JobError("unknown-library", $"no matcap library {args.Library} / {args.Group}; GET /matcaps?refresh=1 first")); return; }
                if (!lib.Options.Any(o => o.Name == args.Option))
                { job.Fail(new JobError("unknown-option", $"{lib.Label} has no option {args.Option}")); return; }
                choice = MatcapChoice.OfLibrary(lib, args.Option!);
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
        MatcapWriter.Outcome outcome;
        MatcapStudio.Prepared? prepared = null;
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
                prepared = MatcapStudio.Prepare(args.ModRoot, rel!, gamePath!, bytes, chosen, choice!, args.Port.Env);
                outcome = prepared.Ok ? MatcapWriter.Apply(args.ModRoot, prepared.Request!) : MatcapWriter.Outcome.Refuse(prepared.Code, prepared.Message);
                break;
            }
            case Action.Change:
            {
                var (src, mtrl, temp, code, msg) = MatcapStudio.PrepareChange(args.ModRoot, args.Entry!, choice!, args.Port.Env);
                outcome = src == null ? MatcapWriter.Outcome.Refuse(code, msg) : MatcapWriter.Change(args.ModRoot, args.Entry!, src, mtrl, temp);
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
        if (changed && args.Kind != Action.Change && gamePath != null && MatcapPlan.TargetOf(gamePath, out _) is { Kind: MatcapPlan.Kind.Face }
            && args.Port.ForgetFaceUpstream is { } forget)
        {
            var facePaths = PenumbraModMeta.ReadAllRedirects(args.ModRoot)
                .Where(r => PenumbraModMeta.SameFile(r.File, rel!) && MatcapPlan.TargetOf(r.GamePath, out _) is { Kind: MatcapPlan.Kind.Face })
                .Select(r => r.GamePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await jobs.Dispatcher.RunAsync(() => { foreach (var p in facePaths) forget(p); return true; }, jobs.Timings.FrameworkStart).ConfigureAwait(false);
        }
        if (changed)
        {
            jobs.PostAfterChange(job, () => job.SetAfterChange(game.AfterModChange(args.ModDir)));
            await jobs.AwaitAfterChange(job).ConfigureAwait(false);
        }
        try { jobs.Backups.WriteAfter(backup, args.ModRoot); }
        catch (Exception e) { jobs.Log?.Warning(e, "[Proteus] test harness: could not record {0}'s state after the matcap", args.ModDir); }

        var record = MatcapRecord.Read(args.ModRoot);
        var entry = prepared?.Request is { } req ? record.Materials.FirstOrDefault(m => m.Name == req.MaterialName) : null;
        job.SetResult(new
        {
            action = args.Kind.ToString().ToLowerInvariant(),
            summary = !outcome.Ok ? (outcome.Code == MatcapWriter.CodeNothing ? "nothing-to-undo" : "refused")
                    : args.Kind switch { Action.Apply => "applied", Action.Change => "changed", Action.Undo => "undone", _ => "forgotten" },
            code = outcome.Code.Length > 0 ? outcome.Code : null,
            resultLine = outcome.Message,
            material = entry?.Name, gamePaths = entry?.GamePaths,
            files = prepared?.Request?.Models.Select(m => new { file = m.Rel, meshesAdded = prepared.Request.Materials.SelectMany(p => p.Meshes).Count(x => x.Rel == m.Rel && x.Source != x.Drawn), lod0Cut = m.Lod0Cut }),
            materials = prepared?.Request?.Materials.Select(p => p.Name),
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
