using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Localization;
using Proteus.Services;

namespace Proteus.Gui;

/// <summary>
/// The game's own gear in the Studio: opened as itself under every tool, and copied into its "&lt;Item&gt; — Proteus
/// edit" mod by the first change saved — never by opening it, so a click cannot bring back a mod the player deleted.
/// </summary>
public sealed partial class PartsPanel
{
    /// <summary>
    /// The game path of the piece just copied while Penumbra registers and switches on its mod (<see
    /// cref="copyAwaitingEnable"/>). Until then the character still draws it from the game, so the live brush stays on
    /// that path while the copy is open, and a click on it opens the copy rather than the game's piece — even after
    /// other models were opened in between, or that click would be refused against the player's own copy. Cleared when
    /// the redraw for the copy goes out.
    /// </summary>
    private string? copyNotDrawnYet;

    /// <summary>The copy's mod directory until Penumbra reports it switched on; then the character is redrawn once.</summary>
    private string? copyAwaitingEnable;

    /// <summary>The last refusal logged, so one that repeats on every retry is logged once.</summary>
    private string? lastCopyRefusal;

    /// <summary>The just-copied piece's game path when it is the model open now; null otherwise.</summary>
    private string? CopyNotDrawnOpen()
        => copyNotDrawnYet != null && modelIndex >= 0 && modelIndex < models.Count && !modelUnreadable
           && string.Equals(modDir, copyAwaitingEnable, StringComparison.OrdinalIgnoreCase)
           && SameFile(models[modelIndex].GamePath, copyNotDrawnYet)
            ? copyNotDrawnYet
            : null;

    /// <summary>
    /// A click on the just-copied piece while the character still draws it from the game: open its copy, or stay on it
    /// when it is already open.
    /// </summary>
    private void OpenCopyNotDrawn(string gamePath)
    {
        if (CopyNotDrawnOpen() != null || copyAwaitingEnable is not { } dir) return;
        SelectMod(dir);
        int index = models.FindIndex(m => SameFile(m.File, gamePath) && SameFile(m.GamePath, gamePath));
        if (index >= 0) SelectModel(index);
    }

    /// <summary>
    /// A brush save on the game's own gear. Nothing to write makes no mod. On teardown nothing is made either: the
    /// mod could be registered but never switched on, and the next edit would find it switched off.
    /// </summary>
    private void SaveVanillaBrush(VanillaPick piece, bool refreshGame)
    {
        if (volume == null || brushBase == null || modelIndex < 0 || !volume.Dirty)
        {
            brushChangedAt = -1;   // undone back to nothing, or never touched: there is nothing to copy for
            return;
        }
        if (!refreshGame)
        {
            log.Warning("[Proteus] parts: an unsaved edit of {0} was dropped on unload; it had not been copied into a mod yet",
                        piece.GamePath);
            brushChangedAt = -1;
            return;
        }

        // Built in memory before any mod is made, so an edit that cannot be written is said once, with no folder made
        // and rolled back on every retry.
        try { MeshVolumeService.Inflate(brushBase, volume); }
        catch (Exception ex)
        {
            status = string.Format(Strings.Parts.VanillaCopyRefusedFmt, ex.Message);
            statusIsError = true;
            log.Warning("[Proteus] parts: the edit of {0} cannot be written: {1}", piece.GamePath, ex.Message);
            brushChangedAt = -1;
            return;
        }

        MeshVolumeService.Outcome? result = null;
        var gameBytes = brushBase;
        var solve = volume;
        if (CopyVanillaForEdit(piece, (root, rel) =>
            {
                result = MeshVolumeService.Apply(root, rel, gameBytes, solve, writeUntouched: true);
                return new VanillaEditCopy.FirstWrite(result.Ok, result.Message);
            }, out bool written) is not { } copy)
        {
            if (written) { brushChangedAt = -1; return; }   // saved, though the panel could not carry on in the copy
            // Still waiting: retried on the debounce, as a failed save is — deleting a refused mod lets it through.
            brushChangedAt = Environment.TickCount64;
            return;
        }
        string copied = status ?? "";
        AfterBrushWritten(copy.Root, models[modelIndex].File, result!, refreshGame, reload: !copy.Made);
        // The save's own line replaced it; the mod the change went into is said too, as Write does.
        status = copied + "\n" + status;
    }

