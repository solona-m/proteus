using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Proteus.Services.Matcap;

/// <summary>
/// Where the game reads an ear-slot model's material — Au Ra horns and Viera ears, <c>chara/human/cNNNN/obj/zear/zNNNN</c>.
/// Penumbra.GameData's rule (<c>GamePaths.Mtrl.Customization</c>, its default branch, which ears take): the folder is the
/// MODEL's race and id, the name is used as written, with no variant folder (<c>CustomizationSwap.CreateMtrl</c> passes
/// <c>Variant.None</c> for ears) and no race substitution — unlike hair (<see cref="HairMaterialPath"/>). Ported here
/// because Proteus does not reference that assembly.
/// <para/>
/// Until the in-game pin confirms the folder, the <c>material/v0001</c> form is published too, and where a name's race or
/// id disagrees with its folder, the name re-derived from the folder as well: the names are Proteus's own, so the extra
/// redirect keys cost nothing and draw nothing anywhere else.
/// </summary>
internal static partial class ZearMaterialPath
{
    /// <summary>The race code and ear id of an ear-slot model path, or null for any other path.</summary>
    public static (ushort Race, int Id)? Parse(string modelGamePath)
    {
        var m = ModelRe().Match(modelGamePath.Replace('\\', '/'));
        return m.Success ? (ushort.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>The ear models the label reads as horns: Au Ra's (13, 14). Viera's (17, 18) read as ears.</summary>
    public static bool IsHorn(ushort race) => race / 100 is 13 or 14;

    /// <summary>The folder Penumbra's rule names for a model of <paramref name="race"/> and ear <paramref name="id"/>.</summary>
    public static string Folder(ushort race, int id) => $"chara/human/c{race:D4}/obj/zear/z{id:D4}/material";

    /// <summary>
    /// Every game path to publish a material named <paramref name="name"/> at, for the model at
    /// <paramref name="modelGamePath"/>: the rule's path first. An absolute name is published exactly as written.
    /// </summary>
    public static List<string> GamePaths(string modelGamePath, string name)
    {
        var paths = new List<string>();
        if (!name.StartsWith('/'))
        {
            paths.Add(name.ToLowerInvariant());
            return paths;
        }
        if (Parse(modelGamePath) is not { } model) return paths;

        void Add(string n)
        {
            foreach (var p in new[] { $"{Folder(model.Race, model.Id)}{n}", $"{Folder(model.Race, model.Id)}/v0001{n}" })
                if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
        }
        Add(name);
        var m = NameRe().Match(name);
        if (m.Success && (ushort.Parse(m.Groups[1].Value) != model.Race || int.Parse(m.Groups[2].Value) != model.Id))
            Add($"/mt_c{model.Race:D4}z{model.Id:D4}{m.Groups[3].Value}");
        return paths;
    }

    [GeneratedRegex(@"(?:^|/)chara/human/c(\d{4})/obj/zear/z(\d{4})/model/[^/]+\.mdl$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelRe();

    [GeneratedRegex(@"^/mt_c(\d{4})z(\d{4})(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NameRe();
}
