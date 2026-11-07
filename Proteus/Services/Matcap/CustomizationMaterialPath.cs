using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Proteus.Services.Matcap;

/// <summary>
/// Where the game reads a face or ear-slot model's material — Au Ra horns are part of the FACE model
/// (<c>chara/human/cNNNN/obj/face/fNNNN</c>, submeshes tagged <c>atr_hrn</c>); Viera ears are ear-slot models
/// (<c>…/obj/zear/zNNNN</c>). Penumbra.GameData's rule (<c>GamePaths.Mtrl.Customization</c>, its default branch, which faces
/// and ears take): the folder is the MODEL's race and id, the name is used as written, with no variant folder and no race
/// substitution — unlike hair (<see cref="HairMaterialPath"/>). Ported here because Proteus does not reference that assembly.
/// <para/>
/// Pinned against the game's own models (<c>CustomizationMaterialPathLocalTests</c>: every material each names sits at
/// exactly this path, flat). Where a name's race or id disagrees with its folder, the name re-derived from the folder is
/// published as well: the names are Proteus's own, so the extra redirect key costs nothing and draws nothing elsewhere.
/// </summary>
internal static partial class CustomizationMaterialPath
{
    public enum Slot { Face, Ear }

    /// <summary>The slot, race code and id of a face or ear-slot model path, or null for any other path.</summary>
    public static (Slot Slot, ushort Race, int Id)? Parse(string modelGamePath)
    {
        var m = ModelRe().Match(modelGamePath.Replace('\\', '/'));
        if (!m.Success) return null;
        var slot = m.Groups[2].Value.Equals("face", StringComparison.OrdinalIgnoreCase) ? Slot.Face : Slot.Ear;
        return (slot, ushort.Parse(m.Groups[1].Value), int.Parse(m.Groups[4].Value));
    }

    /// <summary>The ear models the label reads as horns: Au Ra's (13, 14). Viera's (17, 18) read as ears.</summary>
    public static bool IsHorn(ushort race) => race / 100 is 13 or 14;

    private static string Abbrev(Slot s) => s == Slot.Face ? "f" : "z";
    private static string Folder(Slot s) => s == Slot.Face ? "face" : "zear";

    /// <summary>The folder Penumbra's rule names for a model of this slot, race and id.</summary>
    public static string Folder(Slot slot, ushort race, int id) =>
        $"chara/human/c{race:D4}/obj/{Folder(slot)}/{Abbrev(slot)}{id:D4}/material";

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
            var p = $"{Folder(model.Slot, model.Race, model.Id)}{n}";
            if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
        }
        Add(name);
        var m = NameRe().Match(name);
        if (m.Success && string.Equals(m.Groups[2].Value, Abbrev(model.Slot), StringComparison.OrdinalIgnoreCase)
            && (ushort.Parse(m.Groups[1].Value) != model.Race || int.Parse(m.Groups[3].Value) != model.Id))
            Add($"/mt_c{model.Race:D4}{Abbrev(model.Slot)}{model.Id:D4}{m.Groups[4].Value}");
        return paths;
    }

    [GeneratedRegex(@"(?:^|/)chara/human/c(\d{4})/obj/(face|zear)/(f|z)(\d{4})/model/[^/]+\.mdl$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelRe();

    [GeneratedRegex(@"^/mt_c(\d{4})([fz])(\d{4})(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NameRe();
}
