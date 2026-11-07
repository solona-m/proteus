using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The discovery cache: which mod folders have a sidecar, and each sidecar's text. Driven through injected stat, read
/// and clock delegates over an in-memory "drive", so every stat and read is counted.
/// </summary>
public class SidecarCacheTests
{
    private const string Root = @"C:\mods";

    /// <summary>A fake drive: path → (stamp, text), with counters.</summary>
    private sealed class Drive
    {
        public readonly ConcurrentDictionary<string, (SidecarStamp Stamp, string Text)> Files =
            new(StringComparer.OrdinalIgnoreCase);
        public int Stats, Reads;
        public Func<string, Exception?>? StatThrows, ReadThrows;
        public Action<string>? DuringStat, DuringRead;

        public SidecarStamp? Stat(string path)
        {
            System.Threading.Interlocked.Increment(ref Stats);
            DuringStat?.Invoke(path);
            if (StatThrows?.Invoke(path) is { } ex) throw ex;
            return Files.TryGetValue(path, out var f) ? f.Stamp : null;
        }

        public string Read(string path)
        {
            System.Threading.Interlocked.Increment(ref Reads);
            DuringRead?.Invoke(path);
            if (ReadThrows?.Invoke(path) is { } ex) throw ex;
            return Files.TryGetValue(path, out var f) ? f.Text : throw new FileNotFoundException(path);
        }

        public void Put(string modDir, string text, long ticks = 1000, long? length = null)
            => Files[SidecarCache.MetaPath(Root, modDir)] = (new SidecarStamp(ticks, length ?? text.Length), text);
    }

    private long now = 1_000_000;

    private SidecarCache Make(Drive d) => new(d.Stat, d.Read, () => now);

