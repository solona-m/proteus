using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Proteus.Services;
using Xunit;
using Xunit.Abstractions;
using Data = Proteus.Tests.SizeSetBatchData;

namespace Proteus.Tests;

/// <summary>
/// The size set over the mods and packs on this machine: the group a piece is sized in (<see cref="SizeSetRefit.SizeGroupOf"/>),
/// and the body mods it breaks a tie toward (<see cref="SizeSetRefit.Request.KnownBody"/>).
/// </summary>
[Trait("Speed", "Slow")]
public class SizeSetCorpusLocalTests(ITestOutputHelper output)
{
    /// <summary>The pieces measured to move, by mod folder or pack name: each to the group the game draws it from.</summary>
    private static readonly (string Mod, string GamePath, string Group)[] Expected =
    [
        ("Equinox - by Solona", "chara/equipment/e0317/model/c0201e0317_dwn.mdl", "Bottom Size"),
        ("Equinox - by Solona.pmp", "chara/equipment/e0317/model/c0201e0317_dwn.mdl", "Bottom Size"),
        ("Cowgirls and Assassins", "chara/equipment/e0408/model/c0201e0408_glv.mdl", "Required"),
        ("Precious - by Solona", "chara/equipment/e0041/model/c0201e0041_top.mdl", "Size"),
        ("Precious - by Solona.pmp", "chara/equipment/e0041/model/c0201e0041_top.mdl", "Size"),
        ("[HS] Precious.pmp", "chara/equipment/e0041/model/c0201e0041_top.mdl", "Size"),
        ("Off-Shoulder Hoodie - by Solona.pmp", "chara/equipment/e0041/model/c0201e0041_top.mdl", "Size"),
        ("[HS] Savannah (Default)", "chara/equipment/e0678/model/c0201e0678_dwn.mdl", "Bottoms Options"),
        ("BiboPlus Sheer Elegance", "chara/equipment/e6010/model/c0201e6010_top.mdl", "Chest size"),
        ("BiboPlus Sheer Elegance.pmp", "chara/equipment/e6010/model/c0201e6010_top.mdl", "Chest size"),
        ("[LINXX] Codeine.pmp", "chara/equipment/e6169/model/c0201e6169_top.mdl", "✦ HARNESS"),
    ];

    /// <summary>A pack's manifests alone, unpacked and converted: all <see cref="SizeSetRefit.PiecesOf"/> reads.</summary>
    private static string Manifests(string pack)
    {
        string copy = Data.NewCopy();
        Directory.CreateDirectory(copy);
        using (var zip = ZipFile.OpenRead(pack))
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                                         && !e.FullName.Contains('/') && !e.FullName.Contains('\\')))
                entry.ExtractToFile(Path.Combine(copy, entry.Name), overwrite: true);
        PenumbraModMeta.MigrateWorkCopy(copy, LocalData.Path(LocalData.Mods));
        return copy;
    }

    [LocalDataFact(LocalData.Mods, LocalData.ModPacks)]
    [Trait("Plan", "AC-equinox-legs-slits-7")]
    [Trait("Plan", "AC-equinox-legs-slits-3")]
    [Trait("Plan", "AC-equinox-legs-slits-6")]
    [Trait("Plan", "EDGE-equinox-legs-slits-22")]
    [Trait("Plan", "EDGE-equinox-legs-slits-25")]
    public void The_size_set_sizes_each_flipped_mod_in_the_group_Penumbra_draws()
    {
        var found = new Dictionary<(string Mod, string Path), string>();
        void Scan(string name, string root)
        {
            var pieces = SizeSetRefit.PiecesOf(root, regrouped: (path, was, now, rivals) =>
            {
                found[(name, path.ToLowerInvariant())] = now;
                output.WriteLine($"{name} | {path}: {was} -> {now}; rivals: {(rivals.Count == 0 ? "none" : string.Join(", ", rivals))}");
            });
            foreach (var p in pieces)
                found.TryAdd((name, p.GamePath.ToLowerInvariant()), p.Group ?? "");
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in Directory.GetDirectories(LocalData.Path(LocalData.Mods)))
        {
            present.Add(Path.GetFileName(dir));
            try { Scan(Path.GetFileName(dir), dir); }
            catch (Exception e) { output.WriteLine($"{Path.GetFileName(dir)}: unreadable ({e.Message})"); }
        }
        foreach (string pack in Directory.GetFiles(LocalData.Path(LocalData.ModPacks), "*.pmp"))
        {
            present.Add(Path.GetFileName(pack));
            string copy = "";
            try
            {
                copy = Manifests(pack);
                Scan(Path.GetFileName(pack), copy);
            }
            catch (Exception e) { output.WriteLine($"{Path.GetFileName(pack)}: unreadable ({e.Message})"); }
            finally { Data.Delete(copy); }
        }

        int checkedCount = 0;
        foreach (var (mod, path, group) in Expected)
        {
            if (!present.Contains(mod)) continue;
            checkedCount++;
            Assert.True(found.TryGetValue((mod, path), out string? got), $"{mod}: {path} is not a piece");
            Assert.Equal(group, got);
        }
        output.WriteLine($"{checkedCount} of {Expected.Length} expected moves checked");
    }

    [LocalDataFact(LocalData.Mods, SizeSetBatchData.Rue, SizeSetBatchData.Tbse, SizeSetBatchData.ElegySylvie)]
    [Trait("Plan", "AC-equinox-legs-slits-8")]
    [Trait("Plan", "EDGE-equinox-legs-slits-31")]
    public void The_size_set_knows_the_body_mods_and_not_the_underwear_that_ships_their_skin()
    {
        // Every body mod in the mod folder, as the size set is given them in game.
        string root = LocalData.Path(LocalData.Mods);
        var mods = Directory.GetDirectories(root).Select(Path.GetFileName).ToDictionary(d => d!, d => d!);
        var bodies = new BodyModIndex().RefreshState(mods, root).List;
        var request = new SizeSetRefit.Request("mod", ["rue"], Yas: false, Remake: false, Bodies: bodies);

        var known = bodies.Where(b => request.KnownBody(b.Dir)).Select(b => b.Dir).OrderBy(d => d).ToList();
        output.WriteLine($"{known.Count} of {bodies.Count} body-skin mods are bodies the size table resolves to:");
        foreach (string dir in known) output.WriteLine("  " + dir);

        Assert.True(request.KnownBody(Data.Folder(SizeSetBatchData.Rue)));
        Assert.True(request.KnownBody(Data.Folder(SizeSetBatchData.Tbse)));   // the men's bodies count too
        Assert.False(request.KnownBody(Data.Folder(SizeSetBatchData.ElegySylvie)));
        // A copy given other bodies answers for its own, never for the list it was copied from.
        var none = request with { Bodies = [] };
        Assert.False(none.KnownBody(Data.Folder(SizeSetBatchData.Rue)));
        Assert.True(request.KnownBody(Data.Folder(SizeSetBatchData.Rue)));
    }
}
