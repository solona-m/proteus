using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Which of the game's three shared eye textures (all the iris material names) a file replaces.
/// </summary>
public enum EyeSlot
{
    Base,
    Mask,
    Norm,
}

/// <summary>
/// Reader for a plain <c>.zip</c> of loose eye textures with no manifest: the file names are the manifest,
/// and classification is conservative because a wrong guess redirects every character's eyes.
/// </summary>
public static class EyePackage
{
    public const string Extension = ".zip";

    /// <summary>Where the game keeps the shared eye maps; one set covers every character.</summary>
    public const string TextureFolder = "chara/common/texture/eye";

    /// <summary>The game path each slot replaces.</summary>
    public static string GamePathFor(EyeSlot slot) => slot switch
    {
        EyeSlot.Base => $"{TextureFolder}/eye01_base.tex",
        EyeSlot.Mask => $"{TextureFolder}/eye01_mask.tex",
        _            => $"{TextureFolder}/eye01_norm.tex",
    };

    /// <summary>Filename token → slot; the same vocabulary <see cref="OnionImportService"/> maps.</summary>
    private static readonly Dictionary<string, EyeSlot> Slots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["base"] = EyeSlot.Base, ["diffuse"] = EyeSlot.Base, ["d"] = EyeSlot.Base, ["basecolor"] = EyeSlot.Base,
        ["mask"] = EyeSlot.Mask, ["multi"] = EyeSlot.Mask, ["m"] = EyeSlot.Mask, ["s"] = EyeSlot.Mask,
        ["norm"] = EyeSlot.Norm, ["normal"] = EyeSlot.Norm, ["n"] = EyeSlot.Norm,
    };

    /// <summary>What the readers downstream can decode; the same list the Create tab browses for.</summary>
    private static readonly string[] ImageExtensions =
        [".png", ".dds", ".tex", ".jpg", ".jpeg", ".bmp", ".tga"];

    /// <summary>One file in the zip and what it was taken for.</summary>
    /// <param name="Slot">Null when the name carries no token this reader knows.</param>
    public sealed record PackFile(string Entry, EyeSlot? Slot, long Bytes)
    {
        public string Name => System.IO.Path.GetFileName(Entry);
    }

    /// <summary>A parsed pack. <paramref name="Name"/> is the pack's own folder name where it has one.</summary>
    public sealed record Contents(string Path, string Name, IReadOnlyList<PackFile> Files)
    {
        /// <summary>The files that landed on a slot, one per slot — a later duplicate is ignored rather
        /// than fighting the first.</summary>
        public IReadOnlyDictionary<EyeSlot, PackFile> BySlot
            => Files.Where(f => f.Slot != null)
                    .GroupBy(f => f.Slot!.Value)
                    .ToDictionary(g => g.Key, g => g.First());

        /// <summary>Files whose names said nothing this reader understands.</summary>
        public IReadOnlyList<PackFile> Unclassified => [.. Files.Where(f => f.Slot == null)];
    }

    /// <summary>
    /// Parse the archive. Throws <see cref="InvalidDataException"/> when it is not a readable zip or holds
    /// no images.
    /// </summary>
    public static Contents Read(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var files = new List<PackFile>();
        string? folder = null;

        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;   // directory marker
            var name = Normalize(e.FullName);

            // A traversal entry rejects the whole pack: it is corrupt or hostile.
            if (System.IO.Path.IsPathRooted(name) || name.Split('/').Any(s => s is ".." or "."))
                throw new InvalidDataException($"The archive contains an unsafe entry path: {e.FullName}");

            if (!ImageExtensions.Contains(System.IO.Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                continue;   // readmes and previews ride along in these packs; they are not a fault

            folder ??= name.Contains('/') ? name[..name.IndexOf('/')] : null;
            files.Add(new PackFile(name, SlotOf(name), e.Length));
        }

        if (files.Count == 0)
            throw new InvalidDataException("The archive holds no images, so there is nothing to import.");

        return new Contents(
            zipPath,
            string.IsNullOrWhiteSpace(folder)
                ? System.IO.Path.GetFileNameWithoutExtension(zipPath)
                : folder!,
            files);
    }

    /// <summary>The raw bytes of one entry. Re-opens the archive; call it once per file.</summary>
    public static byte[] ReadEntry(string zipPath, string entry)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var e = zip.GetEntry(entry)
            ?? zip.Entries.FirstOrDefault(x =>
                   string.Equals(Normalize(x.FullName), Normalize(entry), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"The archive no longer contains {entry}.");

        using var src = e.Open();
        using var mem = new MemoryStream(e.Length is > 0 and < int.MaxValue ? (int)e.Length : 0);
        src.CopyTo(mem);
        return mem.ToArray();
    }

    /// <summary>
    /// Whether a pack's names identify it as an eye pack: an explicit <c>_eye</c> in the name, or the game's own
    /// <c>eye0</c> prefix.
    /// </summary>
    public static bool LooksLikeEyes(Contents pack)
        => pack.Files.Any(f => f.Slot != null
                            && (f.Name.Contains("_eye", StringComparison.OrdinalIgnoreCase)
                             || f.Name.StartsWith("eye0", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The slot a file name claims, from the token after its LAST underscore — <c>Butterfly_eye_base.png</c>
    /// is a base. Null when there is no underscore or the token is not one this reader knows.
    /// </summary>
    internal static EyeSlot? SlotOf(string entry)
    {
        var leaf = System.IO.Path.GetFileNameWithoutExtension(entry);
        int at = leaf.LastIndexOf('_');
        if (at < 0 || at == leaf.Length - 1) return null;
        return Slots.TryGetValue(leaf[(at + 1)..], out var slot) ? slot : null;
    }

    private static string Normalize(string p) => p.Replace('\\', '/').TrimStart('/');
}
