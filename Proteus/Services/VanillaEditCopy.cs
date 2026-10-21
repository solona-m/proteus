using System;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Copies a piece of the game's own gear into its "&lt;Item&gt; — Proteus edit" mod on its FIRST edit, with that edit
/// written into the copy. Nothing is copied when the piece is opened: a click that made a mod brought back, at once,
/// the mod a player had just deleted.
/// <para/>
/// For a new mod the edit is written inside <see cref="RefitModService.Ensure"/>'s fill, before Penumbra registers
/// the mod — Penumbra compacts a new mod's files the moment it adds it, holding each one exclusively, so a write after
/// would race it. A refused write throws out of the fill, and Ensure rolls the folder back.
/// <para/>
/// An existing mod is only ever added to. Its copy of this piece is reused only when it is still the game's bytes and
/// no edit is recorded against it; an edited one, or a mod the player has switched off, is refused, never written over.
/// Pure filesystem apart from the injected calls, so it is testable without the game.
/// </summary>
internal static class VanillaEditCopy
{
    /// <summary>What the edit's own write said.</summary>
    internal readonly record struct FirstWrite(bool Ok, string Message);

    internal enum Status
    {
        Copied,
        /// <summary>No mod could be found or made; nothing was written.</summary>
        EnsureFailed,
        /// <summary>The edit's write refused; a new mod was rolled back.</summary>
        WriteRefused,
        /// <summary>The item's mod already holds an edited copy of this piece.</summary>
        EditedCopyThere,
        /// <summary>The item's mod is there but switched off, so a change written into it would not show.</summary>
        ModSwitchedOff,
    }

    /// <param name="Made">The mod was made by this call, so Penumbra is still registering it.</param>
    internal readonly record struct Outcome(Status Status, string Root, string Dir, bool Made, string Message)
    {
        internal bool Ok => Status == Status.Copied;
    }

    /// <summary>The name the copy's mod takes (before any numbering), as <see cref="RefitModService.Ensure"/> makes it.</summary>
    internal static string ModName(string itemName) => RefitModService.NameFor(itemName, RefitModService.EditQualifier);

    /// <param name="gamePath">The piece's game path, which is also its file inside the mod.</param>
    /// <param name="gameBytes">The game's own model — the bytes the edit was made on.</param>
    /// <param name="find">The item's existing edit mod's root, or null (<see cref="RefitModService.Find"/>).</param>
    /// <param name="switchedOn">Whether a mod directory is switched on in the player's collection; null when unknown.</param>
    /// <param name="ensure">Find or make the mod, running the fill on a new one (<see cref="RefitModService.Ensure"/>).</param>
    /// <param name="firstWrite">The edit's own write into (mod root, file).</param>
    internal static Outcome Make(string gamePath, byte[] gameBytes, Func<string?> find, Func<string, bool?> switchedOn,
                                 Func<Action<string>, RefitModService.Result> ensure,
                                 Func<string, string, FirstWrite> firstWrite)
    {
        string rel = gamePath.Replace('\\', '/');

        if (find() is { } found)
        {
            string dir = Path.GetFileName(found);
            if (switchedOn(dir) == false)
                return new(Status.ModSwitchedOff, found, dir, false, "");
            if (HoldsEdit(found, rel, gameBytes))
                return new(Status.EditedCopyThere, found, dir, false, "");
        }

        FirstWrite? written = null;
        var made = ensure(root =>
        {
            RefitModService.AddGameModel(root, gamePath, gameBytes);
            written = firstWrite(root, rel);
            if (!written.Value.Ok) throw new InvalidOperationException(written.Value.Message);
        });
        if (!made.Ok)
            return written is { Ok: false } refused
                ? new(Status.WriteRefused, "", "", false, refused.Message)
                : new(Status.EnsureFailed, "", "", false, made.Message);
        if (made.Made) return new(Status.Copied, made.Root, made.Dir, true, "");

        // Found: checked above, but looked at again — Ensure found it by name, which is the same mod Find did.
        if (HoldsEdit(made.Root, rel, gameBytes))
            return new(Status.EditedCopyThere, made.Root, made.Dir, false, "");

        // Untouched (still the game's bytes) or not there at all: published at the game path, then the edit over it.
        RefitModService.AddGameModel(made.Root, gamePath, gameBytes);
        var w = firstWrite(made.Root, rel);
        return w.Ok
            ? new(Status.Copied, made.Root, made.Dir, false, "")
            : new(Status.WriteRefused, made.Root, made.Dir, false, w.Message);
    }

    /// <summary>
    /// Whether the mod's copy of the piece carries anything that writing the game's bytes over it would lose: an edit
    /// recorded by the brush or the switches, bytes that are no longer the game's, or a file that cannot be read to
    /// tell. A piece the mod does not have is not an edit.
    /// </summary>
    internal static bool HoldsEdit(string root, string rel, byte[] gameBytes)
    {
        string file = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file)) return false;
        if (MeshVolumeService.IsPatched(root, rel)) return true;
        if (MeshToggleService.ReadRecord(root) is { } record
            && record.Items.Any(i => i.Files.Any(f => string.Equals(f.Replace('\\', '/'), rel, StringComparison.OrdinalIgnoreCase))))
            return true;
        try { return !File.ReadAllBytes(file).AsSpan().SequenceEqual(gameBytes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}
