using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Proteus.Services.Matcap;

/// <summary>
/// Where the game reads a face, ear-slot or tail model's material — Au Ra horns are part of the FACE model
/// (<c>chara/human/cNNNN/obj/face/fNNNN</c>, submeshes tagged <c>atr_hrn</c>); Viera ears are ear-slot models
/// (<c>…/obj/zear/zNNNN</c>); tails are <c>…/obj/tail/tNNNN</c>. Penumbra.GameData's rule (<c>GamePaths.Mtrl.Customization</c>):
/// faces and ears take its default branch — the folder is the MODEL's race and id, the name is used as written, with no
/// variant folder and no race substitution, unlike hair (<see cref="HairMaterialPath"/>). Tails (<c>Mtrl.Tail</c>) read a
/// variant folder, <c>material/vNNNN</c>: <c>v0001</c> for every race but Hrothgar, whose tails all read <c>t0001</c>'s
/// folders <c>v0001</c>–<c>v0005</c> (one per fur pattern; the game ships exactly those five). Ported here because Proteus
/// does not reference that assembly.
/// <para/>
/// Pinned against the game's own models (<c>CustomizationMaterialPathLocalTests</c>: every material a face or ear names
/// sits at exactly this path, flat; every variant folder the game ships a tail's material in is among these). Where a name's race or id disagrees with its folder, the name re-derived from the folder is
/// published as well: the names are Proteus's own, so the extra redirect key costs nothing and draws nothing elsewhere.
/// </summary>
internal static partial class CustomizationMaterialPath
{
    public enum Slot { Face, Ear, Tail }

    /// <summary>The slot, race code and id of a face, ear-slot or tail model path, or null for any other path.</summary>
    public static (Slot Slot, ushort Race, int Id)? Parse(string modelGamePath)
    {
        var m = ModelRe().Match(modelGamePath.Replace('\\', '/'));
        if (!m.Success) return null;
        var slot = m.Groups[2].Value.ToLowerInvariant() switch { "face/f" => Slot.Face, "tail/t" => Slot.Tail, _ => Slot.Ear };
        return (slot, ushort.Parse(m.Groups[1].Value), int.Parse(m.Groups[3].Value));
    }

    /// <summary>The ear models the label reads as horns: Au Ra's (13, 14). Viera's (17, 18) read as ears.</summary>
    public static bool IsHorn(ushort race) => race / 100 is 13 or 14;

    /// <summary>Hrothgar (15, 16): every tail reads <c>t0001</c>'s material folders.</summary>
    public static bool IsHrothgar(ushort race) => race / 100 is 15 or 16;

    private static string Abbrev(Slot s) => s switch { Slot.Face => "f", Slot.Tail => "t", _ => "z" };
    private static string Folder(Slot s) => s switch { Slot.Face => "face", Slot.Tail => "tail", _ => "zear" };

    /// <summary>The <c>material</c> folder of a model of this slot, race and id — a face's or ear's whole folder; a tail
    /// reads variant folders under it (<see cref="Folders"/>).</summary>
    public static string Folder(Slot slot, ushort race, int id) =>
        $"chara/human/c{race:D4}/obj/{Folder(slot)}/{Abbrev(slot)}{id:D4}/material";

    /// <summary>Every folder Penumbra's rule names for a model of this slot, race and id: one for a face or ear; for a
    /// tail, its variant folders (see the class remarks).</summary>
    public static IEnumerable<string> Folders(Slot slot, ushort race, int id)
    {
        if (slot != Slot.Tail)
        {
            yield return Folder(slot, race, id);
            yield break;
        }
        for (int v = 1; v <= (IsHrothgar(race) ? 5 : 1); v++)
            yield return $"{Folder(slot, race, FolderId(slot, race, id))}/v{v:D4}";
    }

    /// <summary>The id whose folders the rule reads for a model of this slot and race: Hrothgar tails read t0001.</summary>
    private static int FolderId(Slot slot, ushort race, int id) => slot == Slot.Tail && IsHrothgar(race) ? 1 : id;

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
            foreach (var folder in Folders(model.Slot, model.Race, model.Id))
            {
                var p = $"{folder}{n}";
                if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
            }
        }
        Add(name);
        var id = FolderId(model.Slot, model.Race, model.Id);
        var m = NameRe().Match(name);
        if (m.Success && string.Equals(m.Groups[2].Value, Abbrev(model.Slot), StringComparison.OrdinalIgnoreCase)
            && (ushort.Parse(m.Groups[1].Value) != model.Race || int.Parse(m.Groups[3].Value) != id))
            Add($"/mt_c{model.Race:D4}{Abbrev(model.Slot)}{id:D4}{m.Groups[4].Value}");
        return paths;
    }

    [GeneratedRegex(@"(?:^|/)chara/human/c(\d{4})/obj/(face/f|zear/z|tail/t)(\d{4})/model/[^/]+\.mdl$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelRe();

    [GeneratedRegex(@"^/mt_c(\d{4})([fzt])(\d{4})(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NameRe();
}
