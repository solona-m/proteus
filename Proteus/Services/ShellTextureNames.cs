using System;

namespace Proteus.Services;

/// <summary>
/// The file names a second-skin shell's normal map is published under, and the one place that reads them
/// back. A shell normal is <c>ss_{letter}_norm.tex</c>, or content-addressed as
/// <c>ss_{letter}_norm_{16 hex}.tex</c> when it carries a reinforced toe (the game caches textures by path).
/// Everything that recognises a shell normal, or derives its index or material from it, must go through here.
/// </summary>
internal static class ShellTextureNames
{
    private const int HashHexLength = 16;

    /// <summary>The content-addressed name for a shell normal whose content hash is <paramref name="hash"/>.</summary>
    public static string ContentAddressedNormal(char letter, ulong hash) => $"ss_{letter}_norm_{hash:x16}.tex";

    /// <summary>
    /// The shell stem (<c>ss_{letter}</c>) of a normal map's file name, in either form. False for anything
    /// that is not a shell normal.
    /// </summary>
    public static bool TryNormalStem(string leaf, out string stem)
    {
        stem = "";
        if (string.IsNullOrEmpty(leaf)
         || !leaf.StartsWith("ss_", StringComparison.OrdinalIgnoreCase)
         || !leaf.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            return false;

        var body = leaf[..^".tex".Length];
        if (body.EndsWith("_norm", StringComparison.OrdinalIgnoreCase))
        {
            stem = body[..^"_norm".Length];
        }
        else
        {
            int at = body.LastIndexOf("_norm_", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            var hex = body[(at + "_norm_".Length)..];
            if (hex.Length != HashHexLength) return false;
            foreach (var c in hex)
                if (!Uri.IsHexDigit(c)) return false;
            stem = body[..at];
        }

        // "ss_" and at least the one-character letter.
        if (stem.Length < "ss_".Length + 1) { stem = ""; return false; }
        return true;
    }

    /// <summary>
    /// <paramref name="path"/> with a shell normal's file name replaced by its stem, so that every revision of
    /// one shell's normal shares a key. Anything that is not a shell normal comes back unchanged.
    /// </summary>
    public static string ShellKey(string path)
    {
        // Sliced on the trimmed path, which the leaf's length was measured on.
        var p = path.Trim().Trim('"');
        var leaf = LeafOf(p);
        return TryNormalStem(leaf, out var stem) ? p[..^leaf.Length] + stem : path;
    }

    /// <summary>The shell's index texture, beside its normal: <c>…/ss_{letter}_id.tex</c>.</summary>
    public static string IndexBeside(string normalPath)
    {
        var p = normalPath.Trim().Trim('"');
        var leaf = LeafOf(p);
        return TryNormalStem(leaf, out var stem) ? p[..^leaf.Length] + stem + "_id.tex" : normalPath;
    }

    /// <summary>The material leaf a shell normal belongs to: <c>ss_{letter}.mtrl</c>.</summary>
    public static string MaterialLeaf(string normalLeaf)
        => TryNormalStem(normalLeaf, out var stem) ? stem + ".mtrl" : normalLeaf;

    /// <summary>The file name at the end of a path written with either separator.</summary>
    private static string LeafOf(string path)
    {
        var trimmed = path.Trim().Trim('"');
        int cut = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }
}
