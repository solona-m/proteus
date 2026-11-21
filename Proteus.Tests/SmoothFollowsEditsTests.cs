using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Proteus.Services;
using Xunit;
using K = Proteus.Services.SecondSkinService.BodySourceKind;
using Stamp = Proteus.Services.SecondSkinService.SourceStamp;

namespace Proteus.Tests;

/// <summary>
/// A part second skin republishes smoothed follows edits to the mod file it came from (a Studio save, a repack): the
/// remembered or kept upstream is re-read when that file changes, the composite fingerprint sees the write, and Studio
/// credits the part to its own mod while the game draws it out of our output.
/// </summary>
public class SmoothFollowsEditsTests : IDisposable
{
    private const string Skirt = "chara/equipment/e6087/model/c0201e6087_dwn.mdl";
    private const string BareTop = "chara/equipment/e0000/model/c0201e0000_top.mdl";

    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus-smooth-edits-" + Guid.NewGuid().ToString("N"));
    private string Mods => Path.Combine(root, "mods");
    private string Managed => Path.Combine(Mods, SidecarDiscoveryService.ManagedModDir);
    private string SkirtFile => Path.Combine(Mods, "Dappled Light", "items", "skirt", "c0201e6087_dwn.mdl");

    public SmoothFollowsEditsTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SkirtFile)!);
        Directory.CreateDirectory(Managed);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] Garment(int triangles = 1)
        => SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_c0201e6087_dwn_a.mtrl",
                                                            new SyntheticModel.Sub(0, TrianglesPerIsland: triangles)));

    private static byte[] Body(int triangles = 1)
        => SyntheticModel.Build([], new SyntheticModel.Mesh("/mt_c0201b0001_bibo.mtrl",
                                                            new SyntheticModel.Sub(0, TrianglesPerIsland: triangles)));

    private static SecondSkinService.UpstreamRefresh Refresh(byte[] current, string? src, Stamp? recorded, string outputRoot,
                                                             string gp = Skirt, Func<string, byte[]?>? read = null,
                                                             Func<string, Stamp?>? stat = null)
        => SecondSkinService.RefreshUpstream(current, src, recorded, gp, outputRoot,
                                             stat ?? SecondSkinService.StampOf, read ?? SecondSkinService.SafeReadFile);

    // ── AC-1 / AC-2: re-read after an edit ───────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-1")]
    public void Remembered_body_is_reread_after_its_file_changes()
    {
        var a = Garment(1);
        File.WriteAllBytes(SkirtFile, a);
        var stampA = SecondSkinService.StampOf(SkirtFile);
        var b = Garment(3);
        File.WriteAllBytes(SkirtFile, b);
        File.SetLastWriteTimeUtc(SkirtFile, DateTime.UtcNow.AddSeconds(5));

        var r = Refresh(a, SkirtFile, stampA, Managed);

        Assert.Equal(b, r.Fresh);
        Assert.Equal(SecondSkinService.StampOf(SkirtFile), r.Stamp);
        Assert.False(r.Rejected);
    }

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-1")]
    public void Refreshed_bytes_publish_under_a_new_smoothed_name()
    {
        var dir = Path.Combine(Managed, "models");
        Assert.NotEqual(SecondSkinService.SmoothedBodyPath(dir, Skirt, Garment(1)),
                        SecondSkinService.SmoothedBodyPath(dir, Skirt, Garment(3)));
    }

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-1")]
    public void Harvest_refreshes_the_remembered_branch()
    {
        var branch = Between(Harvest(), "source == BodySourceKind.Remembered", "source == BodySourceKind.Kept");
        Assert.Contains("RefreshUpstream(", branch);
        Assert.Contains("bytes = fresh", branch);
        Assert.Contains("service._upstreamBodies[bodyGamePath] = fresh", branch);
        Assert.Contains("KeepUpstream(", branch);
        Assert.Contains("NoteStamp(", branch);
    }

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-2")]
    public void Kept_copy_is_refreshed_from_its_recorded_source()
    {
        var kept = SecondSkinService.UpstreamKeptPath(Managed, Skirt);
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        var a = Garment(1);
        File.WriteAllBytes(kept, a);
        File.WriteAllBytes(SkirtFile, a);
        ShellBodyAtr.WriteSource(kept, SkirtFile, Mods);
        var b = Garment(4);                      // a different length: ReadSource's length check would refuse it
        File.WriteAllBytes(SkirtFile, b);
        Assert.NotEqual(a.Length, b.Length);

        var src = ShellBodyAtr.ReadSourcePath(kept, Mods);
        Assert.Equal(SkirtFile, src);
        Assert.Equal(b, Refresh(a, src, null, Managed).Fresh);
    }

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-2")]
    public void Harvest_refreshes_the_kept_branch()
    {
        var branch = Between(Harvest(), "source == BodySourceKind.Kept", "else if (bodyIsOurs)");
        int path = branch.IndexOf("ShellBodyAtr.ReadSourcePath(upstreamDisk", StringComparison.Ordinal);
        int refresh = branch.IndexOf("RefreshUpstream(", StringComparison.Ordinal);
        int lengthChecked = branch.IndexOf("ShellBodyAtr.ReadSource(upstreamDisk, modsRoot, bytes.Length)", StringComparison.Ordinal);
        Assert.True(path >= 0 && refresh > path && lengthChecked > refresh);
        Assert.Contains("KeepUpstream(", branch);
    }

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-2")]
    public void KeepUpstream_writes_the_record_with_the_copy()
    {
        var shell = Source("Services/SecondSkinService.ShellSetBuild.cs");
        Assert.DoesNotContain("void KeepUpstream(", Harvest());   // class-level, so HarvestParts' own count of two holds
        var keep = Method(shell, "private void KeepUpstream(");
        Assert.Matches(@"WriteIfChanged\(upstreamDisk[^;]*;\s*ShellBodyAtr\.WriteSource\(upstreamDisk", keep);
        Assert.Equal(2, Regex.Matches(Harvest(), @"WriteIfChanged\(upstreamDisk[^;]*;\s*ShellBodyAtr\.WriteSource\(upstreamDisk").Count);
    }

    // ── AC-3 / AC-6 / EDGE-18 / EDGE-21: the fingerprint ─────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-3")]
    public void Base_line_changes_when_the_source_is_written()
    {
        var memo = Path.Combine(Mods, "lemonade", "dwn.mdl");
        File.WriteAllBytes(SkirtFile, Garment(1));
        File.SetLastWriteTimeUtc(SkirtFile, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        string Line() => CompositorService.FingerprintBaseLine(
            Skirt, memo, SecondSkinService.UpstreamSourceIdOf(SkirtFile, null, Skirt), skinOnly: false);
        var before = Line();

        File.SetLastWriteTimeUtc(SkirtFile, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));   // same length, new time
        var touched = Line();
        File.WriteAllBytes(SkirtFile, Garment(4));                                                  // new length
        var resized = Line();

        Assert.NotEqual(before, touched);
        Assert.NotEqual(touched, resized);
        Assert.Contains("|src=" + SkirtFile, before);
    }

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-3")]
    public void Fingerprint_uses_the_base_line_helper()
    {
        var timeline = Source("Services/CompositorService.Timeline.cs");
        Assert.Contains("FingerprintBaseLine(gamePath", timeline);
        Assert.Contains("secondSkin.UpstreamSourceId(gamePath, managedModDir, modsRoot)", timeline);
        Assert.Contains("gamePath.EndsWith(\".mdl\"", timeline);
    }

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-6")]
    public void Base_line_is_stable_without_a_write()
    {
        File.WriteAllBytes(SkirtFile, Garment(1));
        var id = SecondSkinService.UpstreamSourceIdOf(SkirtFile, null, Skirt);
        Assert.Equal(CompositorService.FingerprintBaseLine(Skirt, null, id, false),
                     CompositorService.FingerprintBaseLine(Skirt, null, SecondSkinService.UpstreamSourceIdOf(SkirtFile, null, Skirt), false));
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-18")]
    public void Base_line_without_a_source_is_todays_format()
    {
        var memo = Path.Combine(Mods, "body.mdl");
        File.WriteAllBytes(memo, [1, 2, 3]);
        var fi = new FileInfo(memo);
        Assert.Equal($"base:{BareTop}={memo}|3|{fi.LastWriteTimeUtc.Ticks}",
                     CompositorService.FingerprintBaseLine(BareTop, memo, null, false));
        Assert.Equal($"base:{BareTop}=(game data)", CompositorService.FingerprintBaseLine(BareTop, null, null, true));
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-21")]
    public void Skin_base_line_carries_src_only_for_body_parts()
    {
        const string id = @"E:\mods\x.mdl|1|2";
        Assert.Contains("|src=", CompositorService.FingerprintBaseLine(Skirt, null, id, skinOnly: false));
        Assert.DoesNotContain("|src=", CompositorService.FingerprintBaseLine(Skirt, null, id, skinOnly: true));
        Assert.Contains("|src=", CompositorService.FingerprintBaseLine(BareTop, null, id, skinOnly: true));
        Assert.Contains("|src=", CompositorService.FingerprintBaseLine(BareTop, null, id, skinOnly: false));

        var parts = CompositorService.BodyModelPathsFor("chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl")!;
        Assert.All(parts, p => Assert.True(CompositorService.IsBareBodyPartModel(p)));
        Assert.False(CompositorService.IsBareBodyPartModel(Skirt));
    }

    // ── AC-4: one body for both phases ───────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-4")]
    public void Seam_map_and_shell_share_the_refresh()
    {
        var rule = Source("Services/SecondSkinService.BodySource.cs");
        var seam = Method(rule, "internal BodySource BodySourceFor(");
        Assert.Contains("RefreshUpstream(", seam);
        Assert.DoesNotContain("_upstreamBodies[", seam);
        Assert.DoesNotContain("NoteStamp(", seam);
        Assert.Contains("RefreshUpstream(", Harvest());
    }

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-4")]
    public void Seam_map_reads_the_refreshed_bytes()
    {
        var a = Garment(1);
        File.WriteAllBytes(SkirtFile, a);
        var stampA = SecondSkinService.StampOf(SkirtFile);
        File.WriteAllBytes(SkirtFile, Garment(2));
        File.SetLastWriteTimeUtc(SkirtFile, DateTime.UtcNow.AddSeconds(5));
        var fresh = Refresh(a, SkirtFile, stampA, Managed).Fresh!;
        Assert.NotEqual(SecondSkinService.BodySourceId(K.Remembered, null, a, Skirt),
                        SecondSkinService.BodySourceId(K.Remembered, null, fresh, Skirt));
    }

    // ── AC-6 / AC-7 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-6")]
    public void Unchanged_file_is_not_read()
    {
        File.WriteAllBytes(SkirtFile, Garment(1));
        int reads = 0;
        var r = Refresh(Garment(1), SkirtFile, SecondSkinService.StampOf(SkirtFile), Managed,
                        read: f => { reads++; return File.ReadAllBytes(f); });
        Assert.Null(r.Fresh);
        Assert.Null(r.Stamp);
        Assert.Equal(0, reads);
    }

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-7")]
    public void Refresh_is_logged()
    {
        var shell = Source("Services/SecondSkinService.ShellSetBuild.cs");
        var log = Method(shell, "private void LogRefresh(");
        Assert.Contains("log.Information(", log);
        Assert.Contains("changed since it was read", log);
        Assert.Equal(2, Regex.Matches(Harvest(), @"LogRefresh\(").Count);   // the remembered and the kept branch
    }

    // ── edges of the refresh ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-1")]
    public void Missing_source_keeps_the_remembered_bytes()
    {
        var gone = Path.Combine(Mods, "Gone", "x.mdl");
        Assert.Equal(default, Refresh(Garment(), gone, null, Managed));
        Assert.Equal(default, Refresh(Garment(), SkirtFile, null, Managed, stat: _ => null));
        Assert.Equal(default, Refresh(Garment(), SkirtFile, null, Managed, stat: _ => throw new IOException("locked")));
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-2")]
    public void Torn_read_records_no_stamp()
    {
        File.WriteAllBytes(SkirtFile, Garment(2));
        int calls = 0;
        var moving = Refresh(Garment(1), SkirtFile, null, Managed,
                             stat: _ => new Stamp(100, ++calls));       // a different stamp after the read than before
        Assert.Null(moving.Fresh);
        Assert.Null(moving.Stamp);
        Assert.False(moving.Rejected);

        var settled = Refresh(Garment(1), SkirtFile, null, Managed);    // next composite: the file has settled
        Assert.NotNull(settled.Fresh);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-3")]
    public void Game_data_source_is_never_refreshed()
    {
        int reads = 0;
        Func<string, byte[]?> read = _ => { reads++; return Garment(); };
        Assert.False(SecondSkinService.MayRefreshFrom(Skirt, Skirt, Managed));
        Assert.False(SecondSkinService.MayRefreshFrom("relative/x.mdl", Skirt, Managed));
        Assert.Equal(default, Refresh(Garment(), Skirt, null, Managed, read: read));
        Assert.Equal(0, reads);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-4")]
    public void Own_output_is_never_a_refresh_source()
        => Assert.False(SecondSkinService.MayRefreshFrom(Path.Combine(Managed, "models", "smoothed_x.mdl"), Skirt, Managed));

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-5")]
    public void Live_preview_is_never_a_refresh_source()
    {
        Assert.False(SecondSkinService.MayRefreshFrom(
            Path.Combine(ManagedModPaths.LivePreviewDir(Managed), "preview_dwn.mdl"), Skirt, Managed));
        Assert.True(SecondSkinService.MayRefreshFrom(SkirtFile, Skirt, Managed));
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-5")]
    public void Preview_resolve_is_not_remembered()
    {
        var resolved = Between(Harvest(), "var resolvedStamp", "if (bytes == null)");
        int guard = resolved.IndexOf("!(bodyDisk != null && LiveBrushPreview.IsPreviewFile(bodyDisk, outputRoot))", StringComparison.Ordinal);
        Assert.True(guard >= 0);
        foreach (var write in new[] { "service._upstreamBodies[bodyGamePath] = bytes", "service._upstreamBodyDisks[bodyGamePath] = bodyDisk",
                                      "WriteIfChanged(upstreamDisk, bytes)", "service.NoteStamp(" })
            Assert.True(resolved.IndexOf(write, StringComparison.Ordinal) > guard, write);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-6")]
    public void Touched_but_identical_file_records_only_the_stamp()
    {
        var a = Garment(1);
        File.WriteAllBytes(SkirtFile, a);
        var old = SecondSkinService.StampOf(SkirtFile);
        File.SetLastWriteTimeUtc(SkirtFile, DateTime.UtcNow.AddSeconds(5));
        var r = Refresh(a, SkirtFile, old, Managed);
        Assert.Null(r.Fresh);
        Assert.NotNull(r.Stamp);
        Assert.NotEqual(old, r.Stamp);
        Assert.False(r.Rejected);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-7")]
    public void Stamp_is_trusted_only_for_its_own_bytes()
    {
        var service = (SecondSkinService)RuntimeHelpers.GetUninitializedObject(typeof(SecondSkinService));
        InitStamps(service);
        var a = Garment(1);
        var s = new Stamp(10, 20);
        service.NoteStamp(Skirt, a, s);
        Assert.Equal(s, service.StampFor(Skirt, a));
        Assert.Null(service.StampFor(Skirt, (byte[])a.Clone()));
        service.NoteStamp(Skirt, a, null);
        Assert.Null(service.StampFor(Skirt, a));

        // Unknown stamp: one read, then the stamp to record.
        File.WriteAllBytes(SkirtFile, a);
        int reads = 0;
        var r = Refresh(a, SkirtFile, null, Managed, read: f => { reads++; return File.ReadAllBytes(f); });
        Assert.Equal(1, reads);
        Assert.Null(r.Fresh);
        Assert.NotNull(r.Stamp);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-8")]
    public void Kept_source_survives_a_length_change()
    {
        var kept = SecondSkinService.UpstreamKeptPath(Managed, Skirt);
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllBytes(kept, Garment(1));
        File.WriteAllBytes(SkirtFile, Garment(1));
        ShellBodyAtr.WriteSource(kept, SkirtFile, Mods);
        File.WriteAllBytes(SkirtFile, Garment(5));
        Assert.Equal(SkirtFile, SecondSkinService.KeptSourceOf(Managed, Skirt, Mods));
        Assert.Contains("KeptSourceOf(", Method(Source("Services/SecondSkinService.BodySource.cs"),
                                                "internal (string? Remembered, string? KeptSource) KnownBodyFiles("));
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-10")]
    public void Bare_body_source_refreshes_like_a_garment()
    {
        var bodyFile = Path.Combine(Mods, "Bibo+", "body", "c0201e0000_top.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(bodyFile)!);
        var a = Body(1);
        File.WriteAllBytes(bodyFile, a);
        var stamp = SecondSkinService.StampOf(bodyFile);
        var b = Body(3);
        File.WriteAllBytes(bodyFile, b);
        File.SetLastWriteTimeUtc(bodyFile, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(b, Refresh(a, bodyFile, stamp, Managed, gp: BareTop).Fresh);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-10")]
    public void Garment_and_body_both_pass_the_parse_check()
    {
        Assert.True(SecondSkinService.ParsesAsModel(Garment()));
        Assert.True(SecondSkinService.ParsesAsModel(Body()));
        Assert.False(SecondSkinService.ParsesAsModel([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.False(SecondSkinService.ParsesAsModel(Garment()[..64]));
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-11")]
    public void Settled_branch_records_its_stamp()
    {
        var harvest = Harvest();
        int stamp = harvest.IndexOf("StampOf(settledDisk)", StringComparison.Ordinal);
        int load = harvest.IndexOf("LoadRawFile(settledDisk", StringComparison.Ordinal);
        Assert.True(stamp >= 0 && stamp < load);
        Assert.Contains("service.NoteStamp(bodyGamePath, settledBytes, settledStamp)", harvest);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-12")]
    public void Refresh_and_credit_never_read_the_compositor_memo()
    {
        var rule = Source("Services/SecondSkinService.BodySource.cs");
        var compositor = Source("Services/CompositorService.cs");
        foreach (var body in new[]
                 {
                     Method(rule, "internal static UpstreamRefresh RefreshUpstream("),
                     Method(rule, "internal static string? UpstreamSourceIdOf("),
                     Method(rule, "internal static string? KeptSourceOf("),
                     Method(rule, "internal string? UpstreamSourceId("),
                 })
        {
            Assert.DoesNotContain("_upstreamByGamePath", body);
            Assert.DoesNotContain("settledUpstream", body);
            Assert.DoesNotContain("resolveUpstream", body);
        }
        var credit = compositor[compositor.IndexOf("internal static string? ManagedModelSource(", StringComparison.Ordinal)..];
        credit = credit[..credit.IndexOf(';')];
        Assert.DoesNotContain("_upstreamByGamePath", credit);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-13")]
    public void OwnHeld_has_no_refresh_or_credit()
    {
        Assert.Null(SecondSkinService.UpstreamSourceIdOf(null, null, Skirt));
        Assert.Null(SecondSkinService.UpstreamSourceIdOf(Skirt, null, Skirt));   // game data names no file
        var smooth = Method(Source("Services/SecondSkinService.ShellSetBuild.cs"), "private void SmoothBody(");
        Assert.Equal(2, Regex.Matches(smooth, @"smoothedSources\[bPath\]\s*=").Count);
        Assert.Equal(2, Regex.Matches(smooth, @"partSources\.TryGetValue\(bPath").Count);
        var ownHeld = Between(Harvest(), "else if (bodyIsOurs)", "var resolvedStamp");
        Assert.DoesNotContain("sourceDisk =", ownHeld);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-19")]
    public void Unparseable_stable_file_is_rejected_once()
    {
        File.WriteAllBytes(SkirtFile, [9, 9, 9, 9, 9, 9, 9, 9, 9, 9]);
        var now = SecondSkinService.StampOf(SkirtFile);
        var first = Refresh(Garment(), SkirtFile, null, Managed);
        Assert.Null(first.Fresh);
        Assert.Equal(now, first.Stamp);
        Assert.True(first.Rejected);

        int reads = 0;
        var again = Refresh(Garment(), SkirtFile, first.Stamp, Managed, read: f => { reads++; return File.ReadAllBytes(f); });
        Assert.Equal(0, reads);
        Assert.Null(again.Fresh);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-22")]
    public void Upstream_disks_follow_a_mod_move()
    {
        var disks = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Skirt] = SkirtFile,
            [BareTop] = Path.Combine(Mods, "Other", "y.mdl"),
        };
        var moves = new Dictionary<string, ModMoveLedger.MoveRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dappled Light"] = new("Dappled Light 2", null, long.MaxValue),
        };
        SecondSkinService.RemapUpstreamDisks(disks, moves, Mods);
        Assert.Equal(Path.Combine(Mods, "Dappled Light 2", "items", "skirt", "c0201e6087_dwn.mdl"), disks[Skirt]);
        Assert.Equal(Path.Combine(Mods, "Other", "y.mdl"), disks[BareTop]);

        // A newer write that lands first is never overwritten by the remap of the older value: compare-and-swap only.
        var remap = Method(Source("Services/SecondSkinService.cs"),
                           "internal static void RemapUpstreamDisks(ConcurrentDictionary<string, string> disks");
        Assert.Contains("disks.TryUpdate(k, v, old)", remap);
        Assert.DoesNotMatch(@"disks\[[^\]]+\]\s*=", remap);

        Assert.Contains("secondSkin.RemapUpstreamDisks(", Source("Services/CompositorService.ModMove.cs"));
    }

    // ── ShellBodyAtr.ReadSourcePath ──────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-8")]
    public void ReadSourcePath_ignores_length_but_ReadSource_still_checks_it()
    {
        var kept = Path.Combine(Managed, "models", "upstream", "x.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllBytes(kept, new byte[100]);
        File.WriteAllBytes(SkirtFile, new byte[100]);
        ShellBodyAtr.WriteSource(kept, SkirtFile, Mods);
        File.WriteAllBytes(SkirtFile, new byte[120]);
        Assert.Equal(SkirtFile, ShellBodyAtr.ReadSourcePath(kept, Mods));
        Assert.Null(ShellBodyAtr.ReadSource(kept, Mods, 100));
        Assert.Equal(SkirtFile, ShellBodyAtr.ReadSource(kept, Mods, 120));
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-9")]
    public void Kept_garment_refresh_keeps_the_bare_only_source_rule()
    {
        var kept = Between(Harvest(), "source == BodySourceKind.Kept", "else if (bodyIsOurs)");
        Assert.Matches(@"if \(isBarePart\)\s*\{\s*sourceDisk = ShellBodyAtr\.ReadSource\(", kept);
        Assert.DoesNotMatch(@"sourceDisk\s*=\s*ShellBodyAtr\.ReadSourcePath", kept);
        Assert.DoesNotMatch(@"sourceDisk\s*=\s*keptSrc", kept);
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-17")]
    public void ReadSourcePath_refuses_rooted_and_parent_records()
    {
        var kept = Path.Combine(Managed, "models", "upstream", "x.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllBytes(kept, [1]);
        File.WriteAllBytes(Path.Combine(Mods, "escaped.mdl"), [1]);
        foreach (var record in new[] { SkirtFile, "../x.mdl", "a/../../escaped.mdl", "Dappled Light/../escaped.mdl", "", "  " })
        {
            File.WriteAllText(kept + ".src", record);
            Assert.Null(ShellBodyAtr.ReadSourcePath(kept, Mods));
        }
    }

    // ── Studio credit ────────────────────────────────────────────────────────────────────────────────────

    private List<(string Mod, string Rel)> Credit(string drawn, string gamePath, string? smoothedSource, string? appendSource = null)
        => WornModelCredit.Credit([drawn], Mods, _ => gamePath,
                                  g => CompositorService.ManagedModelSource(_ => appendSource, _ => smoothedSource, g));

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-5")]
    public void A_smoothed_copy_credits_its_source_mod()
    {
        var drawn = Path.Combine(Managed, "models", "smoothed_chara_equipment_e6087_model_c0201e6087_dwn_cd83.mdl");
        var found = Credit(drawn, Skirt, SkirtFile);
        Assert.Contains(("Dappled Light", "items/skirt/c0201e6087_dwn.mdl"), found);
    }

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-5")]
    public void Studio_worn_files_use_the_managed_model_source()
        => Assert.Contains("WornModelCredit.Credit(files, root, GamePathOf, compositor.GetManagedModelSource)",
                           Source("Gui/PartsPanel.cs"));

    [Fact]
    [Trait("Plan", "AC-smooth-follows-edits-5")]
    public void Smoothed_sources_are_published_with_the_locators()
    {
        Assert.Contains("compositor._smoothedBodySources = compositor.LedgeredPaths(", Source("Services/CompositorService.CompositeRun.cs"));
        Assert.Contains("_smoothedBodySources = new(", Method(Source("Services/CompositorService.cs"), "private void ClearShellLocators("));
        var phase = Source("Services/CompositorService.ShellPhase.cs");
        Assert.Contains("run.nextSmoothedBodySources = null", phase);
        Assert.Contains("shells.SmoothedBodySources", phase);
        Assert.Contains("SmoothedBodySources = smoothedSources", Source("Services/SecondSkinService.ShellSetBuild.cs"));
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-14")]
    public void A_smoothed_source_outside_mods_or_managed_is_not_credited()
    {
        var drawn = Path.Combine(Managed, "models", "smoothed_x.mdl");
        foreach (var source in new[] { null, Skirt, Path.Combine(root, "outside", "x.mdl"),
                                       Path.Combine(Managed, "models", "upstream", "x.mdl") })
            Assert.Single(Credit(drawn, Skirt, source));
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-15")]
    public void Append_host_source_wins_over_smoothed()
    {
        Assert.Equal("A", CompositorService.ManagedModelSource(_ => "A", _ => "B", Skirt));
        Assert.Equal("B", CompositorService.ManagedModelSource(_ => null, _ => "B", Skirt));
    }

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-16")]
    public void Smoothed_sources_follow_a_mod_move()
        => Assert.Contains("Swap(ref _smoothedBodySources, m => ModMoveLedger.RemapPathValues(",
                           Source("Services/CompositorService.ModMove.cs"));

    [Fact]
    [Trait("Plan", "EDGE-smooth-follows-edits-20")]
    public void A_smoothed_bare_body_credits_its_body_mod()
    {
        var bodyFile = Path.Combine(Mods, "Bibo+", "body", "c0201e0000_top.mdl");
        var drawn = Path.Combine(Managed, "models", "smoothed_chara_equipment_e0000_model_c0201e0000_top_ab.mdl");
        Assert.Contains(("Bibo+", "body/c0201e0000_top.mdl"), Credit(drawn, BareTop, bodyFile));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static void InitStamps(SecondSkinService service)
    {
        foreach (var f in typeof(SecondSkinService).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            if (f.Name is "_upstreamBodyStamps" or "_upstreamRejectedWarned")
                f.SetValue(service, Activator.CreateInstance(f.FieldType, StringComparer.OrdinalIgnoreCase));
    }

    private static string Harvest()
        => Method(Source("Services/SecondSkinService.ShellSetBuild.cs"), "private void HarvestParts(");

    private static string Between(string text, string from, string to)
    {
        int a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"no {from}");
        int b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"no {to} after {from}");
        return text[a..b];
    }

    private static string Source(string relative, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", relative));

    private static string Method(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no {signature}");
        int open = source.IndexOf('{', at), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced {signature}");
        return "";
    }
}
