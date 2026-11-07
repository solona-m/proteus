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
        MatcapPlan.IsSkin(part) ? Strings.Matcap.SkinTip : Strings.Matcap.NotHornTip;

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

    /// <summary>
    /// Why Apply can't be pressed, or null when it can. Ticked parts that all have a matcap change it (in the record, same
    /// kind; no material room needed); none having one makes a new one (room needed); a mix is refused.
    /// </summary>
    public static string? ApplyDisabledReason(bool penumbra, IReadOnlyList<ModelPart> ticked, MatcapChoice? choice, int materialRoom,
                                              MatcapRecord record)
    {
        var s = Strings.Matcap;
        if (!penumbra) return s.NoPenumbra;
        if (ticked.Count == 0) return s.NeedParts;
        if (choice == null) return s.NeedMatcap;
        switch (MatcapPlan.KindOfApply(ticked))
        {
            case MatcapPlan.ApplyKind.Mixed:
                return s.AlreadyMatcap;
            case MatcapPlan.ApplyKind.Change:
                foreach (var name in MatcapPlan.ChangeNames(ticked))
                {
                    var entry = record.Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return s.NoEntry;
                    if (entry.Source.IsLibrary != choice.IsLibrary) return s.ChangeKindTip;
                }
                return null;
            default:
                return materialRoom <= 0 ? string.Format(s.MaterialCapFmt, SecondSkinWriter.MaxMaterials) : null;
        }
    }

    /// <summary>
    /// What an Apply that changes matcaps reaches beyond the ticks, shown before it is pressed: the matcaps (in every file
    /// of the mod using them, refits included); how many of this model's parts on them are not ticked; and the other model
    /// files Proteus recorded on them, as examples (carried-in files such as refits are not scanned for).
    /// </summary>
    public static List<string> ChangesLines(IReadOnlyList<ModelPart> ticked, IReadOnlyList<ModelPart> allParts, MatcapRecord record,
                                            string modelFile)
    {
        var s = Strings.Matcap;
        var names = MatcapPlan.ChangeNames(ticked);
        if (names.Count == 0) return [];
        var lines = new List<string> { string.Format(s.ChangesFmt, string.Join(", ", names.Select(MatcapWriter.StemOf))) };
        var tickedLabels = ticked.Select(p => p.Label).ToHashSet(StringComparer.Ordinal);
        int unticked = allParts.Count(p => p.Island < 0 && !tickedLabels.Contains(p.Label)
                                           && names.Contains(p.Material, StringComparer.OrdinalIgnoreCase));
        if (unticked > 0) lines.Add(string.Format(s.ChangesUntickedFmt, unticked));
        var files = record.Materials.Where(m => names.Contains(m.Name, StringComparer.OrdinalIgnoreCase))
                          .SelectMany(m => m.Meshes).Select(x => x.Model)
                          .Where(f => !PenumbraModMeta.SameFile(f, modelFile))
                          .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count > 0) lines.Add(string.Format(s.ChangesFilesFmt, string.Join(", ", files)));
        return lines;
    }

    /// <summary>The empty-library note, naming the folder to put images in.</summary>
    public static string NoImages(string? folder) => string.Format(Strings.Matcap.NoImagesFmt, folder ?? "Penumbra/Proteus/Matcaps");

    /// <summary>
    /// The Proteus source's status line, or null for none: the failure while failed, nothing once every matcap is on
    /// disk (so a complete cache never flickers a progress line), and "n of total" while some are still missing.
    /// </summary>
    public static string? PackLine(MatcapPackStatus st, int available)
    {
        if (st.State == MatcapPackState.Failed) return Strings.Matcap.PackFailed;
        if (available >= st.Total || st.State == MatcapPackState.Ready) return null;
        return string.Format(Strings.Matcap.PackDownloadingFmt, Math.Max(available, st.Have), st.Total);
    }

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
}
