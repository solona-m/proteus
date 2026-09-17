using System;
using System.Collections.Generic;
using System.Linq;

namespace Proteus.Services;

/// <summary>
/// Every human body material path in the game, by material suffix — what an imported overlay lists in its
/// <c>MaterialGamePath</c> so it applies to any race the player switches to.
/// Discovered by probing each stem's vanilla <c>_a.mtrl</c>; modded bodies sit at the same stem with another
/// suffix. A superset is harmless: the compositor drops bodies the character isn't wearing.
/// </summary>
public sealed class BodyMaterialCatalog(Func<string, bool> gameFileExists)
{
    /// <summary>
    /// Race codes to probe: every human code; those with no body of their own drop out.
    /// </summary>
    private static readonly string[] RaceCodes =
    [
        "c0101", "c0201", "c0301", "c0401", "c0501", "c0601", "c0701", "c0801", "c0901",
        "c1001", "c1101", "c1201", "c1301", "c1401", "c1501", "c1601", "c1701", "c1801",
    ];

    /// <summary>
    /// Body ids to probe per race; probing settles which each race actually has.
    /// </summary>
    private static readonly string[] BodyIds = ["b0001", "b0002", "b0003", "b0101"];

    /// <summary>
    /// Used when the probe finds nothing at all (game data unreadable): the common female bodies. Never cached,
    /// so a miss re-probes on the next call.
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
    /// <c>"_a.mtrl"</c>, <c>"_eve.mtrl"</c>). A successful probe is cached for the plugin lifetime.
    /// </summary>
    public IReadOnlyList<string> ForSuffix(string mtrlSuffix)
        => Stems().Select(s => s + mtrlSuffix).ToList();

    /// <summary>How many bodies the probe found, for the import report.</summary>
    public int Count => Stems().Count;

    /// <summary>
    /// Whether the last call answered from the game data rather than <see cref="FallbackStems"/>.
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
                    bool hit;
                    try { hit = exists(stem + "_a.mtrl"); }
                    catch { hit = false; }   // Lumina down mid-probe — treat as absent, fall back below
                    if (hit) found.Add(stem);
                }

            // Cache only a real answer: a miss means the game data wasn't readable at that moment.
            if (found.Count == 0) return FallbackStems;
            return stems = found;
        }
    }
}
