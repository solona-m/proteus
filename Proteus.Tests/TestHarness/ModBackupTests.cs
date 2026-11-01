using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class ModBackupTests : IDisposable
{
    private readonly TempDir temp = new();
    private string ModsRoot => temp.Sub("mods");
    private string BackupsRoot => Path.Combine(temp.Path, "backups");

    public void Dispose() => temp.Dispose();

    private string Mod(string dir = "Outfit")
    {
        string root = temp.Sub("mods", dir);
        File.WriteAllText(Path.Combine(root, "meta.json"), "{}");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        File.WriteAllText(Path.Combine(root, "a", "b", "deep.mdl"), "deep");
        File.SetLastWriteTimeUtc(Path.Combine(root, "a", "b", "deep.mdl"), new DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        return root;
    }

    private static int idCounter;
    private static Func<string> Ids(params string[] ids)
    {
        var q = new Queue<string>(ids);
        return () => q.Count > 0 ? q.Dequeue() : $"20260101-000000-{Interlocked.Increment(ref idCounter) % 0x10000:x4}";
    }

    private ModBackup Backups(Func<string>? ids = null, long cap = ModBackup.DefaultRefitCap, long free = long.MaxValue)
        => new(BackupsRoot, ids, cap, _ => free);

    [Fact]
    public void CreateRestore_ByteIdentical_NestedDirs_MtimesPreserved()
    {
        // [EDGE-qa-harness-44] The mod's file set ends exactly the backup's files\ set: no .owner, meta.json or
        // after.json, every byte and last-write time the same.
        string mod = Mod();
        var before = FolderState.Of(mod);
        var b = Backups().Create(mod, "Outfit", BackupKind.Refit, "s", 1, "job", CancellationToken.None);
        Backups().WriteAfter(b, mod);
        File.WriteAllText(Path.Combine(mod, "a", "b", "deep.mdl"), "changed");
        File.WriteAllText(Path.Combine(mod, "added.mdl"), "added");
        Directory.CreateDirectory(Path.Combine(mod, "newdir"));
        File.WriteAllText(Path.Combine(mod, "newdir", "x.mdl"), "x");

        ModBackup.Restore(b, mod, CancellationToken.None);
        FolderState.AssertSame(before, mod);
        Assert.False(File.Exists(Path.Combine(mod, ModBackup.OwnerFile)));
        Assert.False(File.Exists(Path.Combine(mod, ModBackup.MetaFile + ".x")));
        Assert.False(File.Exists(Path.Combine(mod, ModBackup.AfterFile)));
        Assert.False(Directory.Exists(Path.Combine(mod, "newdir")));
    }

    [Fact]
    public void Restore_ExtrasDeleted_PresentInSnapshot()
    {
        // [EDGE-qa-harness-44] Extra files are deleted from the mod — after a snapshot that keeps them.
        string mod = Mod();
        var backups = Backups();
        var b = backups.Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(mod, "extra.mdl"), "extra");
        var snap = backups.Create(mod, "Outfit", BackupKind.PreRestore, "s", 2, null, CancellationToken.None);
        var (_, deleted) = ModBackup.Restore(b, mod, CancellationToken.None);
        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(mod, "extra.mdl")));
        Assert.Equal("extra", File.ReadAllText(Path.Combine(snap.FilesPath, "extra.mdl")));
    }

    [Fact]
    public void RefitOverCap_Refused_NothingWritten()
    {
        // [EDGE-qa-harness-38]
        string mod = Mod();
        var e = Assert.Throws<BackupException>(() => Backups(cap: 5).Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None));
        Assert.Equal("backup-too-large", e.Code);
        Assert.Empty(Backups().List("Outfit"));
        Assert.False(Directory.Exists(Path.Combine(BackupsRoot, "Outfit")) && Directory.EnumerateDirectories(Path.Combine(BackupsRoot, "Outfit")).Any());
    }

    [Fact]
    public void PreRestoreSnapshot_ExemptFromCap_UnderCapBackupOverOverCapMod_Restores()
    {
        // [EDGE-qa-harness-38] A mod a refit pushed over the cap can still be put back: the snapshot has no cap.
        string mod = Mod();
        var backups = Backups(cap: 100);
        var b = backups.Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(mod, "huge.mdl"), new string('x', 500));
        Assert.Throws<BackupException>(() => backups.Create(mod, "Outfit", BackupKind.Refit, "s", 2, null, CancellationToken.None));
        var snap = backups.Create(mod, "Outfit", BackupKind.PreRestore, "s", 3, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(snap.FilesPath, "huge.mdl")));
        ModBackup.Restore(b, mod, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(mod, "huge.mdl")));
    }

    [Fact]
    public void InsufficientSpace_Refused_BothKinds()
    {
        // [EDGE-qa-harness-38]
        string mod = Mod();
        var backups = Backups(free: 10);
        Assert.Equal("insufficient-space", Assert.Throws<BackupException>(() => backups.Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None)).Code);
        Assert.Equal("insufficient-space", Assert.Throws<BackupException>(() => backups.Create(mod, "Outfit", BackupKind.PreRestore, "s", 1, null, CancellationToken.None)).Code);
        Assert.Empty(Backups().List("Outfit"));
    }

    [Fact]
    public void LockedFile_PartialDeleted()
    {
        // [EDGE-qa-harness-39] A copy that fails partway deletes only its own partial folder.
        string mod = Mod();
        using var locked = new FileStream(Path.Combine(mod, "meta.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var e = Assert.Throws<BackupException>(() => Backups().Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None));
        Assert.Equal("backup-failed", e.Code);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(BackupsRoot, "Outfit")));
    }

    [Fact]
    public void IdCollision_NewId_ExistingBackupUntouched()
    {
        // [AC-qa-harness-9] [EDGE-qa-harness-40] The same id twice: the second backup gets a new one.
        string mod = Mod();
        var backups = Backups(Ids("20260101-000000-aaaa", "20260101-000000-aaaa", "20260101-000000-bbbb"));
        var first = backups.Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None);
        var firstState = FolderState.Of(first.Path);
        File.WriteAllText(Path.Combine(mod, "later.mdl"), "later");
        var second = backups.Create(mod, "Outfit", BackupKind.Refit, "s", 2, null, CancellationToken.None);
        Assert.Equal("20260101-000000-aaaa", first.Id);
        Assert.Equal("20260101-000000-bbbb", second.Id);
        FolderState.AssertSame(firstState, first.Path);
    }

    [Fact]
    public void IdCollision_ExistingIncompleteFolder_Untouched()
    {
        // [EDGE-qa-harness-40] An incomplete folder (a crash mid-copy) is never written into either.
        string mod = Mod();
        string incomplete = Path.Combine(BackupsRoot, "Outfit", "20260101-000000-cccc");
        Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "half.mdl"), "half");
        var b = Backups(Ids("20260101-000000-cccc", "20260101-000000-dddd")).Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None);
        Assert.Equal("20260101-000000-dddd", b.Id);
        Assert.Equal(["half.mdl"], Directory.EnumerateFileSystemEntries(incomplete).Select(Path.GetFileName));
    }

    [Fact]
    public void OwnerClaimFails_NewId_NeverWritesIntoFolder()
    {
        // [EDGE-qa-harness-40] Another writer claimed the folder between our check and our claim: leave it, new id.
        string mod = Mod();
        string raced = Path.Combine(BackupsRoot, "Outfit", "20260101-000000-eeee");
        var backups = Backups(Ids("20260101-000000-eeee", "20260101-000000-ffff"));
        // The race: the folder was free at the check, and another writer creates and claims it before our claim.
        backups.BetweenCheckAndClaim = folder =>
        {
            if (folder != raced) return;
            Directory.CreateDirectory(raced);
            File.WriteAllText(Path.Combine(raced, ModBackup.OwnerFile), "theirs");
        };
        var b = backups.Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None);
        Assert.Equal("20260101-000000-ffff", b.Id);
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(raced, ModBackup.OwnerFile)));
        Assert.Single(Directory.EnumerateFileSystemEntries(raced));
    }

    [Fact]
    public void IdCollision_SecondCopyFails_OnlyNewFolderDeleted_ExistingByteIdentical()
    {
        // [EDGE-qa-harness-40] The second backup's copy fails: only its own folder goes; the first is untouched.
        string mod = Mod();
        var backups = Backups(Ids("20260101-000000-1111", "20260101-000000-1111", "20260101-000000-2222"));
        var first = backups.Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None);
        var firstState = FolderState.Of(first.Path);
        using (new FileStream(Path.Combine(mod, "meta.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<BackupException>(() => backups.Create(mod, "Outfit", BackupKind.Refit, "s", 2, null, CancellationToken.None));
        FolderState.AssertSame(firstState, first.Path);
        Assert.False(Directory.Exists(Path.Combine(BackupsRoot, "Outfit", "20260101-000000-2222")));
    }

    [Fact]
    public void IdSourceAlwaysCollides_16Attempts_BackupFailed_NothingWritten()
    {
        // [EDGE-qa-harness-40] Capped: no unbounded loop, and nothing written into the existing folder.
        string mod = Mod();
        string existing = Path.Combine(BackupsRoot, "Outfit", "20260101-000000-9999");
        Directory.CreateDirectory(existing);
        int asked = 0;
        var e = Assert.Throws<BackupException>(() => Backups(() => { asked++; return "20260101-000000-9999"; })
            .Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None));
        Assert.Equal("backup-failed", e.Code);
        Assert.Equal(ModBackup.MaxIdAttempts, asked);
        Assert.Empty(Directory.EnumerateFileSystemEntries(existing));
    }

    [Fact]
    public void NoCompletionMarker_NeverSelectedOrListed()
    {
        // [EDGE-qa-harness-46] A folder without a complete meta.json is incomplete: not listed, found as incomplete.
        string mod = Mod();
        var backups = Backups();
        var b = backups.Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None);
        File.Delete(Path.Combine(b.Path, ModBackup.MetaFile));
        Assert.Empty(backups.List("Outfit"));
        var (found, incomplete) = backups.Find("Outfit", b.Id);
        Assert.Null(found);
        Assert.True(incomplete);
    }

    [Fact]
    public void ChangedSinceJob_TrueFalseNull()
    {
        // [EDGE-qa-harness-47]
        string mod = Mod();
        var backups = Backups();
        var b = backups.Create(mod, "Outfit", BackupKind.Refit, "s", 1, null, CancellationToken.None);
        Assert.Null(ModBackup.ChangedSince(b, mod));
        backups.WriteAfter(b, mod);
        Assert.False(ModBackup.ChangedSince(b, mod));
        File.WriteAllText(Path.Combine(mod, "x.mdl"), "x");
        Assert.True(ModBackup.ChangedSince(b, mod));
    }

    [Fact]
    public void PathEscape_Refused()
    {
        // [EDGE-qa-harness-49] A mod folder or backup id that would leave the backups root.
        var backups = Backups();
        foreach (var bad in new[] { "..", ".", @"..\x", "../x", @"C:\x", "a/b", "a\\b", "", " lead" })
            Assert.Throws<BackupException>(() => backups.ModFolder(bad));
        Assert.Null(backups.BackupFolder("Outfit", @"..\..\x"));
        Assert.Null(backups.BackupFolder("Outfit", "20260101-000000-zzzz"));
        Assert.Equal((null, false), backups.Find("Outfit", "../../etc"));
    }

    [Fact]
    public void WithinSession_OrderedBySeq_EvenWhenClockStepsBack_NotIdString()
    {
        // [EDGE-qa-harness-45] seq decides within a session, whatever the clock and the ids say.
        var older = Info("20260101-120000-ffff", "s", 1, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var newer = Info("20250101-000000-0000", "s", 2, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal([older.Id, newer.Id], ModBackup.InSessionOrder([newer, older]).Select(b => b.Id));
    }

    [Fact]
    public void AcrossSessions_OrderedByCreatedUtcThenSeq()
    {
        // [EDGE-qa-harness-45] Same second: seq breaks the tie; ids that sort the other way never matter.
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = Info("20260101-000000-ffff", "x", 1, t);
        var b = Info("20260101-000000-0000", "y", 2, t);
        var c = Info("20250101-000000-0000", "z", 1, t.AddSeconds(1));
        Assert.Equal([a.Id, b.Id, c.Id], ModBackup.AcrossSessionsOrder([c, b, a]).Select(x => x.Id));
    }

    private static BackupInfo Info(string id, string session, long seq, DateTime created)
        => new("Outfit", id, id, new BackupMeta(true, BackupKind.Refit, session, seq, created, null, ""));
}
