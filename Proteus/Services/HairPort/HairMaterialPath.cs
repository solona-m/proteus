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

    [GeneratedRegex(@"^/mt_c\d{4}")]
    private static partial Regex ShortName();
}
