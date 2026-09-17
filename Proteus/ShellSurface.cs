using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Proteus.Services;

namespace Proteus;

/// <summary>Which piece of the character a shell is cut from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShellSurfaceKind
{
    /// <summary>The body. Includes body-UV skin that body mods route through <c>chara/equipment/</c> slots.</summary>
    Body,
    Face,

    /// <summary>
    /// The eyes. Cut from a face model like <see cref="Face"/>, but a separate surface because each key shares one
    /// source model and mesh filter, and the iris has its own mesh, material and UV layout.
    /// </summary>
    Iris,

    Hair,
    Tail,
    /// <summary>Viera ears (<c>obj/zear</c>).</summary>
    Ear,

    /// <summary>
    /// Geometry an imported pack authored for ONE race, published at that race with no deform; not cut from the
    /// character. Its <see cref="ShellSurfaceKey.Id"/> is that race code ("0801"), so pieces for different races
    /// never share a surface. Whether a piece is native or cut space depends on the wearer; <c>SecondSkinService</c> decides.
    /// </summary>
    Native,
}

/// <summary>
/// One cuttable surface. <paramref name="Id"/> is the game's part id ("f0001", "h0133", "t0001"), empty for
/// <see cref="ShellSurfaceKind.Body"/>. Overlays naming the same surface share geometry, UV and race space, so
/// they can merge into one shell.
/// </summary>
public readonly record struct ShellSurfaceKey(ShellSurfaceKind Kind, string Id)
{
    public bool IsBody => Kind == ShellSurfaceKind.Body;

    /// <summary>
    /// Whether a shell on this surface must be hosted with NO race deformation. The body lives in a shared model
    /// space the game deforms onto the wearer; every human part is authored at the character's own race, so any
    /// deform is damage. Only a carrier host can promise that.
    /// </summary>
    public bool RequiresNativeHost => Kind != ShellSurfaceKind.Body;

    /// <summary>
    /// How far off the source surface a shell on this one sits, as a multiple of
    /// <c>SecondSkinWriter.BaseOffset</c>. 1 everywhere; the hook stays for a surface that needs another.
    /// </summary>
    public float PushScale => 1f;

    public override string ToString() => Id.Length == 0 ? Kind.ToString() : $"{Kind}:{Id}";
}

/// <summary>
/// Classifies a material game path into the surface a shell for it would be cut from. Path-only, so every caller
/// agrees without a draw object. Separate from <see cref="Proteus.Services.CompositorService.IsBodyUvMaterial"/>,
/// which asks whether art is in body UV.
/// </summary>
public static class ShellSurface
{
    // chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl -> ("face", "f0001")
    private static readonly Regex HumanPartRe = new(
        @"/obj/(face|hair|tail|zear)/([a-z]\d+)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // …/material/mt_c0201f0001_iri_a.mtrl, the eye material inside a face folder. Anchored on the file name.
    private static readonly Regex IrisRe = new(
        @"/mt_[^/]*_iri[_.][^/]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The surface a material belongs to, or null when no shell can be cut for it — gear, accessories,
    /// weapons, mounts, anything that is not this character's own skin.
    /// </summary>
    public static ShellSurfaceKey? KeyFor(string? mtrlGamePath)
    {
        if (string.IsNullOrEmpty(mtrlGamePath)) return null;

        // Anchored on chara/human/ first: "/obj/body/" also appears under chara/weapon/ and chara/monster/.
        if (mtrlGamePath.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase))
        {
            if (mtrlGamePath.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase))
                return new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty);

            var m = HumanPartRe.Match(mtrlGamePath);
            if (!m.Success) return null;
            var kind = m.Groups[1].Value.ToLowerInvariant() switch
            {
                // The eyes ship inside the face folder but are their own surface; matched on the material name.
                "face" => IrisRe.IsMatch(mtrlGamePath) ? ShellSurfaceKind.Iris : ShellSurfaceKind.Face,
                "hair" => ShellSurfaceKind.Hair,
                "tail" => ShellSurfaceKind.Tail,
                _      => ShellSurfaceKind.Ear,
            };
            return new ShellSurfaceKey(kind, m.Groups[2].Value.ToLowerInvariant());
        }

        // Body-UV skin that a body mod ships through an EQUIPMENT slot (mt_c0201e0000_top_*.mtrl). Restricted
        // to that tree for the same reason as above.
        return mtrlGamePath.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase)
            && UVRemapService.InferBodyType(mtrlGamePath) != null
                ? new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty)
                : null;
    }

    /// <summary>
    /// Every distinct surface an overlay paints, in the order its materials name them; an overlay spanning two
    /// surfaces becomes one layer per surface.
    /// </summary>
    public static IReadOnlyList<ShellSurfaceKey> KeysFor(IEnumerable<string>? materialGamePaths)
    {
        if (materialGamePaths == null) return [];
        var seen = new List<ShellSurfaceKey>();
        foreach (var p in materialGamePaths)
            if (KeyFor(p) is { } k && !seen.Contains(k))
                seen.Add(k);
        return seen;
    }

    /// <summary>
    /// Whether any shell at all can be cut for this overlay. An overlay naming no material is left shellable.
    /// </summary>
    public static bool CanShell(IReadOnlyList<string>? materialGamePaths)
        => materialGamePaths == null || materialGamePaths.Count == 0 || KeysFor(materialGamePaths).Count > 0;

    /// <summary>Short display tag for a surface, shared with the material picker's left column.</summary>
    public static string Label(ShellSurfaceKind kind) => kind switch
    {
        ShellSurfaceKind.Body   => "Body",
        ShellSurfaceKind.Face   => "Face",
        ShellSurfaceKind.Iris   => "Iris",
        ShellSurfaceKind.Hair   => "Hair",
        ShellSurfaceKind.Tail   => "Tail",
        ShellSurfaceKind.Native => "Native",
        _                       => "Ear",
    };
}
