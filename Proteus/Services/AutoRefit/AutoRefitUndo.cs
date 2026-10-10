using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Proteus.Services;

/// <summary>
/// Undoing the automatic refit of a piece being worn, on disk: the size it saved into the piece's mod taken out again,
/// and a mod it made for the game's own gear marked for deletion once that leaves it empty. The Penumbra side —
/// reloading, deleting, putting the selection back, and leaving the piece alone from then on — belongs to
/// <see cref="AutoRefitWatcher.UndoWorn"/>.
/// <para/>
/// Pure filesystem, so it is testable without the game.
/// </summary>
internal static class AutoRefitUndo
{
    /// <summary>What undoing did to one mod.</summary>
    /// <param name="Dir">The mod's folder under Penumbra's mods root.</param>
    /// <param name="Groups">The groups options were taken out of.</param>
    /// <param name="Removed">Options taken out.</param>
    /// <param name="Failed">Options that could not be, with why.</param>
    /// <param name="Empty">A mod Proteus made that now carries nothing at all: delete it.</param>
    internal sealed record ModResult(string Dir, List<string> Groups, List<string> Removed, List<string> Failed, bool Empty);

    /// <summary>
    /// Which of a mod's recorded refits of the piece at <paramref name="gamePath"/> the automatic refit made: those
    /// marked <see cref="BodyRetargetWriter.Entry.Auto"/>, and — for refits saved before that mark was kept — one in a
    /// group the automatic refit switched on (<paramref name="switchedOn"/>, <c>"modDir|group"</c>). A size made by hand
    /// in the Body size tool is never one.
    /// <para/>
    /// By the piece, not by the file it draws: a refit saved but not drawn — another size picked, or a design holding
    /// the mod in the author's size with a temporary setting — is the piece's refit all the same. Matching the drawn
    /// file found nothing to undo on "Thorn Princess", whose design held it in the author's small size.
    /// </summary>
    /// <returns>Each (group, option) once, however many slots the option covers.</returns>
    internal static List<(string Group, string Option)> Automatic(string dir, BodyRetargetWriter.Record record,
                                                                  string gamePath, IReadOnlySet<string> switchedOn)
        => record.Options
                 .Where(e => string.Equals(e.GamePath, gamePath, StringComparison.OrdinalIgnoreCase))
                 .Where(e => e.Auto || switchedOn.Contains(AutoRefitDecisions.GroupKey(dir, record.GroupOf(e))))
                 .Select(e => (Group: record.GroupOf(e), Option: e.Name))
                 .DistinctBy(p => (p.Group.ToUpperInvariant(), p.Option.ToUpperInvariant()))
                 .ToList();

    /// <summary>
    /// Take the automatic refit of the piece at <paramref name="gamePath"/> out of the mod at <paramref name="root"/>.
    /// </summary>
    /// <returns>What was done; null when the piece has no automatic refit there — only sizes made by hand, or none.</returns>
    internal static ModResult? Undo(string root, string gamePath, IReadOnlySet<string> switchedOn)
    {
        string dir = Path.GetFileName(root);
        if (BodyRetargetWriter.ReadRecord(root) is not { } record) return null;
        var going = Automatic(dir, record, gamePath, switchedOn);
        if (going.Count == 0) return null;

        var removed = new List<string>();
        var failed = new List<string>();
        foreach (var (group, option) in going)
        {
            var outcome = BodyRetargetWriter.Undo(root, group, option);
            if (outcome.Ok) removed.Add(option);
            else failed.Add($"{option}: {outcome.Message}");
        }
        var groups = going.Select(g => g.Group).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ModResult(dir, groups, removed, failed, removed.Count > 0 && failed.Count == 0 && IsEmptyRefitMod(root));
    }

    /// <summary>
    /// Of <paramref name="groupKeys"/> (<c>"modDir|group"</c>), those of this mod whose group serves
    /// <paramref name="gamePath"/> in some option — the groups that are the piece's, rather than another piece's of the
    /// same mod. An outfit's top and legs are one mod; undoing the top must not switch the legs back.
    /// </summary>
    internal static List<string> GroupsServing(string root, string gamePath, IEnumerable<string> groupKeys)
    {
        string dir = Path.GetFileName(root);
        var serving = PenumbraModMeta.ReadAllRedirects(root)
            .Where(r => string.Equals(r.GamePath, gamePath, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Source.IndexOf(" / ", StringComparison.Ordinal) is var split and >= 0 ? r.Source[..split] : r.Source)
            .Where(g => g.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return groupKeys.Where(k => k.StartsWith(dir + "|", StringComparison.OrdinalIgnoreCase)
                                    && serving.Contains(k[(dir.Length + 1)..]))
                        .ToList();
    }

    /// <summary>
    /// A mod Proteus made — for the game's own gear, by <see cref="RefitModService"/> — that carries nothing now: no
    /// groups, no files, no metadata. Deleting it changes nothing the game draws. A mod with anything left in it — a
    /// size saved by hand, an edit — is never empty, and stays.
    /// </summary>
    internal static bool IsEmptyRefitMod(string root)
    {
        try
        {
            if (!PenumbraModMeta.HasReadableManifest(root)) return false;
            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, PenumbraModMeta.MetaFile))))
                if (!doc.RootElement.TryGetProperty("Author", out var author)
                    || !string.Equals(author.GetString(), "Proteus", StringComparison.Ordinal))
                    return false;

            if (PenumbraModMeta.TryReadGroups(root) is not { } groups || groups.Count > 0) return false;
            // A mod made fresh has no default data at all, which reads as unknown; the manifest was readable, so that
            // is empty (as RefitModService.AddGameModel reads it).
            if (PenumbraModMeta.TryReadDefaultData(root) is not { } data) return true;
            return data.Files.Count == 0 && data.Manipulations.Count == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
