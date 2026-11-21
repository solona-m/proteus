using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>Shared fakes and source reads for the cache-memory tests.</summary>
internal static class CacheMemoryFixtures
{
    internal const long KiB = 1024, MiB = 1024 * KiB, GiB = 1024 * MiB;

    /// <summary>Counting, mutable memory and GC readings.</summary>
    internal sealed class FakeMachine
    {
        public long Phys = 64 * GiB, Commit = 64 * GiB;
        public bool Unreadable;
        public int Gen2;
        public long Index = 1, Committed, Heap;
        public int MemoryReads, GcReads;

        public long Free { set { Phys = value; Commit = value; } }

        public DecodeCacheMemorySeams Seams(long floor, long quantum) => new(
            () => { MemoryReads++; return Unreadable ? null : new PhysicalMemoryStatus(128 * GiB, Phys, Commit); },
            () => { GcReads++; return new GcSnapshot(Gen2, Index, Committed, Heap); },
            floor, quantum);
    }

    internal static TextureLoader Loader(DecodeCacheMemorySeams seams)
        => new(Substitute.For<IDataManager>(), Substitute.For<IPluginLog>(), seams);

    /// <summary>A 16×16 PNG; decoded at 16×16 it is exactly 1 KiB in the cache.</summary>
    internal static string Png(string dir, string name, byte shade)
    {
        var rgba = new byte[16 * 16 * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = shade;
        var path = Path.Combine(dir, name + ".png");
        using var f = File.Create(path);
        new StbImageWriteSharp.ImageWriter().WritePng(rgba, 16, 16, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, f);
        return path;
    }

    internal static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "proteus-cachemem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    internal static string[] Pngs(string dir, int n, string prefix = "t")
        => Enumerable.Range(0, n).Select(i => Png(dir, prefix + i, (byte)(10 + i))).ToArray();

    internal static void Load(TextureLoader loader, params string[] paths)
    {
        foreach (var p in paths) Assert.NotNull(loader.LoadPngAsRgba(p, 16, 16));
    }

    internal static string Source(string relative, [CallerFilePath] string here = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus", relative));

    /// <summary>The text of a member from its signature to the next member signature given.</summary>
    internal static string Between(string text, string from, string to)
    {
        int a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"missing: {from}");
        int b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"missing after {from}: {to}");
        return text[a..b];
    }
}

public class CacheMemoryPolicyTests
{
    private const long GiB = CacheMemoryFixtures.GiB, MiB = CacheMemoryFixtures.MiB;
    private const long Q = DecodeCacheMemoryPolicy.QuantumBytes;

    [Fact]
    [Trait("Plan", "AC-cache-memory-1")]
    [Trait("Plan", "EDGE-cache-memory-2")]
    public void PlentyFree_TargetIsConfigured()
        => Assert.Equal(8 * GiB, DecodeCacheMemoryPolicy.Target(8 * GiB, 40 * GiB, 0, 0, 0, 0));

