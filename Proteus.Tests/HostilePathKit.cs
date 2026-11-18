using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Proteus.Tests;

/// A throwaway mods folder with a "victim" file outside the mod, for proving that a hostile
/// mod's records or redirects can't make Proteus touch anything outside the mod folder.
/// Every hostile path these tests use lands inside the sandbox, so even the unfixed code only
/// ever touches the temp folder. Deliberately does NOT use ModPath: it must work on the old code.
///
///   <temp>/proteus_sec_<rand>/          ← Sandbox (victim.bin lives here)
///   <temp>/proteus_sec_<rand>/mods/     ← ModsRoot
///   <temp>/proteus_sec_<rand>/mods/Mod/ ← ModRoot
internal sealed class HostilePathKit : IDisposable
{
    internal static readonly DateTime PinnedTime = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly byte[] DefaultVictimBytes = "victim — must survive"u8.ToArray();

    public string Sandbox { get; }
    public string ModsRoot { get; }
    public string ModRoot { get; }
    public string Victim { get; }
    byte[] _victimBytes = DefaultVictimBytes;

    /// <param name="victimName">The victim's file name: a sink that only takes certain extensions (a material
    /// redirect must end in .mtrl) needs one it would take.</param>
    public HostilePathKit(string modName = "Mod", string victimName = "victim.bin")
    {
        Sandbox = Path.Combine(Path.GetTempPath(), "proteus_sec_" + Guid.NewGuid().ToString("N")[..10]);
        ModsRoot = Path.Combine(Sandbox, "mods");
        ModRoot = Path.Combine(ModsRoot, modName);
        Directory.CreateDirectory(ModRoot);
        Victim = Path.Combine(Sandbox, victimName);
        SetVictimContent(DefaultVictimBytes);
    }

    /// The victim's full path, as an "absolute rel" a hostile record would carry ('/' separators,
    /// the form Proteus' own records use).
    public string AbsoluteVictim => Victim.Replace('\\', '/');

    /// The exact '..' climb from `fromModRelFolder` (a folder relative to the mod, "." = the mod root)
    /// up to the victim, e.g. "../../victim.bin" from the mod root.
    public string ClimbToVictim(string fromModRelFolder = ".")
    {
        var from = Path.GetFullPath(Path.Combine(ModRoot, fromModRelFolder));
        var rel = Path.GetRelativePath(from, Victim).Replace('\\', '/');
        Assert.StartsWith("../", rel);
        // Prefix the folder itself so the rel is relative to the mod root.
        return fromModRelFolder is "." or "" ? rel : fromModRelFolder.TrimEnd('/', '\\').Replace('\\', '/') + "/" + rel;
    }

    /// Writes attacker bytes where the OLD code would have read a backup for `rel`
    /// (Path.Combine(modRoot, "Proteus", subdir, rel)), only when that lies inside the sandbox.
    public string? PlantOldBackup(string subdir, string rel, byte[] bytes)
    {
        var at = Path.GetFullPath(Path.Combine(ModRoot, "Proteus", subdir, rel.Replace('/', '\\')));
        if (!at.StartsWith(Sandbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(at, Victim, StringComparison.OrdinalIgnoreCase)) return null; // never re-pin the victim here
        Directory.CreateDirectory(Path.GetDirectoryName(at)!);
        File.WriteAllBytes(at, bytes);
        return at;
    }

    /// Gives the victim real fixture content (so content-checking sinks don't skip it on the old
    /// code) and re-pins its write time; AssertVictimUntouched compares against this state.
    public void SetVictimContent(byte[] bytes)
    {
        _victimBytes = bytes.ToArray();
        File.WriteAllBytes(Victim, _victimBytes);
        File.SetLastWriteTimeUtc(Victim, PinnedTime);
    }

    public void AssertVictimUntouched()
    {
        Assert.True(File.Exists(Victim), "the victim outside the mod was deleted");
        Assert.Equal(_victimBytes, File.ReadAllBytes(Victim));
        Assert.Equal(PinnedTime, File.GetLastWriteTimeUtc(Victim));
    }

    public string InMod(string rel) => Path.Combine(ModRoot, rel.Replace('/', '\\'));

    public void Dispose()
    {
        try { Directory.Delete(Sandbox, true); } catch { /* best effort */ }
    }
}