    /// <summary>Switches written on the game's own gear, its first change: copied, with the switches, into its mod.</summary>
    private void CommitVanilla(VanillaPick piece, List<MeshToggleService.Plan> plans)
    {
        var ps = Strings.Parts;
        if (parts == null || brushBase == null) return;

        var modelParts = parts;
        MeshToggleService.Outcome? result = null;
        byte[]? model = null;
        MeshToggleRecord? record = null;
        var copy = CopyVanillaForEdit(piece, (root, rel) =>
        {
            // Every redirect the mod publishes, so a switch reaches the piece's other files in an existing mod too.
            var siblings = PenumbraModMeta.ReadAllRedirects(root);
            var row = siblings.FirstOrDefault(r => SameFile(r.File, rel) && SameFile(r.GamePath, piece.GamePath));
            if (row.File == null) row = new PenumbraModMeta.Redirect(piece.GamePath, rel, "");
            result = MeshToggleService.Write(root, row, modelParts, plans, siblings,
                                             gamePath => textureLoader.LoadRawFile(null, gamePath),
                                             WornVariant(piece.GamePath));
            if (result.Ok)
            {
                // Read back now, before Penumbra is told of a new mod and starts compacting its files.
                model = File.ReadAllBytes(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                record = MeshToggleService.ReadRecord(root);
            }
            return new VanillaEditCopy.FirstWrite(result.Ok, result.Message);
        }, out bool saved);
        if (copy is not { } c)
        {
            if (saved)
            {
                pending.Clear();
                writeError = null;
                ticked.Clear();
                return;
            }
            // Shown under the Write button; the pending list stays, so the user can fix it and press again.
            writeError = status;
            return;
        }

        statusIsError = false;
        string written = string.Format(ps.WrittenFmt, plans.Count, result!.GroupName);
        if (result.Skipped.Count > 0) written += "\n" + string.Format(ps.SkippedFmt, result.Skipped.Count);
        pending.Clear();
        writeError = null;
        ticked.Clear();
        AfterModChange(c.Root, result.Files, c.Made ? (model, record) : null);
        status = written + "\n" + status;
    }

    /// <summary>
    /// Make or find the piece's "— Proteus edit" mod with <paramref name="write"/> as its first change, and carry the
    /// open edit over to it. Null, with the status saying why, when nothing was written.
    /// </summary>
    /// <param name="written">Set when the change was saved although null is returned: the mod had no row to carry on in.</param>
    private (string Root, bool Made)? CopyVanillaForEdit(VanillaPick piece, Func<string, string, VanillaEditCopy.FirstWrite> write,
                                                         out bool written)
    {
        written = false;
        var ps = Strings.Parts;
        if (brushBase == null) return null;
        string modName = VanillaEditCopy.ModName(piece.ItemName);

        VanillaEditCopy.Outcome outcome;
        try
        {
            string? modsRoot = penumbra.GetModDirectory();
            outcome = VanillaEditCopy.Make(
                piece.GamePath, brushBase,
                find: () => modsRoot == null ? null : RefitModService.Find(modsRoot, piece.ItemName, RefitModService.EditQualifier),
                switchedOn: dir => penumbra.GetPlayerCollectionId() is { } coll
                                   && penumbra.GetModSettings(coll, dir) is { } s ? s.Enabled : null,
                ensure: fill => refitMods.Ensure(piece.ItemName, RefitModService.EditQualifier, fill,
                                                 description: string.Format(ps.VanillaModDescriptionFmt, piece.ItemName),
                                                 folder: RefitModService.OwnModsFolder),
                firstWrite: write);
        }
        catch (Exception ex)
        {
            // Logged once while it repeats on every retry, as a refusal is.
            string refusal = $"{piece.GamePath}|{ex.GetType().Name}|{ex.Message}";
            if (refusal != lastCopyRefusal)
                log.Warning(ex, "[Proteus] parts: could not copy {0} into a mod", piece.GamePath);
            lastCopyRefusal = refusal;
            status = string.Format(ps.VanillaCopyRefusedFmt, ex.Message);
            statusIsError = true;
            return null;
        }

        if (!outcome.Ok)
        {
            status = outcome.Status switch
            {
                VanillaEditCopy.Status.EditedCopyThere => string.Format(ps.VanillaEditedCopyThereFmt, modName),
                VanillaEditCopy.Status.ModSwitchedOff => string.Format(ps.VanillaModSwitchedOffFmt, modName),
                _ => string.Format(ps.VanillaCopyRefusedFmt, outcome.Message),
            };
            statusIsError = true;
            // Retried on the debounce for as long as the edit waits, so said in the log once, not every 1.5 s.
            string refusal = $"{piece.GamePath}|{outcome.Status}|{outcome.Message}";
            if (refusal != lastCopyRefusal)
                log.Warning("[Proteus] parts: {0} not copied into {1}: {2} {3}", piece.GamePath, modName, outcome.Status,
                            outcome.Message);
            lastCopyRefusal = refusal;
            return null;
        }

        if (!AdoptCopy(piece, outcome))
        {
            // Written, but the mod lists no row for the panel to carry on in: the change is saved and said to be, the
            // mod reloaded so it shows, and nothing is retried (a retry would only find its own edit and refuse).
            written = true;
            status = string.Format(ps.VanillaCopiedFmt, piece.Label, modName);
            statusIsError = false;
            if (!outcome.Made)
            {
                compositor.ExpectOwnModEdit(outcome.Dir);
                penumbra.ReloadModDirectory(outcome.Dir);
            }
            return null;
        }
        if (outcome.Made)
        {
            copyAwaitingEnable = outcome.Dir;
            copyNotDrawnYet = piece.GamePath;
        }
        else
        {
            compositor.ExpectOwnModEdit(outcome.Dir);
        }
        lastCopyRefusal = null;
        log.Information("[Proteus] parts: copied {0} into {1} on its first edit", piece.GamePath, outcome.Dir);
        status = string.Format(ps.VanillaCopiedFmt, piece.Label, modName);
        statusIsError = false;
        return (outcome.Root, outcome.Made);
    }

    /// <summary>
    /// The open piece becomes the copy's row in its mod, with the edit, the undo, the locks and the view kept: nothing
    /// is reopened, because the solve was built on the game's bytes and the copy was made from those same bytes.
    /// False, with the panel left on the vanilla piece, when the mod has no row that is the piece's own file.
    /// </summary>
    private bool AdoptCopy(VanillaPick piece, VanillaEditCopy.Outcome outcome)
    {
        string rel = piece.GamePath.Replace('\\', '/');
        // A mod still being registered is not read: its one redirect is known, and Penumbra may be holding its files.
        var nextRedirects = outcome.Made
            ? [new PenumbraModMeta.Redirect(piece.GamePath, rel, "")]
            : PenumbraModMeta.ReadAllRedirects(outcome.Root);
        var nextModels = PublishedModels(nextRedirects);
        var nextContent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!outcome.Made) nextModels.AddRange(ContentModels(outcome.Root, nextContent));
        // The piece's own file, never another option's at the same game path: the edit is written from the game's
        // bytes, and landing it on a refit's file would put the game's shape back over the refit.
        int index = nextModels.FindIndex(m => SameFile(m.File, rel) && SameFile(m.GamePath, piece.GamePath));
        if (index < 0)
        {
            log.Warning("[Proteus] parts: {0} has no row for {1} after the copy; left open as the game's", outcome.Dir, rel);
            return false;
        }

        string oldKey = ViewportKey;
        vanilla = null;
        modDir = outcome.Dir;
        modIsLegacy = false;
        redirects = nextRedirects;
        contentFiles.Clear();
        contentFiles.UnionWith(nextContent);
        models = nextModels;
        modelLabels = ModelLabels(models);
        modelIndex = index;
        existing = outcome.Made ? null : MeshToggleService.ReadRecord(outcome.Root);
        brushSaved = outcome.Made ? 0 : MeshVolumeService.PatchedCount(outcome.Root);
        mods = LoadMods();

        string newKey = ViewportKey;
        MoveKeyed(lockedParts, oldKey, newKey);
        MoveKeyed(heldParts, oldKey, newKey);
        MoveKeyed(keepShapeParts, oldKey, newKey);
        viewport.Rekey(oldKey, newKey);
        return true;
    }

