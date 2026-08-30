using System;
using System.Collections.Generic;

namespace Proteus.Services;

/// <summary>
/// Every iris material path in the game — what an imported eye overlay lists in its
/// <c>MaterialGamePath</c> so it follows the wearer across races and faces.
/// <para/>
/// The same shape as <see cref="BodyMaterialCatalog"/>, and for the same reason: the list is DISCOVERED by
/// probing the game data rather than maintained by hand, so nobody has to keep a table of which races have
/// which faces (there isn't one anywhere else in Proteus, and Dawntrail added more).
/// <para/>
/// Why a catalogue at all, when the eye TEXTURES are shared across every character
/// (<c>chara/common/texture/eye/eye01_*.tex</c>)? Because the shared thing is the texture and the
/// per-character thing is the material that names it. Proteus matches an overlay to a material by exact
/// path, so an import pinned to whichever face was worn at the time would stop painting the moment the
/// wearer changed face — or never paint at all for anyone else. A superset costs nothing: the compositor
/// drops materials the character does not have loaded.
/// </summary>
public sealed class IrisMaterialCatalog(Func<string, bool> gameFileExists)
{
    /// <summary>
    /// Race codes to probe — every human code the game defines, as
    /// <see cref="BodyMaterialCatalog"/> uses. Unlike bodies, faces are NOT shared between races: each
    /// race draws its own, so the ones that answer no probe simply drop out.
    /// </summary>
    private static readonly string[] RaceCodes =
    [
        "c0101", "c0201", "c0301", "c0401", "c0501", "c0601", "c0701", "c0801", "c0901",
        "c1001", "c1101", "c1201", "c1301", "c1401", "c1501", "c1601", "c1701", "c1801",
    ];

    /// <summary>
    /// Face ids to probe per race. Faces run f0001 upward, and several races carry a second bank at f0101
    /// (Au Ra's scaled faces, the Hrothgar and Viera variants). Probing settles which each race has;
    /// the range is deliberately wider than any race uses, because an id that does not exist costs one
    /// failed lookup and an id that is missed costs the wearer their overlay.
    /// </summary>
    private static readonly string[] FaceIds =
    [
        "f0001", "f0002", "f0003", "f0004", "f0005", "f0006", "f0007", "f0008",
        "f0101", "f0102", "f0103", "f0104", "f0105", "f0106", "f0107", "f0108",
    ];

    /// <summary>
    /// Used when the probe finds nothing at all — the game data unreadable, or Lumina not up. Midlander
    /// female and male at face 1, which is what most characters are on and is better than an empty list
    /// (which would write a mod that can never apply).
    /// <para/>
    /// Deliberately NOT cached as a probe result, for the reason <see cref="BodyMaterialCatalog"/> gives:
    /// one bad moment would otherwise become permanent for the session.
    /// </summary>
    private static readonly string[] FallbackPaths =
    [
        "chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_iri_a.mtrl",
        "chara/human/c0101/obj/face/f0001/material/mt_c0101f0001_iri_a.mtrl",
    ];

    private readonly Func<string, bool> exists = gameFileExists;
    private readonly object gate = new();
    private IReadOnlyList<string>? paths;

    /// <summary>Every iris material the game ships. Probed once and cached for the plugin lifetime.</summary>
    public IReadOnlyList<string> All() => Paths();

    /// <summary>How many the probe found, for the import report.</summary>
    public int Count => Paths().Count;

    /// <summary>Whether the last call answered from the game data rather than <see cref="FallbackPaths"/>.
    /// False means the list is the hardcoded pair and an import made from it reaches almost nobody.</summary>
    public bool FromGameData { get { lock (gate) return paths != null; } }

    private IReadOnlyList<string> Paths()
    {
        lock (gate)
        {
            if (paths != null) return paths;

            var found = new List<string>();
            foreach (var code in RaceCodes)
                foreach (var face in FaceIds)
                {
                    // No /v0001/ here — a face material sits directly under material/, unlike a body's.
                    var path = $"chara/human/{code}/obj/face/{face}/material/mt_{code}{face}_iri_a.mtrl";
                    bool hit;
                    try { hit = exists(path); }
                    catch { hit = false; }   // Lumina down mid-probe — treat as absent, fall back below
                    if (hit) found.Add(path);
                }

            if (found.Count == 0) return FallbackPaths;
            return paths = found;
        }
    }
}
