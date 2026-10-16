using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Proteus.Services;

/// <summary>
/// The other piece of an outfit a garment is kept outside when it is refitted (see
/// <see cref="BodyRetarget.KeepOutfitOrder"/>): the chest or legs piece its author ships in the same option — the sibling
/// a jacket worn as gloves is sized by (<see cref="SiblingPiece"/>) — as its author made it, and as it is drawn on the
/// new body now.
/// <para/>
/// Only a piece already on the body the garment goes onto, and read as made on the same size the garment was: the
/// author's relation between the two is measured on one body and kept on the other. A bra still on the old body read
/// against the new one stands proud of a smaller chest, and would lift a jacket by the whole difference between the
/// bodies — so anything not known to be there is no partner at all, said in the log.
/// </summary>
internal static class OutfitPartners
{
    /// <param name="Slot">The slot the piece is worn in, "_top" or "_dwn": what the record keeps it by.</param>
    /// <param name="AuthoredRel">The author's file of the piece, in the mod.</param>
    /// <param name="RefittedRel">The file drawn on the new body now, in the mod: its refit, or the author's own.</param>
    internal sealed record Partner(string Slot, string AuthoredRel, string RefittedRel);

    private static string PieceName(string slot) => slot == "_top" ? "chest" : "legs";

    private static bool SameSource(AutoRefitDecisions.Pick? a, AutoRefitDecisions.Pick b)
        => a != null && string.Equals(a.Dir, b.Dir, StringComparison.OrdinalIgnoreCase)
           && RefitCore.Normal(a.Option.Rel) == RefitCore.Normal(b.Option.Rel);

    /// <summary>
    /// Whether a refit's record says it is on <paramref name="targetRel"/> and was made from <paramref name="source"/>:
    /// what it records must agree, and a refit recording neither (made before they were kept) says nothing and is no
    /// partner.
    /// </summary>
    private static bool OnTarget(BodyRetargetWriter.Entry e, string slot, AutoRefitDecisions.Pick source, string targetRel,
                                 Action<string> note, string what)
    {
        if (e.Targets == null || !e.Targets.TryGetValue(slot, out string? onto) || e.SourceDir == null || e.SourceRel == null)
        {
            note($"its {PieceName(slot)} piece's refit {what} records no size it was made on or for, so it was not "
               + "kept outside it");
            return false;
        }
        if (RefitCore.Normal(onto) != RefitCore.Normal(targetRel))
        {
            note($"its {PieceName(slot)} piece's refit {what} is for another size, so it was not kept outside it");
            return false;
        }
        if (!string.Equals(e.SourceDir, source.Dir, StringComparison.OrdinalIgnoreCase)
            || RefitCore.Normal(e.SourceRel) != RefitCore.Normal(source.Option.Rel))
        {
            note($"its {PieceName(slot)} piece's refit {what} was made from another size than it was, so it was not kept "
               + "outside it");
            return false;
        }
        return true;
    }

