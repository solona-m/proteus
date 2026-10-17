using System.Collections.Generic;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>The pure decisions of a hair port: which races, from where, into which of the author's options.</summary>
public class HairPortPlanTests
{
    private static readonly string Miqote = HairPortPlan.HairPath(801, 130);
    private static readonly string Midlander = HairPortPlan.HairPath(201, 130);

    [Fact]
    public void Parses_only_hair_model_paths()
    {
        Assert.Equal(((ushort)801, 130), HairPortPlan.Parse(Miqote));
        Assert.Equal(((ushort)801, 130), HairPortPlan.Parse(Miqote.Replace('/', '\\')));
        Assert.Null(HairPortPlan.Parse("chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl"));
        Assert.Null(HairPortPlan.Parse("chara/human/c0801/obj/hair/h0130/model/c0201h0130_hir.mdl"));
    }

    [Theory]
    [Trait("Plan", "AC-hair-race-port-5")]
    [InlineData((ushort)101)] [InlineData((ushort)201)] [InlineData((ushort)301)] [InlineData((ushort)401)]
    [InlineData((ushort)501)] [InlineData((ushort)601)] [InlineData((ushort)701)] [InlineData((ushort)801)]
    [InlineData((ushort)901)] [InlineData((ushort)1001)] [InlineData((ushort)1101)] [InlineData((ushort)1201)]
    [InlineData((ushort)1301)] [InlineData((ushort)1401)] [InlineData((ushort)1501)] [InlineData((ushort)1601)]
    [InlineData((ushort)1701)] [InlineData((ushort)1801)]
    public void Only_same_gender_races_are_targets(ushort race)
    {
        var targets = HairPortPlan.SameGenderRaces(race).ToList();
        Assert.Equal(8, targets.Count);
        Assert.DoesNotContain(race, targets);
        Assert.All(targets, t => Assert.Equal(race / 100 % 2, t / 100 % 2));
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-2")]
    public void Nearest_race_by_fall_through_then_index()
    {
        // A Highlander woman falls through to Midlander in one step, a Miqo'te woman is two away.
        Assert.Equal([(ushort)201, 801], HairPortPlan.OrderSources(401, [801, 201]).ToArray());
        // Two equally near: the lower number first.
        Assert.Equal([(ushort)801, 1401], HairPortPlan.OrderSources(201, [1401, 801]).ToArray());
    }

    [Fact]
    [Trait("Plan", "EDGE-hair-race-port-3")]
    public void Other_gender_coverage_is_not_a_source()
        => Assert.Empty(HairPortPlan.OrderSources(201, [101, 701, 1501]));

    [Fact]
    [Trait("Plan", "AC-hair-race-port-5")]
    public void Only_the_same_hair_id_is_a_source()
    {
        var owners = new List<HairPortPlan.Owner>
        {
            new("", "", "", new Dictionary<string, string> { [HairPortPlan.HairPath(801, 131)] = "a.mdl", [Miqote] = "b.mdl" }),
        };
        Assert.Equal([(ushort)801], HairPortPlan.AuthoredRaces(owners, 130));
        Assert.Equal([(ushort)801], HairPortPlan.AuthoredRaces(owners, 131));
        Assert.Empty(HairPortPlan.AuthoredRaces(owners, 132));
    }

    private static HairPortPlan.Owner O(string group, string option, string type, params (string Path, string File)[] files)
        => new(group, option, type, files.ToDictionary(f => f.Path, f => f.File));

    [Theory]
    [Trait("Plan", "EDGE-hair-race-port-10")]
    [InlineData("default", false)]
    [InlineData("same", false)]
    [InlineData("otherGroup", false)]
    [InlineData("multiSibling", false)]
    [InlineData("singleSibling", true)]
    [InlineData("ourPortElsewhere", true)]
    [InlineData("none", true)]
    public void CanPortInto_blocks_whatever_is_on_together(string where, bool expected)
    {
        bool multi = where == "multiSibling";
        var into = O("Style", "Long", multi ? "Multi" : "Single", (Miqote, "long.mdl"));
        var owners = new List<HairPortPlan.Owner> { O("", "", ""), into };
        switch (where)
        {
            case "default": owners[0] = O("", "", "", (Midlander, "mid.mdl")); break;
            case "same": owners[1] = into = O("Style", "Long", "Single", (Miqote, "long.mdl"), (Midlander, "mid.mdl")); break;
            case "otherGroup": owners.Add(O("Other", "On", "Single", (Midlander, "mid.mdl"))); break;
            case "multiSibling": owners.Add(O("Style", "Short", "Multi", (Midlander, "mid.mdl"))); break;
            case "singleSibling": owners.Add(O("Style", "Short", "Single", (Midlander, "mid.mdl"))); break;
            case "ourPortElsewhere": owners.Add(O("Style", "Short", "Single", (Midlander, "Hair Ports/x/mid.mdl"))); break;
        }
        Assert.Equal(expected, HairPortPlan.CanPortInto(owners, into, Midlander));
    }

    [Fact]
    [Trait("Plan", "AC-hair-race-port-8")]
    public void Changed_source_or_newer_version_is_stale()
    {
        byte[] source = [1, 2, 3];
        var entry = new HairPortRecord.Entry { SourceSha8 = HairPortPlan.Sha8(source), PortVersion = HairPortBaker.Version };
        Assert.False(HairPortPlan.IsStale(entry, source));
        Assert.True(HairPortPlan.IsStale(entry, [1, 2, 4]));
        entry.PortVersion = HairPortBaker.Version - 1;
        Assert.True(HairPortPlan.IsStale(entry, source));
    }

    [Fact]
    public void Port_files_live_under_the_folder_per_option()
    {
        var a = HairPortPlan.PortFile(O("Style", "Long", "Single"), Midlander);
        var b = HairPortPlan.PortFile(O("Style", "Short", "Single"), Midlander);
        Assert.NotEqual(a, b);
        Assert.True(HairPortPlan.IsPortFile(a));
        Assert.True(HairPortPlan.IsPortFile(a.Replace('/', '\\')));
        Assert.Equal($"Hair Ports/Default/{Midlander}", HairPortPlan.PortFile(O("", "", ""), Midlander));
        Assert.False(HairPortPlan.IsPortFile("hair/c0201h0130_hir.mdl"));
    }

    [Fact]
    public void Port_folders_of_options_whose_names_read_alike_never_collide()
    {
        var a = HairPortPlan.PortFile(O("A - B", "C", "Single"), Midlander);
        var b = HairPortPlan.PortFile(O("A", "B - C", "Single"), Midlander);
        var c = HairPortPlan.PortFile(O("A", "B:C", "Single"), Midlander);
        var d = HairPortPlan.PortFile(O("A", "B?C", "Single"), Midlander);
        Assert.Equal(4, new[] { a, b, c, d }.Distinct().Count());
        Assert.StartsWith("Hair Ports/A - B - C ", a);
        // The same option however its case is written: one folder, as Windows compares folder names.
        Assert.Equal(a, HairPortPlan.PortFile(O("a - b", "c", "Single"), Midlander), System.StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_playable_race_has_head_candidates_starting_with_f0001()
    {
        Assert.Equal("chara/human/c1601/obj/face/f0001/model/c1601f0001_fac.mdl", HairPortPlan.HeadPaths(1601).First());
        Assert.Contains("chara/human/c1601/obj/face/f0101/model/c1601f0101_fac.mdl", HairPortPlan.HeadPaths(1601));
    }
}
