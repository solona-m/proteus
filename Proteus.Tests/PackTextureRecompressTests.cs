using System;
using System.IO;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// A content pack's own textures are republished into the managed mod. Art the author left UNCOMPRESSED is what a
/// viewer's sync client re-encodes on their machine, unchecked, so Proteus re-encodes it here instead — but only
/// that art, and only when it can tell the format apart. These pin the two decisions that gate it.
/// </summary>
public class PackTextureRecompressTests
{
    private static TextureLoader Loader() => new(null!, new SilentLog());

    /// <summary>A .tex written by our own writer, in whichever encoding, to probe back.</summary>
    private static string WriteTex(string dir, string name, TexEncoding encoding, int size = 64)
    {
        // Not a solid colour: WriteTex collapses those to an uncompressed 16x16 and the encoding would not stick.
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(i % 251); rgba[i + 1] = (byte)(i % 97);
            rgba[i + 2] = (byte)(i % 43); rgba[i + 3] = 255;
        }

        var path = Path.Combine(dir, name);
        Assert.True(Loader().WriteTex(rgba, size, size, path, encoding));
        return path;
    }

    /// <summary>
    /// Only the uncompressed layouts count as re-compressible: those are exactly the formats a sync client's
    /// "compress uncompressed textures" pass accepts. Anything already block-compressed it skips, so must we.
    /// </summary>
    [Fact]
    public void OnlyUncompressedTexturesAreRecompressible()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var raw = WriteTex(dir, "raw.tex", TexEncoding.Uncompressed);
            var bc7 = WriteTex(dir, "bc7.tex", TexEncoding.Bc7);
            var bc5 = WriteTex(dir, "bc5.tex", TexEncoding.Bc5);

            Assert.Equal(0x1450u, TextureLoader.TexFormatOf(raw));
            Assert.Equal(0x6432u, TextureLoader.TexFormatOf(bc7));
            Assert.Equal(0x6230u, TextureLoader.TexFormatOf(bc5));

            Assert.True(TextureLoader.IsSyncRecompressible(raw));
            Assert.False(TextureLoader.IsSyncRecompressible(bc7));
            Assert.False(TextureLoader.IsSyncRecompressible(bc5));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A file that is not a .tex at all must not be mistaken for one worth re-encoding.</summary>
    [Fact]
    public void UnreadableFileIsNotRecompressible()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var stub = Path.Combine(dir, "short.tex");
            File.WriteAllBytes(stub, [1, 2, 3]);
            Assert.Null(TextureLoader.TexFormatOf(stub));
            Assert.False(TextureLoader.IsSyncRecompressible(stub));
            Assert.False(TextureLoader.IsSyncRecompressible(Path.Combine(dir, "absent.tex")));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Index art must be recognised by the same tokens a sync client uses, or one of them would compress as an
    /// index something we had just taken an unverified BC7 to.
    /// </summary>
    [Theory]
    [InlineData("chara/equipment/e6255/texture/v01_c0201e6255_top_id.tex")]
    [InlineData("chara/equipment/e6255/texture/thing_id_01.tex")]
    [InlineData("chara/equipment/e6255/texture/thing_idx.tex")]
    [InlineData("chara/equipment/e6255/texture/thing_index.tex")]
    [InlineData("chara/equipment/e6255/texture/index_map.tex")]
    [InlineData(@"E:\mods\pack\textures\SOMETHING_ID.TEX")]
    public void IndexPathsAreRecognised(string path)
        => Assert.True(SecondSkinService.IsIndexTexturePath(path));

    [Theory]
    [InlineData("chara/equipment/e6255/texture/v01_c0201e6255_top_n.tex")]
    [InlineData("chara/equipment/e6255/texture/v01_c0201e6255_top_m.tex")]
    [InlineData("chara/equipment/e6255/texture/diffuse.tex")]
    [InlineData("chara/equipment/e6255/texture/identity.tex")]   // "id" only as part of a word
    [InlineData("")]
    public void NonIndexPathsAreNot(string path)
        => Assert.False(SecondSkinService.IsIndexTexturePath(path));

    /// <summary>
    /// The cache writes a compressed copy INSIDE the pack's sidecar and leaves the author's file untouched — that is
    /// the whole bargain. A second call reuses the copy rather than encoding again.
    /// </summary>
    [Fact]
    public void CacheWritesIntoTheSidecarAndLeavesTheOriginal()
    {
        var modRoot = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var texDir = Path.Combine(modRoot, "textures");
            Directory.CreateDirectory(texDir);
            var src = WriteTex(texDir, "pack_d.tex", TexEncoding.Uncompressed, 128);
            var before = File.ReadAllBytes(src);

            var loader = Loader();
            Assert.True(PackTextureCompressionCache.TryEnsure(modRoot, src, false, loader, null, out var copy));

            // Inside the sidecar, block-compressed, and smaller than what the author shipped.
            Assert.StartsWith(Path.Combine(modRoot, "Proteus", "compressed"), copy, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0x6432u, TextureLoader.TexFormatOf(copy));
            Assert.True(new FileInfo(copy).Length < new FileInfo(src).Length);

            // The author's file is byte-for-byte what it was.
            Assert.Equal(before, File.ReadAllBytes(src));

            // Second call is a hit: same path, and it did not rewrite the file.
            var stamp = File.GetLastWriteTimeUtc(copy);
            Assert.True(PackTextureCompressionCache.TryEnsure(modRoot, src, false, loader, null, out var again));
            Assert.Equal(copy, again);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(copy));
        }
        finally { Directory.Delete(modRoot, true); }
    }

    /// <summary>Changed art gets a new copy, and the stale one is pruned rather than left to rot at full size.</summary>
    [Fact]
    public void ChangedSourceReplacesTheCopy()
    {
        var modRoot = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var texDir = Path.Combine(modRoot, "textures");
            Directory.CreateDirectory(texDir);
            var src = WriteTex(texDir, "pack_d.tex", TexEncoding.Uncompressed, 128);

            var loader = Loader();
            Assert.True(PackTextureCompressionCache.TryEnsure(modRoot, src, false, loader, null, out var first));

            // Rewrite the source with different content, so its stamp moves.
            File.Delete(src);
            WriteTex(texDir, "pack_d.tex", TexEncoding.Uncompressed, 64);

            Assert.True(PackTextureCompressionCache.TryEnsure(modRoot, src, false, loader, null, out var second));
            Assert.NotEqual(first, second);
            Assert.False(File.Exists(first));
            Assert.Single(Directory.GetFiles(Path.Combine(modRoot, "Proteus", "compressed")));
        }
        finally { Directory.Delete(modRoot, true); }
    }

    /// <summary>
    /// Two pack textures sharing a file name in different folders — which packs do constantly, one folder per option
    /// — must not evict each other. A prefix built from the leaf alone made each build prune the other's copy, so
    /// both were re-encoded on every composite for ever.
    /// </summary>
    [Fact]
    public void SameFileNameInTwoFoldersKeepsBothCopies()
    {
        var modRoot = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var a = Path.Combine(modRoot, "optionA");
            var b = Path.Combine(modRoot, "optionB");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            var srcA = WriteTex(a, "shared_d.tex", TexEncoding.Uncompressed, 64);
            var srcB = WriteTex(b, "shared_d.tex", TexEncoding.Uncompressed, 128);

            var loader = Loader();
            Assert.True(PackTextureCompressionCache.TryEnsure(modRoot, srcA, false, loader, null, out var copyA));
            Assert.True(PackTextureCompressionCache.TryEnsure(modRoot, srcB, false, loader, null, out var copyB));

            Assert.NotEqual(copyA, copyB);
            Assert.True(File.Exists(copyA), "option A's copy was pruned by option B");
            Assert.True(File.Exists(copyB));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(modRoot, "Proteus", "compressed")).Length);

            // And asking again for A is still a hit rather than a rebuild.
            var stamp = File.GetLastWriteTimeUtc(copyA);
            Assert.True(PackTextureCompressionCache.TryEnsure(modRoot, srcA, false, loader, null, out var again));
            Assert.Equal(copyA, again);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(copyA));
        }
        finally { Directory.Delete(modRoot, true); }
    }

    /// <summary>
    /// A solid colour is collapsed to an uncompressed 16x16 by the writer, so the "compressed copy" would still be
    /// something a sync client re-encodes. Claiming success there would publish a file that buys nothing, so the
    /// cache refuses and the author's own file is republished instead.
    /// </summary>
    [Fact]
    public void OutputThatCameBackUncompressedIsRefused()
    {
        var modRoot = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var texDir = Path.Combine(modRoot, "textures");
            Directory.CreateDirectory(texDir);

            // Solid colour: WriteTex shrinks it to an uncompressed 16x16 whatever encoding is asked for.
            var rgba = new byte[64 * 64 * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = 10; rgba[i + 1] = 20; rgba[i + 2] = 30; rgba[i + 3] = 255;
            }

            var src = Path.Combine(texDir, "solid_d.tex");
            Assert.True(Loader().WriteTex(rgba, 64, 64, src, TexEncoding.Uncompressed));

            Assert.False(PackTextureCompressionCache.TryEnsure(modRoot, src, false, Loader(), null, out var copy));
            Assert.Equal(string.Empty, copy);

            // Nothing useless left behind either.
            var cacheDir = Path.Combine(modRoot, "Proteus", "compressed");
            Assert.True(!Directory.Exists(cacheDir) || Directory.GetFiles(cacheDir).Length == 0);
        }
        finally { Directory.Delete(modRoot, true); }
    }

    /// <summary>With no sidecar root there is nowhere to keep a copy, and the caller falls back to the bytes.</summary>
    [Fact]
    public void NoModRootMeansNoCopy()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var src = WriteTex(dir, "pack_d.tex", TexEncoding.Uncompressed);
            Assert.False(PackTextureCompressionCache.TryEnsure(null, src, false, Loader(), null, out var path));
            Assert.Equal(string.Empty, path);
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class SilentLog : Dalamud.Plugin.Services.IPluginLog
    {
        public Serilog.Events.LogEventLevel MinimumLogLevel { get; set; }
        public Serilog.ILogger Logger => Serilog.Core.Logger.None;
        public void Debug(string m, params object[] v) { }
        public void Debug(Exception? e, string m, params object[] v) { }
        public void Error(string m, params object[] v) { }
        public void Error(Exception? e, string m, params object[] v) { }
        public void Fatal(string m, params object[] v) { }
        public void Fatal(Exception? e, string m, params object[] v) { }
        public void Info(string m, params object[] v) { }
        public void Info(Exception? e, string m, params object[] v) { }
        public void Information(string m, params object[] v) { }
        public void Information(Exception? e, string m, params object[] v) { }
        public void Verbose(string m, params object[] v) { }
        public void Verbose(Exception? e, string m, params object[] v) { }
        public void Warning(string m, params object[] v) { }
        public void Warning(Exception? e, string m, params object[] v) { }
        public void Write(Serilog.Events.LogEventLevel l, Exception? e, string m, params object[] v) { }
    }
}
