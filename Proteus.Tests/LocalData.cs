using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Test data that cannot live in the repository — other authors' body mods, content packs, the game's own files —
/// found through environment variables rather than paths on one machine:
/// <list type="bullet">
/// <item><c>PROTEUS_MODS</c>: the Penumbra mod folder (body mods such as Neolithe, Bibo+, Rue+).</item>
/// <item><c>PROTEUS_MODPACKS</c>: a folder of .pmp packs (Neolithe Piercings for Proteus.pmp).</item>
/// <item><c>PROTEUS_GAME</c>: the FFXIV install folder, the one holding <c>game\sqpack</c>.</item>
/// </list>
/// A test that needs any of it declares so with <see cref="LocalDataFactAttribute"/> or
/// <see cref="LocalDataTheoryAttribute"/>, and is reported SKIPPED, naming what is missing, wherever it is not there
/// (CI among them). It never returns early and passes: a test that quietly checks nothing looks like coverage.
/// <para/>
/// A need is <c>VARIABLE:relative/path</c>, or <c>VARIABLE</c> alone for the folder itself. The same string names
/// the file in the test body through <see cref="Path"/>.
/// </summary>
internal static class LocalData
{
    internal const string Mods = "PROTEUS_MODS";
    internal const string ModPacks = "PROTEUS_MODPACKS";
    internal const string Game = "PROTEUS_GAME";

    /// <summary>The game's data, under <see cref="Game"/>: what Lumina opens.</summary>
    internal const string GameData = Game + ":game/sqpack";

    /// <summary>The sample content pack, under <see cref="ModPacks"/>.</summary>
    internal const string PiercingsPack = ModPacks + ":Neolithe Piercings for Proteus.pmp";

    /// <summary>Miette's "Shift": heels with stockings to the thigh, a heeled foot of Bibo skin, made on YAB WC.</summary>
    internal const string ShiftPack = ModPacks + ":Shift.pmp";

    /// <summary>voidblood's "Dei Gratia" v1.0.1: an e9068 top fitted on the game's body but drawn with <c>_bibo.mtrl</c>
    /// skin, its chest skin cut away under the blouse — only an upper-chest patch left, its open edge above the bust.</summary>
    internal const string DeiGratiaPack = ModPacks + ":Dei Gratia v1.0.1.pmp";

    /// <summary>Bijeon's "Frost Veil Plus+": an invisible a0002 bracelet whose whole look is an item effect, in the v3 layout.</summary>
    internal const string FrostVeilPack = ModPacks + ":Frost Veil Plus+.pmp";

    /// <summary>
    /// The uv transfer maps, which live in the repository (<c>Proteus/uvmaps</c>) but through Git LFS, so a checkout
    /// may hold only their pointers. Not a variable: found by walking up from the test binaries. <see cref="Path"/> gives
    /// the folder to hand <c>UVRemapService</c> as its plugin folder.
    /// </summary>
    internal const string UvMaps = "<uv transfer maps>";

    /// <summary>The plugin folder whose <c>uvmaps</c> hold real maps, or null.</summary>
    private static string? UvMapsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var map = System.IO.Path.Combine(dir.FullName, "Proteus", "uvmaps", "gen3_to_bibo_transfer.tif");
            if (File.Exists(map)) return IsTiff(map) ? System.IO.Path.Combine(dir.FullName, "Proteus") : null;
        }
        return null;
    }

    /// <summary>Whether a file opens as a TIFF; an LFS pointer begins "version https://…".</summary>
    private static bool IsTiff(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (s.Read(magic) < 4) return false;
            return (magic[0] == (byte)'I' && magic[1] == (byte)'I' && magic[2] == 42 && magic[3] == 0)
                || (magic[0] == (byte)'M' && magic[1] == (byte)'M' && magic[2] == 0 && magic[3] == 42);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The one entry of a .pmp whose path ends in <paramref name="suffix"/>. By ending, so folder names a pack spells in
    /// characters a zip may encode either way (Shift's "•") do not matter; throws unless exactly one entry matches.
    /// </summary>
    internal static byte[] PackEntryEndingWith(string packNeed, string suffix)
    {
        using var zip = ZipFile.OpenRead(Path(packNeed));
        var matches = zip.Entries.Where(e => e.FullName.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                                 .ToList();
        if (matches.Count != 1)
            throw new InvalidDataException($"{matches.Count} entries of {Path(packNeed)} end in {suffix}, not one");
        using var s = matches[0].Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>
    /// Where a need points. When its variable is not set the result is a relative path under a folder that does not
    /// exist, so nothing ever reads the wrong file — the attribute has skipped the test before it got here.
    /// </summary>
    internal static string Path(string need)
    {
        if (need == UvMaps) return UvMapsFolder() ?? "<uv transfer maps not here>";
        int colon = need.IndexOf(':');
        string variable = colon < 0 ? need : need[..colon];
        string relative = colon < 0 ? "" : need[(colon + 1)..].Replace('/', System.IO.Path.DirectorySeparatorChar);
        string root = Environment.GetEnvironmentVariable(variable) is { Length: > 0 } set ? set : "<" + variable + " not set>";
        return relative.Length == 0 ? root : System.IO.Path.Combine(root, relative);
    }

    /// <summary>Why these needs cannot be met here, or null when all of them can.</summary>
    internal static string? Missing(IEnumerable<string> needs)
    {
        var missing = new List<string>();
        foreach (var need in needs)
        {
            if (need == UvMaps)
            {
                if (UvMapsFolder() == null) missing.Add("the uv transfer maps are not here (Git LFS not fetched)");
                continue;
            }
            int colon = need.IndexOf(':');
            string variable = colon < 0 ? need : need[..colon];
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
                missing.Add(variable + " is not set");
            else if (!File.Exists(Path(need)) && !Directory.Exists(Path(need)))
                missing.Add(Path(need) + " does not exist");
        }
        return missing.Count == 0 ? null : "needs local data: " + string.Join("; ", missing.Distinct());
    }

    /// <summary>One entry of a .pmp (a zip), by its path inside the pack.</summary>
    internal static byte[] PackEntry(string packNeed, string entry)
    {
        using var zip = ZipFile.OpenRead(Path(packNeed));
        var e = zip.GetEntry(entry) ?? throw new FileNotFoundException($"{entry} is not in {Path(packNeed)}");
        using var s = e.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips, saying why, unless every need is met (see <see cref="LocalData"/>).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LocalDataFactAttribute : FactAttribute
{
    public LocalDataFactAttribute(params string[] needs)
    {
        if (LocalData.Missing(needs) is { } why) Skip = why;
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips, saying why, unless every need is met (see <see cref="LocalData"/>).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LocalDataTheoryAttribute : TheoryAttribute
{
    public LocalDataTheoryAttribute(params string[] needs)
    {
        if (LocalData.Missing(needs) is { } why) Skip = why;
    }
}
