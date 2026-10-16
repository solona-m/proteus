using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Which mods Studio counts as worn. A ring Proteus appends its second skin into is drawn out of Proteus' own output
/// mod, so the mod that supplied the ring has to be credited from what the composite recorded — or Studio says the
/// plug you are wearing is not on.
/// </summary>
public class WornModelCreditTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "proteus-worn-mods");
    private const string RingGamePath = "chara/accessory/a0174/model/c0201a0174_rir.mdl";
    private const string Plug = "Butt Plug - True Colors";

    private static string InRoot(params string[] parts) => Path.Combine([Root, .. parts]);

    private static readonly string Shell = InRoot(SidecarDiscoveryService.ManagedModDir, "models", "secondskin_1.mdl");
    private static readonly string PlugModel = InRoot(Plug, "plog", "sized", "default_fit.mdl");

    private static List<(string Mod, string Rel)> Credit(IEnumerable<string> drawn,
                                                         Dictionary<string, string> gamePaths,
                                                         Dictionary<string, string> sources)
        => WornModelCredit.Credit(drawn, Root,
                                  f => gamePaths.TryGetValue(f, out var g) ? g : null,
                                  g => sources.TryGetValue(g, out var s) ? s : null);

    [Fact]
    public void An_append_hosts_drawn_file_credits_its_source_mod()   // [AC-spare-carry-append-worn-4]
    {
        var worn = Credit([Shell], new() { [Shell] = RingGamePath }, new() { [RingGamePath] = PlugModel });
        Assert.Contains(worn, w => w.Mod == Plug);
    }

    [Fact]
    public void The_credited_rel_matches_the_mods_redirect_spelling()   // [AC-spare-carry-append-worn-5]
    {
        var worn = Credit([Shell], new() { [Shell] = RingGamePath }, new() { [RingGamePath] = PlugModel });
        Assert.Contains((Plug, "plog/sized/default_fit.mdl"), worn);
    }

    [Fact]
    public void Ordinary_mod_files_are_credited_as_before()   // [AC-spare-carry-append-worn-6]
    {
        var top = InRoot("Oktoberfest", "chara", "equipment", "e6114", "model", "c0201e6114_top.mdl");
        var legs = InRoot("Neolithe", "legs", "gen c small.mdl");
        var worn = Credit([top, legs, Shell], new(), new());

        Assert.Equal(
        [
            ("Oktoberfest", "chara/equipment/e6114/model/c0201e6114_top.mdl"),
            ("Neolithe", "legs/gen c small.mdl"),
            (SidecarDiscoveryService.ManagedModDir, "models/secondskin_1.mdl"),
        ], worn);
    }

    [Fact]
    public void A_vanilla_append_host_credits_no_mod()   // [AC-spare-carry-append-worn-6]
    {
        // Penumbra echoes an unredirected game path back; that is what the composite records for a vanilla ring.
        var worn = Credit([Shell], new() { [Shell] = RingGamePath }, new() { [RingGamePath] = RingGamePath });
        Assert.Equal([(SidecarDiscoveryService.ManagedModDir, "models/secondskin_1.mdl")], worn);
    }

    [Fact]
    public void A_source_outside_the_mods_root_or_null_is_not_credited()   // [EDGE-spare-carry-append-worn-10]
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "default_fit.mdl");
        foreach (var source in new string?[] { null, "", RingGamePath, outside })
        {
            var sources = source == null ? new Dictionary<string, string>() : new() { [RingGamePath] = source };
            var worn = Credit([Shell], new() { [Shell] = RingGamePath }, sources);
            Assert.Single(worn);
        }
    }

    [Fact]
    public void A_source_inside_our_output_is_not_credited()   // [EDGE-spare-carry-append-worn-11]
    {
        // A composite that ran while a live preview was on the ring can record the preview copy, which lives in our
        // own folder. Crediting it would say our output mod is worn twice; the plug is simply not credited then.
        var preview = InRoot(SidecarDiscoveryService.ManagedModDir, "LivePreview", "41680-8fe1fc2f-000022.mdl");
        var worn = Credit([Shell], new() { [Shell] = RingGamePath }, new() { [RingGamePath] = preview });
        Assert.Equal([(SidecarDiscoveryService.ManagedModDir, "models/secondskin_1.mdl")], worn);
    }

    [Fact]
    public void A_carrier_host_credits_nothing()   // [EDGE-spare-carry-append-worn-12]
    {
        // The Emperor's ring, or anything before the first composite: drawn from our output, no source recorded.
        var carrier = InRoot(SidecarDiscoveryService.ManagedModDir, "models", "secondskin_0.mdl");
        var worn = Credit([carrier], new() { [carrier] = "chara/accessory/a0053/model/c0201a0053_rir.mdl" }, new());
        Assert.Equal([(SidecarDiscoveryService.ManagedModDir, "models/secondskin_0.mdl")], worn);
    }

    [Fact]
    public void Unknown_game_paths_skip_append_credit_only()   // [EDGE-spare-carry-append-worn-13]
    {
        var top = InRoot("Oktoberfest", "top.mdl");
        var worn = WornModelCredit.Credit([top, Shell], Root, _ => null, _ => PlugModel);
        Assert.Equal([("Oktoberfest", "top.mdl"), (SidecarDiscoveryService.ManagedModDir, "models/secondskin_1.mdl")], worn);
    }

    [Fact]
    public void Game_paths_are_asked_only_for_our_output_files()   // [EDGE-spare-carry-append-worn-14]
    {
        int asked = 0;
        string? GamePath(string f) { asked++; return RingGamePath; }

        WornModelCredit.Credit([InRoot("Oktoberfest", "top.mdl")], Root, GamePath, _ => PlugModel);
        Assert.Equal(0, asked);

        WornModelCredit.Credit([InRoot("Oktoberfest", "top.mdl"), Shell], Root, GamePath, _ => PlugModel);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void An_append_host_not_drawn_is_not_credited()   // [EDGE-spare-carry-append-worn-15]
    {
        // The ring came off; the composite that forgets it has not landed yet.
        var top = InRoot("Oktoberfest", "top.mdl");
        var worn = Credit([top], new(), new() { [RingGamePath] = PlugModel });
        Assert.DoesNotContain(worn, w => w.Mod == Plug);
    }

    [Fact]
    public void Case_and_separators_do_not_matter()   // [EDGE-spare-carry-append-worn-16]
    {
        var shell = InRoot(SidecarDiscoveryService.ManagedModDir.ToLowerInvariant(), "models", "secondskin_1.mdl");
        var mixed = Root + Path.DirectorySeparatorChar + Plug + "/plog\\sized/default_fit.mdl";
        var worn = Credit([shell], new() { [shell] = RingGamePath }, new() { [RingGamePath] = mixed });
        Assert.Contains((Plug, "plog/sized/default_fit.mdl"), worn);
    }
}
