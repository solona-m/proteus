using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Proteus.Gui;
using Proteus.Localization;
using Proteus.Services;
using Xunit;
using static Proteus.Tests.HairPortFixtures;

namespace Proteus.Tests;

/// <summary>
/// #71: a port by hand from one hairstyle of a mod silently replaced another hairstyle's port of the same race, and the
/// Studio listed that race as open. The list now says "ported from hairstyle …" (tickable, never ticked for the user),
/// and a port that replaces one says so. #72: refusals for missing bones say which skeleton lacks them, and win over
/// advice to port the material's sharers.
/// </summary>
public class HairPortOtherSourceTests
{
    private static readonly ushort[] Races = [101, 201, 401, 601, 801, 1001, 1101, 1201, 1401, 1601, 1801];
    private static readonly string Miqo = HairPortPlan.HairPath(801, Id);
    private static readonly string AuRa = HairPortPlan.HairPath(1401, Id);
    private static readonly string Target = HairPortPlan.HairPath(201, Id);

    /// <summary>One option publishing the hairstyle for two races (as "wavy" does), each from its own file unless
    /// <paramref name="sameFile"/>.</summary>
    private static Mod TwoStyles(bool sameFile = false)
    {
        var mod = new Mod("[" + Group("Cut", "Single",
            ("Long", new() { [Miqo] = "miqo.mdl", [AuRa] = sameFile ? "miqo.mdl" : "aura.mdl" })) + "]");
        mod.Put("miqo.mdl", HairModel(801));
        if (!sameFile) mod.Put("aura.mdl", HairModel(1401));
        return mod;
    }

    private static HairPortBuilder.Plan PortByHand(Mod mod, string source, params ushort[] races)
    {
        var plan = HairPortBuilder.Build(mod.Root, source, races, Pbd(), Game(Races), withSharers: false);
        Assert.True(HairPortWriter.Save(mod.Root, plan.Writes, auto: false).Ok);
        return plan;
    }

    private static List<HairPortPanel.Row> ReadRows(Mod mod, string source)
        => HairPortPanel.ReadRows(mod.Root, source, null, Game(Races)).Rows;

