using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Discovery through its cache: <see cref="SidecarDiscoveryService.DiscoverCore"/> with injected Penumbra answers, and
/// <see cref="SidecarDiscoveryService.ReadForDiscovery"/> over real temp sidecars. The SidecarDiscoveryTests of the plan.
/// </summary>
public class SidecarDiscoveryCoreTests : IDisposable
{
    private const string Root = @"C:\mods";
    private static readonly Guid Coll = Guid.NewGuid();
    private static readonly ProteusMetadata Meta = new();

    private readonly string temp = Path.Combine(Path.GetTempPath(), "proteus-disc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch { }
    }

    private static SidecarDiscoveryService.DiscoveryInputs Inputs(
        IReadOnlyDictionary<string, string>? mods, SidecarCache cache,
        Func<string, (bool, int)?>? settings = null,
        Func<string, SidecarDiscoveryService.DiscoveryRead>? read = null,
        string? root = Root, Guid? coll = null, bool noColl = false)
        => new(root, mods, noColl ? null : coll ?? Coll,
               (_, dir) => settings != null ? settings(dir) : (true, 0),
               cache,
               (dir, _) => read != null ? read(dir) : new SidecarDiscoveryService.DiscoveryRead(Meta, false, 1));

    /// <summary>A cache whose drive has a sidecar exactly for <paramref name="withSidecar"/>, counting stats.</summary>
    private static (SidecarCache Cache, Func<int> Stats) CacheFor(params string[] withSidecar)
    {
        int stats = 0;
        var set = new HashSet<string>(withSidecar.Select(m => SidecarCache.MetaPath(Root, m)), StringComparer.OrdinalIgnoreCase);
        var cache = new SidecarCache(p => { stats++; return set.Contains(p) ? new SidecarStamp(1, 2) : null; },
                                     _ => "{}", () => 0);
        return (cache, () => stats);
    }

