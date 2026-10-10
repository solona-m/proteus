using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;

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
    {
        var pieces = new List<IReadOnlyCollection<int>>();
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
            covered.Add((part.Mesh, part.Submesh));
        }
        foreach (var part in garment.Parts)
            if (part.Island < 0 && labels.Contains(part.Label) && !covered.Contains((part.Mesh, part.Submesh)))
                pieces.Add(part.Triangles.Distinct().ToArray());
        return pieces;
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
    {
        var probes = BodySizeMatch.ProbeKeys(garment);
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
        if (AutoRefitDecisions.Choose(full) is not { } chosen) return null;
        note?.Invoke($"family {string.Join(", ", full.Select(m => $"{m.Dir} {m.Share:P1}"))}; made on {chosen}");

        // 3. The chosen mod's sizes, ranked as the Studio ranks them.
        var source = catalogs[chosen];
        var ranking = BodySizeMatch.Rank(garment, source.For(slot, race), source.PathOf, bones);
        return ranking.Best is { } best
            ? new AutoRefitDecisions.Pick(chosen, best.Option, ranking.Confidence, ranking.FromCloth)
            : null;
    }

    /// <summary>A relative path the way Penumbra compares them: forward slashes, no leading slash, any case.</summary>
    internal static string Normal(string rel) => rel.Replace('\\', '/').Trim('/').ToLowerInvariant();
}