    // ── #71 ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-10")]
    public void Race_ported_from_another_source_in_a_shared_owner_is_ported_elsewhere()
    {
        using var mod = TwoStyles();
        PortByHand(mod, Miqo, 201);

        var row = ReadRows(mod, AuRa).Single(r => r.Race == 201);
        Assert.Equal(HairPortPanel.State.PortedElsewhere, row.State);
        Assert.Equal(Miqo, row.PortedFrom, StringComparer.OrdinalIgnoreCase);
        Assert.True(HairPortPanel.CanTick(row));
        Assert.DoesNotContain((ushort)201, HairPortPanel.DefaultTicks(ReadRows(mod, AuRa)));
        Assert.Contains($"c0801h{Id:D4}", HairPortPanel.StateText(row));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-10")]
    public void Porting_over_another_sources_port_reports_it_replaced()
    {
        using var mod = TwoStyles();
        PortByHand(mod, Miqo, 201);

        var plan = HairPortBuilder.Build(mod.Root, AuRa, [201], Pbd(), Game(Races), withSharers: false);
        Assert.Equal(Miqo, plan.Replaced![201], StringComparer.OrdinalIgnoreCase);
        var outcome = HairPortWriter.Save(mod.Root, plan.Writes, auto: false);
        Assert.True(outcome.Ok);
        var (text, failed, _) = HairPortPanel.ResultOf(plan, outcome, removed: false);
        Assert.False(failed);
        Assert.Contains(string.Format(Strings.HairPort.ReplacedFmt, 201, $"c0801h{Id:D4}"), text);
        // The record now holds the Au Ra's port of the race, and the Miqo'te hairstyle lists it as ported elsewhere.
        Assert.Contains(HairPortRecord.Read(mod.Root).Ports, e => e.TargetGamePath == Target && e.SourceGamePath == AuRa);
        Assert.Equal(HairPortPanel.State.PortedElsewhere, ReadRows(mod, Miqo).Single(r => r.Race == 201).State);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-36")]
    public void Identical_source_bytes_still_replace_and_report()
    {
        using var mod = TwoStyles(sameFile: true);
        PortByHand(mod, Miqo, 201);
        var plan = HairPortBuilder.Build(mod.Root, AuRa, [201], Pbd(), Game(Races), withSharers: false);
        Assert.DoesNotContain((ushort)201, plan.Current);
        Assert.Contains(plan.Writes, w => w.TargetGamePath == Target && w.SourceGamePath == AuRa);
        Assert.Equal(Miqo, plan.Replaced![201], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-26")]
    public void Other_sources_port_in_an_unserving_owner_leaves_the_row_open()
    {
        // Each hairstyle in its own option: a port of the Miqo'te's sits where the Au Ra's is not published.
        using var mod = new Mod("[" + Group("Cut", "Single",
            ("Miqo", new() { [Miqo] = "miqo.mdl" }), ("AuRa", new() { [AuRa] = "aura.mdl" })) + "]");
        mod.Put("miqo.mdl", HairModel(801));
        mod.Put("aura.mdl", HairModel(1401));
        PortByHand(mod, Miqo, 201);

        Assert.Equal(HairPortPanel.State.Open, ReadRows(mod, AuRa).Single(r => r.Race == 201).State);
        var plan = HairPortBuilder.Build(mod.Root, AuRa, [201], Pbd(), Game(Races), withSharers: false);
        Assert.Empty(plan.Replaced!);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-27")]
    public void Own_port_wins_over_another_sources()
    {
        var owners = new List<HairPortPlan.Owner> { new("Cut", "Long", "Single", new Dictionary<string, string> { [Miqo] = "m.mdl" }) };
        var record = new HairPortRecord
        {
            Ports =
            [
                new HairPortRecord.Entry { Group = "Cut", Option = "Long", SourceGamePath = Miqo, TargetGamePath = Target, PortVersion = HairPortBaker.Version },
                new HairPortRecord.Entry { Group = "Cut", Option = "Long", SourceGamePath = AuRa, TargetGamePath = Target, PortVersion = HairPortBaker.Version },
            ],
        };
        var rows = HairPortPanel.Rows(Miqo, owners, record, _ => true, _ => false, owners);
        Assert.Equal(HairPortPanel.State.Ported, rows.Single(r => r.Race == 201).State);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-37")]
    public void PortedElsewhere_never_overrides_earlier_states()
    {
        var owners = new List<HairPortPlan.Owner>
        {
            new("Cut", "Long", "Single", new Dictionary<string, string> { [Miqo] = "m.mdl", [HairPortPlan.HairPath(1201, Id)] = "own.mdl" }),
        };
        var other = HairPortPlan.HairPath(1801, Id);
        var record = new HairPortRecord
        {
            Ports = new ushort[] { 1201, 1601, 101 }.Select(r => new HairPortRecord.Entry
            {
                Group = "Cut", Option = "Long", SourceGamePath = other, TargetGamePath = HairPortPlan.HairPath((ushort)r, Id),
                PortVersion = HairPortBaker.Version,
            }).ToList(),
        };
        // 1201 the author made (Covered); 1601 the game offers no such hairstyle (NoStyle); 101 of the other sex with the
        // mod's own version for it would be OtherSexAuthored — none turns PortedElsewhere or tickable.
        var rows = HairPortPanel.Rows(Miqo, owners, record, r => r != 1601, _ => false, owners);
        Assert.Equal(HairPortPanel.State.Covered, rows.Single(r => r.Race == 1201).State);
        Assert.Equal(HairPortPanel.State.NoStyle, rows.Single(r => r.Race == 1601).State);
        Assert.False(HairPortPanel.CanTick(rows.Single(r => r.Race == 1201)));
        Assert.False(HairPortPanel.CanTick(rows.Single(r => r.Race == 1601)));
        Assert.Equal(HairPortPanel.State.PortedElsewhere, rows.Single(r => r.Race == 101).State);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-29")]
    public void Remove_ports_leaves_another_sources_port()
    {
        using var mod = TwoStyles();
        PortByHand(mod, Miqo, 201);
        PortByHand(mod, Miqo, 401);
        PortByHand(mod, AuRa, 201);   // replaces the Miqo'te's 201

        var outcome = HairPortPanel.RemoveByHand(mod.Root, Miqo);
        Assert.True(outcome.Ok, outcome.Message);
        var ports = HairPortRecord.Read(mod.Root).Ports;
        Assert.Contains(ports, e => e.TargetGamePath == Target && e.SourceGamePath == AuRa);
        Assert.DoesNotContain(ports, e => e.SourceGamePath == Miqo);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-46")]
    [Trait("Plan", "EDGE-issue-sweep-c-38")]
    public void Serving_carries_combining_unreadable_and_missing_author_refusals()
    {
        using var combining = new Mod("[" + Group("Cut", "Combining", ("Long", new() { [Miqo] = "miqo.mdl" })) + "]");
        var c = HairPortBuilder.Serving(combining.Root, combining.Owners, Miqo);
        Assert.NotNull(c.Refusal);
        Assert.False(c.Unreadable);
        Assert.Single(c.Owners);

        // An earless copy whose author's file is gone: that place does not serve.
        using var earless = new Mod("[" + Group("Cut", "Single",
            ("Long", new() { [Miqo] = EarlessHairPlan.Folder + "/long/c0801h0005_hir.mdl" })) + "]");
        var e = HairPortBuilder.Serving(earless.Root, earless.Owners, Miqo);
        Assert.Empty(e.Owners);

        using var plain = TwoStyles();
        var p = HairPortBuilder.Serving(plain.Root, plain.Owners, Miqo);
        Assert.Null(p.Refusal);
        Assert.Equal("miqo.mdl", p.SourceOf[p.Owners.Single()]);
    }

    // ── #72 ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-9")]
    [Trait("Plan", "EDGE-issue-sweep-c-23")]
    public void A_target_refused_for_bones_is_not_advised_to_port_sharers()
    {
        const int id = 105;
        string source = HairPortPlan.HairPath(801, id);
        string mat = "chara/human/c0801/obj/hair/h0105/material/v0001/mt_c0801h0105_hir_a.mtrl";
        using var mod = new Mod(defaults: new Dictionary<string, string> { [source] = "a.mdl", [mat] = "a.mtrl" });
        mod.Put("a.mdl", HairModel(801, material: "/mt_c0801h0105_hir_a.mtrl", bones: [("j_ex_h0105_ke_b", 1f)]));
        mod.Put("a.mtrl", [5]);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [HairSkeletonTable.GamePath] = EstBytes((201, id, id)),
        };
        foreach (var r in Races)
        {
            files[HairPortPlan.HeadPaths(r).First()] = HeadModel(r);
            files[HairPortPlan.HairPath(r, id)] = HairModel(r);
        }

        var plan = HairPortBuilder.Build(mod.Root, source, [201], Pbd(), p => files.GetValueOrDefault(p), withSharers: false);
        Assert.Empty(plan.Writes);
        Assert.DoesNotContain("port to them too", plan.Refused[201]);
        // Within a sex the wording is the bend's own, unchanged.
        Assert.Equal(string.Format(ProteusLoc.Localize("HairPort.Refused.Bones.Fmt",
            "it moves with bones c{0:D4} does not have for this hairstyle ({1})"), 201, "j_ex_h0105_ke_b"), plan.Refused[201]);
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-9")]
    [Trait("Plan", "EDGE-issue-sweep-c-24")]
    public void BonesRefusal_names_the_source_skeleton_across_the_sexes()
    {
        var model = HairModel(201, bones: [("j_ex_h0005_ke_b", 1f)]);
        var none = new HashSet<string>();
        var own = new HairPortSkeleton.Choice(HairPortSkeleton.Route.Own, 0, 0, 0, none);
        var carry = new HairPortSkeleton.Choice(HairPortSkeleton.Route.Carry, 0, 0, Id, none);

        var lacks = HairPortBuilder.BonesRefusal(201, 101, Id, own, carried: false, none, model)!;
        Assert.Contains("c0201", lacks);
        Assert.DoesNotContain("c0101", lacks);
        Assert.Contains("j_ex_h0005_ke_b", lacks);

        var noSlot = HairPortBuilder.BonesRefusal(201, 101, Id, carry, carried: false, none, model)!;
        Assert.Contains("c0201", noSlot);
        Assert.Contains("c0101", noSlot);
        Assert.NotEqual(lacks, noSlot);

        Assert.Null(HairPortBuilder.BonesRefusal(201, 101, Id, carry, carried: true,
                                                 new HashSet<string> { "j_ex_h0005_ke_b" }, model));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-9")]
    [Trait("Plan", "EDGE-issue-sweep-c-45")]
    [Trait("Plan", "EDGE-issue-sweep-c-51")]
    public void One_PrepareOwner_for_the_loop_and_BonesFirst_with_the_loops_skips()
    {
        var src = Source("Services/HairPort/HairPortBuilder.cs");
        Assert.Contains("var prep = PrepareOwner(place, record, bytes, from, to, id, slot);", src);
        Assert.Contains("slot = prep.Slot;", src);                         // the loop keeps the carry slot
        int bf = src.IndexOf("private static string? BonesFirst(", StringComparison.Ordinal);
        var body = src[bf..src.IndexOf("\n    }", bf, StringComparison.Ordinal)];
        int prior = body.IndexOf("if (withSharers && cross && !HasPrior(record, owner, target, sourceGamePath)) continue;", StringComparison.Ordinal);
        int canPort = body.IndexOf("if (was == null && !HairPortPlan.CanPortInto(owners, owner, target)) continue;", StringComparison.Ordinal);
        int prep = body.IndexOf("PrepareOwner(place, record, sources[sourceOf[owner]]!, from, to, id, 0).BonesWhy", StringComparison.Ordinal);
        Assert.True(prior > 0 && canPort > 0 && prep > prior && prep > canPort, "the loop's skips come first, then only the bones verdict");
        Assert.Equal(2, CountOf(src, "PrepareOwner(place, record,"));      // the loop and BonesFirst, nothing else
    }

    private static int CountOf(string s, string needle)
    {
        int n = 0;
        for (int i = s.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = s.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string Source(string relative, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", relative));
}
