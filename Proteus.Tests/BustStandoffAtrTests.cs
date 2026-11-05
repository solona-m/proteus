using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Proteus.Services;
using Xunit;
using K = Proteus.Services.SecondSkinService.BodySourceKind;

namespace Proteus.Tests;

/// <summary>
/// #55: on NIRÆ, the skin overlays' bust standoff read every chest NIRÆ's Chest option switches (161k vertices, ~3 min a
/// pass) instead of the one drawn. It now leaves out what the body mod's options switch off — the shell's own rule.
/// </summary>
public class BustStandoffAtrTests
{
    private const int Size = 256;

    /// <summary>One chest (small) drawn; with <paramref name="withLarge"/>, a fuller one beside it on atrx_c_large.</summary>
    private static byte[] Chests(bool withLarge)
    {
        var small = SyntheticChest.Surface(0f, 0f);
        var pos = small.Pos.ToList(); var nrm = small.Nrm.ToList(); var uv = small.Uv.ToList();
        var w = small.Pos.Select(p => (((string, float)[]?)SyntheticChest.BodyBones(p))).ToList();
        var subs = new List<(uint, int[])> { (0u, small.Tris) };
        if (withLarge)
        {
            int b = pos.Count;
            foreach (var p in small.Pos) { pos.Add(p with { Z = p.Z * 1.8f + 0.01f }); w.Add(SyntheticChest.BodyBones(p)); }
            nrm.AddRange(small.Nrm); uv.AddRange(small.Uv);
            subs.Add((1u, small.Tris.Select(t => t + b).ToArray()));
        }
        return SyntheticModel.BuildSurface(["atrx_c_large"],
            new SyntheticModel.SurfaceMesh(SyntheticChest.Skin, [.. pos], [.. nrm], [.. uv], [.. w], [.. subs]));
    }

    private static byte[] Covered() => Enumerable.Repeat((byte)255, Size * Size).ToArray();

