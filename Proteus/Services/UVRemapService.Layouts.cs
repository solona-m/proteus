using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// The men's body layouts, for overlays only: which material of a man is drawn in TBSE's layout or the men's vanilla one,
/// and when a woman's art on him needs the bibo↔TBSE maps to land there.
/// <para/>
/// <see cref="InferBodyType"/> is not sex-aware and is left so: it reads a man's TBSE skin as gen3 and his vanilla skin as
/// gen2, and thirty callers rely on that reading. Everything a man's OWN art does still goes through it, byte for byte;
/// only a woman's art reaching a man takes the route decided here.
/// </summary>
public partial class UVRemapService
{
    /// <summary>
    /// Whether the character wears a man's body in the layout TBSE shares: the Glamourer-displayed body code when there is
    /// one; otherwise every body code drawn and every head code drawn are men's. Unknown is not a man — an empty or
    /// missing set answers false — and a woman's face vetoes a body drawn wholly in c0101 gear the game fell back to.
    /// </summary>
    internal static bool WearerIsMan(string? glamCode, IReadOnlyCollection<string>? bodyCodes, IReadOnlyCollection<string>? headCodes)
    {
        if (glamCode != null) return SkinLayouts.GroupOfCode(glamCode) == 'M';
        if (bodyCodes == null || bodyCodes.Count == 0) return false;
        if (!bodyCodes.All(c => SkinLayouts.GroupOfCode(c) == 'M')) return false;
        return headCodes == null || headCodes.All(c => SkinLayouts.GroupOfCode(c) == 'M');
    }

    /// <summary>
    /// The men's layout a body skin material is drawn in on a man: <see cref="TbseSpace"/> for <c>_b</c>,
    /// <see cref="MaleVanillaSpace"/> for <c>_a</c>, under <c>chara/human/cRRxx/obj/body/</c> for a race of the men's
    /// layout group. Null for anything else — a woman, Lalafell, male Hrothgar, <c>_bibo</c>/<c>_eve</c> skin,
    /// equipment-hosted skin — which keeps <see cref="InferBodyType"/>'s reading.
    /// <para/>
    /// TBSE's Highlander chest (c0301) shares the Midlander one's uv layout texel for texel (measured: IoU 1.0), so every
    /// race of the group is TBSE's layout.
    /// </summary>
    internal static string? LayoutOfMaterial(string mtrlGamePath, bool wearerIsMan)
    {
        if (!wearerIsMan || !mtrlGamePath.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase)) return null;
        if (SkinLayouts.GroupOfCode(HumanCode(mtrlGamePath)) != 'M') return null;
        if (mtrlGamePath.EndsWith("_b.mtrl", StringComparison.OrdinalIgnoreCase)) return TbseSpace;
        if (mtrlGamePath.EndsWith("_a.mtrl", StringComparison.OrdinalIgnoreCase)) return MaleVanillaSpace;
        return null;
    }

    /// <summary>
    /// The men's layout of a body part read from a model, by its skin material's short name (<c>/mt_c0101b0001_b.mtrl</c>):
    /// what <see cref="LayoutOfMaterial"/> answers for a full path, for a shell, whose parts carry no game path.
    /// </summary>
    internal static string? LayoutOfPartMaterial(string materialName, bool wearerIsMan)
    {
        if (!wearerIsMan) return null;
        var n = materialName.TrimStart('/');
        if (!n.StartsWith("mt_c", StringComparison.OrdinalIgnoreCase) || n.Length < 9 || char.ToLowerInvariant(n[8]) != 'b')
            return null;
        if (SkinLayouts.GroupOf(n.Substring(4, 2)) != 'M') return null;
        if (n.EndsWith("_b.mtrl", StringComparison.OrdinalIgnoreCase)) return TbseSpace;
        if (n.EndsWith("_a.mtrl", StringComparison.OrdinalIgnoreCase)) return MaleVanillaSpace;
        return null;
    }

    /// <summary>"c0101" from <c>chara/human/c0101/…</c>, else null.</summary>
    private static string? HumanCode(string path)
    {
        const string prefix = "chara/human/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        int slash = path.IndexOf('/', prefix.Length);
        return slash > prefix.Length ? path[prefix.Length..slash] : null;
    }

    /// <summary>Whether a material an overlay names is a woman's body skin: a women's race's, or bibo/Eve skin.</summary>
    private static bool IsWomensBodyPath(string path)
        => path.EndsWith("_bibo.mtrl", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith("_eve.mtrl", StringComparison.OrdinalIgnoreCase)
           || (path.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase) && SkinLayouts.GroupOfCode(HumanCode(path)) == 'F');

    /// <summary>
    /// The route a woman's art takes onto a man's material in layout <paramref name="dstLayout"/>, or null when it is not
    /// a woman's art (or the material is no man's), which leaves it on today's path exactly.
    /// <para/>
    /// A woman's art is art saved as bibo — only ever a women's layout — or art with no saved layout whose material list
    /// names a woman's body and reads as gen3. Art saved as gen3 or gen2 on a man is HIS: the Luminis and emissive
    /// importers save the wearer's own reading, which for TBSE is gen3, and Onion's women's gen3/vanilla layers cannot be
    /// told from it (the user's call: his stays his).
    /// </summary>
    internal static MensRoute? MensRouteFor(string? declared, string? legacySrc, IReadOnlyCollection<string> paths, string? dstLayout)
    {
        if (dstLayout is not (TbseSpace or MaleVanillaSpace)) return null;
        if (string.Equals(declared, "bibo", StringComparison.OrdinalIgnoreCase)
            || (declared == null && string.Equals(legacySrc, "bibo", StringComparison.OrdinalIgnoreCase)))
            return new MensRoute("bibo", dstLayout);
        if (declared == null && string.Equals(legacySrc, "gen3", StringComparison.OrdinalIgnoreCase) && paths.Any(IsWomensBodyPath))
            return new MensRoute("gen3", dstLayout);
        return null;
    }

    private const string MensKeyPrefix = "men:";

    /// <summary>
    /// A source type carrying a men's route, for the compositor's remap sites: they all pass the overlay's source type
    /// along (and cache by it), so a route folded into it reaches every one of them — overlay, mask, index, claim,
    /// silhouette — without each being taught about routes. <paramref name="legacy"/> is what the site would have used,
    /// and what it falls back to when the route cannot be taken.
    /// </summary>
    internal static string MensKey(MensRoute route, string? legacy) => $"{MensKeyPrefix}{route.Src}>{route.Dst}|{legacy}";

    /// <summary>The route and fallback a <see cref="MensKey"/> carries; null for any other source type.</summary>
    internal static (MensRoute Route, string? Legacy)? ParseMensKey(string? srcType)
    {
        if (srcType == null || !srcType.StartsWith(MensKeyPrefix, StringComparison.Ordinal)) return null;
        int gt = srcType.IndexOf('>'), bar = srcType.IndexOf('|');
        if (gt < 0 || bar < gt) return null;
        string legacy = srcType[(bar + 1)..];
        return (new MensRoute(srcType[MensKeyPrefix.Length..gt], srcType[(gt + 1)..bar]), legacy.Length == 0 ? null : legacy);
    }
}

/// <summary>A woman's art landing on a man's material: from layout <paramref name="Src"/> to men's layout <paramref name="Dst"/>.</summary>
internal sealed record MensRoute(string Src, string Dst);