    /// <summary>
    /// "Every size for other bodies": the sibling's refit in the same option, made earlier in the run — the chest and
    /// legs pieces are sized before the shoes and gloves. The option is the size: the size set names one per size of a
    /// body and records no more (no <see cref="BodyRetargetWriter.Entry.Targets"/>), and its chest is read once per run
    /// for its own sizes and the glove's alike. An entry that does record a size or a reading must agree. Null, noted,
    /// when there is none, or it disagrees.
    /// </summary>
    /// <param name="option">The option the garment's size goes into, which its sibling's went into too.</param>
    /// <param name="source">What the garment was read as made on.</param>
    /// <param name="targetRel">The target body's file of <paramref name="slot"/> the garment goes onto.</param>
    internal static Partner? ForSizeSet(BodyRetargetWriter.Record? record, string option, SiblingPiece.Candidate sibling,
                                        string slot, AutoRefitDecisions.Pick source, string targetRel, Action<string> note)
    {
        var entry = record?.Options.FirstOrDefault(e => string.Equals(e.Name, option, StringComparison.OrdinalIgnoreCase)
                                                        && string.Equals(e.GamePath, sibling.GamePath,
                                                                         StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            note($"\"{option}\": no refit of its {PieceName(slot)} piece there, so it was not kept outside it");
            return null;
        }
        bool recordsNothing = entry.Targets == null && entry.SourceDir == null && entry.SourceRel == null;
        return recordsNothing || OnTarget(entry, slot, source, targetRel, note, $"in \"{option}\"")
            ? new Partner(slot, sibling.File, entry.File)
            : null;
    }

    /// <summary>
    /// The automatic refit: the sibling as it is drawn now — what its own refit in this batch came to
    /// (<paramref name="outcome"/>), or, when it was not refitted in it, a refit of ours it was already wearing at the
    /// walk, by that refit's record. Only when the piece worn at the walk is the sibling itself — the author's file, or
    /// a refit at its game path in the same mod. Null, noted, otherwise.
    /// </summary>
    /// <param name="modRoot">The garment's mod.</param>
    /// <param name="besideRoot">The mod the piece worn in <paramref name="slot"/> at the walk came from; null for none.</param>
    /// <param name="besideGamePath">Its game path.</param>
    /// <param name="besideRel">Its file in that mod.</param>
    /// <param name="outcome">What its own refit came to in this batch; null when it was not refitted in it.</param>
    /// <param name="record">The mod's record.</param>
    internal static Partner? ForAutoRefit(string modRoot, SiblingPiece.Candidate sibling, string slot,
                                          AutoRefitDecisions.Pick source, string targetRel, string? besideRoot,
                                          string? besideGamePath, string? besideRel, PieceOutcome? outcome,
                                          BodyRetargetWriter.Record? record, Action<string> note)
    {
        string piece = PieceName(slot);
        if (besideRoot == null || besideRel == null
            || !string.Equals(Path.GetFullPath(besideRoot).TrimEnd('\\', '/'), Path.GetFullPath(modRoot).TrimEnd('\\', '/'),
                              StringComparison.OrdinalIgnoreCase)
            || !string.Equals(besideGamePath, sibling.GamePath, StringComparison.OrdinalIgnoreCase))
        {
            note($"its {piece} piece is not worn with it, so it was not kept outside one");
            return null;
        }
        bool authors = RefitCore.Normal(besideRel) == RefitCore.Normal(sibling.File);
        bool ours = RefitCore.Normal(besideRel).StartsWith(RefitCore.Normal(BodyRetargetWriter.Subfolder) + "/",
                                                            StringComparison.Ordinal);
        if (!authors && !ours)
        {
            note($"the {piece} piece worn with it is another option's, so it was not kept outside it");
            return null;
        }

        if (outcome != null)
        {
            if (!outcome.OnTarget || outcome.File == null)
            {
                note($"its {piece} piece was not refitted to the size chosen, so it was not kept outside it");
                return null;
            }
            if (!SameSource(outcome.Source, source))
            {
                note($"its {piece} piece was read as made on another size, so it was not kept outside it");
                return null;
            }
            return new Partner(slot, sibling.File, outcome.File);
        }

        if (ours)
        {
            var entry = record?.Options.FirstOrDefault(e => RefitCore.Normal(e.File) == RefitCore.Normal(besideRel)
                                                            && string.Equals(e.GamePath, besideGamePath,
                                                                             StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                note($"its {piece} piece's refit has no record, so it was not kept outside it");
                return null;
            }
            return OnTarget(entry, slot, source, targetRel, note, $"\"{entry.Name}\"")
                ? new Partner(slot, sibling.File, entry.File)
                : null;
        }
        note($"its {piece} piece is not on the size chosen yet, so it was not kept outside it; once it is, use "
           + "\"Refit what I'm wearing now\"");
        return null;
    }

    /// <summary>The partner's two models, read from the mod — or null, noted, when either cannot be.</summary>
    internal static BodyRetarget.Outfit? Load(string modRoot, Partner partner, Action<string> note)
    {
        if (SiblingPiece.TryOpen(modRoot, partner.AuthoredRel, note) is not { } authored) return null;
        if (RefitCore.Normal(partner.RefittedRel) == RefitCore.Normal(partner.AuthoredRel))
            return new BodyRetarget.Outfit(authored.Model, authored.Model);
        return SiblingPiece.TryOpen(modRoot, partner.RefittedRel, note) is { } refitted
            ? new BodyRetarget.Outfit(authored.Model, refitted.Model)
            : null;
    }

    /// <summary>What a refit's record keeps of its partner (see <see cref="BodyRetargetWriter.Entry.Layers"/>): by
    /// slot, the refitted file and the start of its content's hash.</summary>
    internal static Dictionary<string, string> LayersOf(string modRoot, Partner partner)
    {
        string hash;
        try
        {
            var bytes = File.ReadAllBytes(BodySizeCatalog.ResolveCaseInsensitive(modRoot, partner.RefittedRel));
            hash = Convert.ToHexString(SHA256.HashData(bytes))[..8].ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hash = "-";
        }
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [partner.Slot] = RefitCore.Normal(partner.RefittedRel) + "|" + hash,
        };
    }

    /// <summary>
    /// Whether a refit made before was kept outside the partner it would be kept outside now. With no partner now it is
    /// as good as any — worn without its bra, a jacket is not remade, and then remade again when the bra goes back on.
    /// A refit recording none was made before this was kept: put back as it is, unless the player asked for what they
    /// are wearing now to be made again.
    /// </summary>
    internal static bool SameLayers(IReadOnlyDictionary<string, string>? recorded, IReadOnlyDictionary<string, string>? now,
                                    bool forced)
    {
        if (now == null || now.Count == 0) return true;
        if (recorded == null) return !forced;
        return recorded.Count == now.Count
               && now.All(kv => recorded.TryGetValue(kv.Key, out string? was)
                                && string.Equals(was, kv.Value, StringComparison.OrdinalIgnoreCase));
    }
}
