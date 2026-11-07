using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Localization;
using Proteus.Services;
using Proteus.Services.Matcap;

namespace Proteus.Gui;

/// <summary>
/// Every decision the Matcaps panel shows, pure: what can be ticked, why Apply is disabled, what each existing matcap's
/// state reads as, and the warnings about a library slot. The panel only draws what this says.
/// </summary>
internal static class MatcapPanelModel
{
    /// <summary>Why a part's tick box is disabled under Matcaps.</summary>
    public static string UntickableTip(ModelPart part) =>
        MatcapPlan.IsSkin(part) ? Strings.Matcap.SkinTip
        : MatcapPlan.IsOnMatcap(part) ? Strings.Matcap.AlreadyTip
        : Strings.Matcap.NotHornTip;

    /// <summary>
    /// The line after an Apply about the file the character draws for <paramref name="gamePath"/>
    /// (<paramref name="drawnFile"/>, resolved through Penumbra): null when it is one of the files written; the neutral
    /// <see cref="MatcapStrings.DrawnThroughFace"/> when the face overlay's rewrite draws it from the edited face; else the
    /// EDGE-47 warning naming the file that wins (for a refit size, with the hint to remake sizes).
    /// </summary>
    /// <param name="faceUpstream">The disk file the face rewrite reads the face from, if known.</param>
    public static string? DrawnStatus(string root, string gamePath, IReadOnlyList<string> written, string drawnFile,
                                      string modsRoot, string? faceUpstream)
    {
        if (!HatCompatService.InMods(drawnFile, modsRoot, out var modRoot, out var rel)) return null;
        if (string.Equals(System.IO.Path.GetFileName(modRoot), SidecarDiscoveryService.ManagedModDir, StringComparison.OrdinalIgnoreCase)
            && MatcapPlan.TargetOf(gamePath, out _) is { Kind: MatcapPlan.Kind.Face })
        {
            // Rebuilt from our file on the next composite — unless the rewrite reads another mod's file, which outranks ours.
            bool ours = faceUpstream == null || written.Any(w => PenumbraModMeta.SameFile(
                System.IO.Path.GetFullPath(System.IO.Path.Combine(root, w)), System.IO.Path.GetFullPath(faceUpstream)));
            if (ours) return Strings.Matcap.DrawnThroughFace;
            var shown = HatCompatService.InMods(faceUpstream!, modsRoot, out var upRoot, out var upRel)
                ? System.IO.Path.GetFileName(upRoot) + "/" + upRel : System.IO.Path.GetFileName(faceUpstream!);
            return string.Format(Strings.Matcap.DrawnElsewhereFmt, shown);
        }
        if (PenumbraModMeta.SameFile(modRoot, root) && written.Any(w => PenumbraModMeta.SameFile(w, rel))) return null;
        var line = string.Format(Strings.Matcap.DrawnElsewhereFmt, rel);
        if (PenumbraModMeta.SameFile(modRoot, root) && MatcapPlan.Regenerated(root, rel) == MatcapPlan.RefitKind) line += " " + Strings.Matcap.RemakeSizes;
        return line;
    }

    /// <summary>A file of the managed mod the face overlay's rewrite published (<c>facelr_*.mdl</c>).</summary>
    public static bool IsManagedFaceRewrite(string file) =>
        System.IO.Path.GetFileName(file).StartsWith("facelr_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why Apply can't be pressed, or null when it can.</summary>
    public static string? ApplyDisabledReason(bool penumbra, int ticked, bool hasChoice, int materialRoom)
    {
        var s = Strings.Matcap;
        if (!penumbra) return s.NoPenumbra;
        if (ticked == 0) return s.NeedParts;
        if (!hasChoice) return s.NeedMatcap;
        if (materialRoom <= 0) return string.Format(s.MaterialCapFmt, SecondSkinWriter.MaxMaterials);
        return null;
    }

    /// <summary>The empty-library note, naming the folder to put images in.</summary>
    public static string NoImages(string? folder) => string.Format(Strings.Matcap.NoImagesFmt, folder ?? "Penumbra/Proteus/Matcaps");

    /// <summary>The other matcaps of this mod on the same library slot, by material name.</summary>
    public static List<string> SlotUsers(MatcapRecord record, string texture, string? except = null) =>
        record.Materials.Where(m => m.Source.IsLibrary
                                    && string.Equals(m.Source.Library?.Texture, texture, StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(m.Name, except, StringComparison.OrdinalIgnoreCase))
                        .Select(m => m.Name).ToList();

    /// <summary>The warnings to show for a library before Apply, in order.</summary>
    public static List<string> LibraryWarnings(MatcapLibrary lib, MatcapSelection.Status status, MatcapRecord record,
                                               IReadOnlyList<string> providers)
    {
        var s = Strings.Matcap;
        var list = new List<string>();
        int users = SlotUsers(record, lib.Texture).Count;
        if (users > 0) list.Add(string.Format(s.SharedSlotFmt, users));
        if (status.Known && status.Inherited) list.Add(string.Format(s.InheritedFmt, status.Collection));
        if (status.Known && status.IsBase) list.Add(string.Format(s.BaseCollectionFmt, status.Collection));
        if (status.HeldBy != null) list.Add(string.Format(s.HeldFmt, status.HeldBy));
        var others = providers.Where(p => !string.Equals(p, lib.Mod, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0) list.Add(string.Format(s.DuplicatesFmt, string.Join(", ", others)));
        list.Add(ShareNote(lib.Name, lib.Website));
        return list;
    }

    /// <summary>The share note: anyone the mod goes to needs the library (and its link, as its terms ask).</summary>
    public static string ShareNote(string name, string website) =>
        string.Format(Strings.Matcap.ShareNoteFmt, name, string.IsNullOrWhiteSpace(website) ? "—" : website);

    /// <summary>One existing matcap's state line, or null when all is well.</summary>
    /// <param name="installed">Whether its library is installed; ignored for an image.</param>
    /// <param name="enabled">Whether its library is on in the collection; ignored for an image.</param>
    public static string? EntryState(MatcapRecord.MaterialEntry entry, MatcapWriter.State state, bool installed, bool enabled)
    {
        var s = Strings.Matcap;
        if (state == MatcapWriter.State.Superseded) return s.Superseded;
        if (state == MatcapWriter.State.Edited) return s.Edited;
        if (entry.Source.IsLibrary && entry.Source.Library is { } lib)
        {
            if (!installed) return string.Format(s.LibraryMissingFmt, lib.Name);
            if (!enabled) return string.Format(s.LibraryOffFmt, lib.Name);
        }
        return null;
    }

    /// <summary>"gem.png" or "Library — option": where an existing matcap comes from.</summary>
    public static string SourceText(MatcapRecord.SourceRef src) =>
        string.Format(Strings.Matcap.SourceFmt, src.IsLibrary && src.Library is { } l ? $"{l.Name} — {l.Option}" : src.Image ?? "?");

    /// <summary>Whether Change may be pressed for an entry with this choice: same kind only.</summary>
    public static bool CanChange(MatcapRecord.MaterialEntry entry, MatcapChoice? choice) =>
        choice != null && entry.Source.IsLibrary == choice.IsLibrary;
}