    [Fact]
    [Trait("Plan", "AC-cache-memory-1")]
    public void DefaultBudgetUnchanged()
    {
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        int expected = ram <= 0 ? 2048 : (int)Math.Clamp(ram / 8 / (1024 * 1024), 2048, 16384);
        Assert.Equal(expected, Configuration.DefaultDecodeCacheBudgetMb());
        var config = new Configuration { DecodeCacheBudgetMb = 777 };
        var back = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(config))!;
        Assert.Equal(777, back.DecodeCacheBudgetMb);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-2")]
    public void Scarce_HalfOfControllable()
        => Assert.Equal(4 * GiB + GiB / 4,
            DecodeCacheMemoryPolicy.Target(8 * GiB, GiB + GiB / 2, 6 * GiB, GiB / 2, GiB / 2, 0));

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-3")]
    public void NearZero_Floor1GiB()
        => Assert.Equal(GiB, DecodeCacheMemoryPolicy.Target(8 * GiB, 0, 0, 0, 0, 0));

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-25")]
    public void SixteenGbDefault_CanDropTo1GiB()
    {
        long target = DecodeCacheMemoryPolicy.Target(2048 * MiB, GiB / 2, GiB, 0, 0, 0);
        Assert.Equal(GiB, target);
        Assert.Equal(GiB, DecodeCacheMemoryPolicy.Next(2048 * MiB, target, 2048 * MiB, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-4")]
    public void ConfiguredBelowFloor_StaysConfigured()
    {
        Assert.Equal(512 * MiB, DecodeCacheMemoryPolicy.Target(512 * MiB, 0, 0, 0, 0, 0));
        Assert.Equal(512 * MiB, DecodeCacheMemoryPolicy.Next(512 * MiB, 0, 512 * MiB, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-1")]
    public void UnknownFree_TargetIsConfigured()
        => Assert.Equal(8 * GiB, DecodeCacheMemoryPolicy.Target(8 * GiB, null, 0, 0, 0, 0));

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-5")]
    public void EvictedCountsAsControllable()
        => Assert.Equal(DecodeCacheMemoryPolicy.Target(8 * GiB, GiB, 6 * GiB, 0, 0, 0),
                        DecodeCacheMemoryPolicy.Target(8 * GiB, GiB, 4 * GiB, 2 * GiB, 0, 0));

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-33")]
    public void AddedSinceSnapshot_OffsetsUnused()
    {
        long c = 16 * GiB, f = GiB, h = 2 * GiB, u = 3 * GiB;
        Assert.Equal(DecodeCacheMemoryPolicy.Target(c, f, h, 0, u, 0), DecodeCacheMemoryPolicy.Target(c, f, h + u, 0, u, u));
        // More added than the GC had unused: unused clamps at 0, never negative.
        Assert.Equal(DecodeCacheMemoryPolicy.Target(c, f, h, 0, 0, 0), DecodeCacheMemoryPolicy.Target(c, f, h, 0, u, 2 * u));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-6")]
    public void Hysteresis_MidRange_AtRealQuantum()
    {
        long c = 8 * GiB, cur = 4 * GiB;
        Assert.Equal(cur, DecodeCacheMemoryPolicy.Next(cur, cur - Q + 1, c, false));
        Assert.Equal(cur - Q, DecodeCacheMemoryPolicy.Next(cur, cur - Q, c, false));
        Assert.Equal(cur, DecodeCacheMemoryPolicy.Next(cur, cur + Q - 1, c, false));
        Assert.Equal(cur + Q, DecodeCacheMemoryPolicy.Next(cur, cur + Q, c, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-32")]
    public void Ends_AlwaysReachable_AtRealQuantum()
    {
        long c = 8 * GiB;
        Assert.Equal(c, DecodeCacheMemoryPolicy.Next(c - Q + 1, c, c, false));
        Assert.Equal(c, DecodeCacheMemoryPolicy.Next(c - Q + 1, 20 * GiB, c, false));
        Assert.Equal(GiB, DecodeCacheMemoryPolicy.Next(GiB + Q - 1, GiB, c, false));
        Assert.Equal(GiB, DecodeCacheMemoryPolicy.Next(GiB + Q - 1, 0, c, false));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-31")]
    public void ConfiguredChanged_SkipsHysteresis()
    {
        Assert.Equal(2148 * MiB, DecodeCacheMemoryPolicy.Next(2048 * MiB, 2148 * MiB, 2148 * MiB, true));
        // Lowered configured, target 10 MiB under the current: taken at once.
        Assert.Equal(4 * GiB - 10 * MiB, DecodeCacheMemoryPolicy.Next(4 * GiB, 4 * GiB - 10 * MiB, 6 * GiB, true));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-24")]
    public void Saturates_AndClampsNegatives()
    {
        Assert.Equal(8 * GiB, DecodeCacheMemoryPolicy.Target(8 * GiB, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, 0));
        Assert.Equal(GiB, DecodeCacheMemoryPolicy.Target(8 * GiB, -5, -5, -5, -5, -5));
        Assert.Equal(0, DecodeCacheMemoryPolicy.Target(-1, 5 * GiB, 0, 0, 0, 0));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-24")]
    public void PhysicalMemory_Saturate()
    {
        Assert.Equal(long.MaxValue, PhysicalMemory.Saturate(ulong.MaxValue));
        Assert.Equal(long.MaxValue, PhysicalMemory.Saturate((ulong)long.MaxValue + 1));
        Assert.Equal(long.MaxValue, PhysicalMemory.Saturate((ulong)long.MaxValue));
        Assert.Equal(5, PhysicalMemory.Saturate(5));
        Assert.Equal(0, PhysicalMemory.Saturate(0));
        var read = CacheMemoryFixtures.Between(CacheMemoryFixtures.Source("Services/PhysicalMemory.cs"),
            "public static PhysicalMemoryStatus? Read()", "internal static long Saturate(");
        Assert.Contains("new PhysicalMemoryStatus(Saturate(s.ullTotalPhys), Saturate(s.ullAvailPhys), Saturate(s.ullAvailPageFile))", read);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-1")]
    [Trait("Plan", "EDGE-cache-memory-34")]
    public void PhysicalMemory_LiveReadSucceeds()
    {
        // A wrong struct layout or dwLength makes the call fail, and the cap would silently never apply.
        var s = PhysicalMemory.Read();
        Assert.NotNull(s);
        Assert.True(s!.Value.TotalPhysBytes > 0);
        Assert.InRange(s.Value.AvailPhysBytes, 1, s.Value.TotalPhysBytes);
        Assert.True(s.Value.AvailCommitBytes > 0);
        Assert.Equal(Math.Min(s.Value.AvailPhysBytes, s.Value.AvailCommitBytes), s.Value.FreeBytes);
    }
}

public class CacheMemoryDecodeCapTests
{
    private const long KiB = CacheMemoryFixtures.KiB, MiB = CacheMemoryFixtures.MiB, GiB = CacheMemoryFixtures.GiB;
    private const long E = KiB;   // one 16×16 RGBA entry

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-29")]
    public void SeamsInConstructor_UsedByFirstAssignment()
    {
        var m = new CacheMemoryFixtures.FakeMachine { Free = 6 * KiB };
        var loader = new TextureLoader(Substitute.For<IDataManager>(), Substitute.For<IPluginLog>(), m.Seams(KiB, KiB))
        {
            DecodeCacheBudgetBytes = 10 * KiB,
        };
        Assert.Equal(1, m.MemoryReads);
        Assert.Equal(3 * KiB, loader.EffectiveDecodeCacheBudgetBytes);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-1")]
    [Trait("Plan", "EDGE-cache-memory-31")]
    public void FreshLoader_2200MiB_PlentyFree()
    {
        var m = new CacheMemoryFixtures.FakeMachine();
        var loader = CacheMemoryFixtures.Loader(m.Seams(DecodeCacheMemoryPolicy.FloorBytes, DecodeCacheMemoryPolicy.QuantumBytes));
        loader.DecodeCacheBudgetBytes = 2200 * MiB;
        Assert.Equal(2200 * MiB, loader.EffectiveDecodeCacheBudgetBytes);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-1")]
    [Trait("Plan", "EDGE-cache-memory-31")]
    public void ConfiguredRaisedBy100MiB_PlentyFree()
    {
        var m = new CacheMemoryFixtures.FakeMachine();
        var loader = CacheMemoryFixtures.Loader(m.Seams(DecodeCacheMemoryPolicy.FloorBytes, DecodeCacheMemoryPolicy.QuantumBytes));
        loader.DecodeCacheBudgetBytes = 2048 * MiB;
        loader.DecodeCacheBudgetBytes = 2148 * MiB;
        Assert.Equal(2148 * MiB, loader.EffectiveDecodeCacheBudgetBytes);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-1")]
    [Trait("Plan", "EDGE-cache-memory-32")]
    [Trait("Plan", "EDGE-cache-memory-7")]
    public void OneQuantumMinusOneBelow_ThenFrees_ReachesConfigured()
    {
        const long C = 8 * GiB, Q = DecodeCacheMemoryPolicy.QuantumBytes;
        var m = new CacheMemoryFixtures.FakeMachine { Free = 2 * (C - Q + 1) };
        var loader = CacheMemoryFixtures.Loader(m.Seams(DecodeCacheMemoryPolicy.FloorBytes, Q));
        loader.DecodeCacheBudgetBytes = C;
        Assert.Equal(C - Q + 1, loader.EffectiveDecodeCacheBudgetBytes);
        m.Free = 64 * GiB;
        loader.RefreshMemoryBudget();
        Assert.Equal(C, loader.EffectiveDecodeCacheBudgetBytes);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-2")]
    [Trait("Plan", "EDGE-cache-memory-34")]
    public void CommitLimited_CapsBudget()
    {
        Assert.Equal(2 * GiB, new PhysicalMemoryStatus(64 * GiB, 30 * GiB, 2 * GiB).FreeBytes);
        Assert.Equal(3 * GiB, new PhysicalMemoryStatus(64 * GiB, 3 * GiB, 9 * GiB).FreeBytes);
        var m = new CacheMemoryFixtures.FakeMachine { Phys = 30 * GiB, Commit = 2 * GiB };
        var loader = CacheMemoryFixtures.Loader(m.Seams(DecodeCacheMemoryPolicy.FloorBytes, DecodeCacheMemoryPolicy.QuantumBytes));
        loader.DecodeCacheBudgetBytes = 8 * GiB;
        Assert.Equal(2 * GiB, loader.LastFreeBytes);
        Assert.Equal(GiB, loader.EffectiveDecodeCacheBudgetBytes);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-2")]
    public void RefreshAppliesCap_AndTrims()
    {
        var dir = CacheMemoryFixtures.TempDir();
        var m = new CacheMemoryFixtures.FakeMachine { Free = GiB };
        var loader = CacheMemoryFixtures.Loader(m.Seams(KiB, 1));
        loader.DecodeCacheBudgetBytes = 10 * E;
        CacheMemoryFixtures.Load(loader, CacheMemoryFixtures.Pngs(dir, 8));
        Assert.Equal(8 * E, loader.CacheState().Bytes);
        m.Free = 0;   // (0 + 8 E held) / 2 = 4 E
        loader.RefreshMemoryBudget();
        Assert.Equal(4 * E, loader.EffectiveDecodeCacheBudgetBytes);
        Assert.True(loader.CacheState().Bytes <= 4 * E);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-2")]
    [Trait("Plan", "EDGE-cache-memory-10")]
    public void NoSeamCallsPerAccess()
    {
        var dir = CacheMemoryFixtures.TempDir();
        var m = new CacheMemoryFixtures.FakeMachine();
        var loader = CacheMemoryFixtures.Loader(m.Seams(KiB, KiB));
        loader.DecodeCacheBudgetBytes = 100 * E;
        var files = CacheMemoryFixtures.Pngs(dir, 5);
        int mem = m.MemoryReads, gc = m.GcReads;
        for (int i = 0; i < 50; i++) CacheMemoryFixtures.Load(loader, files[i % files.Length]);
        Assert.Equal(mem, m.MemoryReads);
        Assert.Equal(gc, m.GcReads);
        loader.ResetStats();
        Assert.Equal(mem + 1, m.MemoryReads);
        Assert.Equal(gc + 1, m.GcReads);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-3")]
    [Trait("Plan", "EDGE-cache-memory-9")]
    public void TightenedCap_EvictsOlderGenerationFirst()
    {
        var dir = CacheMemoryFixtures.TempDir();
        var m = new CacheMemoryFixtures.FakeMachine { Free = GiB };
        var loader = CacheMemoryFixtures.Loader(m.Seams(KiB, 1));
        loader.DecodeCacheBudgetBytes = 10 * E;
        var f = CacheMemoryFixtures.Pngs(dir, 4);
        CacheMemoryFixtures.Load(loader, f[0], f[1]);
        loader.ResetStats();
        CacheMemoryFixtures.Load(loader, f[2], f[3]);
        m.Free = 0;   // (0 + 4 E) / 2 = 2 E
        loader.RefreshMemoryBudget();
        var keys = loader.CachedKeysForTests();
        Assert.Equal(2, keys.Count);
        Assert.Contains(keys, k => k.Contains(Path.GetFileName(f[2])));
        Assert.Contains(keys, k => k.Contains(Path.GetFileName(f[3])));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-5")]
    public void NoRatchetBeforeGen2()
    {
        var dir = CacheMemoryFixtures.TempDir();
        var m = new CacheMemoryFixtures.FakeMachine { Free = GiB };
        var loader = CacheMemoryFixtures.Loader(m.Seams(KiB, 1));
        loader.DecodeCacheBudgetBytes = 10 * E;
        CacheMemoryFixtures.Load(loader, CacheMemoryFixtures.Pngs(dir, 4));
        m.Free = 0;
        loader.RefreshMemoryBudget();   // trims 4 E -> 2 E
        long cap = loader.EffectiveDecodeCacheBudgetBytes;
        int evictions = loader.Evictions;
        loader.RefreshMemoryBudget();   // identical readings, no gen2: the 2 E evicted still count
        Assert.Equal(cap, loader.EffectiveDecodeCacheBudgetBytes);
        Assert.Equal(evictions, loader.Evictions);
        m.Gen2++; m.Index++; m.Free = 2 * E;   // the gen2 freed them back to the OS
        loader.RefreshMemoryBudget();
        Assert.Equal(cap, loader.EffectiveDecodeCacheBudgetBytes);
        Assert.Equal(evictions, loader.Evictions);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-26")]
    public void Gen2WithMemoryRetained_DropsLessThanAQuantum()
    {
        var dir = CacheMemoryFixtures.TempDir();
        var m = new CacheMemoryFixtures.FakeMachine { Free = GiB };
        var loader = CacheMemoryFixtures.Loader(m.Seams(KiB, 4 * E));
        loader.DecodeCacheBudgetBytes = 10 * E;
        CacheMemoryFixtures.Load(loader, CacheMemoryFixtures.Pngs(dir, 8));
        m.Free = 2 * E;   // (2 + 8) / 2 = 5 E, a 5 E drop: one quantum or more, so it moves
        loader.RefreshMemoryBudget();
        Assert.Equal(5 * E, loader.EffectiveDecodeCacheBudgetBytes);
        int evictions = loader.Evictions;
        m.Gen2++; m.Index++; m.Committed = 3 * E; m.Heap = 0;   // the GC kept the 3 evicted entries' space
        loader.RefreshMemoryBudget();
        Assert.True(5 * E - loader.EffectiveDecodeCacheBudgetBytes < 4 * E);
        Assert.Equal(evictions, loader.Evictions);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-33")]
    public void AddingUnusedBytes_LeavesTargetUnchanged()
    {
        var dir = CacheMemoryFixtures.TempDir();
        var m = new CacheMemoryFixtures.FakeMachine { Free = 10 * E, Committed = 4 * E, Heap = 0, Index = 7 };
        var loader = CacheMemoryFixtures.Loader(m.Seams(KiB, 1));
        loader.DecodeCacheBudgetBytes = 100 * E;   // the refresh has seen index 7
        long before = loader.EffectiveDecodeCacheBudgetBytes;
        Assert.Equal(7 * E, before);   // (10 + 4 unused) / 2
        CacheMemoryFixtures.Load(loader, CacheMemoryFixtures.Pngs(dir, 4));   // 4 E into the GC's unused space
        loader.RefreshMemoryBudget();
        Assert.Equal(before, loader.EffectiveDecodeCacheBudgetBytes);
        // The next GC sees them as heap: nothing unused, and the OS's free memory never moved.
        m.Index++; m.Committed = 4 * E; m.Heap = 4 * E;
        loader.RefreshMemoryBudget();
        Assert.Equal(before, loader.EffectiveDecodeCacheBudgetBytes);
        // A later GC frees other space it keeps committed: the added count was reset at the new index, so that
        // unused space counts in full (without the reset the stale 4 E would cancel it).
        m.Index++; m.Committed = 8 * E; m.Heap = 4 * E;
        loader.RefreshMemoryBudget();
        Assert.Equal(before + 2 * E, loader.EffectiveDecodeCacheBudgetBytes);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-31")]
    [Trait("Plan", "EDGE-cache-memory-6")]
    public void FirstRefreshSkipsHysteresis_LaterRefreshesKeepIt()
    {
        var m = new CacheMemoryFixtures.FakeMachine { Free = 2 * 2098 * MiB };   // target 2098 MiB, mid-range
        var loader = CacheMemoryFixtures.Loader(m.Seams(DecodeCacheMemoryPolicy.FloorBytes, DecodeCacheMemoryPolicy.QuantumBytes));
        loader.DecodeCacheBudgetBytes = 2148 * MiB;   // from the 2048 MiB field default: a 50 MiB move, under a quantum
        Assert.Equal(2098 * MiB, loader.EffectiveDecodeCacheBudgetBytes);
        m.Free = 2 * 1998 * MiB;   // a 100 MiB drop, still mid-range: held by the hysteresis
        loader.RefreshMemoryBudget();
        Assert.Equal(2098 * MiB, loader.EffectiveDecodeCacheBudgetBytes);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-8")]
    public void SliderLower_TrimsAtOnce_SliderRaise_StillCapped()
    {
        var dir = CacheMemoryFixtures.TempDir();
        var m = new CacheMemoryFixtures.FakeMachine { Free = GiB };
        var loader = CacheMemoryFixtures.Loader(m.Seams(KiB, KiB));
        loader.DecodeCacheBudgetBytes = 10 * E;
        CacheMemoryFixtures.Load(loader, CacheMemoryFixtures.Pngs(dir, 8));
        loader.DecodeCacheBudgetBytes = 3 * E;
        Assert.Equal(3 * E, loader.EffectiveDecodeCacheBudgetBytes);
        Assert.True(loader.CacheState().Bytes <= 3 * E);
        m.Free = 0;
        loader.DecodeCacheBudgetBytes = 10 * E;   // (0 + 3 held + 5 evicted) / 2 = 4 E, not the 10 E set
        Assert.Equal(4 * E, loader.EffectiveDecodeCacheBudgetBytes);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-1")]
    public void ReaderNull_EffectiveIsConfigured()
    {
        var m = new CacheMemoryFixtures.FakeMachine { Free = 0 };
        var loader = CacheMemoryFixtures.Loader(m.Seams(KiB, KiB));
        loader.DecodeCacheBudgetBytes = 10 * E;
        Assert.Equal(KiB, loader.EffectiveDecodeCacheBudgetBytes);
        m.Unreadable = true;
        loader.RefreshMemoryBudget();
        Assert.Equal(10 * E, loader.EffectiveDecodeCacheBudgetBytes);
        Assert.Null(loader.LastFreeBytes);
    }
}

public class CacheMemoryWiringTests
{
    private const long MiB = CacheMemoryFixtures.MiB, GiB = CacheMemoryFixtures.GiB;

    [Fact]
    [Trait("Plan", "AC-cache-memory-4")]
    public void DecodeBudgetText_Formats()
    {
        Assert.Equal("2048 MB of 8192 MB set, 5120 MB free", CompositorService.DecodeBudgetText(2 * GiB, 8 * GiB, 5 * GiB));
        Assert.Equal("2048 MB of 8192 MB set, ? MB free", CompositorService.DecodeBudgetText(2 * GiB, 8 * GiB, null));
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-4")]
    public void PhasesLog_TemplateOnlyBudgetChanged()
    {
        var blend = CacheMemoryFixtures.Source("Services/CompositorService.BlendStats.cs");
        Assert.Contains("\"{52} material(s) | cache {53} entries, {54:F0} MB, {55} evicted (budget {56}) | setup steps: {64}\"", blend);
        Assert.Contains("DecodeBudgetText(textureLoader.EffectiveDecodeCacheBudgetBytes, textureLoader.DecodeCacheBudgetBytes,", blend);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-5")]
    [Trait("Plan", "EDGE-cache-memory-30")]
    public void MemoryFootprint_Format()
    {
        Assert.Equal("GC committed 3072 MB, private 5120 MB", MemoryFootprint.Format(3 * GiB, 5 * GiB));
        Assert.Equal("GC committed 3072 MB, private ? MB", MemoryFootprint.Format(3 * GiB, null));
        Assert.StartsWith("GC committed ", MemoryFootprint.Describe());
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-2")]
    public void TrimCache_UsesEffective_NoSeams()
    {
        var trim = CacheMemoryFixtures.Between(CacheMemoryFixtures.Source("Services/TextureLoader.cs"),
            "private void TrimCache()", "    /// <summary>");
        Assert.Contains("EffectiveDecodeCacheBudgetBytes", trim);
        Assert.DoesNotContain("ReadMemory", trim);
        Assert.DoesNotContain("ReadGc", trim);
        Assert.DoesNotContain("PhysicalMemory", trim);
        Assert.DoesNotContain("DecodeCacheBudgetBytes;", trim.Replace("EffectiveDecodeCacheBudgetBytes;", ""));
    }
}

public class CacheMemoryAoBlurTests
{
    private sealed class FakeClock { public long Now = 1_000_000; }

    private static (AoBlurCache Cache, FakeClock Clock) Make(long budget = 512L << 20)
    {
        var clock = new FakeClock();
        return (new AoBlurCache { BudgetBytes = budget, Clock = () => clock.Now }, clock);
    }

    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

    [Fact]
    [Trait("Plan", "AC-cache-memory-5")]
    [Trait("Plan", "EDGE-cache-memory-16")]
    public void ReleaseIfIdle_OnlyAfterIdle()
    {
        var (cache, clock) = Make();
        cache.Put("a", new byte[100]);
        long t = clock.Now;
        Assert.Equal(0, cache.ReleaseIfIdle(Idle, t + (long)TimeSpan.FromMinutes(4).TotalMilliseconds));
        Assert.True(cache.TryGetEntry("a", out _));
        clock.Now = t;   // the touch above happened at t
        Assert.Equal(1, cache.ReleaseIfIdle(Idle, t + (long)Idle.TotalMilliseconds));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-16")]
    public void TouchResetsIdle()
    {
        var (cache, clock) = Make();
        long t = clock.Now;
        cache.Put("a", new byte[100]);
        clock.Now = t + (long)TimeSpan.FromMinutes(4).TotalMilliseconds;
        Assert.True(cache.TryGetEntry("a", out _));
        clock.Now = t + (long)TimeSpan.FromMinutes(8).TotalMilliseconds;
        Assert.Equal(0, cache.ReleaseIfIdle(Idle));
        clock.Now = t + (long)TimeSpan.FromMinutes(9).TotalMilliseconds;
        Assert.Equal(1, cache.ReleaseIfIdle(Idle));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-17")]
    public void ConcurrentPutAndRelease_Consistent()
    {
        var cache = new AoBlurCache { BudgetBytes = 1000 };
        Parallel.For(0, 2000, i =>
        {
            switch (i % 3)
            {
                case 0: cache.Put("k" + (i % 37), new byte[10]); break;
                case 1: cache.TryGetEntry("k" + (i % 37), out _); break;
                default: cache.ReleaseIfIdle(TimeSpan.Zero); break;
            }
        });
        Assert.Equal(cache.Count * 10L, cache.Bytes);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-13")]
    public void NullEntry_CachedAndCharged()
    {
        var (cache, _) = Make();
        cache.Put("none", null);
        Assert.True(cache.TryGetEntry("none", out var plane));
        Assert.Null(plane);
        Assert.Equal(AoBlurCache.NullEntryCost, cache.Bytes);
        Assert.Equal(1024, AoBlurCache.NullEntryCost);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-14")]
    public void OversizeEntry_KeptAlone()
    {
        var (cache, _) = Make(budget: 10);
        cache.Put("small", new byte[4]);
        cache.Put("big", new byte[20]);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGetEntry("big", out _));
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-5")]
    public void IdleTimer_ReleasesBothCaches_WithFootprint()
    {
        var src = CacheMemoryFixtures.Source("Services/CompositorService.cs");
        var timer = CacheMemoryFixtures.Between(src, "idleCacheTimer = new Timer(", "}, null, TimeSpan.FromSeconds(30)");
        Assert.Contains("aoBlurCache.ReleaseIfIdle(DecodeCacheIdleRelease)", timer);
        Assert.Contains("_bustStandoff.ReleaseIfIdle(DecodeCacheIdleRelease)", timer);
        Assert.Contains("textureLoader.RefreshMemoryBudget()", timer);
        Assert.Contains("MemoryFootprint.Describe()", timer);
    }
}

public class CacheMemoryStandoffTests
{
    private const int Size = 256;

    private static byte[] Plane(byte v, int n = Size * Size) => Enumerable.Repeat(v, n).ToArray();

    private static byte[]? Map(byte[] coverage)
        => BodyBridge.BustStandoffMap([BustStandoffAtrTests.Chests(false)], coverage, Size, Size, 1f, Size, 0.0015f);

    [Fact]
    [Trait("Plan", "AC-cache-memory-6")]
    public void StandoffBudget_IsBoundedLru()
    {
        Assert.Equal(256L << 20, CompositorService.BustStandoffBudgetBytes);
        // The same rule at a scale a test can hold: 16 slots of 16 bytes, a 17th evicts the least recently used.
        var cache = new AoBlurCache { BudgetBytes = 16 * 16 };
        for (int i = 0; i < 16; i++) cache.Put("m" + i, new byte[16]);
        Assert.True(cache.TryGetEntry("m0", out _));   // re-touched
        cache.Put("m16", new byte[16]);
        Assert.Equal(16, cache.Count);
        Assert.True(cache.TryGetEntry("m0", out _));
        Assert.False(cache.TryGetEntry("m1", out _));
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-6")]
    public void StandoffCache_Wiring()
        => Assert.Contains("AoBlurCache _bustStandoff = new AoBlurCache { BudgetBytes = BustStandoffBudgetBytes };",
                           CacheMemoryFixtures.Source("Services/CompositorService.BlendStats.cs"));

    [Fact]
    [Trait("Plan", "AC-cache-memory-7")]
    public void CoverageChangesTheMap()
    {
        var covered = Map(Plane(255));
        Assert.NotNull(covered);
        Assert.NotEqual(covered, Map(Plane(0)));
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-7")]
    [Trait("Plan", "EDGE-cache-memory-28")]
    public void CrossingFloor_ChangesMark()
    {
        var a = Plane(7);
        var b = (byte[])a.Clone();
        b[12345] = 8;
        Assert.NotEqual(BodyBridge.BustStandoffCoverageMark(a, Size, Size), BodyBridge.BustStandoffCoverageMark(b, Size, Size));
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-7")]
    [Trait("Plan", "EDGE-cache-memory-27")]
    public void OpacityOnly_SameMark_SameMap()
    {
        var a = new byte[Size * Size];
        var b = new byte[Size * Size];
        for (int i = 0; i < a.Length; i++)
        {
            bool on = (i / Size) < Size / 2;   // top half covered
            a[i] = on ? (byte)255 : (byte)0;
            b[i] = on ? (byte)100 : (byte)7;
        }
        Assert.Equal(BodyBridge.BustStandoffCoverageMark(a, Size, Size), BodyBridge.BustStandoffCoverageMark(b, Size, Size));
        Assert.Equal(Map(a), Map(b));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-12")]
    public void SameContentNewArray_SameMark()
    {
        var a = Plane(200);
        a[5] = 0;
        Assert.Equal(BodyBridge.BustStandoffCoverageMark(a, Size, Size),
                     BodyBridge.BustStandoffCoverageMark((byte[])a.Clone(), Size, Size));
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-11")]
    public void IgnoredCoverage_Dash()
    {
        Assert.Equal("-", BodyBridge.BustStandoffCoverageMark(null, Size, Size));
        Assert.Equal("-", BodyBridge.BustStandoffCoverageMark(Plane(255), 0, Size));
        Assert.Equal("-", BodyBridge.BustStandoffCoverageMark(Plane(255, Size * Size - 1), Size, Size));
        var mark = BodyBridge.BustStandoffCoverageMark(Plane(255), Size, Size);
        Assert.Equal(16, mark.Length);
        Assert.Matches("^[0-9a-f]{16}$", mark);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-35")]
    public void Mark_IndependentOfParallelism()
    {
        const int N = 4096;
        var plane = new byte[N * N];
        new Random(1234).NextBytes(plane);
        Assert.Equal(BodyBridge.BustStandoffCoverageMark(plane, N, N, 1),
                     BodyBridge.BustStandoffCoverageMark(plane, N, N, Environment.ProcessorCount));
        var body = CacheMemoryFixtures.Between(CacheMemoryFixtures.Source("Services/BodyBridge.cs"),
            "internal static string BustStandoffCoverageMark(byte[]? coverage, int covW, int covH, int maxParallelism)",
            "    /// <summary>");
        Assert.DoesNotContain("new byte[", body);
        Assert.DoesNotContain("new bool[", body);
        Assert.Equal(1, body.Split("new ulong[").Length - 1);
        Assert.Contains("var parts = new ulong[chunks];", body);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-7")]
    public void Standoff_KeysWithCoverageMark_AfterGates()
    {
        var blend = CacheMemoryFixtures.Source("Services/CompositorService.BlendStats.cs");
        var body = CacheMemoryFixtures.Between(blend, "private byte[]? BustStandoff(",
            "private IReadOnlyList<IReadOnlySet<string>?> StandoffHiddenList(");
        int hidden = body.IndexOf("StandoffHiddenList(models, sources, settingsOf)", StringComparison.Ordinal);
        int mark = body.IndexOf("BodyBridge.BustStandoffCoverageMark(coverage, w, h)", StringComparison.Ordinal);
        Assert.True(hidden > 0 && mark > hidden, "the coverage mark is worked out past the gates and the hidden list");
        Assert.Contains("ms, ", body);   // the solveMs line stays
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-7")]
    public void CoverageMark_MirrorsMapFloor()
    {
        var bridge = CacheMemoryFixtures.Source("Services/BodyBridge.cs");
        var mark = CacheMemoryFixtures.Between(bridge,
            "internal static string BustStandoffCoverageMark(byte[]? coverage, int covW, int covH, int maxParallelism)",
            "    /// <summary>");
        Assert.Contains(">= CoverageFloor", mark);
        Assert.Contains("coverage.Length < (long)covW * covH", mark);
        Assert.Contains("covered[i] = coverage[y * covW + x] >= CoverageFloor;", bridge);
        Assert.Contains("coverage != null && covW > 0 && covH > 0 && coverage.Length >= covW * covH", bridge);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-23")]
    public void SameInputs_OneKey()
    {
        // The diffuse and normal passes at one size: distinct strap arrays with equal content give one key.
        var strapD = Plane(255);
        strapD[77] = 3;
        var strapN = (byte[])strapD.Clone();
        IReadOnlySet<string>? none = null;
        string K(byte[] strap) => CompositorService.BustStandoffKey("Mod", Size, 3, 1f, ["body|1"],
            BodyBridge.BustStandoffCoverageMark(strap, Size, Size), [none]);
        Assert.Equal(K(strapD), K(strapN));
    }
}

public class CacheMemoryRemapTests
{
    [Fact]
    [Trait("Plan", "AC-cache-memory-8")]
    public void LastLeaveClears()
    {
        var memo = new BuildScopedMemo<string, byte[]?>();
        using (memo.Enter())
        {
            memo["a"] = [1];
            memo["b"] = null;
            Assert.Equal(2, memo.Count);
        }
        Assert.Equal(0, memo.Count);
        Assert.Equal(0, memo.InFlight);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-18")]
    public void OverlapKeepsMemoUntilLastLeave()
    {
        var memo = new BuildScopedMemo<string, byte[]?>();
        var first = memo.Enter();
        var second = memo.Enter();
        memo["a"] = [1];
        memo["b"] = [2];
        first.Dispose();
        Assert.Equal(2, memo.Count);
        second.Dispose();
        Assert.Equal(0, memo.Count);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-19")]
    public void ThrowingScopeStillClears()
    {
        var memo = new BuildScopedMemo<string, byte[]?>();
        void Build()
        {
            using (memo.Enter())
            {
                memo["a"] = [1];
                throw new InvalidOperationException();
            }
        }
        Assert.Throws<InvalidOperationException>(Build);
        Assert.Equal(0, memo.Count);
        Assert.Equal(0, memo.InFlight);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-20")]
    public void DoubleDispose_CountedOnce()
    {
        var memo = new BuildScopedMemo<string, byte[]?>();
        var open = memo.Enter();
        var twice = memo.Enter();
        memo["a"] = [1];
        twice.Dispose();
        twice.Dispose();
        Assert.Equal(1, memo.InFlight);
        Assert.Equal(1, memo.Count);
        open.Dispose();
        Assert.Equal(0, memo.InFlight);
    }

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-21")]
    public void HandedOutBufferSurvivesClear()
    {
        var memo = new BuildScopedMemo<string, byte[]?>();
        byte[]? held;
        using (memo.Enter())
        {
            memo["a"] = [1, 2, 3];
            Assert.True(memo.TryGetValue("a", out held));
        }
        Assert.Equal(0, memo.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, held);
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-8")]
    [Trait("Plan", "EDGE-cache-memory-19")]
    public void Build_WrapsRunInScope()
    {
        var src = CacheMemoryFixtures.Source("Services/SecondSkinService.cs");
        Assert.Contains("private readonly BuildScopedMemo<(string Path, string? Src, string? Dst, int W, int H, ResampleFilter F), byte[]?> remapCache", src);
        Assert.Contains("RemapPath/LoadRemapped must only be called inside Build's scope", src);
        var build = CacheMemoryFixtures.Between(src, "internal Result? Build(", "internal sealed record ShellWriteFailures(");
        int scope = build.IndexOf("using (remapCache.Enter())", StringComparison.Ordinal);
        int run = build.IndexOf("try { return build.Run(); }", StringComparison.Ordinal);
        int fin = build.IndexOf("finally { writes = build.WriteFailures(); }", StringComparison.Ordinal);
        Assert.True(scope > 0 && run > scope && fin > run, "the scope encloses the run and its failure hand-back");
    }

    [Fact]
    [Trait("Plan", "AC-cache-memory-8")]
    public void RemapCallersAreInsideBuild()
    {
        foreach (var file in Directory.EnumerateFiles(ProteusDir(), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            bool shellFile = name == "SecondSkinService.cs" || name.StartsWith("SecondSkinService.Shell", StringComparison.Ordinal);
            if (!shellFile)
                Assert.DoesNotContain("RemapPath(", File.ReadAllText(file));
        }
        // The service's LoadRemapped is private, so nothing outside the service reaches it.
        Assert.Contains("private byte[]? LoadRemapped(string? rel, string sidecarRoot,",
                        CacheMemoryFixtures.Source("Services/SecondSkinService.cs"));
    }

    private static string ProteusDir([CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "..", "Proteus");

    [Fact]
    [Trait("Plan", "EDGE-cache-memory-22")]
    public void StartOfBuildClearKept()
        => Assert.Contains("service.remapCache.Clear();", CacheMemoryFixtures.Source("Services/SecondSkinService.ShellSetBuild.cs"));
}
