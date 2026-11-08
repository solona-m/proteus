using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using static Proteus.Services.DesignRestoreSweep;

namespace Proteus.Tests;

/// <summary>
/// The 2026-11-07 case on the real files: YAB+ kept by the Chenille design, clia skin drawn but not named. The sweep's
/// reading of the installed mods must find clia as the supplier of game-less files YAB+ needs, with "Skin Materials" on
/// Matte (its material's textures) and Off (the body model's bibo material itself). The collection is stood in for by a
/// resolver in which YAB+'s chosen options win and clia supplies the rest, as a restore's raised holds arrange it.
/// </summary>
public class DesignRestoreSweepLocalTests(ITestOutputHelper output)
{
    private const string Yab = "hs-Yet Another Body+-4.2.0-tmi";
    private const string Clia = "clia skin - by solona";

    private static readonly Lazy<Lumina.GameData> Game = new(() => new Lumina.GameData(LocalData.Path(LocalData.GameData)));

    private static bool Exists(string gamePath) => Game.Value.FileExists(gamePath);

    private static string ModsRoot => LocalData.Path(LocalData.Mods);

    /// <summary>YAB+'s files the restore's chosen options make active: Required Files, a Smallclothes chest and the
    /// given "Skin Materials" option (null: Off).</summary>
    private static readonly string[] ChosenGroups = ["Required Files / ---", "Chest - Smallclothes / Medium"];

    private static (Dictionary<string, ModDirFiles> Kept, Dictionary<string, ModDirFiles> Strays, Dictionary<string, string?> Resolved)
        Case(string? skin, string strayDir = Clia, Func<ModFile, bool>? strayFiles = null)
    {
        var reader = new SweepModReader();
        var yabAll = reader.Files(ModsRoot, Yab);
        Assert.False(yabAll.ManifestFailed);
        var sources = PenumbraModMeta.ReadAllRedirects(Path.Combine(ModsRoot, Yab))
            .Select(r => (Game: NormGame(r.GamePath), r.Source)).ToList();
        var chosen = ChosenGroups.Concat(skin == null ? [] : [$"Skin Materials / {skin}"]).ToHashSet(StringComparer.Ordinal);

        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < yabAll.Files.Count; i++)
            if (chosen.Contains(sources[i].Source)) resolved.TryAdd(yabAll.Files[i].GamePath, yabAll.Files[i].FullPath);

        var stray = strayDir == Clia ? reader.Files(ModsRoot, Clia) : new ModDirFiles(false, yabAll.Files.Where(strayFiles!).ToList());
        Assert.False(stray.ManifestFailed);
        foreach (var f in stray.Files) resolved.TryAdd(f.GamePath, f.FullPath);   // the kept mod wins any shared path

        return (new() { [Yab] = yabAll }, new() { [strayDir] = stray }, resolved);
    }

    private static SweepInputs In(Dictionary<string, ModDirFiles> kept, Dictionary<string, ModDirFiles> strays,
                                  Dictionary<string, string?> resolved)
        => new(ModsRoot, SidecarDiscoveryService.ManagedModDir, kept, new HashSet<string>(), strays,
               new Dictionary<string, ModDirFiles>(), resolved, [], Exists);

    [LocalDataFact(LocalData.YabMod, LocalData.CliaMod, LocalData.GameData)]
    [Trait("Plan", "AC-sweep-keeps-deps-1")]
    public void LocalData_YabMatte_Clia_Spared()
    {
        var (kept, strays, resolved) = Case("Matte");
        var p = FindProviders(In(kept, strays, resolved));
        Assert.True(p.ContainsKey(Clia), "clia skin supplies textures YAB+'s Matte material needs");
        Assert.Contains(p[Clia], n => n.GamePath == "chara/bibo_mid_base.tex");
        Assert.Contains(p[Clia], n => n.GamePath == "chara/bibo_mid_norm.tex");

        // The pre-check for the in-game let-go scenario: which "Skin Materials" options name bibo_mid_*.
        var sources = PenumbraModMeta.ReadAllRedirects(Path.Combine(ModsRoot, Yab));
        var files = kept[Yab].Files;
        foreach (var option in new[] { "Matte", "Pores", "Oily" })
        {
            bool names = files.Where((f, i) => sources[i].Source == $"Skin Materials / {option}")
                              .Any(f => f.Refs.Any(r => r.Contains("bibo_mid", StringComparison.OrdinalIgnoreCase)));
            output.WriteLine($"Skin Materials / {option}: {(names ? "references" : "does not reference")} bibo_mid_*");
        }
    }

    [LocalDataFact(LocalData.YabMod, LocalData.CliaMod, LocalData.GameData)]
    [Trait("Plan", "AC-sweep-keeps-deps-4")]
    public void LocalData_YabSkinOff_Clia_SparedViaBiboMaterial()
    {
        var (kept, strays, resolved) = Case(skin: null);
        var p = FindProviders(In(kept, strays, resolved));
        Assert.True(p.ContainsKey(Clia), "clia skin supplies the bibo material YAB+'s body model declares");
        Assert.Contains(p[Clia], n => n.GamePath.EndsWith("/mt_c0201b0001_bibo.mtrl", StringComparison.Ordinal));
    }

    [LocalDataFact(LocalData.YabMod, LocalData.GameData)]
    [Trait("Plan", "AC-sweep-keeps-deps-4")]
    public void LocalData_YabSkinOff_C1401_SparedViaSuffix()
    {
        // A stray supplying only another race's bibo material (YAB+'s own Viera one stands in for it) still matches the
        // kept c0201 model's "/mt_c0201b0001_bibo.mtrl": the game swaps race and body into shared-body skin names.
        var (kept, strays, resolved) = Case(skin: null, strayDir: "Viera Skin",
            strayFiles: f => f.GamePath.Contains("/c1401/", StringComparison.Ordinal) && f.GamePath.EndsWith("_bibo.mtrl", StringComparison.Ordinal));
        Assert.NotEmpty(strays["Viera Skin"].Files);
        // Only the stray's files resolve to it: YAB+'s Skin Materials are Off, so nothing kept supplies these paths.
        foreach (var f in strays["Viera Skin"].Files) resolved[f.GamePath] = $"{ModsRoot}/Viera Skin/{Path.GetFileName(f.FullPath)}";
        var renamed = new Dictionary<string, ModDirFiles>
        {
            ["Viera Skin"] = new(false, strays["Viera Skin"].Files
                .Select(f => f with { FullPath = NormFile($"{ModsRoot}/Viera Skin/{Path.GetFileName(f.FullPath)}") }).ToList()),
        };
        Assert.True(FindProviders(In(kept, renamed, resolved)).ContainsKey("Viera Skin"));
    }

    [LocalDataFact(LocalData.YabMod)]
    [Trait("Plan", "EDGE-sweep-keeps-deps-37")]
    public void LocalData_YabTopModel_PrefixDeclaresBiboMaterial()
    {
        var files = new SweepModReader().Files(ModsRoot, Yab);
        var top = files.Files.First(f => f.GamePath == "chara/equipment/e0000/model/c0201e0000_top.mdl");
        Assert.Contains(top.Refs, r => r.EndsWith("mt_c0201b0001_bibo.mtrl", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SecondSkinWriter.MaterialNames(File.ReadAllBytes(top.FullPath)), top.Refs);
    }
}
