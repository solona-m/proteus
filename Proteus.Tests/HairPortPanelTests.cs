using System.Collections.Generic;
using System.Linq;
using Proteus.Gui;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>The Studio's "Port to all races": what it lists, and when it shows instead of the garment refit.</summary>
public class HairPortPanelTests
{
    private static readonly string Source = HairPortPlan.HairPath(801, Id);

    private static List<HairPortPanel.Row> Rows(Mod mod, HashSet<ushort>? noStyle = null, bool stale = false)
        => HairPortPanel.Rows(Source, mod.Owners, HairPortRecord.Read(mod.Root), r => noStyle?.Contains(r) != true, _ => stale);

    [Fact]
    [Trait("Plan", "AC-hair-race-port-6")]
    public void Rows_list_every_other_same_gender_race_with_its_state()
    {
        using var mod = new Mod(defaults: new Dictionary<string, string>
        {
            [Source] = "a.mdl",
            [HairPortPlan.HairPath(1401, Id)] = "aura.mdl",                        // the author's own
            [HairPortPlan.HairPath(201, Id)] = $"Hair Ports/Default/x.mdl",         // a port of ours
        });
        mod.Put("Proteus/hairport.json", System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
            new HairPortRecord { Ports = [new HairPortRecord.Entry { SourceGamePath = Source, TargetGamePath = HairPortPlan.HairPath(201, Id),
                                                                     File = "Hair Ports/Default/x.mdl", PortVersion = HairPortBaker.Version }] })));
        var rows = Rows(mod, noStyle: [1601]);

        Assert.Equal(HairPortPlan.SameGenderRaces(801), rows.Select(r => r.Race));
        Assert.Equal(HairPortPanel.State.Ported, rows.Single(r => r.Race == 201).State);
        Assert.Equal(HairPortPanel.State.Covered, rows.Single(r => r.Race == 1401).State);
        Assert.Equal(HairPortPanel.State.NoStyle, rows.Single(r => r.Race == 1601).State);
        Assert.Equal(HairPortPanel.State.Open, rows.Single(r => r.Race == 401).State);
        Assert.Equal([401, 601, 1001, 1201, 1801], HairPortPanel.DefaultTicks(rows).Select(r => (int)r).ToArray());
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-15")]
    public void Stale_port_row_says_out_of_date_and_is_ticked()
    {
        using var mod = new Mod(defaults: new Dictionary<string, string>
        {
            [Source] = "a.mdl", [HairPortPlan.HairPath(201, Id)] = "Hair Ports/Default/x.mdl",
        });
        mod.Put("Proteus/hairport.json", System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(
            new HairPortRecord { Ports = [new HairPortRecord.Entry { SourceGamePath = Source, TargetGamePath = HairPortPlan.HairPath(201, Id),
                                                                     File = "Hair Ports/Default/x.mdl" }] })));
        var rows = Rows(mod, stale: true);
        Assert.Equal(HairPortPanel.State.Stale, rows.Single(r => r.Race == 201).State);
        Assert.Contains((ushort)201, HairPortPanel.DefaultTicks(rows));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-20")]
    public void Race_without_the_hairstyle_is_listed_and_not_ticked()
    {
        using var mod = new Mod(defaults: new Dictionary<string, string> { [Source] = "a.mdl" });
        var rows = Rows(mod, noStyle: [1201]);
        Assert.Equal(HairPortPanel.State.NoStyle, rows.Single(r => r.Race == 1201).State);
        Assert.DoesNotContain((ushort)1201, HairPortPanel.DefaultTicks(rows));
    }

    [Theory]
    [Trait("Plan", "AC-hair-race-port-6")]
    [InlineData("chara/human/c0801/obj/hair/h0130/model/c0801h0130_hir.mdl", true)]
    [InlineData("chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl", false)]
    [InlineData("chara/equipment/e0100/model/c0201e0100_top.mdl", false)]
    [InlineData(null, false)]
    public void Panel_replaces_body_size_for_hair_models_only(string? gamePath, bool shows)
        => Assert.Equal(shows, HairPortPanel.Shows(gamePath));
}
