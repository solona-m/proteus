using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

/// <summary>The sidecar-edit ledger: backups, put-back, the edit/put-back lock, and the leftover scan, over temp folders.</summary>
public class HarnessSidecarTests : IDisposable
{
    private readonly TempDir temp = new();
    private string Backups => Path.Combine(temp.Path, "config", "test-harness", "backups");
    private string Meta(string mod) => Path.Combine(temp.Path, "mods", mod, "Proteus", "metadata.json");

    public void Dispose() => temp.Dispose();

    private string PutMeta(string mod, string text)
    {
        var path = Meta(mod);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private HarnessSidecar Make(string session = "s1") => new(Backups, session, null);

    /// <summary>A save that writes <paramref name="text"/> and, like SaveMetadata, snapshots the defaults first.</summary>
    private static Func<bool> Saving(string path, string text, bool snapshot = true) => () =>
    {
        var defaults = HarnessSidecar.DefaultsPath(path);
        if (snapshot && !File.Exists(defaults)) File.Copy(path, defaults);
        File.WriteAllText(path, text);
        return true;
    };

    private static ChangeResult Edit(HarnessSidecar s, string mod, string path, Func<bool> save, bool staged = false)
        => s.Edit(mod, path, () => staged, save, b => b!);

    private static readonly Func<string, bool> NoneStaged = _ => false;

    [Fact]
    [Trait("Plan", "AC-idle-disk-io-10")]
    [Trait("Plan", "EDGE-idle-disk-io-37")]
    public void FirstEditBacksUp_SecondDoesNot_PutBackRestoresFirstBefore()
    {
        // [AC-idle-disk-io-10] [EDGE-idle-disk-io-37]
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        Assert.Equal(200, Edit(s, "Skin", path, Saving(path, "EDIT-1")).Status);
        Assert.Equal(200, Edit(s, "Skin", path, Saving(path, "EDIT-2")).Status);
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(Backups, "s1", "sidecar", "Skin", "metadata.json")));

