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
    /// <summary>The body — the only surface shells were ever cut from before this existed. Includes body-UV
    /// skin that body mods route through <c>chara/equipment/</c> slots.</summary>
    Body,
    Face,

    /// <summary>
    /// The eyes. Lives in the face's own folder and is cut from a face model like <see cref="Face"/> is,
    /// but is a surface of its OWN because the two must never share one.
    /// <para/>
    /// A face model draws several meshes — <c>_fac</c> beside <c>_iri</c> and <c>_etc</c> — each with its
    /// own material and its own UV layout. Surfaces are resolved once per key and every layer on a key
    /// shares one source model and one mesh filter, so folding the iris into Face meant a character
    /// wearing both a face overlay and an eye overlay got a single shell: one overlay's art on the other's
    /// geometry, at the other's UVs, with nothing saying so.
    /// </summary>
    Iris,

    Hair,
    Tail,
    /// <summary>Viera ears (<c>obj/zear</c>).</summary>
    Ear,

    /// <summary>
    /// Geometry an imported pack authored for ONE race, which must be published at that race with no
    /// deform. Not cut from the character at all — the pack brought it.
    /// <para/>
    /// Its <see cref="ShellSurfaceKey.Id"/> is the race code the model was authored for ("0801"), so two
    /// pieces built for different races can never share a surface, a host, or a published material.
    /// <para/>
    /// The distinction it exists for: a model at <c>c0201</c> is in the shared cut space every "Midlander-
    /// bodied" race falls through to, and the game deforms it onto the wearer — which is what content pieces
    /// have always relied on. A model at <c>c0801</c> is already Miqo'te-shaped, so that same deform is
    /// damage. Deciding between the two is <c>SecondSkinService</c>'s job, because it depends on who is
    /// wearing it: a pack shipping both is cut space for a Midlander and native for a Miqo'te.
    /// </summary>
    Native,
}

/// <summary>
/// One cuttable surface. <paramref name="Id"/> is the game's part id — "f0001", "h0133", "t0001" — and is
/// empty for <see cref="ShellSurfaceKind.Body"/>, which a character only ever has one of. Two overlays
/// naming the same surface share its geometry, its UV space and its race space, which is precisely what
/// makes them mergeable into one shell.
/// </summary>
public readonly record struct ShellSurfaceKey(ShellSurfaceKind Kind, string Id)
{
    public bool IsBody => Kind == ShellSurfaceKind.Body;

    /// <summary>
    /// Whether a shell on this surface must be hosted with NO race deformation. The body is cut from
    /// equipment models in a shared model space (c0201 for every "Midlander-bodied" race) and depends on
    /// the game deforming it onto the wearer. Every human part is the opposite: it is authored at the
    /// character's own race (c1401f0001_fac.mdl) and is already the right shape, so any deform is damage.
    /// Only a carrier host can promise that — an append host's EQDP belongs to the player's own item.
    /// </summary>
    public bool RequiresNativeHost => Kind != ShellSurfaceKind.Body;

    /// <summary>
    /// How far off the source surface a shell on this one sits, as a multiple of
    /// <c>SecondSkinWriter.BaseOffset</c>.
    /// <para/>
    /// That offset is an empirical millimetre tuned against a torso, where it is invisible. An eye is a few
    /// millimetres across, so the same push is a large fraction of the iris radius — enough to stand the
    /// shell off the cornea or through the eyelid. Everything else keeps the tuned value; only the surface
    /// whose scale is an order of magnitude smaller asks for less.
    /// </summary>
    public float PushScale => Kind == ShellSurfaceKind.Iris ? 0.1f : 1f;

    public override string ToString() => Id.Length == 0 ? Kind.ToString() : $"{Kind}:{Id}";
}

/// <summary>
/// Classifies a material game path into the surface a shell for it would be cut from. Path-only and
/// game-state-free, so the compositor, the shell builder and the editor can all agree without any of them
/// needing a draw object — the same reason <see cref="RenderModeInference"/> is shaped this way.
/// <para/>
/// Deliberately separate from <see cref="Proteus.Services.CompositorService.IsBodyUvMaterial"/>, which asks
/// a different question ("is this art in body UV") for the skin bake. Conflating the two would make every
/// non-body surface look like a body one.
/// </summary>
public static class ShellSurface
{
    // chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl -> ("face", "f0001")
    private static readonly Regex HumanPartRe = new(
        @"/obj/(face|hair|tail|zear)/([a-z]\d+)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // …/material/mt_c0201f0001_iri_a.mtrl — the eye material inside a face folder. Anchored on the file
    // name so a folder that merely contains "iri" cannot trip it.
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

        // Anchored on chara/human/ before anything else is asked, because "/obj/body/" is NOT unique to this
        // character. Two real paths off a live scene carry it:
        //   chara/weapon/w0801/obj/body/b0006/material/v0001/mt_w0801b0006_a.mtrl   (an equipped greatsword)
        //   chara/monster/m0361/obj/body/b0001/material/v0001/mt_m0361b0001_a.mtrl (a mount or minion)
        // Both would otherwise classify as the character's Body surface — and the second one gets there
        // through InferBodyType as well, so testing the suffix instead of the tree does not save you.
        // A shell cut for a mount is not a thing that can be made to work.
        if (mtrlGamePath.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase))
        {
            if (mtrlGamePath.Contains("/obj/body/", StringComparison.OrdinalIgnoreCase))
                return new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty);

            var m = HumanPartRe.Match(mtrlGamePath);
            if (!m.Success) return null;
            var kind = m.Groups[1].Value.ToLowerInvariant() switch
            {
                // The eyes ship inside the face folder but are their own surface — see ShellSurfaceKind.Iris.
                // Matched on the material's own name, which is the model's declared target and cannot drift
                // the way a folder convention might.
                "face" => IrisRe.IsMatch(mtrlGamePath) ? ShellSurfaceKind.Iris : ShellSurfaceKind.Face,
                "hair" => ShellSurfaceKind.Hair,
                "tail" => ShellSurfaceKind.Tail,
                _      => ShellSurfaceKind.Ear,
            };
            return new ShellSurfaceKey(kind, m.Groups[2].Value.ToLowerInvariant());
        }

        // Body-UV skin that a body mod ships through an EQUIPMENT slot (mt_c0201e0000_top_bibo.mtrl and
        // friends). No /obj/body/ segment anywhere, but it is the body, and shells have always been cut
        // from exactly these models. Restricted to that one tree for the reason above.
        return mtrlGamePath.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase)
            && UVRemapService.InferBodyType(mtrlGamePath) != null
                ? new ShellSurfaceKey(ShellSurfaceKind.Body, string.Empty)
                : null;
    }

    /// <summary>
    /// Every distinct surface an overlay paints, in the order its materials name them. Usually one — an
    /// overlay that spans two surfaces (a mod painting face and body from a single entry) is legitimate and
    /// becomes one layer per surface, since each needs its own geometry and its own coverage.
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
    /// Whether any shell at all can be cut for this overlay. An overlay naming no material is left
    /// shellable: it cannot be placed either way, so this keeps the pre-existing behaviour rather than
    /// silently taking a feature away from it.
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