    private static string Meta(string modDir) => SidecarCache.MetaPath(Root, modDir);

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-3")]
    public void RepeatSweep_WithinTtl_StatsNoNonProteusMod()
    {
        // [AC-idle-disk-io-3]
        var d = new Drive();
        d.Put("mod0007", "{}");
        d.Put("mod0420", "{}");
        var cache = Make(d);
        var mods = Enumerable.Range(0, 1000).Select(i => $"mod{i:D4}").ToList();

        var first = mods.Where(m => cache.MayHaveSidecar(Root, m)).ToList();
        Assert.Equal(["mod0007", "mod0420"], first);
        Assert.Equal(1000, d.Stats);

        d.Stats = 0;
        now += 10_000;
        var second = mods.Where(m => cache.MayHaveSidecar(Root, m)).ToList();
        Assert.Equal(first, second);
        Assert.Equal(0, d.Stats);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-3")]
    [Trait("Plan", "EDGE-idle-disk-io-7")]
    public void FullSweep_AtMostOncePer30s()
    {
        // [AC-idle-disk-io-3] [EDGE-idle-disk-io-7]
        var d = new Drive();
        var cache = Make(d);
        var mods = Enumerable.Range(0, 50).Select(i => $"m{i}").ToList();
        foreach (var m in mods) cache.MayHaveSidecar(Root, m);
        d.Stats = 0;

        now += 29_000;
        foreach (var m in mods) cache.MayHaveSidecar(Root, m);
        Assert.Equal(0, d.Stats);

        // A sidecar that appeared with no event is seen once the negative entry expires.
        d.Put("m3", "{}");
        now += 2_000;
        Assert.True(cache.MayHaveSidecar(Root, "m3"));
        foreach (var m in mods) cache.MayHaveSidecar(Root, m);
        Assert.Equal(mods.Count, d.Stats);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-3")]
    public void ReadMetadata_UnchangedTrustedStamp_NoRead()
    {
        // [AC-idle-disk-io-3]
        var d = new Drive();
        d.Put("a", "{\"x\":1}");
        var cache = Make(d);

        Assert.Equal("{\"x\":1}", cache.ReadMetadata("a", Meta("a")).Text);
        now += 3_001;
        d.Stats = d.Reads = 0;
        var again = cache.ReadMetadata("a", Meta("a"));
        Assert.Equal(MetaReadKind.Ok, again.Kind);
        Assert.Equal("{\"x\":1}", again.Text);
        Assert.Equal(1, d.Stats);
        Assert.Equal(0, d.Reads);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-4")]
    public void ReadMetadata_ChangedLengthOrMtime_ReReads()
    {
        // [AC-idle-disk-io-4]
        var d = new Drive();
        d.Put("a", "one", ticks: 10);
        var cache = Make(d);
        cache.ReadMetadata("a", Meta("a"));
        now += 5_000;

        d.Put("a", "two", ticks: 20);                 // same length, new mtime
        Assert.Equal("two", cache.ReadMetadata("a", Meta("a")).Text);
        now += 5_000;
        d.Put("a", "three!", ticks: 20);              // same mtime, new length
        Assert.Equal("three!", cache.ReadMetadata("a", Meta("a")).Text);
    }

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-4")]
    public void Forget_MakesNewSidecarVisible()
    {
        // [AC-idle-disk-io-4]
        var d = new Drive();
        var cache = Make(d);
        Assert.False(cache.MayHaveSidecar(Root, "new"));

        d.Put("new", "{}");
        Assert.False(cache.MayHaveSidecar(Root, "new"));   // still within the TTL
        cache.Forget("new");
        Assert.True(cache.MayHaveSidecar(Root, "new"));

        // ForgetAll does the same for every folder.
        Assert.False(cache.MayHaveSidecar(Root, "other"));
        d.Put("other", "{}");
        cache.ForgetAll();
        Assert.True(cache.MayHaveSidecar(Root, "other"));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-8")]
    public void ForgetDuringStatOrRead_NotStored()
    {
        // [EDGE-idle-disk-io-8]
        var d = new Drive();
        var cache = Make(d);

        // A stat that saw no sidecar, overtaken by a Forget (the mod was just added): the "absent" is not kept.
        d.DuringStat = _ => { d.Put("m", "{}"); d.DuringStat = null; cache.Forget("m"); };
        cache.MayHaveSidecar(Root, "m");
        d.Stats = 0;
        Assert.True(cache.MayHaveSidecar(Root, "m"));
        Assert.Equal(1, d.Stats);

        // A read overtaken by a Forget: its text is not cached, so the next call reads again.
        now += 10_000;
        d.DuringRead = _ => { d.DuringRead = null; cache.Forget("m"); };
        cache.ReadMetadata("m", Meta("m"));
        now += 10_000;
        d.Reads = 0;
        cache.ReadMetadata("m", Meta("m"));
        Assert.Equal(1, d.Reads);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-9")]
    public void ModsRootChange_Clears()
    {
        // [EDGE-idle-disk-io-9]
        var d = new Drive();
        var cache = Make(d);
        Assert.False(cache.MayHaveSidecar(Root, "m"));
        d.Files[SidecarCache.MetaPath(@"D:\other", "m")] = (new SidecarStamp(1, 2), "{}");
        d.Stats = 0;
        Assert.True(cache.MayHaveSidecar(@"D:\other", "m"));
        Assert.Equal(1, d.Stats);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-10")]
    public void PresentThenMissing_BecomesUnknown_ReStatNextCall()
    {
        // [EDGE-idle-disk-io-10]
        var d = new Drive();
        d.Put("m", "{}");
        var cache = Make(d);
        Assert.True(cache.MayHaveSidecar(Root, "m"));
        cache.ReadMetadata("m", Meta("m"));

        d.Files.Clear();
        Assert.Equal(MetaReadKind.Missing, cache.ReadMetadata("m", Meta("m")).Kind);

        // Back a moment later (an atomic replace): seen on the very next call, inside the 30 s TTL.
        d.Put("m", "{}");
        d.Stats = 0;
        Assert.True(cache.MayHaveSidecar(Root, "m"));
        Assert.Equal(1, d.Stats);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-11")]
    [Trait("Plan", "EDGE-idle-disk-io-18")]
    public void NewStamp_ReReadUntilSeenFor3s()
    {
        // [EDGE-idle-disk-io-11] [EDGE-idle-disk-io-18]
        var d = new Drive();
        d.Put("m", "v1", ticks: 50);
        var cache = Make(d);

        cache.ReadMetadata("m", Meta("m"));
        now += 1_000;
        // A same-stamp rewrite inside the window (a coarse-timestamp filesystem) is still picked up.
        d.Put("m", "v2", ticks: 50);
        Assert.Equal("v2", cache.ReadMetadata("m", Meta("m")).Text);
        now += 1_500;
        cache.ReadMetadata("m", Meta("m"));
        Assert.Equal(3, d.Reads);

        // 3 s after the first read under this stamp: trusted.
        now += 600;
        d.Reads = 0;
        Assert.Equal("v2", cache.ReadMetadata("m", Meta("m")).Text);
        Assert.Equal(0, d.Reads);

        // A stamp change restarts the window.
        d.Put("m", "v3", ticks: 60);
        cache.ReadMetadata("m", Meta("m"));
        now += 1_000;
        cache.ReadMetadata("m", Meta("m"));
        Assert.Equal(2, d.Reads);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-12")]
    public void BomFile_TextParsesAsBefore()
    {
        // [EDGE-idle-disk-io-12]
        var dir = Path.Combine(Path.GetTempPath(), "proteus-cache-bom-" + Guid.NewGuid().ToString("N"));
        try
        {
            var meta = Path.Combine(dir, "mod", SidecarDiscoveryService.SidecarSubdir, SidecarDiscoveryService.MetadataFile);
            Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
            File.WriteAllText(meta, "{\"Version\":1}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var read = new SidecarCache().ReadMetadata("mod", meta);
            Assert.Equal(MetaReadKind.Ok, read.Kind);
            var parsed = JsonSerializer.Deserialize<ProteusMetadata>(read.Text!, ProteusJson.MetadataRead);
            Assert.NotNull(parsed);
            Assert.Equal(File.ReadAllText(meta), read.Text);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-4")]
    [Trait("Plan", "EDGE-idle-disk-io-15")]
    public void IoError_NeverCached()
    {
        // [EDGE-idle-disk-io-4] [EDGE-idle-disk-io-15]
        var d = new Drive();
        d.Put("m", "{}");
        var cache = Make(d);

        d.ReadThrows = _ => new IOException("locked");
        Assert.Equal(MetaReadKind.IoError, cache.ReadMetadata("m", Meta("m")).Kind);
        now += 10_000;
        d.ReadThrows = null;
        d.Reads = 0;
        Assert.Equal(MetaReadKind.Ok, cache.ReadMetadata("m", Meta("m")).Kind);
        Assert.Equal(1, d.Reads);

        // A failing stat is no answer either: it says "maybe" and stores nothing.
        d.StatThrows = _ => new UnauthorizedAccessException();
        Assert.True(cache.MayHaveSidecar(Root, "x"));
        Assert.Equal(MetaReadKind.IoError, cache.ReadMetadata("x", Meta("x")).Kind);
        d.StatThrows = null;
        Assert.False(cache.MayHaveSidecar(Root, "x"));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-15")]
    public void NoteUnparsable_CachedUnderStamp_NotReRead_UntilStampChanges()
    {
        // [EDGE-idle-disk-io-15]
        var d = new Drive();
        d.Put("m", "{not json", ticks: 5);
        var cache = Make(d);
        var first = cache.ReadMetadata("m", Meta("m"));
        cache.NoteUnparsable(Meta("m"), first.Stamp);

        now += 5_000;
        d.Reads = 0;
        Assert.Equal(MetaReadKind.Unparsable, cache.ReadMetadata("m", Meta("m")).Kind);
        Assert.Equal(0, d.Reads);

        d.Put("m", "{}", ticks: 6);
        Assert.Equal(MetaReadKind.Ok, cache.ReadMetadata("m", Meta("m")).Kind);
        Assert.Equal(1, d.Reads);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-32")]
    public void FutureMtime_TrustedAfter3sUnchanged()
    {
        // [EDGE-idle-disk-io-32]
        var d = new Drive();
        // An mtime far ahead of any clock: trust comes from how long the stamp stays put, not from the mtime.
        d.Put("m", "{}", ticks: DateTime.MaxValue.Ticks);
        var cache = Make(d);
        cache.ReadMetadata("m", Meta("m"));
        now += 2_000;
        cache.ReadMetadata("m", Meta("m"));
        Assert.Equal(2, d.Reads);
        now += 1_500;
        d.Reads = 0;
        cache.ReadMetadata("m", Meta("m"));
        Assert.Equal(0, d.Reads);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-15")]
    public void UntrustedUnparsable_IsReRead()
    {
        // [EDGE-idle-disk-io-15]
        var d = new Drive();
        d.Put("m", "{half", ticks: 9, length: 99);
        var cache = Make(d);
        var first = cache.ReadMetadata("m", Meta("m"));
        cache.NoteUnparsable(Meta("m"), first.Stamp);

        // Within the window the writer may still be writing: read again, and the finished text is served.
        now += 1_000;
        d.Put("m", "{\"done\":1}", ticks: 9, length: 99);
        var again = cache.ReadMetadata("m", Meta("m"));
        Assert.Equal(MetaReadKind.Ok, again.Kind);
        Assert.Equal("{\"done\":1}", again.Text);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-16")]
    public void ParallelLookupsAndForgets_Consistent()
    {
        // [EDGE-idle-disk-io-16]
        var d = new Drive();
        var mods = Enumerable.Range(0, 200).Select(i => $"m{i}").ToList();
        foreach (var m in mods.Where((_, i) => i % 7 == 0)) d.Put(m, "{}");
        var cache = new SidecarCache(d.Stat, d.Read, () => Environment.TickCount64);

        Parallel.For(0, 4000, i =>
        {
            var m = mods[i % mods.Count];
            switch (i % 5)
            {
                case 0: cache.Forget(m); break;
                case 1: cache.ReadMetadata(m, Meta(m)); break;
                default: cache.MayHaveSidecar(Root, m); break;
            }
        });

        cache.ForgetAll();
        foreach (var (m, i) in mods.Select((m, i) => (m, i)))
            Assert.Equal(i % 7 == 0, cache.MayHaveSidecar(Root, m));
    }
}
