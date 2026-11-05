using System;
using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The gear shader upgrade's per-collection temporary mod (<see cref="ShaderUpgradeRedirects"/>), against a fake Penumbra.</summary>
public class ShaderUpgradeRedirectsTests
{
    private sealed class FakePenumbra
    {
        public readonly List<string> Calls = [];
        public readonly Dictionary<Guid, Dictionary<string, string>> Live = [];
        public readonly HashSet<Guid> Refuse = [];

        public bool Set(Guid c, Dictionary<string, string> map)
        {
            Calls.Add($"set {Short(c)} {map.Count}");
            Live[c] = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            return true;
        }

        public bool Remove(Guid c)
        {
            Calls.Add($"remove {Short(c)}");
            if (Refuse.Contains(c)) return false;
            Live.Remove(c);
            return true;
        }

        public static string Short(Guid g) => g.ToString()[..4];
    }

    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    private static RedirectUnit Unit(string name, bool mask = false)
        => new($"chara/equipment/e0001/material/v0001/mt_c0201e0001_{name}_a.mtrl", $@"C:\P\ShaderUpgrade\1-{name}.mtrl",
               mask ? new RedirectMask($"chara/proteus/shaderupgrade/{name}_mask.tex", $@"C:\P\ShaderUpgrade\1-{name}_mask.tex") : null,
               Upstream: $@"C:\mods\Dress\{name}.mtrl", SourceLabel: $"Dress/{name}.mtrl");