    private static byte[]? Map(byte[] body, IReadOnlySet<string>? hidden)
        => BodyBridge.BustStandoffMap([body], Covered(), Size, Size, 1f, Size, 0.0015f, null, [hidden]);

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-1")]
    [Trait("Plan", "EDGE-issue-sweep-b-3")]
    public void Hidden_chest_is_left_out_of_the_standoff()
    {
        var one = Map(Chests(false), null);
        Assert.NotNull(one);   // the bridge fires on the drawn chest: the comparison means something
        var two = Chests(true);
        Assert.Equal(one, Map(two, new HashSet<string> { "atrx_c_large" }));   // exactly the one-chest map
        Assert.NotEqual(one, Map(two, null));   // every chest read: another map
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-1")]
    [Trait("Plan", "EDGE-issue-sweep-b-3")]
    public void Hidden_triangles_are_left_out_of_the_geometry()
    {
        var two = Chests(true);
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(two, out var p, out _, out var all, out _, out _));
        Assert.True(SecondSkinWriter.TryReadLod0Geometry(two, out var p2, out _, out var drawn, out _, out _,
                                                         hiddenAttributes: new HashSet<string> { "atrx_c_large" }));
        Assert.Equal(p, p2);   // vertices untouched
        Assert.Equal(all.Length / 2, drawn.Length);
        Assert.True(drawn.Max() < p.Length / 6);   // only the drawn chest's vertices are used
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-2")]
    public void No_switches_is_byte_identical()
    {
        var body = Chests(true);
        var before = BodyBridge.BustStandoffMap([body], Covered(), Size, Size, 1f, Size, 0.0015f);
        Assert.Equal(before, BodyBridge.BustStandoffMap([body], Covered(), Size, Size, 1f, Size, 0.0015f, null, null));
        Assert.Equal(before, Map(body, null));
        Assert.Equal(before, Map(body, new HashSet<string>()));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-1")]
    [Trait("Plan", "EDGE-issue-sweep-b-1")]
    [Trait("Plan", "EDGE-issue-sweep-b-2")]
    public void Hidden_set_follows_settings_and_keeps_the_last_good_one()
    {
        var good = new ShellBodyAtr.PartAtr(new HashSet<string> { "atrx_c_flat", "atrx_c_large" },
                                            new ShellBodyAtr.Use("NIRAE", "root", "top", 1, "atrx_c_flat,atrx_c_large"), true, null);
        var off = CompositorService.StandoffHiddenOrLast(good, null, out bool isGood);
        Assert.True(isGood);
        Assert.Equal(good.Off, off);
        var unreadable = new ShellBodyAtr.PartAtr(null, new ShellBodyAtr.Use("NIRAE", "root", "top", 1, ShellBodyAtr.NoSay), false, "x");
        Assert.Same(off, CompositorService.StandoffHiddenOrLast(unreadable, off, out isGood));
        Assert.False(isGood);
        Assert.Null(CompositorService.StandoffHiddenOrLast(unreadable, null, out _));
        var guard = new ShellBodyAtr.PartAtr(null, new ShellBodyAtr.Use("NIRAE", "root", "top", 1, ShellBodyAtr.NoSay), true, "x", Warning: true);
        Assert.Same(off, CompositorService.StandoffHiddenOrLast(guard, off, out _));
        // A manifest not readable just now (NoSay, readable, no warning) and the resolver's own failure: undecided too.
        var noManifest = new ShellBodyAtr.PartAtr(null, new ShellBodyAtr.Use("NIRAE", "root", "top", 1, ShellBodyAtr.NoSay), true, null);
        Assert.Same(off, CompositorService.StandoffHiddenOrLast(noManifest, off, out isGood));
        Assert.False(isGood);
        var threw = new ShellBodyAtr.PartAtr(null, null, true, "could not tell", Warning: true);
        Assert.Same(off, CompositorService.StandoffHiddenOrLast(threw, off, out isGood));
        Assert.False(isGood);
        // Not a switching body (no Use): nothing hidden, and nothing to remember.
        var plain = new ShellBodyAtr.PartAtr(null, null, true, null);
        Assert.Null(CompositorService.StandoffHiddenOrLast(plain, off, out isGood));
        Assert.False(isGood);
        // No mod file (our own output held, or game data): ForPart never asks the settings.
        bool asked = false;
        var none = ShellBodyAtr.ForPart("top", true, null, @"E:\mods", 1, _ => { asked = true; return null; });
        Assert.False(asked);
        Assert.Null(none.Off);
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-b-32")]
    public void Last_good_is_keyed_by_body_mod_part_and_race()
    {
        var a = CompositorService.StandoffBodyKey("NIRÆ body", "top", 1);
        Assert.Equal(a, CompositorService.StandoffBodyKey("nirÆ BODY", "top", 1));
        Assert.NotEqual(a, CompositorService.StandoffBodyKey("Other body", "top", 1));
        Assert.NotEqual(a, CompositorService.StandoffBodyKey("NIRÆ body", "glv", 1));
        Assert.NotEqual(a, CompositorService.StandoffBodyKey("NIRÆ body", "top", 4));
        Assert.Null(CompositorService.StandoffBodyKey(null, "top", 1));
        Assert.Equal("top", CompositorService.StandoffPart("chara/equipment/e0000/model/c0201e0000_top.mdl"));
        Assert.Null(CompositorService.StandoffPart(@"E:\mods\NIRAE\c0201e0000_top_crimson_curvy_v69.mdl"));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-1")]
    [Trait("Plan", "EDGE-issue-sweep-b-5")]
    public void Cache_evicts_only_hidden_siblings()
    {
        IReadOnlySet<string>? Off(string a) => new HashSet<string> { a };
        string Key(string ids, int w, IReadOnlySet<string>? h, int radius = 2, float strength = 1f)
            => CompositorService.BustStandoffKey("Bralettes", w, radius, strength, [ids], [h]);
        var cache = new ConcurrentDictionary<string, byte[]?>(StringComparer.Ordinal);
        string small = Key("nirae|1|2", 512, Off("atrx_c_large")), large = Key("nirae|1|2", 512, Off("atrx_c_small"));
        string otherIds = Key("rue|1|2", 512, Off("atrx_c_large")), otherSize = Key("nirae|1|2", 1024, Off("atrx_c_large"));
        Assert.NotEqual(small, large);
        string otherRadius = Key("nirae|1|2", 512, Off("atrx_c_large"), radius: 3);
        string otherStrength = Key("nirae|1|2", 512, Off("atrx_c_large"), strength: 0.5f);
        foreach (var k in new[] { small, otherIds, otherSize, otherRadius, otherStrength }) cache[k] = [1];
        CompositorService.EvictSiblings(cache, large);
        Assert.False(cache.ContainsKey(small));   // the same body, size and radius: replaced
        foreach (var k in new[] { otherIds, otherSize, otherRadius, otherStrength }) Assert.True(cache.ContainsKey(k));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-1")]
    [Trait("Plan", "EDGE-issue-sweep-b-4")]
    [Trait("Plan", "EDGE-issue-sweep-b-5")]
    public void Standoff_wiring()
    {
        var blend = Source("Services/CompositorService.BlendStats.cs");
        int at = blend.IndexOf("private byte[]? BustStandoff(", StringComparison.Ordinal);
        var body = blend[at..blend.IndexOf("private IReadOnlyList<IReadOnlySet<string>?> StandoffHiddenList(", at, StringComparison.Ordinal)];
        int gate1 = body.IndexOf("if (entry?.Metadata.BustBridge != true) return null;", StringComparison.Ordinal);
        int gate2 = body.IndexOf("if (strength <= 0f) return null;", StringComparison.Ordinal);
        int hidden = body.IndexOf("StandoffHiddenList(models, sources, settingsOf)", StringComparison.Ordinal);
        Assert.True(gate1 > 0 && gate2 > gate1 && hidden > gate2, "the hidden list is worked out only past the gates");
        Assert.Contains("ConcurrentDictionary<string, byte[]?> _bustStandoff", blend);
        Assert.Contains("ms, ", body);
        var occ = Source("Services/CompositorService.OcclusionPass.cs");
        Assert.Equal(2, occ.Split("BustStandoff(modDir, bodyMdls, bodySources, material.run.RunModSettings").Length - 1);
        Assert.Contains("kept {fPos.Length / 3}/{total} vertices", Source("Services/BodyBridge.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-1")]
    [Trait("Plan", "EDGE-issue-sweep-b-28")]
    public void Chest_flip_reaches_the_standoff_through_the_shell_watch()
    {
        Assert.Contains("ShellBodyAtr.Moved(_lastBodyAtrUses", Source("Services/CompositorService.Events.cs"));
        var run = Source("Services/CompositorService.CompositeRun.cs");
        Assert.Contains("ShellBodyAtr.Moved(compositor._lastBodyAtrUses", run);
        Assert.Contains("TryForceForShapes(null, \"body-shapes-moved\")", run);
        Assert.Contains("{HiddenMark}{string.Join(\";\", hidden.Select(ShellBodyAtr.Key))}", Source("Services/CompositorService.BlendStats.cs"));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-b-1")]
    [Trait("Plan", "EDGE-issue-sweep-b-2")]
    public void Mod_file_follows_the_chosen_source()
    {
        Assert.Equal("res", SecondSkinService.SourceModFile(K.Resolved, "res", "rem", "kept"));
        Assert.Equal("set", SecondSkinService.SourceModFile(K.Settled, "set", "rem", "kept"));
        Assert.Equal("rem", SecondSkinService.SourceModFile(K.Remembered, null, "rem", "kept"));
        Assert.Equal("kept", SecondSkinService.SourceModFile(K.Kept, "keptfile", "rem", "kept"));
        Assert.Null(SecondSkinService.SourceModFile(K.OwnHeld, "ours", "rem", "kept"));
    }

    private static string Source(string relative, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", relative));
}
