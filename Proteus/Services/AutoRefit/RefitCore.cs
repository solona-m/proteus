using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using Proteus.Localization;

namespace Proteus.Services;

/// <summary>
/// The parts of a Body size refit that do not depend on who is asking: the Studio panel, which a user drives a
/// dropdown at a time, and <see cref="AutoRefitWatcher"/>, which runs the same refit unattended when gear goes on.
/// <para/>
/// Static and free of UI state on purpose. Each helper here was lifted out of <c>BodyRetargetPanel</c> as it stood, so
/// the two callers cannot drift apart on what a refit IS — which group it goes into, what its option is called, when a
/// garment is baked for another race — and only differ in how its inputs are chosen.
/// </summary>
internal static class RefitCore
{
    /// <summary>
    /// The pieces the refit moves whole, from the labels marked "keep shape". An island is one piece; a marked submesh is
    /// each of its islands separately — a chain is many links, each kept, not one rigid chain — or itself when the reader
    /// found none.
    /// </summary>
    internal static List<IReadOnlyCollection<int>> ShapePieces(ModelParts garment, IReadOnlySet<string> labels)
        => ShapePieces(garment, labels, out _);

    /// <inheritdoc cref="ShapePieces(ModelParts, IReadOnlySet{string})"/>
    /// <param name="names">Each piece's label, in the same order: the island's own label, or the submesh's where it is
    /// one piece — how the report names a piece the refit let go (see <see cref="BodyRetarget.ShapeOutcome"/>).</param>
    internal static List<IReadOnlyCollection<int>> ShapePieces(ModelParts garment, IReadOnlySet<string> labels,
                                                               out List<string> names)
    {
        var pieces = new List<IReadOnlyCollection<int>>();
        names = [];
        if (labels.Count == 0) return pieces;

        var wholeSubmesh = new HashSet<(int, int)>();
        foreach (var part in garment.Parts)
            if (part.Island < 0 && labels.Contains(part.Label))
                wholeSubmesh.Add((part.Mesh, part.Submesh));

        var covered = new HashSet<(int, int)>();
        foreach (var part in garment.Parts)
        {
            if (part.Island < 0) continue;
            if (!labels.Contains(part.Label) && !wholeSubmesh.Contains((part.Mesh, part.Submesh))) continue;
            pieces.Add(part.Triangles.Distinct().ToArray());
            names.Add(part.Label);
            covered.Add((part.Mesh, part.Submesh));
        }
        foreach (var part in garment.Parts)
            if (part.Island < 0 && labels.Contains(part.Label) && !covered.Contains((part.Mesh, part.Submesh)))
            {
                pieces.Add(part.Triangles.Distinct().ToArray());
                names.Add(part.Label);
            }
        return pieces;
    }

    /// <summary>One "keep shape" piece the refit judged: its label, how far moving it whole would have sunk it (metres),
    /// and whether it was let go for that.</summary>
    internal readonly record struct KeptPiece(string Name, float Sink, bool Released, float Share = 0f);

    /// <summary>
    /// The pieces <paramref name="report"/> judged, named from <paramref name="names"/> (the list
    /// <see cref="ShapePieces(ModelParts, IReadOnlySet{string}, out List{string})"/> gave alongside the pieces). Pieces
    /// it could not judge are left out.
    /// </summary>
    internal static List<KeptPiece> KeptPieces(BodyRetarget.Report report, IReadOnlyList<string> names)
    {
        var list = new List<KeptPiece>();
        if (report.Shapes is not { } shapes) return list;
        for (int k = 0; k < shapes.Count && k < names.Count; k++)
            if (shapes[k].Judged) list.Add(new KeptPiece(names[k], shapes[k].Sink, shapes[k].Released, shapes[k].Share));
        return list;
    }

