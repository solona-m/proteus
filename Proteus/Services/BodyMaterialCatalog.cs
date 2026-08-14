using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Every human body material path in the game, by material suffix — what an imported overlay lists in its
/// <c>MaterialGamePath</c> so it applies to any race the player switches to.
/// <para/>
/// The list is DISCOVERED, not hardcoded: only the vanilla <c>_a.mtrl</c> of each body actually ships in
/// the game data, so probing for that answers "which race/body pairs exist" without anyone having to
/// maintain a race table (there isn't one anywhere else in Proteus). Modded bodies place their own
/// material beside the vanilla one at the same stem — <c>mt_c0201b0001_bibo.mtrl</c> next to
/// <c>mt_c0201b0001_a.mtrl</c> — so swapping the suffix onto a discovered stem names the modded path
/// correctly for every race at once.
/// <para/>
/// A superset is harmless: bodies the character isn't wearing are dropped at composite time by
/// CompositorService's body-material filter, which keeps only the effective race's paths.
/// </summary>
public sealed class BodyMaterialCatalog(Func<string, bool> gameFileExists)
{
    /// <summary>
    /// Race codes to probe. Every human code the game defines (Midlander through Viera, both sexes); the
    /// ones with no body of their own — Elezen, Miqo'te and Lalafell draw a Midlander body — simply don't
    /// answer the probe and drop out.
    /// </summary>
    private static readonly string[] RaceCodes =
    [
        "c0101", "c0201", "c0301", "c0401", "c0501", "c0601", "c0701", "c0801", "c0901",
        "c1001", "c1101", "c1201", "c1301", "c1401", "c1501", "c1601", "c1701", "c1801",
    ];

    /// <summary>
    /// Body ids to probe per race. b0001 is the ordinary body; Roegadyn females use b0002, and the Au Ra
    /// "b0101" variant is a real second body. Probing settles which of these each race actually has.
    /// </summary>
    private static readonly string[] BodyIds = ["b0001", "b0002", "b0003", "b0101"];

    /// <summary>
    /// Used when the probe finds nothing at all — Lumina not up, or the game data unreadable. These are the
    /// stems the community "Extras" Proteus pack ships, i.e. the female bodies a skin overlay actually
    /// targets in practice. Better than an empty list, which would write a mod that can never apply.
    /// <para/>
    /// Deliberately NOT cached as if it were a probe result: it names no male body and no race outside this
    /// list, so a session that fell back once and then remembered it would quietly write imports that can
    /// never apply to half of all characters. A miss stays a miss and re-probes on the next call.
    /// </summary>
    private static readonly string[] FallbackStems =
    [
        "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001",
        "chara/human/c0401/obj/body/b0001/material/v0001/mt_c0401b0001",
        "chara/human/c1001/obj/body/b0002/material/v0001/mt_c1001b0002",
        "chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001",
        "chara/human/c1401/obj/body/b0101/material/v0001/mt_c1401b0101",
        "chara/human/c1601/obj/body/b0001/material/v0001/mt_c1601b0001",
        "chara/human/c1801/obj/body/b0001/material/v0001/mt_c1801b0001",
    ];

    private readonly Func<string, bool> exists = gameFileExists;
    private readonly object gate = new();
    private IReadOnlyList<string>? stems;

    /// <summary>
    /// Body material paths for one material suffix (<c>"_bibo.mtrl"</c>, <c>"_b.mtrl"</c>,
    /// <c>"_a.mtrl"</c>, <c>"_eve.mtrl"</c>). The probe runs once and is cached for the plugin lifetime —
    /// the game's own body list can't change while it's running.
    /// </summary>
    public IReadOnlyList<string> ForSuffix(string mtrlSuffix)
        => Stems().Select(s => s + mtrlSuffix).ToList();

    /// <summary>How many bodies the probe found, for the import report.</summary>
    public int Count => Stems().Count;

    /// <summary>
    /// Whether the last call answered from the game data rather than <see cref="FallbackStems"/>. False
    /// means the list is the hardcoded female-only set and an import made from it won't reach a male body.
    /// </summary>
    public bool FromGameData { get { lock (gate) return stems != null; } }

    private IReadOnlyList<string> Stems()
    {
        lock (gate)
        {
            if (stems != null) return stems;

            var found = new List<string>();
            foreach (var code in RaceCodes)
                foreach (var body in BodyIds)
                {
                    var stem = $"chara/human/{code}/obj/body/{body}/material/v0001/mt_{code}{body}";
                    // The vanilla suffix is the probe: it is the only one that ships in the game data, and
                    // a modded body's material sits at the same stem.
                    bool hit;
                    try { hit = exists(stem + "_a.mtrl"); }
                    catch { hit = false; }   // Lumina down mid-probe — treat as absent, fall back below
                    if (hit) found.Add(stem);
                }

            // Cache ONLY a real answer. The game's body list can't change while it's running, so a hit is
            // good forever — but a miss means the game data wasn't readable at that moment, and pinning the
            // fallback would make one bad moment permanent for the session.
            if (found.Count == 0) return FallbackStems;
            return stems = found;
        }
    }
}
