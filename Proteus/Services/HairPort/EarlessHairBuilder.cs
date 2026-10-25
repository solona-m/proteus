using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CheapLoc;

namespace Proteus.Services;

/// <summary>
/// Works out how one hairstyle of a mod is drawn without its ears, ready for <see cref="EarlessHairWriter.Save"/>: for
/// every place in the mod publishing it from an author's file with ear fur, an earless copy and the swap of that redirect
/// to it; for a copy of ours out of date, the copy made again. Reads the mod, writes nothing.
/// </summary>
internal static class EarlessHairBuilder
{
    /// <summary>One redirect to point at an earless copy (or a copy to make again), in one place.</summary>
    /// <param name="From">What the redirect names now: the author's file, or for a remake the copy itself.</param>
    /// <param name="To">The copy it names afterwards.</param>
    /// <param name="Model">The copy's bytes; empty when the copy is there and current already (another place draws it),
    /// or for a redirect pointed back at the author's own file.</param>
    /// <param name="Remake">A copy of ours made again — its redirect already names it.</param>
    /// <param name="TrimRefusal">Why the copy keeps its lower LODs (see <see cref="EarlessHair.Result.TrimRefusal"/>).</param>
    public sealed record SwapWrite(HairPortPlan.Owner Into, string GamePath, string AuthorFile, string AuthorSha8,
                                   string From, string To, byte[] Model, bool Remake, string TrimRefusal = "");

    /// <param name="Current">Places already drawn from an up-to-date copy.</param>
    /// <param name="Refusal">Why nothing is written; "" when nothing needs to be, or the writes are the answer.</param>
    /// <param name="Unreadable">Refused because a file could not be read (held open, mid-update): no verdict — the next
    /// look may read it.</param>
    public sealed record Plan(IReadOnlyList<SwapWrite> Writes, int Current, string Refusal = "", bool Unreadable = false);

    public static Plan Build(string modRoot, string gamePath)
    {
        if (PenumbraModMeta.IsLegacyFolder(modRoot))
            return new Plan([], 0, new PenumbraModMeta.LegacyFolderException(Path.GetFileName(modRoot)).Message);
        if (HairPortPlan.ReadOwners(modRoot) is not { } owners)
            return new Plan([], 0, HairPortWriter.Unreadable, Unreadable: true);
        var record = EarlessHairRecord.Read(modRoot);
        if (record.Unreadable) return new Plan([], 0, EarlessHairRecord.UnreadableMessage, Unreadable: true);

        // Every place that publishes the hairstyle — a port of ours aside, which is another race's bend, never eared.
        var serving = owners.Where(o => HairPortPlan.FileFor(o, gamePath) is { } rel && !HairPortPlan.IsPortFile(rel)).ToList();
        if (serving.FirstOrDefault(o => o.IsCombining) is { } combining)
            return new Plan([], 0, HairPortWriter.Combining(combining.Group));

        var exists = EarlessHairPlan.Exists(modRoot);
        var made = new Dictionary<string, (EarlessHair.Result? Result, string Sha8)>(StringComparer.OrdinalIgnoreCase);
        var writes = new List<SwapWrite>();
        int current = 0;
        foreach (var owner in serving)
        {
            string rel = HairPortRecord.Rel(HairPortPlan.FileFor(owner, gamePath)!);
            // A path climbing out of the mod is never read, copied or written for.
            if (!EarlessHairPlan.StaysInside(rel)) continue;
            bool copy = EarlessHairPlan.IsEarlessFile(rel);
            // An option naming a model it does not ship draws nothing there: nothing to take ears off.
            if (!copy && !exists(rel)) continue;
            string? author = EarlessHairPlan.AuthorFileFor(record, owner, gamePath, rel, exists);
            if (author == null || !exists(author)) return new Plan([], 0, AuthorMissing(author ?? EarlessHairPlan.AuthorOfCopy(rel)));

            if (!made.TryGetValue(author, out var m))
            {
                if (HairPortBuilder.ReadMod(modRoot, author) is not { } bytes)
                    return new Plan([], 0, ProteusLoc.Localize("Parts.Retarget.Bake.Unreadable", "the model could not be read"),
                                    Unreadable: true);
                var result = EarlessHair.Make(bytes, out var why);
                if (result == null && why.Length > 0) return new Plan([], 0, why);
                made[author] = m = (result, HairPortPlan.Sha8(bytes));
            }

            if (!copy)
            {
                // The author's own file, without ear fur to take out: nothing to do there.
                if (m.Result == null) continue;
                string to = EarlessHairPlan.CopyFile(author);
                // Another place already draws an up-to-date copy of the same file: point this one at it too, without
                // writing the copy over — which would throw away a hat fit made of it since.
                bool shared = exists(to) && record.Swaps.Any(e => e.File.Equals(to, StringComparison.OrdinalIgnoreCase)
                    && e.EarlessVersion >= EarlessHair.Version && string.Equals(e.AuthorSha8, m.Sha8, StringComparison.OrdinalIgnoreCase));
                // A copy the user edited since is theirs: it is not spread to another place either.
                if (exists(to) && HairPortWriter.EditedBy(modRoot, to) is { } editedBy)
                    return new Plan([], 0, ModelBackupOrder.BlockedMessage(editedBy));
                writes.Add(new SwapWrite(owner, gamePath, author, m.Sha8, rel, to, shared ? [] : m.Result.Model,
                                         Remake: false, shared ? "" : m.Result.TrimRefusal));
                continue;
            }

            // A copy of ours: current while it is there and made by this build from the author's file as it is now.
            var entry = record.Find(owner, gamePath, rel);
            bool stale = entry == null || entry.EarlessVersion < EarlessHair.Version || !exists(rel)
                         || !string.Equals(entry.AuthorSha8, m.Sha8, StringComparison.OrdinalIgnoreCase);
            if (!stale) { current++; continue; }
            if (HairPortWriter.EditedBy(modRoot, rel) is { } edited)
                return new Plan([], 0, ModelBackupOrder.BlockedMessage(edited));
            // The author took the ears out since: their own file is earless now, and the redirect goes back to it.
            writes.Add(m.Result == null
                ? new SwapWrite(owner, gamePath, author, m.Sha8, rel, author, [], Remake: false)
                : new SwapWrite(owner, gamePath, author, m.Sha8, rel, rel, m.Result.Model, Remake: true, m.Result.TrimRefusal));
        }
        return new Plan(writes, current);
    }

    internal static string AuthorMissing(string rel)
        => string.Format(ProteusLoc.Localize("HairPort.Earless.Refused.AuthorMissing.Fmt",
            "the author's file {0} is missing, so the earless copy was kept"), rel);
}