    /// <summary>
    /// For a log line: every piece the refit let go, with how far moving it whole would have sunk it — "2.1.7 (6.5 mm),
    /// …". Null when none was.
    /// </summary>
    internal static string? ReleasedLog(BodyRetarget.Report report, IReadOnlyList<string> names)
    {
        var released = KeptPieces(report, names).Where(p => p.Released).ToList();
        return released.Count == 0
            ? null
            : string.Join(", ", released.Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                                                                   $"{p.Name} ({p.Sink * 1000f:F1} mm)")));
    }

    /// <summary>The most pieces <see cref="ReleasedWarning"/> names before it counts the rest.</summary>
    internal const int ReleasedNamed = 5;

    /// <summary>
    /// For the Studio: how many kept pieces the refit let go, the deepest any would have sunk, and the first
    /// <see cref="ReleasedNamed"/> of them by name ("+N" for the rest). Null when none was.
    /// </summary>
    internal static string? ReleasedWarning(BodyRetarget.Report report, IReadOnlyList<string> names)
    {
        var released = KeptPieces(report, names).Where(p => p.Released).ToList();
        if (released.Count == 0) return null;
        string listed = string.Join(", ", released.Take(ReleasedNamed).Select(p => p.Name));
        if (released.Count > ReleasedNamed) listed += $", +{released.Count - ReleasedNamed}";
        return string.Format(Strings.Parts.RetargetShapeReleasedFmt, released.Count,
                             released.Max(p => p.Sink) * 1000f, listed);
    }

    /// <summary>
    /// The "keep shape" labels a model starts with before anyone has unticked one: its pieces that look hard
    /// (<see cref="BodyRetarget.HardPieces"/>) — rings, bands, chain links — with a row whose pieces are ALL hard
    /// ticked as the row, the way a user ticking it would leave it.
    /// </summary>
    internal static HashSet<string> DefaultKeepShape(ModelParts model)
    {
        var set = new HashSet<string>(BodyRetarget.HardPieces(model), StringComparer.Ordinal);
        foreach (var row in model.Parts.Where(p => p.Island < 0))
        {
            var pieces = model.Parts.Where(p => p.Island >= 0 && p.Mesh == row.Mesh && p.Submesh == row.Submesh).ToList();
            if (pieces.Count == 0 || !pieces.All(p => set.Contains(p.Label))) continue;
            foreach (var piece in pieces) set.Remove(piece.Label);
            set.Add(row.Label);
        }
        return set;
    }

    /// <summary>
    /// The garment without its hand or foot: every triangle touching one of <paramref name="ends"/> left out, so a reading
    /// sees only the leg or arm it reaches. The vertices themselves stay, numbered as before; parts left with nothing are
    /// dropped. See <see cref="BodyRetarget.EndVertices"/>.
    /// </summary>
    internal static ModelParts LimbView(ModelParts garment, IReadOnlySet<int> ends)
    {
        if (ends.Count == 0) return garment;
        var parts = new List<ModelPart>(garment.Parts.Count);
        foreach (var part in garment.Parts)
        {
            var tris = new List<int>(part.Triangles.Length);
            var ordinals = new List<int>(part.Ordinals.Length);
            for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
            {
                int a = part.Triangles[t], b = part.Triangles[t + 1], c = part.Triangles[t + 2];
                if (ends.Contains(a) || ends.Contains(b) || ends.Contains(c)) continue;
                tris.Add(a);
                tris.Add(b);
                tris.Add(c);
                if (t / 3 < part.Ordinals.Length) ordinals.Add(part.Ordinals[t / 3]);
            }
            if (tris.Count == 0) continue;
            parts.Add(new ModelPart
            {
                Mesh = part.Mesh,
                Submesh = part.Submesh,
                Island = part.Island,
                Label = part.Label,
                Material = part.Material,
                Triangles = [.. tris],
                Ordinals = [.. ordinals],
                AttributeMask = part.AttributeMask,
                Min = part.Min,
                Max = part.Max,
                Toggleable = part.Toggleable,
                AuthorSwitched = part.AuthorSwitched,
            });
        }
        return new ModelParts
        {
            Positions = garment.Positions,
            Normals = garment.Normals,
            MeshSpans = garment.MeshSpans,
            Wind = garment.Wind,
            HasWindChannel = garment.HasWindChannel,
            FirstColorNotWhite = garment.FirstColorNotWhite,
            Parts = parts,
            AttributeNames = garment.AttributeNames,
            Min = garment.Min,
            Max = garment.Max,
            ShatteredSubmeshes = garment.ShatteredSubmeshes,
        };
    }

    /// <summary>
    /// The IMC attribute mask a body mod gives its model in <paramref name="slot"/> under a choice of its options —
    /// which of the body's variant parts the game draws (see <see cref="BodyRetarget.UndrawnVariants"/>). Null when the
    /// mod has no say, the game's own bodies included: every part is then taken as drawn.
    /// </summary>
    /// <param name="selected">The mod's ticked options in the collection, as Penumbra reports them; null reads the
    /// mod's defaults.</param>
    internal static ushort? MaskOf(string? dir, BodySizeCatalog? mod, string slot,
                                   IReadOnlyDictionary<string, List<string>>? selected)
    {
        if (dir == null || dir == VanillaBodyCatalog.Key || mod == null) return null;
        if (BodyRetarget.ImcSlotName(slot) is not { } equipSlot) return null;
        return ImcEntrySource.MaskFor(mod.ModRoot, 0, equipSlot, selected);
    }

    /// <summary>The mod's single-choice groups, in the author's order.</summary>
    internal static List<string> SingleGroups(string modRoot) => GroupsOfType(modRoot, "Single", int.MaxValue);

    /// <summary>
    /// Penumbra keeps a multi-choice group's selection as a bitmask, so it holds at most this many options. A refit is
    /// only added to one with room left.
    /// </summary>
    internal const int MaxMultiOptions = 32;

    /// <summary>The mod's multi-choice groups with room for another option, in the author's order.</summary>
    internal static List<string> MultiGroups(string modRoot) => GroupsOfType(modRoot, "Multi", MaxMultiOptions - 1);

    private static List<string> GroupsOfType(string modRoot, string type, int maxOptions)
        => (PenumbraModMeta.TryReadGroups(modRoot) ?? [])
            .Where(g => string.Equals(PenumbraModMeta.TypeOf(g.Group), type, StringComparison.OrdinalIgnoreCase)
                        && (PenumbraModMeta.FileOptionsOf(g.Group)?.Count ?? 0) <= maxOptions)
            .Select(g => g.Name).ToList();

    /// <summary>
    /// The author's group a new size of <paramref name="gamePath"/> belongs in, or null when there is none and the
    /// refit gets a group of its own.
    /// <para/>
    /// First a single-choice group that already switches the model — the author's size group, where a new size sits
    /// beside theirs. Failing that, the group whose option the drawn model comes from, even a multi-choice one: an
    /// outfit whose pieces are ticked in one "Items" list gets the new size in that same list, beside the piece it is a
    /// size of, rather than in a group of its own (see <see cref="SelectionFor"/> for how it is then worn).
    /// </summary>
    /// <param name="modelRel">The drawn model's file in the mod, or null when unknown.</param>
    /// <param name="ownGroups">The groups a refit made itself (<see cref="BodyRetargetWriter.Record.OwnGroups"/>):
    /// never the author's size group, even though they switch the model too.</param>
    /// <param name="multiGroups">The mod's multi-choice groups with room for one more option.</param>
    internal static string? SwitchingGroup(IEnumerable<PenumbraModMeta.Redirect> redirects, string gamePath,
                                           IEnumerable<string>? ownGroups, IReadOnlyCollection<string> singleGroups,
                                           string? modelRel = null, IReadOnlyCollection<string>? multiGroups = null)
    {
        var own = new HashSet<string>(ownGroups ?? [], StringComparer.OrdinalIgnoreCase);
        var list = redirects.ToList();
        foreach (var r in list)
        {
            if (!string.Equals(r.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (GroupOf(r) is { } group && !own.Contains(group)
                && singleGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
                return group;
        }

        if (modelRel == null || multiGroups is not { Count: > 0 }) return null;
        string want = Normal(modelRel);
        foreach (var r in list)
        {
            if (!string.Equals(r.GamePath, gamePath, StringComparison.OrdinalIgnoreCase) || Normal(r.File) != want) continue;
            if (GroupOf(r) is { } group && !own.Contains(group)
                && multiGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
                return group;
        }
        return null;

        static string? GroupOf(PenumbraModMeta.Redirect r)
        {
            int split = r.Source.IndexOf(" / ", StringComparison.Ordinal);
            return split > 0 ? r.Source[..split] : null;
        }
    }

    /// <summary>
    /// What to tick in <paramref name="group"/> to wear a refit saved as <paramref name="option"/>. In a single-choice
    /// group, just that. In a multi-choice group, everything already ticked stays ticked — the choker, the jacket —
    /// except the option the refit was cut from, which it replaces: both ticked would draw two models of one piece.
    /// </summary>
    /// <param name="multi">The group is multi-choice.</param>
    /// <param name="ticked">What the collection has ticked in the group now; null for nothing.</param>
    /// <param name="cutFrom">The option the refitted model came from, or null.</param>
    internal static List<string> SelectionFor(bool multi, string option, IReadOnlyCollection<string>? ticked,
                                              string? cutFrom)
    {
        if (!multi) return [option];
        var selection = (ticked ?? [])
            .Where(t => !string.Equals(t, cutFrom, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(t, option, StringComparison.OrdinalIgnoreCase))
            .ToList();
        selection.Add(option);
        return selection;
    }

    /// <summary>Whether the mod's group of that name is a multi-choice one.</summary>
    internal static bool IsMulti(string modRoot, string group)
        => (PenumbraModMeta.TryReadGroups(modRoot) ?? [])
            .Any(g => string.Equals(g.Name, group, StringComparison.OrdinalIgnoreCase)
                      && string.Equals(PenumbraModMeta.TypeOf(g.Group), "Multi", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The option's name for one refitted size: what the user will pick in Penumbra, so it says which body and which
    /// size — the garment's own slot's size first, then each other part's.
    /// </summary>
    internal static string OptionName(string bodyName, IEnumerable<string> targetLabels)
        => $"{bodyName} — {string.Join(" + ", targetLabels)}";

    /// <summary>The model of a body part or an accessory — the pieces a size group can hold several of.</summary>
    private static readonly System.Text.RegularExpressions.Regex PieceModel = new(
        @"_(top|dwn|glv|sho|nek|ear|wrs|rir|ril)\.mdl$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Whether <paramref name="group"/> is one of the author's single-choice groups that sizes several pieces together —
    /// Geometric's "Chest Size", every option of which serves its top and its jacket — where two refits saved as two
    /// options would switch each other off (see <see cref="JoinOption"/>). Counted by piece models only: a size group
    /// serving one model and its materials sizes one piece.
    /// </summary>
    internal static bool IsSharedSingleGroup(IEnumerable<PenumbraModMeta.Redirect> redirects,
                                             BodyRetargetWriter.Record? record, string group,
                                             IReadOnlyCollection<string> singleGroups)
    {
        if (!singleGroups.Contains(group, StringComparer.OrdinalIgnoreCase)) return false;
        var ours = new HashSet<string>(record?.Options.Where(e => string.Equals(record.GroupOf(e), group,
                                                                                StringComparison.OrdinalIgnoreCase))
                                                      .Select(e => e.Name) ?? [],
                                       StringComparer.OrdinalIgnoreCase);
        string prefix = group + " / ";
        return redirects.Where(d => d.Source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                    && !ours.Contains(d.Source[prefix.Length..])
                                    && PieceModel.IsMatch(d.GamePath))
                        .Select(d => d.GamePath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Skip(1).Any();
    }

    /// <summary>
    /// The option a refit of <paramref name="gamePath"/> is saved as in a group that sizes several pieces together
    /// (<see cref="IsSharedSingleGroup"/>): an automatic refit of another piece already there onto the same sizes, joined,
    /// so switching one on does not switch the other off; otherwise <paramref name="proposed"/>.
    /// <para/>
    /// Joined only where every entry of the option is the automatic refit's (undoing it removes them all — never a size
    /// made by hand), none is for this game path, the two share a part, and every part the option records went onto the
    /// size chosen for it now. A top saved onto "XS + legs S" is not joined once legs M is chosen.
    /// </summary>
    /// <param name="chosen">The target body's file chosen now, per slot; a slot with none is left out.</param>
    /// <param name="own">The slots this refit goes onto, each with its target's file.</param>
    internal static string JoinOption(BodyRetargetWriter.Record? record, string group, string gamePath, string bodyMod,
                                      IReadOnlyDictionary<string, string> chosen, IReadOnlyDictionary<string, string> own,
                                      string proposed)
    {
        if (record == null) return proposed;
        var inGroup = record.Options.Where(e => string.Equals(record.GroupOf(e), group, StringComparison.OrdinalIgnoreCase))
                                    .ToList();
        // The same refit saved before keeps its own name: Existing decides whether it is put back or made again.
        if (inGroup.Any(e => string.Equals(e.Name, proposed, StringComparison.OrdinalIgnoreCase))) return proposed;

        foreach (var option in inGroup.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var entries = option.ToList();
            if (entries.Any(e => !e.Auto || e.Targets == null
                                 || !string.Equals(e.BodyMod, bodyMod, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(e.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)))
                continue;
            var recorded = entries.SelectMany(e => e.Targets!).ToList();
            bool current = recorded.All(t => chosen.TryGetValue(t.Key, out string? rel) && Normal(rel) == Normal(t.Value));
            bool shares = recorded.Any(t => own.ContainsKey(t.Key));
            if (current && shares) return option.Key;
        }
        return proposed;
    }

    /// <summary>What the record says a refit was made from: the sizes, behind the body mod's name when it was another.</summary>
    internal static string LabelFrom(string? sourceBodyName, IEnumerable<string> sourceLabels)
    {
        string sizes = string.Join(" + ", sourceLabels);
        return sourceBodyName != null ? sourceBodyName + " — " + sizes : sizes;
    }

    /// <summary>The races a body mod has bodies of, as <c>"0201"</c> codes.</summary>
    internal static HashSet<string> RacesOf(BodySizeCatalog? catalog)
        => (catalog?.Options ?? []).Select(o => BodySizeCatalog.RaceOf(o.GamePath))
                                   .OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The race to bake a garment drawn at <paramref name="gamePath"/> into before it is refitted for a wearer of
    /// <paramref name="wearerRace"/> — or 0 to refit it as drawn. See <see cref="RacialModelBake.Target"/>.
    /// <para/>
    /// Never over a model the mod already has for that race: an author who ships both a man's and a woman's model made
    /// the woman's on purpose, and a bake of the man's saved at her path would take its place in the option. A size this
    /// tool saved there is not the author's, or the first save would switch the bake off for the next.
    /// </summary>
    /// <param name="redirects">The garment mod's redirects; empty for the game's own gear, which has no author.</param>
    /// <param name="ownRecord">The garment mod's refit record, which says which of those redirects are refits.</param>
    internal static ushort BakeTarget(string gamePath, ushort wearerRace, Func<ushort, bool> hasBodies,
                                      IEnumerable<PenumbraModMeta.Redirect> redirects,
                                      BodyRetargetWriter.Record? ownRecord)
    {
        ushort drawn = ModelSkinReader.RaceOf(gamePath);
        ushort target = RacialModelBake.Target(drawn, wearerRace, hasBodies);
        if (target != 0 && BodyRetargetWriter.AuthorProvides(redirects, ownRecord,
                                                             RacialModelBake.WithRace(gamePath, target)))
            return 0;
        return target;
    }

    /// <summary>
    /// A refit of a BAKED garment, with the skin the refit kept of the garment's own drawn with the body's material.
    /// A bake names its skin after the new race, where the old race's material may not exist; without this the model
    /// is not drawn.
    /// </summary>
    internal static BodyRetarget.Planned WithBodySkin(BodyRetarget.Planned plan, string targetPath, IPluginLog log)
    {
        var model = RacialModelBake.SkinLikeBody(plan.Model, File.ReadAllBytes(targetPath), out var renamed);
        if (renamed.Count == 0) return plan;
        log.Information("[Proteus] retarget: baked garment's own skin {0} drawn with the body's",
                        string.Join(", ", renamed));
        return plan with { Model = model };
    }

    /// <summary>
    /// Draw whatever skin a refit LEFT in the garment the way the new body draws its own: in the new body's texture
    /// layout, with the new body's skin material.
    /// <para/>
    /// Replacing the skin swaps a garment's skin meshes for the new body's — but only the meshes lying wholly on the new
    /// body (see <see cref="BodyRetarget.SwapSkin"/>). A mesh partly off it, a posed foot, a sculpted piece, is kept, and
    /// so was its material: a garment made for the game's body or a gen3 one kept naming <c>_a</c> or <c>_b</c> on a
    /// Rue character, whose body is <c>_bibo</c>. That draws a different skin texture from the body's — the wrong skin,
    /// and none of the tattoos Proteus composites into the body's own. Players reported exactly that.
    /// <para/>
    /// The material NAME cannot say which layout the kept skin is in — Araneidae is a Bibo garment naming
    /// <c>_b</c>, which gen3 uses too — so the layout is read off the body the garment was MADE on, whose mesh its
    /// author copied. Where that differs from the new body's, the kept skin's texture coordinates are converted first;
    /// where they cannot be, it is left exactly as it was, since a renamed material over unconverted coordinates draws
    /// the wrong part of the sheet. A material only a skin by the shape of its name is touched: an author's own skin
    /// material (a tattoo baked into it) is not a body-skin name, and stands.
    /// </summary>
    /// <param name="sourcePath">The body the garment was made on (its own slot's).</param>
    /// <param name="targetPath">The body it was refitted onto.</param>
    /// <param name="male">The bodies are a man's, whose layouts are named apart (see <c>BodyCorrespondence.LayoutOf</c>).</param>
    internal static BodyRetarget.Planned MatchSkinToBody(BodyRetarget.Planned plan, string sourcePath, string targetPath,
                                                         UVRemapService? uvRemap, bool male, IPluginLog log)
    {
        var model = MatchSkinToBody(plan.Model, sourcePath, targetPath, uvRemap, male, log);
        return ReferenceEquals(model, plan.Model) ? plan : plan with { Model = model };
    }

    /// <inheritdoc cref="MatchSkinToBody(BodyRetarget.Planned, string, string, UVRemapService?, bool, IPluginLog)"/>
    /// <returns>The model with its kept skin redrawn, or the very same array when there was nothing to do.</returns>
    internal static byte[] MatchSkinToBody(byte[] model, string sourcePath, string targetPath, UVRemapService? uvRemap,
                                           bool male, IPluginLog log)
    {
        var plan = model;
        try
        {
            var target = File.ReadAllBytes(targetPath);
            var bodySkins = SecondSkinWriter.Parse(target).MatNames.Where(SecondSkinWriter.IsBodySkinMaterial).ToList();
            if (bodySkins.Count == 0) return plan;
            var left = SecondSkinWriter.Parse(model).MatNames
                                       .Where(m => SecondSkinWriter.IsBodySkinMaterial(m) && !bodySkins.Contains(m))
                                       .ToHashSet(StringComparer.Ordinal);
            if (left.Count == 0) return plan;

            string? from = ModelPartReader.Read(File.ReadAllBytes(sourcePath)) is { } source
                ? BodyCorrespondence.LayoutOf(source, male) : null;
            string? to = ModelPartReader.Read(target) is { } body ? BodyCorrespondence.LayoutOf(body, male) : null;
            if (from == null || to == null)
            {
                log.Information("[Proteus] retarget: left the garment's own skin {0} as it is — the bodies' layouts are "
                              + "unknown", string.Join(", ", left));
                return plan;
            }
            // Only skin a mesh still DRAWS has coordinates to convert. A material the swap left in the table with no mesh
            // under it (the Scion Adventurer's Jacket keeps its "_a" so) is only renamed, so the model loads one skin.
            var drawn = (ModelPartReader.Read(model)?.Parts ?? [])
                        .Where(p => p.Island < 0 && left.Contains(p.Material)).Select(p => p.Material)
                        .ToHashSet(StringComparer.Ordinal);
            if (drawn.Count > 0 && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                if (uvRemap?.UvConverter(from, to, unmirror: true) is not { } convert
                    || FaceUvRewriter.RewriteFaceUv0(model, drawn.Contains, convert, out var stats,
                                                     m => log.Information("[Proteus] retarget: {0}", m), tiled: true)
                       is not { } converted)
                {
                    log.Information("[Proteus] retarget: left the garment's own skin {0} as it is — no way to convert {1} "
                                  + "to {2}", string.Join(", ", left), from, to);
                    return plan;
                }
                model = converted;
                log.Information("[Proteus] retarget: the garment's own skin converted {0} → {1} ({2} vertices)", from, to,
                                stats.VerticesWritten);
            }
            foreach (string name in left) model = ModelAttributeWriter.RenameMaterial(model, name, bodySkins[0]);
            log.Information("[Proteus] retarget: the garment's own skin {0} now drawn with the body's {1}",
                            string.Join(", ", left), bodySkins[0]);
            return model;
        }
        catch (Exception ex)
        {
            // A cosmetic step on a refit that is otherwise done: failing it leaves the skin as it was, not the refit lost.
            log.Warning(ex, "[Proteus] retarget: could not redraw the garment's own skin like the body's");
            return plan;
        }
    }

    /// <summary>
    /// The option of <paramref name="slot"/> whose file the collection resolves the body model to — which size the
    /// character is wearing there.
    /// <para/>
    /// Matched by FILE, not by name: Neolithe has eight options all called "SFW M" and only the file each one points at
    /// tells them apart. It also answers the question actually being asked — which body is being drawn — rather than
    /// which options happen to be ticked in some group.
    /// </summary>
    /// <param name="resolve">Resolves a game path through the player's collection (Penumbra IPC, so whatever thread
    /// the caller is allowed to ask from).</param>
    internal static BodyOption? WornOption(BodySizeCatalog catalog, string slot, string? race,
                                           Func<string, string?> resolve)
    {
        var options = catalog.For(slot, race);
        foreach (string gamePath in options.Select(o => o.GamePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (resolve(gamePath) is not { } resolved) continue;
            string full = Path.GetFullPath(resolved);
            foreach (var option in options)
                if (string.Equals(option.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFullPath(catalog.PathOf(option)), full, StringComparison.OrdinalIgnoreCase))
                    return option;
        }
        return null;
    }

    /// <summary>
    /// The body mod — and which of its sizes — a garment was made on, out of every mod in <paramref name="mods"/>.
    /// <para/>
    /// In three steps, cheapest first, because ranking every size of every body mod froze the game (see
    /// <see cref="AutoRefitDecisions.Family"/>): a few sizes of each mod place the garment in a body family; every size of
    /// the family's mods picks the one mod (<see cref="AutoRefitDecisions.Choose"/>); and only that mod's sizes are
    /// ranked, exactly as the Studio's Body size tool ranks them. Worker thread: it reads models.
    /// </summary>
    /// <param name="mods">Every body to consider, the preferred one first; order breaks the last ties.</param>
    /// <param name="preferredDir">The body mod refitted onto.</param>
    /// <param name="enabled">Whether a body mod is switched on in the collection.</param>
    /// <param name="note">Where to say what was decided, for the log.</param>
    internal static AutoRefitDecisions.Pick? DetectSource(ModelParts garment, IReadOnlySet<string> bones, string slot,
                                                          string race, IReadOnlyList<(string Dir, BodySizeCatalog Catalog)> mods,
                                                          string preferredDir, Func<string, bool> enabled,
                                                          Action<string>? note = null)
        => Detect(garment, bones, slot, race, mods, preferredDir, enabled, note, byNearness: true, onNearness: null);

    /// <summary>
    /// What <see cref="DetectSource"/> reads a garment as, beside what the exact reading alone would have — the
    /// comparison that shows which garments the nearness reading changes. The exact reading is run a second time only
    /// for a garment that went to nearness; for any other the two are one reading.
    /// </summary>
    internal static (AutoRefitDecisions.Pick? Exact, AutoRefitDecisions.Pick? Now) CompareReadings(
        ModelParts garment, IReadOnlySet<string> bones, string slot, string race,
        IReadOnlyList<(string Dir, BodySizeCatalog Catalog)> mods, string preferredDir, Func<string, bool> enabled,
        Action<string>? note = null)
    {
        bool near = false;
        var now = Detect(garment, bones, slot, race, mods, preferredDir, enabled, note, byNearness: true,
                         onNearness: () => near = true);
        var exact = near ? Detect(garment, bones, slot, race, mods, preferredDir, enabled, null, byNearness: false, null)
                         : now;
        return (exact, now);
    }

    /// <param name="byNearness">Read a garment no body matches exactly by nearness (<see cref="DetectByNearness"/>).
    /// Off only to measure what the exact reading alone would have said.</param>
    /// <param name="onNearness">Called when the garment is read by nearness.</param>
    private static AutoRefitDecisions.Pick? Detect(ModelParts garment, IReadOnlySet<string> bones, string slot,
                                                   string race, IReadOnlyList<(string Dir, BodySizeCatalog Catalog)> mods,
                                                   string preferredDir, Func<string, bool> enabled, Action<string>? note,
                                                   bool byNearness, Action? onNearness)
    {
        using var reading = BodySizeMatch.BeginReading();
        var skinPoints = BodySizeMatch.SkinPoints(garment);
        var probes = BodySizeMatch.ProbeKeys(skinPoints);
        if (probes.Length == 0)
        {
            var any = mods.Select(m => m.Catalog.For(slot, race).FirstOrDefault()).FirstOrDefault(o => o != null);
            return any == null ? null
                : new AutoRefitDecisions.Pick(preferredDir, any, BodySizeMatch.Confidence.NoBodyMesh, false);
        }

        var catalogs = mods.ToDictionary(o => o.Dir, o => o.Catalog, StringComparer.OrdinalIgnoreCase);
        AutoRefitDecisions.ModShare ShareOf(string dir, float share)
            => new(dir, share, string.Equals(dir, preferredDir, StringComparison.OrdinalIgnoreCase), enabled(dir),
                   catalogs[dir].For(slot, race).Count);

        // 1. A few sizes of every mod: which family. Each mod's samples also say what its skin IS, so a fork shipping
        //    the same bodies under other file names is recognised before its every size is read a second time.
        var sampled = new List<AutoRefitDecisions.ModShare>();
        var geometry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, catalog) in mods)
        {
            var options = catalog.For(slot, race);
            if (options.Count == 0) continue;
            var contents = new List<string?>();
            float share = 0f;
            foreach (var option in AutoRefitDecisions.SamplesOf(options))
            {
                share = Math.Max(share, BodySizeMatch.Shared(probes, catalog.PathOf(option), out string? content));
                contents.Add(content);
            }
            sampled.Add(ShareOf(dir, share));
            if (contents.All(c => c != null)) geometry[dir] = options.Count + "|" + string.Join("|", contents);
        }
        var family = AutoRefitDecisions.Family(sampled);
        if (family.Count == 0)
        {
            // Matching no installed body at all is not a weak match: a garment made for a body the player lacks lies a few
            // millimetres from some unrelated body, and nearness would take that body for its own.
            note?.Invoke($"the garment's skin matches no installed body (best {sampled.Select(m => m.Share).DefaultIfEmpty(0f).Max():P0})");
            return null;
        }

        // 2. More sizes of the family's mods, one of each fork: which mod. A mod whose every size was sampled already has
        //    its answer.
        var kept = family.Select(dir => sampled.First(m => m.Dir == dir))
                         .GroupBy(m => geometry.TryGetValue(m.Dir, out var g) ? g : m.Dir, StringComparer.Ordinal)
                         .Select(g => g.OrderByDescending(m => m.Preferred).ThenByDescending(m => m.Enabled).First())
                         .ToList();
        var full = kept.Select(m => m.Sizes <= AutoRefitDecisions.Samples
                                        ? m
                                        : m with
                                        {
                                            Share = AutoRefitDecisions.SamplesOf(catalogs[m.Dir].For(slot, race),
                                                                                 AutoRefitDecisions.ChoiceSamples)
                                                .Select(o => BodySizeMatch.Shared(probes, catalogs[m.Dir].PathOf(o), out _))
                                                .Max(),
                                        })
                       .ToList();

        // Weak only over every size read, not the first three: a Tight&Firm top shared 22% with three of its body's sizes
        // and 35% with the one it was made on.
        float bestShare = full.Max(m => m.Share);
        string? chosen = AutoRefitDecisions.Choose(full);
        // Kept after nearness found no body clearly, the exact answer has been doubted: its size must then be at least
        // Likely, as nearness's own would have to be. Kept because nearness had nothing to say, it stands as it was.
        bool doubted = false;
        if (byNearness && AutoRefitDecisions.Weak(bestShare))
        {
            onNearness?.Invoke();
            var (outcome, near) = DetectByNearness(garment, skinPoints, bones, slot, race, mods, catalogs, preferredDir,
                                                   enabled, note, bestShare, family, chosen);
            if (outcome is not (AutoRefitDecisions.NearOutcome.KeepExact or AutoRefitDecisions.NearOutcome.KeepDoubted))
                return near;
            doubted = outcome == AutoRefitDecisions.NearOutcome.KeepDoubted;
        }
        if (chosen == null)
        {
            note?.Invoke($"family {string.Join(", ", full.Select(m => $"{m.Dir} {m.Share:P1}"))}; "
                       + (bestShare < AutoRefitDecisions.MinShared
                              ? $"none shares enough to choose (best {bestShare:P1})"
                              : "no one mod among them"));
            return null;
        }
        note?.Invoke($"family {string.Join(", ", full.Select(m => $"{m.Dir} {m.Share:P1}"))}; made on {chosen}");

        // 3. The chosen mod's sizes, ranked as the Studio ranks them.
        var source = catalogs[chosen];
        var ranking = BodySizeMatch.Rank(garment, source.For(slot, race), source.PathOf, bones, skinPoints);
        if (ranking.Best is not { } best) return null;
        return new AutoRefitDecisions.Pick(chosen, best.Option,
                                           doubted ? AutoRefitDecisions.Sure(ranking.Confidence) : ranking.Confidence,
                                           ranking.FromCloth);
    }

    /// <summary>
    /// Every body <see cref="DetectSource"/> searches, in the order it breaks ties by: the body refitted onto first, every
    /// other installed body mod, and the game's own body last.
    /// </summary>
    /// <param name="vanilla">The game's own body, or null to leave it out (outside the game, where it cannot be read).</param>
    internal static List<(string Dir, BodySizeCatalog Catalog)> SearchOrder(
        string targetDir, BodySizeCatalog target, IEnumerable<(string Dir, BodySizeCatalog Catalog)> bodies,
        BodySizeCatalog? vanilla)
    {
        var order = new List<(string Dir, BodySizeCatalog Catalog)> { (targetDir, target) };
        order.AddRange(bodies.Where(b => !string.Equals(b.Dir, targetDir, StringComparison.OrdinalIgnoreCase)));
        if (vanilla is { IsBody: true }) order.Add((VanillaBodyCatalog.Key, vanilla));
        return order;
    }

    /// <summary>
    /// <see cref="DetectSource"/> for a garment no installed body shares much with exactly — one made on an earlier release
    /// of its body mod (see <see cref="AutoRefitDecisions.WeakShare"/>). The same three steps, read by how close the
    /// garment's skin lies to each body (<see cref="BodySizeMatch.Near"/>) instead of how much of it lies exactly on one.
    /// <para/>
    /// Unattended, a guess is not good enough where the readings disagree: a wrong source body is what pushes a bust 13 mm
    /// through a sweater. Where nearness overrules the exact answer, a ranking of the new body's sizes that is not at least
    /// Likely is returned as Ambiguous, which the watcher does not refit from; where the two agree, the size is read as it
    /// always was. A reading that finds no body clearly refuses, unless the exact answer is not clearly further than the
    /// nearest (<see cref="AutoRefitDecisions.Settle"/>) — then it stands, its size held to Likely.
    /// </summary>
    /// <param name="exactFamily">The exact reading's family: read again here over more sizes, so a mod whose three
    /// sampled sizes missed the garment's is still weighed.</param>
    /// <param name="exactChosen">The exact reading's answer, or null.</param>
    /// <param name="skinPoints">The garment's skin points, as the exact reading took them (BodySizeMatch.SkinPoints).</param>
    /// <returns>What becomes of the garment (<see cref="AutoRefitDecisions.Settle"/>), and the body to refit from when
    /// that is <see cref="AutoRefitDecisions.NearOutcome.Refit"/>.</returns>
    private static (AutoRefitDecisions.NearOutcome Outcome, AutoRefitDecisions.Pick? Pick) DetectByNearness(
        ModelParts garment, List<System.Numerics.Vector3> skinPoints, IReadOnlySet<string> bones, string slot, string race,
        IReadOnlyList<(string Dir, BodySizeCatalog Catalog)> mods, Dictionary<string, BodySizeCatalog> catalogs,
        string preferredDir, Func<string, bool> enabled, Action<string>? note, float bestShare,
        IReadOnlyList<string> exactFamily, string? exactChosen)
    {
        var probes = BodySizeMatch.NearProbePoints(skinPoints);

        // Each body measured once, by the FILE it is: the twelve sizes of step 2 include the three of step 1, and two
        // forks ship the same files under other names.
        var measured = new Dictionary<string, float[]?>(StringComparer.Ordinal);
        var skinOf = new Dictionary<string, string>(StringComparer.Ordinal);   // file → what its skin is
        var readBy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Read(string dir, IEnumerable<BodyOption> options)
        {
            if (!readBy.TryGetValue(dir, out var files)) readBy[dir] = files = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options)
            {
                string path = catalogs[dir].PathOf(option);
                // A file measured already under another mod's name is not measured again; one not read at all yet is read
                // and parsed once, for its keys and its distances together.
                if (BodySizeMatch.CachedFile(path) is { } known && measured.ContainsKey(known.File))
                {
                    skinOf[known.File] = known.Body;
                    files.Add(known.File);
                    continue;
                }
                var distances = BodySizeMatch.Near(probes, path, out string? file, out string? skin);
                if (file == null) continue;
                measured.TryAdd(file, distances);
                if (skin != null) skinOf[file] = skin;
                files.Add(file);
            }
        }

        // The points weighed: those some body reaches at all. A neck or hand the chest bodies lack lies beyond the cap
        // from every one of them, and counted, a tenth of the skin there would hold every body's 90th percentile at the
        // cap — the reading would say nothing, and the exact reading's ancestor would be refitted from after all.
        bool[] Reached()
        {
            var reached = new bool[probes.Length];
            foreach (var d in measured.Values)
                if (d != null)
                    for (int i = 0; i < d.Length; i++)
                        reached[i] |= d[i] < BodySizeMatch.NearCap;
            return reached;
        }
        List<AutoRefitDecisions.ModNear> Weigh(bool[] reached)
        {
            var p90 = measured.ToDictionary(kv => kv.Key,
                                            kv => kv.Value is { } d ? BodySizeMatch.NearP90(d, reached) : float.MaxValue,
                                            StringComparer.Ordinal);
            return readBy.Select(kv =>
                         {
                             string? closest = kv.Value.OrderBy(c => p90[c]).ThenBy(c => c, StringComparer.Ordinal)
                                                 .FirstOrDefault();
                             // Bodies compared by what their skin IS, not by file: a fork re-saving one mesh with other
                             // materials is another file of the same body.
                             string Skin(string file) => skinOf.GetValueOrDefault(file, file);
                             return new AutoRefitDecisions.ModNear(
                                 kv.Key, closest == null ? float.MaxValue : p90[closest],
                                 string.Equals(kv.Key, preferredDir, StringComparison.OrdinalIgnoreCase), enabled(kv.Key),
                                 catalogs[kv.Key].For(slot, race).Count, closest == null ? null : Skin(closest));
                         })
                         .ToList();
        }

        // Nearness has nothing to say: the exact answer stands as it was.
        (AutoRefitDecisions.NearOutcome, AutoRefitDecisions.Pick?) NothingToTell(string head, int weighed,
                                                                                 IReadOnlyList<AutoRefitDecisions.ModNear> read)
        {
            string why = AutoRefitDecisions.TooFewReached(weighed, probes.Length)
                ? $"only {weighed} of {probes.Length} points lie near any body"
                : $"the nearest body is {Mm(read.Min(m => m.Distance))} off, on none of them";
            note?.Invoke($"{head}; {why}, too little to tell by; "
                       + (exactChosen != null ? $"the exact reading's {exactChosen} stands" : "and the exact reading has none"));
            return (AutoRefitDecisions.NearOutcome.KeepExact, null);
        }

        // 1. A few sizes of every mod: which family.
        foreach (var (dir, catalog) in mods)
            if (catalog.For(slot, race).Count > 0) Read(dir, AutoRefitDecisions.SamplesOf(catalog.For(slot, race)));
        var firstReached = Reached();
        var sampled = Weigh(firstReached);

        // Nothing to measure already: more sizes will not change that, and reading twelve of every mod — every mod is
        // "near" the best when all are at the cap — is the sweep that once froze the game.
        int firstWeighed = firstReached.Count(r => r);
        if (AutoRefitDecisions.TooFewReached(firstWeighed, probes.Length)
            || AutoRefitDecisions.ChooseNear(sampled, BodySizeMatch.NearCap, out _, out _) == AutoRefitDecisions.NearVerdict.NoReading)
            return NothingToTell($"no body matches exactly (best {bestShare:P1})", firstWeighed, sampled);
        var family = AutoRefitDecisions.NearFamily(sampled)
                                       .Union(exactFamily, StringComparer.OrdinalIgnoreCase)
                                       .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 2. More sizes of the family's mods: which mod. Every other mod is weighed too, at its sampled distance, so a
        //    body nearly as close is never left out of the clarity check for having been sampled badly.
        foreach (string dir in family)
            if (catalogs[dir].For(slot, race).Count > AutoRefitDecisions.Samples)
                Read(dir, AutoRefitDecisions.SamplesOf(catalogs[dir].For(slot, race), AutoRefitDecisions.ChoiceSamples));
        BodySizeMatch.NeedNear(measured.Count);
        var reached = Reached();
        int weighed = reached.Count(r => r);
        var full = Weigh(reached);
        string reading = string.Join(", ", full.Where(m => family.Contains(m.Dir))
                                               .Select(m => $"{m.Dir} {Mm(m.Distance)}"));
        var verdict = AutoRefitDecisions.ChooseNear(full, BodySizeMatch.NearCap, out string? chosen, out var closestBody);
        float? exactDistance = exactChosen == null ? null
            : full.Where(m => string.Equals(m.Dir, exactChosen, StringComparison.OrdinalIgnoreCase))
                  .Select(m => (float?)m.Distance).FirstOrDefault();
        var outcome = AutoRefitDecisions.Settle(verdict, weighed, probes.Length, closestBody, exactChosen,
                                                full.Count > 0 ? full.Min(m => m.Distance) : float.MaxValue, exactDistance);
        string head = $"no body matches exactly (best {bestShare:P1}); nearest {reading}";
        if (outcome == AutoRefitDecisions.NearOutcome.KeepExact) return NothingToTell(head, weighed, full);
        if (outcome == AutoRefitDecisions.NearOutcome.KeepDoubted)
        {
            note?.Invoke($"{head}; no body clearly, and the exact reading's {exactChosen} is not clearly further than the "
                       + "nearest, so it stands if its size is sure");
            return (outcome, null);
        }
        if (outcome == AutoRefitDecisions.NearOutcome.Refuse || chosen == null)
        {
            note?.Invoke(head + (verdict == AutoRefitDecisions.NearVerdict.NoneClose
                                     ? "; none close enough to refit from"
                                     : "; two bodies about as close, so neither is sure"));
            return (AutoRefitDecisions.NearOutcome.Refuse, null);
        }
        note?.Invoke($"{head}; made on {chosen}");

        // 3. The chosen mod's sizes, ranked as the Studio ranks them.
        var source = catalogs[chosen];
        var ranking = BodySizeMatch.Rank(garment, source.For(slot, race), source.PathOf, bones, skinPoints);
        // Where nearness overrules the exact reading, its answer must be sure of its size; where the two agree on the body —
        // whichever of its forks each picked — the size is read as it always was.
        string? BodyOf(string? dir) => full.FirstOrDefault(m => string.Equals(m.Dir, dir, StringComparison.OrdinalIgnoreCase)).Body;
        bool overruled = !string.Equals(chosen, exactChosen, StringComparison.OrdinalIgnoreCase)
                         && (BodyOf(chosen) is not { } chosenBody || chosenBody != BodyOf(exactChosen));
        return ranking.Best is { } best
            ? (outcome, new AutoRefitDecisions.Pick(chosen, best.Option,
                                                    overruled ? AutoRefitDecisions.Sure(ranking.Confidence) : ranking.Confidence,
                                                    ranking.FromCloth))
            : (AutoRefitDecisions.NearOutcome.Refuse, null);
    }

    /// <summary>A nearness distance for the log: millimetres, or "unreadable" for a body none of whose files could be read.</summary>
    private static string Mm(float metres) => metres >= 1f ? "unreadable" : $"{metres * 1000f:0.0} mm";

    /// <summary>
    /// The automatic refit of a chest or legs piece, its skin as <see cref="AutoRefitDecisions.SkinFor"/> says: shaped
    /// pieces kept whole, nothing held, the skin replaced across bodies.
    /// </summary>
    /// <param name="pieceNames">The kept pieces' labels, in order — for <see cref="ReleasedLog"/>.</param>
    internal static BodyRetarget.Planned PlanRefit(ModelParts garment, byte[] bytes, IReadOnlyList<BodyRetarget.SlotPair> pairs,
                                                   string primary, bool across, bool gameGear, out List<string> pieceNames,
                                                   Action<string>? log = null, Action<string>? warn = null)
    {
        var skin = AutoRefitDecisions.SkinFor(gameGear);
        var pieces = ShapePieces(garment, DefaultKeepShape(garment), out pieceNames);
        return BodyRetarget.Plan(garment, bytes, pairs, primary, held: new HashSet<int>(),
                                 replaceSkin: across, acrossBodies: across, clearBody: skin.ClearBody,
                                 cutHidden: skin.CutHidden, keepShape: pieces, log: log, warn: warn);
    }

    /// <summary>
    /// Whether the garment at <paramref name="gamePath"/> of the mod at <paramref name="modRoot"/> (null: the game's own
    /// file) is the game's own garment, read off the mod on disk — what a save records as
    /// <see cref="BodyRetargetWriter.Entry.FromGame"/>, so a game item's own mod stays one however it is refitted. See
    /// <see cref="AutoRefitDecisions.IsGameGear"/>.
    /// </summary>
    internal static bool IsGameGear(string? modRoot, string gamePath)
        => modRoot == null
           || AutoRefitDecisions.IsGameGear(modRoot, gamePath, PenumbraModMeta.ReadAllRedirects(modRoot),
                                            BodyRetargetWriter.ReadRecord(modRoot), RefitModService.IsMadeByProteus(modRoot));

    /// <summary>A relative path the way Penumbra compares them: forward slashes, no leading slash, any case.</summary>
    internal static string Normal(string rel) => rel.Replace('\\', '/').Trim('/').ToLowerInvariant();
}