    private static (FakePenumbra, ShaderUpgradeRedirects) Make()
    {
        var p = new FakePenumbra();
        return (p, new ShaderUpgradeRedirects(p.Set, p.Remove));
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-2")]
    public void Apply_redirects_each_unit_in_the_players_collection()
    {
        var (p, r) = Make();
        Assert.True(r.Apply(r.Begin(), A, [Unit("top"), Unit("dwn", mask: true)]));
        var live = p.Live[A];
        Assert.Equal(3, live.Count);
        Assert.Equal(@"C:\P\ShaderUpgrade\1-top.mtrl", live["chara/equipment/e0001/material/v0001/mt_c0201e0001_top_a.mtrl"]);
        Assert.Equal(@"C:\P\ShaderUpgrade\1-dwn_mask.tex", live["chara/proteus/shaderupgrade/dwn_mask.tex"]);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-10")]
    public void Unchanged_map_makes_no_penumbra_call()
    {
        var (p, r) = Make();
        r.Apply(r.Begin(), A, [Unit("top")]);
        p.Calls.Clear();
        Assert.True(r.Apply(r.Begin(), A, [Unit("top")]));
        Assert.Empty(p.Calls);
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-5")]
    public void Teardown_removes_from_every_held_collection()
    {
        var (p, r) = Make();
        r.Apply(r.Begin(), A, [Unit("top")]);
        p.Refuse.Add(A);
        r.Apply(r.Begin(), B, [Unit("dwn")]);   // A would not let go, so both are held
        p.Refuse.Clear();
        p.Calls.Clear();
        var (removed, paths, _) = r.Teardown();
        Assert.True(removed);
        Assert.Contains("remove aaaa", p.Calls);
        Assert.Contains("remove bbbb", p.Calls);
        Assert.Empty(p.Live);
        Assert.NotEmpty(paths);
        Assert.False(r.AnythingHeld);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-41")]
    public void Teardown_reports_the_paths_to_forget()
    {
        var (_, r) = Make();
        r.Apply(r.Begin(), A, [Unit("top", mask: true)]);
        var (_, paths, _) = r.Teardown();
        Assert.Contains("chara/equipment/e0001/material/v0001/mt_c0201e0001_top_a.mtrl", paths);
        Assert.Contains("chara/proteus/shaderupgrade/top_mask.tex", paths);
    }

    [Fact]
    [Trait("Plan", "AC-dt-shader-upgrade-6")]
    public void A_trusted_peek_removes_resolves_and_restores_in_one_step()
    {
        var (p, r) = Make();
        r.Apply(r.Begin(), A, [Unit("top")]);
        p.Calls.Clear();
        bool sawRedirect = true;
        var answer = r.Peek(A, () => { sawRedirect = p.Live.ContainsKey(A); p.Calls.Add("resolve"); return 42; });
        Assert.Equal(42, answer);
        Assert.False(sawRedirect);
        Assert.Equal(["remove aaaa", "resolve", "set aaaa 1"], p.Calls);
        Assert.Equal(@"C:\P\ShaderUpgrade\1-top.mtrl", p.Live[A].Values.Single());
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-27")]
    public void An_untrusted_peek_stays_down_until_apply()
    {
        var (p, r) = Make();
        r.Apply(r.Begin(), A, [Unit("top")]);
        Assert.True(r.PeekBegin(A));
        Assert.False(p.Live.ContainsKey(A));
        // The same map again after a peek is NOT "unchanged": it must go back on.
        p.Calls.Clear();
        Assert.True(r.Apply(r.Begin(), A, [Unit("top")]));
        Assert.Equal(["set aaaa 1"], p.Calls);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-11")]
    public void A_result_landing_after_teardown_is_not_applied()
    {
        var (p, r) = Make();
        int gen = r.Begin();
        r.Teardown();
        p.Calls.Clear();
        Assert.False(r.Apply(gen, A, [Unit("top")]));
        Assert.Empty(p.Calls);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-11")]
    public void An_older_generation_never_overwrites_a_newer_one()
    {
        var (p, r) = Make();
        int older = r.Begin(), newer = r.Begin();
        Assert.True(r.Apply(newer, A, [Unit("dwn")]));
        Assert.False(r.Apply(older, A, [Unit("top")]));
        Assert.Contains("mt_c0201e0001_dwn_a.mtrl", p.Live[A].Keys.Single());
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-12")]
    public void Collection_change_moves_the_redirect()
    {
        var (p, r) = Make();
        r.Apply(r.Begin(), A, [Unit("top")]);
        r.Apply(r.Begin(), B, [Unit("top")]);
        Assert.False(p.Live.ContainsKey(A));
        Assert.True(p.Live.ContainsKey(B));
        Assert.Equal(B, r.CurrentCollection);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-12")]
    public void A_refused_removal_is_retried()
    {
        var (p, r) = Make();
        r.Apply(r.Begin(), A, [Unit("top")]);
        p.Refuse.Add(A);
        r.Apply(r.Begin(), B, [Unit("top")]);
        Assert.True(p.Live.ContainsKey(A));
        p.Refuse.Clear();
        p.Calls.Clear();
        r.Apply(r.Begin(), B, [Unit("dwn")]);
        Assert.Contains("remove aaaa", p.Calls);
        Assert.False(p.Live.ContainsKey(A));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-13")]
    public void Teardown_without_reload_still_removes()
    {
        var (p, r) = Make();
        r.Apply(r.Begin(), A, [Unit("top")]);
        r.Teardown();   // the redirect state knows nothing of reloads: dispose calls exactly this
        Assert.Empty(p.Live);
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-50")]
    public void Units_stay_whole_through_drop_peek_restore_and_apply()
    {
        var (p, r) = Make();
        var withMask = Unit("dwn", mask: true);
        r.Apply(r.Begin(), A, [withMask]);
        Assert.Equal(2, p.Live[A].Count);
        r.Peek(A, () => 0);
        Assert.Equal(2, p.Live[A].Count);
        r.Apply(r.Begin(), A, [Unit("top")]);   // the unit dropped: material and mask together
        Assert.DoesNotContain(p.Live[A].Keys, k => k.Contains("dwn"));
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-52")]
    public void ReferencedMasks_includes_every_held_collection()
    {
        var (p, r) = Make();
        r.Apply(r.Begin(), A, [Unit("top", mask: true)]);
        p.Refuse.Add(A);
        r.Apply(r.Begin(), B, [Unit("dwn", mask: true)]);
        Assert.Equal(new HashSet<string> { "1-top_mask.tex", "1-dwn_mask.tex" }, r.ReferencedMasks().ToHashSet());
    }

    [Fact]
    [Trait("Plan", "EDGE-dt-shader-upgrade-37")]
    public void Shared_copies_keep_each_game_paths_source()
    {
        var (_, r) = Make();
        var one = new RedirectUnit("p/top.mtrl", @"C:\P\ShaderUpgrade\1-same.mtrl", null, Upstream: @"C:\mods\Dress\a.mtrl");
        var two = new RedirectUnit("p/dwn.mtrl", @"C:\P\ShaderUpgrade\1-same.mtrl", null, Upstream: @"C:\mods\Skirt\a.mtrl");
        r.Apply(r.Begin(), A, [one, two]);
        Assert.Equal(@"C:\mods\Dress\a.mtrl", r.SourceFor("p/top.mtrl"));
        Assert.Equal(@"C:\mods\Skirt\a.mtrl", r.SourceFor("p/dwn.mtrl"));
        Assert.Equal(@"C:\mods\Skirt\a.mtrl", r.SourceForCopy(@"C:\P\ShaderUpgrade\1-same.mtrl", ["p/dwn.mtrl"]));
        Assert.Null(r.SourceForCopy(@"C:\mods\Dress\a.mtrl"));
    }
}