    private static Dictionary<string, string> Mods(params string[] dirs) => dirs.ToDictionary(d => d, d => d);

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-1")]
    [Trait("Plan", "EDGE-idle-disk-io-1")]
    public void DiscoverCore_Complete_FalseForNullRootListCollection_OrEmptyList()
    {
        // [AC-idle-disk-io-1] [EDGE-idle-disk-io-1]
        var (cache, _) = CacheFor();
        SidecarDiscoveryService.DiscoverCore(Inputs(Mods("a"), cache, root: null), true, out var c1);
        SidecarDiscoveryService.DiscoverCore(Inputs(null, cache), true, out var c2);
        SidecarDiscoveryService.DiscoverCore(Inputs(Mods("a"), cache, noColl: true), true, out var c3);
        SidecarDiscoveryService.DiscoverCore(Inputs(Mods(), cache), true, out var c4);
        Assert.False(c1);
        Assert.False(c2);
        Assert.False(c3);
        Assert.False(c4);

        // Readable, nothing enabled: a complete, empty answer.
        var (cache2, _) = CacheFor("a");
        var none = SidecarDiscoveryService.DiscoverCore(
            Inputs(Mods("a", "b"), cache2, settings: _ => (false, 0)), true, out var complete);
        Assert.Empty(none);
        Assert.True(complete);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-2")]
    [Trait("Plan", "EDGE-idle-disk-io-3")]
    public void DiscoverCore_UnreadableSettings_Incomplete_EnabledStillReturned()
    {
        // [EDGE-idle-disk-io-2] [EDGE-idle-disk-io-3]
        var (cache, _) = CacheFor("on", "unreadable");
        var found = SidecarDiscoveryService.DiscoverCore(
            Inputs(Mods("on", "unreadable"), cache, settings: d => d == "on" ? (true, 0) : null), true, out var complete);
        Assert.Equal(["on"], found.Select(e => e.ModDirectory));
        Assert.False(complete);

        var (cache2, _) = CacheFor("unreadable");
        var none = SidecarDiscoveryService.DiscoverCore(
            Inputs(Mods("unreadable"), cache2, settings: _ => null), true, out var complete2);
        Assert.Empty(none);
        Assert.False(complete2);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-4")]
    public void DiscoverCore_EnabledIoFailure_Incomplete_BadJsonComplete()
    {
        // [EDGE-idle-disk-io-4]
        var (cache, _) = CacheFor("a");
        SidecarDiscoveryService.DiscoverCore(Inputs(Mods("a"), cache,
            read: _ => new SidecarDiscoveryService.DiscoveryRead(null, true, 0)), true, out var ioFailed);
        Assert.False(ioFailed);

        SidecarDiscoveryService.DiscoverCore(Inputs(Mods("a"), cache,
            read: _ => new SidecarDiscoveryService.DiscoveryRead(null, false, 0)), true, out var badJson);
        Assert.True(badJson);

        // A disabled mod's sidecar failing to read does not leave the enabled-set answer incomplete.
        SidecarDiscoveryService.DiscoverCore(Inputs(Mods("a"), cache, settings: _ => (false, 0),
            read: _ => new SidecarDiscoveryService.DiscoveryRead(null, true, 0)), false, out var disabledIo);
        Assert.True(disabledIo);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-3")]
    public void DiscoverCore_SecondCall_StatsOnlyProteusMods()
    {
        // [AC-idle-disk-io-3]
        var (cache, stats) = CacheFor("mod0010", "mod0500");
        var mods = Enumerable.Range(0, 1000).ToDictionary(i => $"mod{i:D4}", i => $"Mod {i}");
        var settingsAsked = new List<string>();
        var inputs = Inputs(mods, cache, settings: d => { settingsAsked.Add(d); return (true, 0); });

        var first = SidecarDiscoveryService.DiscoverCore(inputs, false, out _);
        Assert.Equal(2, first.Count);
        Assert.Equal(1000, stats());

        settingsAsked.Clear();
        var second = SidecarDiscoveryService.DiscoverCore(inputs, false, out _);
        Assert.Equal(2, second.Count);
        Assert.Equal(1000, stats());   // the reads are injected here; the presence sweep made no stat at all
        Assert.Equal(["mod0010", "mod0500"], settingsAsked);
    }

    private (SidecarDiscoveryService Service, string ModDir, string MetaPath) TempSidecar(string json)
    {
        var modDir = "Skin";
        var meta = Path.Combine(temp, modDir, SidecarDiscoveryService.SidecarSubdir, SidecarDiscoveryService.MetadataFile);
        Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(meta, json);
        return (new SidecarDiscoveryService(null!, Substitute.For<IPluginLog>()), modDir, meta);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-4")]
    [Trait("Plan", "EDGE-idle-disk-io-11")]
    public void SaveMetadata_ForgetsPath_EvenWithSameStamp()
    {
        // [AC-idle-disk-io-4] [EDGE-idle-disk-io-11]
        var (svc, modDir, meta) = TempSidecar("{}");
        var entry = new OverlayEntry(modDir, modDir, 0, true,
            new ProteusMetadata { ColorTableRows = [new() { Row = 3 }] }, Path.GetDirectoryName(meta)!);
        svc.SaveMetadata(entry);
        var written = File.ReadAllText(meta);
        var mtime = File.GetLastWriteTimeUtc(meta);
        Assert.Equal(3, svc.ReadForDiscovery(modDir, meta)!.ColorTableRows![0].Row);

        // Rewrite to the same length with the old mtime put back: the stamp cannot tell, the explicit forget can.
        entry.Metadata.ColorTableRows![0].Row = 4;
        svc.SaveMetadata(entry);
        Assert.Equal(written.Length, File.ReadAllText(meta).Length);
        File.SetLastWriteTimeUtc(meta, mtime);
        Assert.Equal(4, svc.ReadForDiscovery(modDir, meta)!.ColorTableRows![0].Row);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-13")]
    public void ReadForDiscovery_IndependentGraphs()
    {
        // [EDGE-idle-disk-io-13]
        var (svc, modDir, meta) = TempSidecar("{\"ColorTableRows\":[{\"Row\":1}]}");
        var a = svc.ReadForDiscovery(modDir, meta)!;
        a.ColorTableRows![0].Row = 99;
        a.ColorTableRows.Add(new ColorTableRowPreset { Row = 2 });
        var b = svc.ReadForDiscovery(modDir, meta)!;
        Assert.NotSame(a, b);
        Assert.Single(b.ColorTableRows!);
        Assert.Equal(1, b.ColorTableRows![0].Row);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-14")]
    public void ReadForDiscovery_StagedWins_DeletedNull()
    {
        // [EDGE-idle-disk-io-14]
        var (svc, modDir, meta) = TempSidecar("{\"ColorTableRows\":[{\"Row\":1}]}");
        var entry = new OverlayEntry(modDir, modDir, 0, true,
            new ProteusMetadata { ColorTableRows = [new() { Row = 7 }] }, Path.GetDirectoryName(meta)!);
        svc.StageMetadata(entry);
        Assert.Equal(7, svc.ReadForDiscovery(modDir, meta)!.ColorTableRows![0].Row);

        File.Delete(meta);
        Assert.Null(svc.ReadForDiscovery(modDir, meta));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-15")]
    public void ReadForDiscovery_Unparsable_WarnsOncePerStamp()
    {
        // [EDGE-idle-disk-io-15]
        var log = Substitute.For<IPluginLog>();
        var modDir = "Bad";
        var meta = Path.Combine(temp, modDir, SidecarDiscoveryService.SidecarSubdir, SidecarDiscoveryService.MetadataFile);
        Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        File.WriteAllText(meta, "{ not json");
        var svc = new SidecarDiscoveryService(null!, log);

        for (int i = 0; i < 5; i++) Assert.Null(svc.ReadForDiscovery(modDir, meta));
        log.Received(1).Warning(Arg.Any<Exception>(), Arg.Is<string>(s => s.Contains("Failed to parse")), Arg.Any<object[]>());

        // A new version of the file is a new reason to say so.
        File.WriteAllText(meta, "{ still not json!");
        File.SetLastWriteTimeUtc(meta, DateTime.UtcNow.AddMinutes(1));
        Assert.Null(svc.ReadForDiscovery(modDir, meta));
        log.Received(2).Warning(Arg.Any<Exception>(), Arg.Is<string>(s => s.Contains("Failed to parse")), Arg.Any<object[]>());
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-31")]
    public void AttachHandlers_NullOrEmptyFolder_Ignored_NoThrow()
    {
        // [EDGE-idle-disk-io-31]
        var (svc, modDir, meta) = TempSidecar("{}");
        var root = temp;

        svc.OnPenumbraModAdded(null);
        svc.OnPenumbraModAdded("");
        svc.OnPenumbraModDeleted(null);
        svc.OnPenumbraModMoved(null, "");
        svc.OnPenumbraModSettingChanged(Penumbra.Api.Enums.ModSettingChange.Edited, Guid.Empty, null, false);
        svc.OnPenumbraReady();

        // A real folder name is forgotten: a sidecar made since is seen at once.
        Assert.False(svc.Cache.MayHaveSidecar(root, "absent"));
        var absentMeta = Path.Combine(root, "absent", SidecarDiscoveryService.SidecarSubdir, SidecarDiscoveryService.MetadataFile);
        Directory.CreateDirectory(Path.GetDirectoryName(absentMeta)!);
        File.WriteAllText(absentMeta, "{}");
        Assert.False(svc.Cache.MayHaveSidecar(root, "absent"));
        svc.OnPenumbraModAdded("absent");
        Assert.True(svc.Cache.MayHaveSidecar(root, "absent"));

        // Renamed: both names are forgotten.
        Assert.False(svc.Cache.MayHaveSidecar(root, "renamed"));
        var renamedMeta = Path.Combine(root, "renamed", SidecarDiscoveryService.SidecarSubdir, SidecarDiscoveryService.MetadataFile);
        Directory.CreateDirectory(Path.GetDirectoryName(renamedMeta)!);
        File.WriteAllText(renamedMeta, "{}");
        svc.OnPenumbraModMoved("absent", "renamed");
        Assert.True(svc.Cache.MayHaveSidecar(root, "renamed"));
    }
}