        var forgotten = new System.Collections.Generic.List<string>();
        var undo = Assert.Single(s.PutBack(NoneStaged, forgotten.Add));
        Assert.Equal("restored", undo.Result);
        Assert.Equal("ORIGINAL", File.ReadAllText(path));
        Assert.Equal(["Skin"], forgotten);
        Assert.True(s.Empty);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-38")]
    public void PutBack_ChangedFile_LeftAndReported()
    {
        // [EDGE-idle-disk-io-38]
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        Edit(s, "Skin", path, Saving(path, "HARNESS"));
        File.WriteAllText(path, "THE USER'S OWN EDIT");
        var undo = Assert.Single(s.PutBack(NoneStaged, _ => { }));
        Assert.Equal("changed", undo.Result);
        Assert.Equal("THE USER'S OWN EDIT", File.ReadAllText(path));
        Assert.True(File.Exists(Path.Combine(Backups, "s1", "sidecar", "Skin", "metadata.json")));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-39")]
    public void PutBack_DeletesOwnDefaults_OnlyWhenCreatedUnchangedAndRestored()
    {
        // [EDGE-idle-disk-io-39]
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        Edit(s, "Skin", path, Saving(path, "HARNESS"));
        Assert.True(File.Exists(HarnessSidecar.DefaultsPath(path)));
        Assert.Equal("deleted", Assert.Single(s.PutBack(NoneStaged, _ => { })).Defaults);
        Assert.False(File.Exists(HarnessSidecar.DefaultsPath(path)));

        // One that was there before the harness's save is never touched.
        var s2 = Make("s2");
        var path2 = PutMeta("Other", "ORIGINAL2");
        File.WriteAllText(HarnessSidecar.DefaultsPath(path2), "USER DEFAULTS");
        Edit(s2, "Other", path2, Saving(path2, "HARNESS"));
        Assert.Equal("none", Assert.Single(s2.PutBack(NoneStaged, _ => { })).Defaults);
        Assert.Equal("USER DEFAULTS", File.ReadAllText(HarnessSidecar.DefaultsPath(path2)));

        // Created by the harness's save but edited since: kept.
        var s3 = Make("s3");
        var path3 = PutMeta("Third", "ORIGINAL3");
        Edit(s3, "Third", path3, Saving(path3, "HARNESS"));
        File.WriteAllText(HarnessSidecar.DefaultsPath(path3), "EDITED DEFAULTS");
        var undo3 = Assert.Single(s3.PutBack(NoneStaged, _ => { }));
        Assert.Equal("restored", undo3.Result);
        Assert.Equal("kept", undo3.Defaults);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-39")]
    public void PutBack_Changed_KeepsDefaults()
    {
        // [EDGE-idle-disk-io-39]
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        Edit(s, "Skin", path, Saving(path, "HARNESS"));
        File.WriteAllText(path, "USER EDIT");
        var undo = Assert.Single(s.PutBack(NoneStaged, _ => { }));
        Assert.Equal(("changed", "kept"), (undo.Result, undo.Defaults));
        Assert.True(File.Exists(HarnessSidecar.DefaultsPath(path)));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-40")]
    [Trait("Plan", "AC-idle-disk-io-10")]
    public void UserEditHeld_409_NoBackupNoSave()
    {
        // [EDGE-idle-disk-io-40] [AC-idle-disk-io-10]
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        bool saved = false;
        var r = Edit(s, "Skin", path, () => saved = true, staged: true);
        Assert.Equal((409, "user-edit-held"), (r.Status, r.Code));
        Assert.False(saved);
        Assert.False(Directory.Exists(Path.Combine(Backups, "s1", "sidecar", "Skin")));
        Assert.True(s.Empty);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-55")]
    public void PutBack_UserEditHeld_ChangedAndLeft_LedgerLeft()
    {
        // [EDGE-idle-disk-io-55]
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        Edit(s, "Skin", path, Saving(path, "HARNESS"));
        var undo = Assert.Single(s.PutBack(_ => true, _ => { }));
        Assert.Equal(("changed", "user-edit-held"), (undo.Result, undo.Code));
        Assert.Equal("HARNESS", File.ReadAllText(path));
        var record = File.ReadAllText(Path.Combine(Backups, "s1", "sidecar", "Skin", "record.json"));
        Assert.Contains("\"State\":\"left\"", record);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-52")]
    public void StartScan_ReportsOtherSessionsNotDone_RestoresNothing()
    {
        // [EDGE-idle-disk-io-52]
        var crashed = Make("old");
        var path = PutMeta("Skin", "ORIGINAL");
        Edit(crashed, "Skin", path, Saving(path, "HARNESS"));   // never put back: the game crashed

        var found = Assert.Single(HarnessSidecar.ScanLeftovers(Backups, "new"));
        Assert.Equal(("old", "Skin", true), (found.Session, found.Mod, found.FileMatchesHarnessWrite));
        Assert.Equal("HARNESS", File.ReadAllText(path));
        Assert.Empty(HarnessSidecar.ScanLeftovers(Backups, "old"));   // its own session is not a leftover
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-52")]
    public void StartScan_PathsRelativeToConfigFolder()
    {
        // [EDGE-idle-disk-io-52]
        var crashed = Make("old");
        var path = PutMeta("Skin", "ORIGINAL");
        Edit(crashed, "Skin", path, Saving(path, "HARNESS"));
        var found = Assert.Single(HarnessSidecar.ScanLeftovers(Backups, "new"));
        Assert.Equal("test-harness/backups/old/sidecar/Skin", found.Backup);
        Assert.DoesNotContain(temp.Path, found.Backup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-52")]
    public void PutBack_MarksLedgerDone()
    {
        // [EDGE-idle-disk-io-52]
        var s = Make("old");
        var path = PutMeta("Skin", "ORIGINAL");
        Edit(s, "Skin", path, Saving(path, "HARNESS"));
        s.PutBack(NoneStaged, _ => { });
        Assert.Empty(HarnessSidecar.ScanLeftovers(Backups, "new"));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-41")]
    public void RecordWrittenBeforeSave()
    {
        // [EDGE-idle-disk-io-41]
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        var record = Path.Combine(Backups, "s1", "sidecar", "Skin", "record.json");
        bool recordedFirst = false;
        Edit(s, "Skin", path, () => { recordedFirst = File.Exists(record); return Saving(path, "HARNESS")(); });
        Assert.True(recordedFirst);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-41")]
    public void OffThreadStop_RestoresSidecars_SkipsRecomposite()
    {
        // [EDGE-idle-disk-io-41] Put-back is file work and a forget: it runs on any thread (here, a pool thread).
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        Edit(s, "Skin", path, Saving(path, "HARNESS"));
        s.BeginStop();
        var undo = Task.Run(() => s.PutBack(NoneStaged, _ => { })).GetAwaiter().GetResult();
        Assert.Equal("restored", Assert.Single(undo).Result);
        Assert.Equal("ORIGINAL", File.ReadAllText(path));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-41")]
    public void Leftovers_ListsOnlyFailures()
    {
        // [EDGE-idle-disk-io-41]
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        Edit(s, "Skin", path, Saving(path, "HARNESS"));
        Assert.Single(s.Leftovers());
        s.PutBack(NoneStaged, _ => { });
        Assert.Empty(s.Leftovers());
        Assert.DoesNotContain(temp.Path, string.Join(" ", s.Leftovers()));
    }

    [Fact]
    public void SaveFailed_KeepsBackup_500()
    {
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        var r = Edit(s, "Skin", path, () => false);
        Assert.Equal((500, "save-failed"), (r.Status, r.Code));
        Assert.True(File.Exists(Path.Combine(Backups, "s1", "sidecar", "Skin", "metadata.json")));
        Assert.Equal("unchanged", Assert.Single(s.PutBack(NoneStaged, _ => { })).Result);
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-56")]
    public void PutBack_WaitsForEditInFlight_ThenRestores()
    {
        // [EDGE-idle-disk-io-56] An edit's save is blocked mid-save; a put-back started then waits for it, and restores.
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        using var inSave = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var edit = Task.Run(() => Edit(s, "Skin", path, () =>
        {
            inSave.Set();
            release.Wait();
            return Saving(path, "HARNESS")();
        }));
        Assert.True(inSave.Wait(5000));
        var putBack = Task.Run(() => s.PutBack(NoneStaged, _ => { }));
        Assert.False(putBack.Wait(150));   // waiting on the lock, not racing the save
        release.Set();
        Assert.Equal(200, edit.GetAwaiter().GetResult().Status);
        Assert.Equal("restored", Assert.Single(putBack.GetAwaiter().GetResult()).Result);
        Assert.Equal("ORIGINAL", File.ReadAllText(path));
    }

    [Fact]
    [Trait("Plan", "EDGE-idle-disk-io-57")]
    public void Edit_StoppingInsideLock_503_NoSave()
    {
        // [EDGE-idle-disk-io-57]
        var s = Make();
        var path = PutMeta("Skin", "ORIGINAL");
        s.BeginStop();
        bool saved = false;
        var r = Edit(s, "Skin", path, () => saved = true);
        Assert.Equal((503, "harness-stopping"), (r.Status, r.Code));
        Assert.False(saved);
        Assert.Equal("ORIGINAL", File.ReadAllText(path));
    }

    [Fact]
    public void BomFile_RestoredByteForByte()
    {
        var s = Make();
        var path = Meta("Skin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"Version\":1}")).ToArray();
        File.WriteAllBytes(path, original);
        Edit(s, "Skin", path, Saving(path, "{\"Version\":2}"));
        s.PutBack(NoneStaged, _ => { });
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}
