using System;
using System.IO;
using System.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The one containment check every path from a mod's own records and redirects passes through. Pure: none of these
/// touch the disk, the hostile forms least of all.
/// </summary>
public class ModPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "proteus_modpath", "mods", "Bob");

    // [AC-mod-path-containment-6][EDGE-mod-path-containment-3][EDGE-mod-path-containment-4][EDGE-mod-path-containment-5]
    // [EDGE-mod-path-containment-6][EDGE-mod-path-containment-7][EDGE-mod-path-containment-8]
    [Theory]
    [InlineData(@"C:\Users\Public\victim.bin")]
    [InlineData("C:/Users/Public/victim.bin")]
    [InlineData("../../x")]
    [InlineData(@"\\server\share\x")]
    [InlineData("//server/share/x")]
    [InlineData("C:x")]
    [InlineData(@"\x")]
    [InlineData("/x")]
    [InlineData("a/../../x")]
    [InlineData(@"..\..\x")]
    [InlineData(@"a\..\../x")]
    [InlineData("...")]
    [InlineData(".. /x")]
    [InlineData(". /x")]
    [InlineData("model.mdl:evil")]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"\\.\x")]
    [InlineData("a/../b.mdl")]
    public void HostileRelsAreRejected(string rel)
    {
        Assert.False(ModPath.IsSafeRel(rel));
        Assert.False(ModPath.TryResolve(Root, rel, out var full));
        Assert.Equal("", full);
        var ex = Assert.Throws<UnsafeModPathException>(() => ModPath.Resolve(Root, rel));
        Assert.IsAssignableFrom<IOException>(ex);
    }

    // [EDGE-mod-path-containment-9]
    [Theory]
    [InlineData(@"chara\equipment\e6255\model\top.mdl")]
    [InlineData("chara/equipment/e6255/model/top.mdl")]
    [InlineData("Body Retarget/Size L.v2/top.mdl")]
    [InlineData("NIRÆ/chest.mdl")]
    [InlineData("髪型/ヘア.mdl")]
    [InlineData("not asking./chara/x.mdl")]
    public void LegitimateRelsResolveInsideTheMod(string rel)
    {
        Assert.True(ModPath.IsSafeRel(rel));
        Assert.True(ModPath.TryResolve(Root, rel, out var full));
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, rel.Replace('/', '\\'))), full);
        Assert.StartsWith(Root + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
    }

    // [EDGE-mod-path-containment-10]
    [Fact]
    public void TheRootItselfIsRefused()
    {
        Assert.False(ModPath.TryResolve(Root, "", out _));
        Assert.False(ModPath.TryResolve(Root, ".", out _));
        Assert.False(ModPath.TryResolve(Root, "/", out _));
        Assert.False(ModPath.TryResolve(Root, "Shader Upgrade", out _, within: "Shader Upgrade"));
        Assert.False(ModPath.TryResolve(Root, null, out _));
    }

    // [EDGE-mod-path-containment-23] Only lexical or containment grounds: a NUL is lexical; other odd characters and a
    // great length are not grounds — the later file operation fails as it always did. A root GetFullPath can't take is
    // false, never a throw.
    [Fact]
    public void OnlyLexicalOrContainmentGroundsReject()
    {
        Assert.False(ModPath.TryResolve(Root, "a\0b.mdl", out _));
        Assert.True(ModPath.TryResolve(Root, "a|b.mdl", out var pipe));
        Assert.StartsWith(Root, pipe, StringComparison.OrdinalIgnoreCase);
        Assert.True(ModPath.TryResolve(Root, "a<b.mdl", out _));
        // Far past MAX_PATH but within what Windows itself resolves (32,767): length is never a ground of ours.
        var longRel = string.Join("/", Enumerable.Repeat(new string('a', 200), 100)) + ".mdl";
        Assert.True(ModPath.TryResolve(Root, longRel, out var longFull));
        Assert.EndsWith(longRel.Replace('/', '\\'), longFull);
        // Past what Windows resolves, GetFullPath itself refuses: false, never a throw.
        Assert.False(ModPath.TryResolve(Root, new string('a', 40_000) + ".mdl", out _));
        Assert.False(ModPath.TryResolve("bad\0root", "a.mdl", out _));
    }

    // [EDGE-mod-path-containment-24]
    [Fact]
    public void SiblingFolderWithSharedPrefixIsRefused()
    {
        Assert.False(ModPath.TryResolve(Root, "../Bobby/x.mdl", out _));
        Assert.False(ModPath.TryResolve(Root + "by", "../Bob/x.mdl", out _));
    }

    // [EDGE-mod-path-containment-24]
    [Fact]
    public void DriveRootAndTrailingSeparatorRootsResolve()
    {
        Assert.True(ModPath.TryResolve(@"E:\Bob", "x.mdl", out var a));
        Assert.Equal(@"E:\Bob\x.mdl", a);
        Assert.True(ModPath.TryResolve(@"E:\Bob\", "x.mdl", out var b));
        Assert.Equal(@"E:\Bob\x.mdl", b);
        Assert.True(ModPath.TryResolve(@"E:\", "x.mdl", out var c));
        Assert.Equal(@"E:\x.mdl", c);
    }

    // [AC-mod-path-containment-6]
    [Fact]
    public void WithinConfinesToTheSubfolder()
    {
        Assert.True(ModPath.TryResolve(Root, "Shader Upgrade/a.tex", out _, within: "Shader Upgrade"));
        Assert.False(ModPath.TryResolve(Root, "chara/x.tex", out _, within: "Shader Upgrade"));
        Assert.False(ModPath.TryResolve(Root, "Shader Upgrade/../x.tex", out _, within: "Shader Upgrade"));

        Assert.True(ModPath.TryBackup(Root, "parts-backup", "chara/x.mdl", out var backup));
        Assert.Equal(Path.Combine(Root, "Proteus", "parts-backup", "chara", "x.mdl"), backup);
        Assert.Equal(backup, ModPath.Backup(Root, "parts-backup", "chara/x.mdl"));
        Assert.False(ModPath.TryBackup(Root, "parts-backup", "../../x.mdl", out _));
        Assert.Throws<UnsafeModPathException>(() => ModPath.Backup(Root, "parts-backup", "C:/x.mdl"));
    }

    // [AC-mod-path-containment-2][EDGE-mod-path-containment-31]
    [Fact]
    public void NoteDroppedLogsOncePerFeaturePerMod()
    {
        var mods = Path.Combine(Path.GetTempPath(), "proteus_modpath_notes_" + Guid.NewGuid().ToString("N"));
        var one = Path.Combine(mods, "One");
        var two = Path.Combine(mods, "Two");

        Assert.False(ModPath.NoteDropped("brush", one, 0));
        Assert.True(ModPath.NoteDropped("brush", one, 2));
        Assert.False(ModPath.NoteDropped("brush", one, 5));
        Assert.True(ModPath.NoteDropped("matcaps", one, 1));
        Assert.True(ModPath.NoteDropped("brush", two, 3));

        var drops = ModPath.DropsUnder(mods);
        Assert.Equal(3, drops.Count);
        Assert.Equal(2, drops["brush|" + one]);
        Assert.Empty(ModPath.DropsUnder(Path.Combine(mods, "Three")));
    }
}
