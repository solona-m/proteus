using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Women's overlays on a man: whether, where and how a woman's art is drawn on a man of the men's layout group, decided
/// once for the skin composite and the second-skin shell alike.
/// <para/>
/// A woman's art is drawn on a man only in his own layout, through the bibo↔TBSE maps (see <see cref="MensRoute"/>):
/// never pasted in hers. So art the route will carry is drawn; art it would carry but cannot yet — the maps not here, a
/// map that would not load — or that the player switched off for men is held off him altogether; and art that is not a
/// woman's (his own, or anything <see cref="UVRemapService.MensRouteFor"/> gives no route) is left exactly as it was.
/// <para/>
/// A Create-tab overlay made on a woman names only women's races, so the composite's race filter drops it on a man
/// before anything could route it. <see cref="Plan"/> carries it onto his own live body materials.
/// </summary>
internal static class WomensArtOnMen
{
    internal enum MenVerdict { NotHers, Draw, AwaitMaps, OptedOut, RouteMissing }

    /// <summary>
    /// What happens to art <paramref name="d"/>, read as <paramref name="legacySrc"/> by the site asking, on a man's material
    /// in layout <paramref name="dstLayout"/>. <see cref="MenVerdict.NotHers"/> leaves it on today's path; anything but
    /// <see cref="MenVerdict.Draw"/> holds it off him.
    /// </summary>
    internal static (MenVerdict Verdict, MensRoute? Route) Judge(OverlayDescriptor d, string? legacySrc, string? dstLayout,
                                                                  bool menMapsReady, bool menAllowed,
                                                                  Func<MensRoute, bool> routeAvailable)
    {
        if (UVRemapService.MensRouteFor(d.SourceBodyType, legacySrc, d.MaterialGamePaths, dstLayout) is not { } route)
            return (MenVerdict.NotHers, null);
        if (!menAllowed) return (MenVerdict.OptedOut, route);
        if (!menMapsReady) return (MenVerdict.AwaitMaps, route);
        return routeAvailable(route) ? (MenVerdict.Draw, route) : (MenVerdict.RouteMissing, route);
    }

