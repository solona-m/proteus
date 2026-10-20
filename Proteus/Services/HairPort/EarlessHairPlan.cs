using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// The decisions of an earless Miqo'te hairstyle that need no model: where its copy goes, whose file a copy was made
/// from, and which of the mod's own options already draw it without ears. Read by the earless builder and writer, and by
/// <see cref="HairPortBuilder"/>, which ports from the author's file and never from a copy.
/// </summary>
internal static class EarlessHairPlan
{
    /// <summary>The folder every earless copy is written under, inside the mod. Proteus's own, as
    /// <see cref="HairPortPlan.Folder"/> is: what nothing names there is deleted.</summary>
    public const string Folder = "Proteus Earless";

    /// <summary>Whether a mod-relative file is one of the earless copies Proteus wrote — never a source for anything.</summary>
    public static bool IsEarlessFile(string rel)
        => HairPortRecord.Rel(rel).StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The copy of an author's file: the same path under <see cref="Folder"/>, so options naming one file share
    /// one copy, and the author's file can be read back from the copy's path.</summary>
    public static string CopyFile(string authorRel) => $"{Folder}/{HairPortRecord.Rel(authorRel)}";

    /// <summary>
    /// The author's file behind a redirect of <paramref name="gamePath"/> to <paramref name="rel"/>: <paramref name="rel"/>
    /// itself unless it is an earless copy; for a copy, what the record says it was made from, else the copy's own path
    /// without <see cref="Folder"/> when that file is there. Null for a copy whose author's file is gone.
    /// </summary>
    public static string? AuthorFileFor(EarlessHairRecord record, HairPortPlan.Owner owner, string gamePath, string rel,
                                        Func<string, bool> fileExists)
    {
        if (!IsEarlessFile(rel)) return rel;
        if (record.Find(owner, gamePath, rel) is { } entry) return entry.AuthorFile;
        var derived = HairPortRecord.Rel(rel)[(Folder.Length + 1)..];
        return derived.Length > 0 && fileExists(derived) ? derived : null;
    }

    /// <summary>Whether a mod-relative file is there.</summary>
    public static Func<string, bool> Exists(string modRoot)
        => rel => File.Exists(Path.Combine(modRoot, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));

    /// <summary>
    /// The mod's own earless versions of the worn hairstyle, as "Group / Option", up to three in manifest order: every
    /// place publishing <paramref name="gamePath"/> from an author's file — any group, any type — that draws hair but no
    /// ear fur, other than <paramref name="worn"/>, the file served now. Never a port or an earless copy of Proteus's.
    /// Read only: told to the player, never switched to — an option cannot be known to be the same variant without its ears.
    /// </summary>
    public static IReadOnlyList<string> EarlessOptionHint(IReadOnlyList<HairPortPlan.Owner> owners, string gamePath,
                                                          string worn, Func<string, byte[]?> readMod)
    {
        var hint = new List<string>();
        var verdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in owners)
        {
            if (HairPortPlan.FileFor(owner, gamePath) is not { } rel) continue;
            rel = HairPortRecord.Rel(rel);
            if (HairPortPlan.IsPortFile(rel) || IsEarlessFile(rel)
                || rel.Equals(HairPortRecord.Rel(worn), StringComparison.OrdinalIgnoreCase)) continue;
            if (!verdicts.TryGetValue(rel, out bool earless))
                verdicts[rel] = earless = readMod(rel) is { } bytes && Draws(bytes) && EarlessHair.DrawnFur(bytes) == 0;
            if (!earless) continue;
            hint.Add(HairPortWriter.Describe(owner));
            if (hint.Count == 3) break;
        }
        return hint;
    }

    /// <summary>Whether a model draws anything at all: an option of nothing ("No hair") has no ears either.</summary>
    private static bool Draws(byte[] mdl)
        => ModelPartReader.Read(mdl) is { } parts && parts.Parts.Any(p => p.Island == -1 && p.TriangleCount > 0);
}
