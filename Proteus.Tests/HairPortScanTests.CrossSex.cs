using System.Collections.Generic;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>A check of a Midlander woman's hair mods when one is a men's mod: never fitted to her by a check, but a port
/// the Studio made there is kept current by the check of that mod.</summary>
public partial class HairPortScanTests
{
    private const int MenId = 5;

    /// <summary>A men's mod publishing the Midlander man's model of hairstyle 5 from its default data, resolved.</summary>
    private static string MensMod(Setup s, string name = "His Hair")
    {
        var root = Path.Combine(s.ModsRoot, name);
        Setup.WriteMod(root, name, "[]", new() { [HairPortPlan.HairPath(101, MenId)] = "default group/h0005.mdl" });
        Setup.Put(root, "default group/h0005.mdl", HairModelOf(101, MenId));
        s.Host.Files[HairPortPlan.HairPath(101, MenId)] = Path.Combine(root, "default group", "h0005.mdl");
        return root;
    }

    /// <summary>The Studio's port of it to the player's race, saved and served.</summary>
    private static void StudioPort(Setup s, string root)
    {
        var plan = HairPortBuilder.Build(root, HairPortPlan.HairPath(101, MenId), [Me], Pbd(), s.Host.Game, withSharers: false);
        Assert.Empty(plan.Refused);
        Assert.True(HairPortWriter.Save(root, plan.Writes, auto: false).Ok);
        s.Reloaded(root, MenId);
    }

    /// <summary>[AC-hair-material-scalp-fit-9] After the update, the check remakes the old ports for the race the player
    /// wears only: another race's old port stays out of date until worn as that race or remade in the Studio.</summary>
    [Fact]
    [Trait("Plan", "AC-hair-material-scalp-fit-9")]
    public void The_check_remakes_only_the_players_race()
    {
        using var s = new Setup();
        var root = MensMod(s);
        var plan = HairPortBuilder.Build(root, HairPortPlan.HairPath(101, MenId), [Me, 401], Pbd(), s.Host.Game, withSharers: false);
        Assert.Empty(plan.Refused);
        Assert.True(HairPortWriter.Save(root, plan.Writes, auto: false).Ok);
        var record = HairPortRecord.Read(root);
        foreach (var e in record.Ports) e.PortVersion = 1;
        HairPortRecord.Write(root, record);
        s.Reloaded(root, MenId);

        s.Run(HairPortScan.IdsInMod(HairPortPlan.ReadOwners(root), Me, HairPortRecord.Read(root)), force: true);
        var after = HairPortRecord.Read(root).Ports;
        Assert.Equal(HairPortBaker.Version, after.Single(e => e.TargetGamePath == HairPortPlan.HairPath(Me, MenId)).PortVersion);
        Assert.Equal(1, after.Single(e => e.TargetGamePath == HairPortPlan.HairPath(401, MenId)).PortVersion);
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-5")]
    public void A_check_never_ports_across_the_sexes()
    {
        using var s = new Setup();
        var root = MensMod(s);
        var result = s.Run(HairPortScan.IdsForFull(Me, s.Host.GameHas));
        Assert.Empty(result.Rows);
        Assert.Empty(result.Written);
        Assert.False(HairPortRecord.Exists(root));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-6")]
    [Trait("Plan", "EDGE-cross-sex-hair-port-19")]
    public void The_mod_check_reaches_a_recorded_cross_sex_port()
    {
        using var s = new Setup();
        var root = MensMod(s);
        StudioPort(s, root);
        var changed = HairModelOf(101, MenId, radius: 0.101f);
        Setup.Put(root, "default group/h0005.mdl", changed);

        var owners = HairPortPlan.ReadOwners(root);
        Assert.DoesNotContain(MenId, HairPortScan.IdsInMod(owners, Me));
        var ids = HairPortScan.IdsInMod(owners, Me, HairPortRecord.Read(root));
        Assert.Contains(MenId, ids);
        var row = s.Run(ids, force: true).Rows.Single();
        Assert.Equal((MenId, HairPortScan.RowState.Remade), (row.Id, row.State));
        Assert.Equal(HairPortPlan.Sha8(changed), HairPortRecord.Read(root).Ports.Single().SourceSha8);
    }

    [Fact]
    [Trait("Plan", "EDGE-cross-sex-hair-port-20")]
    public void A_stale_remakes_refusal_is_stamped_with_its_source_file()
    {
        using var s = new Setup();
        var root = MensMod(s);
        var file = Path.Combine(root, "default group", "h0005.mdl");
        var d = new HairPortWatcher.Decide.Decision(HairPortWatcher.Decide.Kind.Port, root, HairPortPlan.HairPath(101, MenId),
                                                    Stale: true, SourceFile: file);
        var info = new FileInfo(file);
        var key = HairPortScan.RefusalKey(root, d, null, Me);
        Assert.Contains($"{info.Length}:{info.LastWriteTimeUtc.Ticks}", key);
        Setup.Put(root, "default group/h0005.mdl", HairModelOf(101, MenId, radius: 0.12f));
        Assert.NotEqual(key, HairPortScan.RefusalKey(root, d, null, Me));
    }

    [Fact]
    [Trait("Plan", "AC-cross-sex-hair-port-7")]
    public void A_cross_sex_port_left_for_the_authors_own_version_is_already_fitted()
    {
        using var s = new Setup();
        var root = MensMod(s);
        StudioPort(s, root);
        // The author ships their own women's model since — for an Au Ra — and updates the men's.
        var meta = Path.Combine(root, "meta.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(meta))!;
        node["DefaultData"]!["Files"]![HairPortPlan.HairPath(1401, MenId)] = "default group/au.mdl";
        File.WriteAllText(meta, node.ToJsonString());
        Setup.Put(root, "default group/au.mdl", HairModelOf(1401, MenId));
        Setup.Put(root, "default group/h0005.mdl", HairModelOf(101, MenId, radius: 0.101f));

        var result = s.Run(HairPortScan.IdsForFull(Me, s.Host.GameHas), force: true);
        var row = Assert.Single(result.Rows);
        Assert.Equal((MenId, HairPortScan.RowState.AlreadyFitted), (row.Id, row.State));
        Assert.Empty(result.Written);
    }
}