    /// <summary>
    /// The source layout the skin composite reads off an overlay (<c>MaterialComposite.SrcTypeOf</c> and the blend pass):
    /// declared, else bibo when any path is <c>_bibo</c>, else gen3 when any path reads gen3, else none.
    /// </summary>
    internal static string? SkinLegacySourceOf(OverlayDescriptor d)
    {
        if (d.SourceBodyType != null) return d.SourceBodyType;
        if (d.MaterialGamePaths.Any(p => p.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase))) return "bibo";
        if (d.MaterialGamePaths.Any(p => UVRemapService.InferBodyType(p) == "gen3")) return "gen3";
        return null;
    }

    /// <summary>Whether art names a human body skin material (<c>chara/human/cRRxx/obj/body/…</c>): only that is carried onto a body.</summary>
    internal static bool HasBodySkinPath(OverlayDescriptor d)
        => d.MaterialGamePaths.Any(p => p.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase)
                                        && p.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a shell layer is cut from the body surface: a mask shell, art naming no surface, or art whose first surface is the body.</summary>
    internal static bool IsBodyShellLayer(OverlayDescriptor d)
    {
        if (d.IsMaskShell || d.MaterialGamePaths.Count == 0) return true;
        var keys = ShellSurface.KeysFor(d.MaterialGamePaths);
        return keys.Count == 0 || keys[0].IsBody;
    }

    internal sealed record SkinPlan(
        IReadOnlyList<(string Material, (OverlayEntry Entry, ResolvedOverlay Overlay) Pair)> Removed,
        IReadOnlyList<(string Material, MensRoute Route, (OverlayEntry Entry, ResolvedOverlay Overlay) Pair)> Carried,
        IReadOnlySet<string> AwaitingMods,
        IReadOnlySet<ResolvedOverlay> HeldGear,
        // Mods held off him with nothing of theirs drawn on him: they cast no AO on his materials.
        IReadOnlySet<string> HeldMods,
        bool MapsWanted,
        int Destinations)
    {
        internal static readonly SkinPlan Empty = new([], [], new HashSet<string>(), new HashSet<ResolvedOverlay>(), new HashSet<string>(), false, 0);
    }

    /// <summary>
    /// The skin composite's women's art on a man: pairs already on his material that must be held, overlays to carry onto
    /// his live men's-layout body materials, and — for the diagnosis — which gear layers the shell will hold and which mods
    /// wait for the maps. Empty for anyone not a men's-group man.
    /// </summary>
    /// <param name="raceSettled">False while the snapshot does not yet show the race Glamourer displays: nothing is carried
    /// then, and the post-redraw composite does it.</param>
    /// <param name="shellLegacyOf">How the shell reads a layer's source layout (see SecondSkinService.ShellLegacySourceOf).</param>
    internal static SkinPlan Plan(
        IReadOnlyDictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> byMaterial,
        IEnumerable<(OverlayEntry Entry, ResolvedOverlay Overlay)> allOverlays,
        IEnumerable<(OverlayEntry Entry, ResolvedOverlay Overlay)> gearOverlays,
        IReadOnlyCollection<string>? activeMaterials,
        bool wearerIsMan, bool raceSettled, bool menMapsReady,
        Func<string, bool> vanillaAllowed, Func<string, bool> menAllowed, Func<MensRoute, bool> routeAvailable,
        Func<OverlayDescriptor, string?> shellLegacyOf)
    {
        if (!wearerIsMan) return SkinPlan.Empty;

        var removed = new List<(string, (OverlayEntry, ResolvedOverlay))>();
        var carried = new List<(string, MensRoute, (OverlayEntry, ResolvedOverlay))>();
        var awaiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var heldMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var heldGear = new HashSet<ResolvedOverlay>(ReferenceEqualityComparer.Instance);
        // Mods with something drawn on him all the same (his own art, or hers through the route): they keep their AO.
        var drawnMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool mapsWanted = false;

        void Note(MenVerdict v, string mod)
        {
            if (v == MenVerdict.AwaitMaps) { mapsWanted = true; awaiting.Add(mod); heldMods.Add(mod); }
            else if (v is MenVerdict.OptedOut or MenVerdict.RouteMissing) heldMods.Add(mod);
            else drawnMods.Add(mod);
        }

        // 1. Pairs already on a man's material (an import naming his race, a sibling of one): routed, or held.
        foreach (var (material, list) in byMaterial)
        {
            if (UVRemapService.LayoutOfMaterial(material, true) is not { } layout) continue;
            foreach (var pair in list)
            {
                var d = pair.Overlay.Descriptor;
                var (v, _) = Judge(d, SkinLegacySourceOf(d), layout, menMapsReady, menAllowed(pair.Entry.ModDirectory), routeAvailable);
                Note(v, pair.Entry.ModDirectory);
                if (v is not (MenVerdict.Draw or MenVerdict.NotHers)) removed.Add((material, pair));
            }
        }

        // 2. Carry women's art onto his live men's-layout body materials.
        var destinations = activeMaterials?
            .Where(m => UVRemapService.LayoutOfMaterial(m, true) != null)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (raceSettled && destinations.Count > 0)
            foreach (var pair in allOverlays)
            {
                var d = pair.Overlay.Descriptor;
                if (d.Layer == OverlayLayer.Gear || !HasBodySkinPath(d)) continue;
                string mod = pair.Entry.ModDirectory;
                foreach (var m in destinations)
                {
                    var layout = UVRemapService.LayoutOfMaterial(m, true)!;
                    // Vanilla switched off for this mod: his vanilla skin is no destination at all — nothing to wait for.
                    if (layout == UVRemapService.MaleVanillaSpace && !vanillaAllowed(mod)) continue;
                    var (v, route) = Judge(d, SkinLegacySourceOf(d), layout, menMapsReady, menAllowed(mod), routeAvailable);
                    Note(v, mod);
                    if (v != MenVerdict.Draw) continue;
                    if (byMaterial.TryGetValue(m, out var there))
                    {
                        if (there.Any(p => ReferenceEquals(p.Overlay, pair.Overlay))) continue;
                        // The same pack already paints his own art here (a unisex pack): don't lay hers over it.
                        if (there.Any(p => string.Equals(p.Entry.ModDirectory, mod, StringComparison.OrdinalIgnoreCase)
                                           && UVRemapService.MensRouteFor(p.Overlay.Descriptor.SourceBodyType,
                                                  SkinLegacySourceOf(p.Overlay.Descriptor),
                                                  p.Overlay.Descriptor.MaterialGamePaths, layout) == null))
                            continue;
                    }
                    carried.Add((m, route!, pair));
                }
            }

        // 3. The shell's gear layers it will hold (for the diagnosis): judged against TBSE, as his shell's space.
        if (destinations.Count > 0)
            foreach (var (entry, overlay) in gearOverlays)
            {
                var d = overlay.Descriptor;
                if (!IsBodyShellLayer(d)) continue;
                var (v, _) = Judge(d, shellLegacyOf(d), UVRemapService.TbseSpace, menMapsReady, menAllowed(entry.ModDirectory), routeAvailable);
                Note(v, entry.ModDirectory);
                if (v is MenVerdict.AwaitMaps or MenVerdict.OptedOut or MenVerdict.RouteMissing) heldGear.Add(overlay);
            }

        heldMods.ExceptWith(drawnMods);
        return new SkinPlan(removed, carried, awaiting, heldGear, heldMods, mapsWanted, destinations.Count);
    }

    /// <summary>The plan applied: held pairs taken off his materials, carried pairs appended to them.</summary>
    internal static void Apply(Dictionary<string, List<(OverlayEntry Entry, ResolvedOverlay Overlay)>> byMaterial, SkinPlan plan)
    {
        foreach (var (material, pair) in plan.Removed)
            if (byMaterial.TryGetValue(material, out var list))
                list.RemoveAll(p => ReferenceEquals(p.Overlay, pair.Overlay) && ReferenceEquals(p.Entry, pair.Entry));
        foreach (var (material, _, pair) in plan.Carried)
        {
            if (!byMaterial.TryGetValue(material, out var list)) byMaterial[material] = list = [];
            list.Add(pair);
        }
    }

    /// <summary>One line saying what the plan did, or null when it did nothing worth saying.</summary>
    internal static string? Summary(SkinPlan plan)
    {
        if (plan.Carried.Count == 0 && plan.Removed.Count == 0 && plan.AwaitingMods.Count == 0 && plan.HeldGear.Count == 0)
            return null;
        var mods = plan.Carried.Select(c => c.Pair.Entry.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(m => m, StringComparer.OrdinalIgnoreCase);
        var routes = plan.Carried.Select(c => $"{c.Route.Src}→{c.Route.Dst}").Distinct().OrderBy(r => r, StringComparer.Ordinal);
        return $"[Proteus] women's art on a man: carried {plan.Carried.Count} overlay(s) onto {plan.Destinations} body material(s) "
             + $"[{string.Join(", ", mods)}] ({string.Join(", ", routes)}); held {plan.Removed.Count} skin pair(s) and "
             + $"{plan.HeldGear.Count} shell layer(s); waiting for the men's maps: [{string.Join(", ", plan.AwaitingMods.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))}]";
    }

    /// <summary>
    /// The paths a top mask shell takes from its seed: the seed's own when it is a woman's art on a man (so the mask is
    /// routed, or held, like the art it masks), else none — exactly as before for women and for a man's own packs.
    /// </summary>
    internal static List<string> MaskShellPathsFor(OverlayDescriptor seed, bool wearerIsMan)
        => wearerIsMan && UVRemapService.MensRouteFor(seed.SourceBodyType, SkinLegacySourceOf(seed), seed.MaterialGamePaths,
                                                      UVRemapService.TbseSpace) != null
            ? [.. seed.MaterialGamePaths]
            : [];

    /// <summary>
    /// A man's shell with women's layers it cannot draw in his layout taken off it. With his shell's men's layout known,
    /// every woman's layer not judged <see cref="MenVerdict.Draw"/> is held; with it unknown (parts that do not agree on
    /// one men's layout), a woman's layer switched off for men or waiting for the maps is still held. Women's shells and
    /// a man's own layers are untouched.
    /// </summary>
    internal static (List<(OverlayEntry Entry, ResolvedOverlay Overlay)> Kept, List<((OverlayEntry Entry, ResolvedOverlay Overlay) Pair, MenVerdict Verdict)> Held)
        HoldShellLayers(IReadOnlyList<(OverlayEntry Entry, ResolvedOverlay Overlay)> layers, string? menLayout, bool wearerIsMan,
                        bool menMapsReady, Func<string, bool> menAllowed, Func<MensRoute, bool> routeAvailable,
                        Func<OverlayDescriptor, string?> legacyOf, bool holdEveryRouted = false)
    {
        var kept = new List<(OverlayEntry, ResolvedOverlay)>();
        var held = new List<((OverlayEntry, ResolvedOverlay), MenVerdict)>();
        foreach (var pair in layers)
        {
            var d = pair.Overlay.Descriptor;
            if (!wearerIsMan || !IsBodyShellLayer(d)) { kept.Add(pair); continue; }
            var (v, _) = Judge(d, legacyOf(d), menLayout ?? UVRemapService.TbseSpace, menMapsReady,
                               menAllowed(pair.Entry.ModDirectory), routeAvailable);
            bool hold = holdEveryRouted ? v != MenVerdict.NotHers
                      : menLayout != null ? v is not (MenVerdict.Draw or MenVerdict.NotHers)
                      : v is MenVerdict.OptedOut or MenVerdict.AwaitMaps;
            if (hold) held.Add((pair, v)); else kept.Add(pair);
        }
        return (kept, held);
    }
}
