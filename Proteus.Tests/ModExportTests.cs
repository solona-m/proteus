using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Proteus;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Offline coverage for the .pmp writer: what goes into the archive, where it lands inside it, and what
/// is left on disk afterwards. No Penumbra — <see cref="ModExportService.WritePmp"/> is the pure half.
/// </summary>
public class ModExportTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "proteus_export_" + System.IO.Path.GetRandomFileName());
        public TempDir() => Directory.CreateDirectory(Path);
        public string File(string rel) => System.IO.Path.Combine(Path, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void Write(string rel, string content)
        {
            var p = File(rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            System.IO.File.WriteAllText(p, content);
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    /// <summary>A mod folder in the shape "For Creators.md" describes a .pmp as carrying.</summary>
    private static void WriteModFolder(TempDir tmp)
    {
        tmp.Write("meta.json", """{"FileVersion":3,"Name":"Ven","Author":"Almaden"}""");
        tmp.Write("default_mod.json", """{"Files":{},"Swaps":{},"Manipulations":[]}""");
        tmp.Write("group_001_body uv.json", """{"Name":"Body UV","Type":"Single","Options":[]}""");
        tmp.Write("Proteus/metadata.json", """{"FormatVersion":1,"Name":"Ven"}""");
        tmp.Write("Proteus/overlays/bibo_diffuse_0.png", "not really a png");
    }

    private static string[] EntriesOf(string pmp)
    {
        using var zip = ZipFile.OpenRead(pmp);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void WritePmp_puts_the_mod_root_at_the_archive_root()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        WriteModFolder(src);

        var pmp = dst.File("Ven.pmp");
        Assert.Equal(5, ModExportService.WritePmp(src.Path, pmp));

        // Penumbra looks for meta.json at the TOP of the archive — a wrapper folder named after the mod
        // would make the pack unimportable. Separators are forward slashes on every platform.
        Assert.Equal(
        [
            "Proteus/metadata.json",
            "Proteus/overlays/bibo_diffuse_0.png",
            "default_mod.json",
            "group_001_body uv.json",
            "meta.json",
        ], EntriesOf(pmp));
    }

    [Fact]
    public void WritePmp_keeps_nested_sidecar_folders_and_the_defaults_snapshot()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        WriteModFolder(src);
        // The Reset-to-defaults baseline: the recipient should get the AUTHOR's originals, not nothing.
        src.Write("Proteus/metadata.default.json", "{}");
        src.Write("Proteus/Masks/Straps.png", "mask");
        src.Write("Proteus/Effects/Swirl.png", "effect");

        var pmp = dst.File("Ven.pmp");
        ModExportService.WritePmp(src.Path, pmp);

        var entries = EntriesOf(pmp);
        Assert.Contains("Proteus/metadata.default.json", entries);
        Assert.Contains("Proteus/Masks/Straps.png", entries);
        Assert.Contains("Proteus/Effects/Swirl.png", entries);
    }

    [Fact]
    public void WritePmp_excludes_temp_files_and_os_noise()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        WriteModFolder(src);
        // PenumbraModMeta.AtomicWrite leaves these behind when a write is interrupted — shipping one would
        // put a half-written manifest in someone else's mod folder.
        src.Write("meta.json.deadbeef.tmp", "half a manifest");
        src.Write("desktop.ini", "[.ShellClassInfo]");
        src.Write("Proteus/Thumbs.db", "junk");

        var pmp = dst.File("Ven.pmp");
        Assert.Equal(5, ModExportService.WritePmp(src.Path, pmp));

        var entries = EntriesOf(pmp);
        Assert.DoesNotContain(entries, e => e.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("desktop.ini", entries);
        Assert.DoesNotContain("Proteus/Thumbs.db", entries);
    }

    [Fact]
    public void WritePmp_replaces_an_existing_pack_and_leaves_no_temp_file()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        WriteModFolder(src);

        var pmp = dst.File("Ven.pmp");
        File.WriteAllText(pmp, "an older, unrelated file");

        ModExportService.WritePmp(src.Path, pmp);

        Assert.Contains("meta.json", EntriesOf(pmp));   // a real archive now, not the old bytes
        Assert.Empty(Directory.EnumerateFiles(dst.Path, "*.tmp"));
    }

    [Fact]
    public void WritePmp_on_an_empty_folder_reports_nothing_and_writes_nothing()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        // Only excluded files — the same "nothing to ship" case as a genuinely empty folder.
        src.Write("meta.json.abc.tmp", "x");

        var pmp = dst.File("Ven.pmp");
        Assert.Equal(0, ModExportService.WritePmp(src.Path, pmp));
        Assert.False(File.Exists(pmp));
        Assert.Empty(Directory.EnumerateFiles(dst.Path, "*.tmp"));
    }

    [Fact]
    public void WritePmp_leaves_an_existing_pack_intact_when_the_source_is_gone()
    {
        using var dst = new TempDir();
        var pmp = dst.File("Ven.pmp");
        File.WriteAllText(pmp, "the previous export");

        Assert.ThrowsAny<Exception>(() =>
            ModExportService.WritePmp(Path.Combine(dst.Path, "no_such_mod"), pmp));

        // The failure must not have truncated what was already there, nor left a temp behind.
        Assert.Equal("the previous export", File.ReadAllText(pmp));
        Assert.Empty(Directory.EnumerateFiles(dst.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("Ven", "Ven Mod", "Ven Mod.pmp")]
    [InlineData("Ven", "Ven's / Tattoo!", "Vens Tattoo.pmp")]
    [InlineData("Ven Dir", "★", "Ven Dir.pmp")]          // name sanitises away → fall back to the directory
    public void SuggestedFileName_sanitises_and_falls_back(string dir, string name, string expected)
    {
        var entry = new OverlayEntry(dir, name, 0, true, new ProteusMetadata(), "");
        Assert.Equal(expected, ModExportService.SuggestedFileName(entry));
    }
}
