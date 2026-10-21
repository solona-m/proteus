using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using Proteus.Services;

namespace Proteus.Refit;

/// <summary>
/// <c>--for-race</c>: a garment refitted for the other sex (see <see cref="CrossSexRefit"/>), through the same job the
/// real-data gates run (<see cref="CrossSexRefitJob"/>). Each size that passes the save check is written as
/// <c>&lt;label&gt;.mdl</c>, to be saved at the item's path for the picked race with the EQDP entry the report names.
/// </summary>
internal static class CrossSexCommand
{
    /// <returns>0 when it ran (refused sizes say so in the report), 2 on a usage error.</returns>
    internal static int Run(Dictionary<string, string> opts, Func<Dictionary<string, string>, string, string> required,
                            Action<string> usageError, IPluginLog log)
    {
        string garmentPath = required(opts, "garment");
        string slot = opts.TryGetValue("slot", out var s) ? s : SlotOfFile(garmentPath) ?? "_top";
        if (CrossSexRefit.ValidateCli(opts.GetValueOrDefault("race"), opts.GetValueOrDefault("for-race"), slot, opts.Keys)
            is { } error)
        {
            usageError(error);
            return 2;
        }
        ushort drawn = ushort.Parse(opts["race"]), to = ushort.Parse(opts["for-race"]);
        int set = SetOfFile(garmentPath) ?? int.Parse(required(opts, "set"));
        string gamePath = $"chara/equipment/e{set:D4}/model/c{drawn:D4}e{set:D4}{slot}.mdl";

        var data = new Lumina.GameData(required(opts, "game"));
        byte[]? Game(string path) => data.GetFile(path)?.Data;
        string? skeleton = opts.GetValueOrDefault("skeleton");
        var wearer = OtherRaceWearer.Gather(to, gamePath, Game,
            path => skeleton != null && path == OtherRaceWearer.BaseSkeletonPath(to) ? Path.GetFullPath(skeleton) : null,
            _ => null, []);

        var madeFor = BodySizeCatalog.Read(required(opts, "from-root"));
        var onto = BodySizeCatalog.Read(required(opts, "body-root"));
        string fromArg = required(opts, "from");
        var targets = required(opts, "to").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                         .Select(t => t.Split('=', 2)).ToDictionary(t => Norm(t[0]), t => t.Length > 1 ? t[1] : t[0]);
        // The size it was made on, named: across the sexes there is no detecting it against the other sex's bodies, and
        // a guess would write models from the wrong size.
        var original = madeFor.Options.FirstOrDefault(o => Norm(o.Rel) == Norm(fromArg));
        if (original == null)
        {
            usageError(fromArg == "auto"
                ? "--for-race needs --from <rel>: the size the garment was made on (auto is not read across the sexes)"
                : $"--from names no body of {opts["from-root"]}: \"{fromArg}\"");
            return 2;
        }
        Func<BodyOption, bool> fromPick = o => o.Group == original.Group && o.Section == original.Section && o.Name == original.Name;
        if (targets.Keys.FirstOrDefault(t => onto.Options.All(o => Norm(o.Rel) != t)) is { } unknown)
        {
            usageError($"--to names no body of {opts["body-root"]}: \"{unknown}\"");
            return 2;
        }

        var uv = new UVRemapService(log, required(opts, "uvmaps"));
        var result = CrossSexRefitJob.Run(new CrossSexRefitJob.Inputs(
            File.ReadAllBytes(garmentPath), gamePath, to, madeFor, fromPick, onto, o => targets.ContainsKey(Norm(o.Rel)),
            wearer, uv, log, PushOut: !opts.ContainsKey("no-push"), CarryPiercings: opts.ContainsKey("piercings")));

        string outDir = required(opts, "out-dir");
        var sizes = new JsonArray();
        foreach (var size in result.Sizes)
        {
            string label = targets[Norm(size.Target.Rel)];
            var entry = new JsonObject
            {
                ["label"] = label, ["to"] = size.Target.Rel, ["folds"] = size.Folds,
                ["coverage"] = size.Landed?.VertexCoverage ?? 0f,
            };
            string refusal = size.Refusal.Length > 0 ? size.Refusal
                           : !size.SaveAllowed ? string.Format(Localization.Strings.Parts.RetargetRefuseCrossSexQualityFmt,
                                 size.Folds, $"{size.Landed?.VertexCoverage:P0}", CrossSexRefit.MaxFolds,
                                 $"{CrossSexRefit.MinCoverage:P0}")
                           : "";
            if (refusal.Length > 0) entry["refusal"] = refusal;
            else
            {
                Directory.CreateDirectory(outDir);
                string file = Path.Combine(outDir, label + ".mdl");
                File.WriteAllBytes(file, size.Saved!.Model);
                entry["file"] = file;
            }
            sizes.Add(entry);
        }
        string savePath = result.Route.Any ? result.Route.SavePath(gamePath) : gamePath;
        var report = new JsonObject
        {
            ["garment"] = garmentPath, ["race"] = $"{drawn:D4}", ["forRace"] = $"{to:D4}", ["eqdpRace"] = $"{to:D4}",
            ["savePath"] = savePath, ["from"] = result.SourcePath,
            ["note"] = $"save each model at {savePath} with an EQDP entry giving c{to:D4} a model of the set",
            ["sizes"] = sizes,
        };
        if (result.Refusal.Length > 0) report["refusal"] = result.Refusal;
        Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static string Norm(string rel) => rel.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    /// <summary>"_dwn" off a file named like the game's (c0201e6107_dwn.mdl), or null.</summary>
    private static string? SlotOfFile(string path)
        => BodySizeCatalog.SlotOf("x" + Path.GetFileName(path).ToLowerInvariant());

    /// <summary>6107 off a file named like the game's (c0201e6107_dwn.mdl), or null.</summary>
    private static int? SetOfFile(string path)
        => System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path), @"^c\d{4}e(\d{4})_",
                                                      System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m
            ? int.Parse(m.Groups[1].Value) : null;
}