    /// <summary>Once the copy's mod is switched on, redraw so the character draws it; the preview, if up, already does.</summary>
    private void RedrawIfCopyEnabled()
    {
        if (copyAwaitingEnable == null || refitMods.IsPending) return;
        copyAwaitingEnable = null;
        copyNotDrawnYet = null;
        if (!preview.Active) compositor.RedrawForChangedModel("Studio parts");
    }

    private static bool SameFile(string? a, string? b)
        => string.Equals(a?.Replace('\\', '/'), b?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Move one model's entry to another key, as its row changes key; the target's own is replaced.</summary>
    internal static void MoveKeyed<T>(Dictionary<string, T> map, string from, string to)
    {
        if (from == to || !map.Remove(from, out var value)) return;
        map[to] = value;
    }

    /// <summary>
    /// The file the live brush finds on the character: the game path for a piece drawn from the game (open as itself,
    /// or copied but not yet drawn from its mod), otherwise the mod's file; null when there is neither.
    /// </summary>
    internal static string? ArmedModelFile(string? gamePath, string? modRoot, string file)
        => gamePath ?? (modRoot == null ? null : Path.Combine(modRoot, file.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// The notes under a vanilla piece's header, each with whether it is a warning. Under Body size: where a refit is
    /// saved and what it costs, or that it cannot refit this piece; under every other tool: that nothing is saved
    /// until the first change, and the mod that change will go into.
    /// </summary>
    internal static List<(string Text, bool Warn)> VanillaNotes(bool bodySize, bool refittable, string itemName)
    {
        var ps = Strings.Parts;
        if (bodySize)
            return refittable
                ? [(ps.VanillaSaveNote, false), (ps.VanillaCaveats, true)]
                : [(ps.VanillaNotRefittable, true)];
        return [(string.Format(ps.VanillaCopyNoteFmt, VanillaEditCopy.ModName(itemName)), false)];
    }
}
