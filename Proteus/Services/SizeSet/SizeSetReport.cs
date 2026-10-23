using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace Proteus.Services;

/// <summary>
/// The headless size set's report: what was asked, what the run found and every size's outcome, as JSON a batch script
/// sorts mods by. Local output — it names folders on this machine and exception text — and never published with a pack.
/// </summary>
internal static class SizeSetReport
{
    internal static JsonObject Build(string mod, int build, SizeSetBatch.Outcome outcome, SizeSetRefit.RunLog runLog,
                                     bool men, bool ends)
    {
        var sizes = new JsonArray();
        foreach (var s in runLog.Sizes)
        {
            var m = s.Measures;
            sizes.Add(new JsonObject
            {
                ["piece"] = s.Piece, ["gamePath"] = s.GamePath, ["slot"] = s.Slot, ["group"] = s.Group,
                ["option"] = s.Option, ["body"] = s.Body, ["bodyMod"] = s.BodyMod, ["yas"] = s.Yas, ["men"] = s.Men,
                ["status"] = s.Status.ToString().ToLowerInvariant(), ["reasonCode"] = s.ReasonCode, ["reason"] = s.Reason,
                ["from"] = s.From,
                ["folded"] = m?.Folded, ["unmatched"] = m?.Unmatched, ["worstMoveMm"] = Round(m?.WorstMoveMm),
                ["worstPushMm"] = Round(m?.WorstPushMm), ["pushed"] = m?.Pushed, ["snapRate"] = Round(m?.SnapRate, 3),
                ["shapeFallbacks"] = m?.ShapeFallbacks, ["coverage"] = Round(m?.Coverage, 3),
                ["skinOnly"] = m?.SkinOnly ?? false, ["seconds"] = Round(s.Seconds),
            });
        }
        return new JsonObject
        {
            ["mod"] = mod,
            ["build"] = build,
            ["bodies"] = new JsonArray(outcome.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["men"] = men, ["yas"] = true, ["ends"] = ends,
            ["refusal"] = outcome.Refusal, ["refusalCode"] = outcome.RefusalCode,
            ["migrated"] = outcome.Migrated is { } mig
                ? new JsonObject
                {
                    ["fromVersion"] = mig.FromVersion, ["groups"] = mig.Groups,
                    ["leftover"] = new JsonArray(mig.Leftover.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
                }
                : null,
            ["cancelled"] = outcome.Cancelled,
            ["made"] = runLog.Made,
            ["seconds"] = Round(outcome.Seconds), ["scanSeconds"] = Round(outcome.ScanSeconds),
            ["migrateSeconds"] = Round(outcome.MigrateSeconds, 3),
            ["bodyMods"] = new JsonArray(outcome.BodyMods.Select(b => (JsonNode?)new JsonObject
            {
                ["key"] = b.Key, ["slot"] = b.Slot, ["yas"] = b.Yas, ["folder"] = b.Folder,
            }).ToArray()),
            ["missingBodies"] = new JsonArray(outcome.MissingBodies.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["notes"] = new JsonArray(runLog.Notes.Distinct(StringComparer.Ordinal).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["warnings"] = new JsonArray(runLog.Warnings.Distinct(StringComparer.Ordinal).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["sizes"] = sizes,
        };
    }

    private static double? Round(double? value, int digits = 2) => value is { } v ? Math.Round(v, digits) : null;
    private static double? Round(float? value, int digits = 2) => value is { } v ? Math.Round((double)v, digits) : null;

    /// <summary>The report as text: indented, non-ASCII names as themselves, numbers in the invariant culture.</summary>
    internal static string ToText(JsonObject report)
        => report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = ProteusJson.Encoder });
}
