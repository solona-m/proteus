using System;
using System.Text.RegularExpressions;

namespace Proteus.Services;

/// <summary>
/// Which material file the game loads for a hair model's material, by the wearer's race — the rule Penumbra resolves
/// hair materials with (Penumbra.GameData <c>MaterialHandling.GetGameGenderRace</c> and <c>GamePaths.Mtrl.Hair</c>,
/// ported here because Proteus does not reference that assembly).
/// <para/>
/// A hair model names its materials short — <c>/mt_c0801h0130_hir_a.mtrl</c> — and the game builds the path from the
/// MODEL's race and hairstyle, not from the name: most hairstyles above 100 share one material per sex, Midlander's,
/// whatever the name says. So a ported model's materials may land on a file the source race never reads.
/// </summary>
internal static partial class HairMaterialPath
{
    /// <summary>The race whose hair material folder the game reads for a <paramref name="wearer"/> wearing this
    /// hairstyle, as a code (201 for c0201).</summary>
    public static ushort Race(ushort wearer, int hairId)
    {
        int n = wearer / 100;
        // Hrothgar share nothing.
        if (n is 15 or 16) return wearer;
        bool female = n % 2 == 0;
        ushort midlander = female ? (ushort)201 : (ushort)101;
        // 101–115 are the Miqo'te's own styles, shared by everyone else.
        if (hairId is >= 101 and <= 115) return n is 7 or 8 ? wearer : midlander;
        if (hairId is >= 116 and <= 200) return midlander;
        return wearer;
    }

    /// <summary>
    /// The game path a model of <paramref name="modelRace"/>'s hairstyle <paramref name="hairId"/> loads for the
    /// material it names <paramref name="name"/>. Null for a name the rule does not cover (an absolute path, a name
    /// too short to carry a race), which the game uses as it stands.
    /// </summary>
    public static string? GamePath(ushort modelRace, int hairId, string name)
    {
        if (!name.StartsWith("/mt_c", StringComparison.Ordinal) || name.Length <= 9) return null;
        ushort race = Race(modelRace, hairId);
        string folder = $"chara/human/c{race:D4}/obj/hair/h{hairId:D4}/material/v0001";
        return race == modelRace ? $"{folder}{name}" : $"{folder}/mt_c{race:D4}{name[9..]}";
    }

    /// <summary>A short hair material name given another race's code: <c>/mt_c0801h0130_hir_a.mtrl</c> to c0201.
    /// Unchanged for any other name.</summary>
    public static string RenameRace(string name, ushort race)
        => ShortName().IsMatch(name) ? $"/mt_c{race:D4}{name[9..]}" : name;

    /// <summary>
    /// Whether another playable race of <paramref name="race"/>'s sex reads the same material folder for this hairstyle
    /// — every playable race, whether or not the game lets it choose the style, so the answer never moves with a patch.
    /// </summary>
    public static bool IsShared(ushort race, int hairId)
    {
        ushort folder = Race(race, hairId);
        foreach (var other in HairPortPlan.SameGenderRaces(race))
            if (Race(other, hairId) == folder) return true;
        return false;
    }

    /// <summary>
    /// The port's own name for a short material name: <c>/mt_c0201h0168_hir_b.mtrl</c> to c0101 is
    /// <c>/mt_c0101h0168_hir_b_port0101.mtrl</c>. The game keeps everything after the race code (Penumbra.GameData
    /// <c>GamePaths.Mtrl.Hair</c>; Penumbra's hair Item Swap gives a swap its own file in a shared folder the same way),
    /// so it reads a file in the shared folder that no game model names. Unchanged for a name that already carries the
    /// marker, and for any name that is not short.
    /// </summary>
    public static string OwnName(string name, ushort race)
    {
        if (!ShortName().IsMatch(name) || !name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)) return name;
        var renamed = RenameRace(name, race);
        string marker = $"{PortMarker}{race:D4}.mtrl";
        if (renamed.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) return renamed;
        return renamed[..^".mtrl".Length] + marker;
    }

    /// <summary>
    /// The name a port of <paramref name="from"/>'s hairstyle <paramref name="hairId"/> gives a material for
    /// <paramref name="to"/>: the target's race code — and, across the sexes into a folder other races of that sex also
    /// read (<see cref="IsShared"/>), the port's own name (<see cref="OwnName"/>), so redirecting it dresses nobody else.
    /// One rule for the model's rename, its redirects and its sharers, so they never disagree.
    /// </summary>
    public static string PortName(string name, ushort from, ushort to, int hairId)
        => CrossSexRefit.CrossesSex(from, to) && IsShared(to, hairId) ? OwnName(name, to) : RenameRace(name, to);

    /// <summary>Whether a material name or game path is a port's own (<see cref="OwnName"/>): its file name ends in
    /// <c>_portNNNN.mtrl</c>.</summary>
    public static bool IsOwnName(string nameOrPath) => OwnMarker().IsMatch(nameOrPath);

    /// <summary>The race an own name was made for, or 0 for any other name.</summary>
    public static ushort OwnRace(string nameOrPath)
        => OwnMarker().Match(nameOrPath) is { Success: true } m ? ushort.Parse(m.Groups[1].Value) : (ushort)0;

    /// <summary>
    /// Whether <paramref name="gamePath"/> is a game-named material in the folder <paramref name="race"/> reads for
    /// hairstyle <paramref name="hairId"/> — a file that race's own model of the style may load. A port's own name never
    /// is: no game model names it.
    /// </summary>
    public static bool ReadBy(ushort race, int hairId, string gamePath)
    {
        if (IsOwnName(gamePath)) return false;
        string folder = $"chara/human/c{Race(race, hairId):D4}/obj/hair/h{hairId:D4}/material/";
        return gamePath.Replace('\\', '/').StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    private const string PortMarker = "_port";

    [GeneratedRegex(@"^/mt_c\d{4}")]
    private static partial Regex ShortName();

    [GeneratedRegex(@"_port(\d{4})\.mtrl$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnMarker();
}
