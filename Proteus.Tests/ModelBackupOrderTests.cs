using System;
using System.IO;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// The three in-place model edits — part switches, hat fitting, the brush — must be undone in the reverse of
/// the order they were made, because each keeps its own backup of the file and so each one's "original" is
/// whatever the previous feature left.
/// </summary>
public class ModelBackupOrderTests : IDisposable
{
    private const string Rel = "chara/equipment/e0001/model/c0201e0001_dwn.mdl";

    private readonly string root = Path.Combine(Path.GetTempPath(), "proteus_backups_" + Path.GetRandomFileName());

    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    private void Backup(string subdir, DateTime written)
    {
        var at = Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, subdir,
                              Rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(at)!);
        File.WriteAllBytes(at, [1, 2, 3]);
        File.SetLastWriteTimeUtc(at, written);
    }

    /// <summary>Switches first, brush second: undoing the switches must wait for the brush.</summary>
    [Fact]
    public void AnEarlierEditIsBlockedByALaterOne()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Backup(MeshToggleService.BackupSubdir, t0);
        Backup(MeshVolumeService.BackupSubdir, t0.AddMinutes(5));

        Assert.NotNull(ModelBackupOrder.LaterFeature(root, MeshToggleService.BackupSubdir, Rel));
        Assert.Null(ModelBackupOrder.LaterFeature(root, MeshVolumeService.BackupSubdir, Rel));
    }

    /// <summary>And the same for hat fitting under a later brush, which did not check at all before.</summary>
    [Fact]
    public void HatFittingIsBlockedByALaterBrush()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Backup(HatCompatService.BackupSubdir, t0);
        Backup(MeshVolumeService.BackupSubdir, t0.AddMinutes(5));

        Assert.NotNull(ModelBackupOrder.LaterFeature(root, HatCompatService.BackupSubdir, Rel));
    }

    /// <summary>A file only one feature has touched is never blocked.</summary>
    [Fact]
    public void ALoneEditIsNeverBlocked()
    {
        Backup(MeshToggleService.BackupSubdir, DateTime.UtcNow);
        Assert.Null(ModelBackupOrder.LaterFeature(root, MeshToggleService.BackupSubdir, Rel));
    }

    /// <summary>
    /// The toggles revert refuses, and restores nothing, while a later brush backup exists — the case from
    /// the review: reverting switches under a brush edit threw the brush edit away.
    /// </summary>
    [Fact]
    public void TheSwitchRevertRefusesUnderALaterBrushEdit()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Backup(MeshToggleService.BackupSubdir, t0);
        Backup(MeshVolumeService.BackupSubdir, t0.AddMinutes(5));

        var model = Path.Combine(root, Rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllBytes(model, [9, 9, 9]);
        File.WriteAllText(Path.Combine(root, SidecarDiscoveryService.SidecarSubdir, MeshToggleService.RecordFile),
            "{\"Items\":[{\"GroupName\":\"Proteus Switches\",\"SetId\":1,\"Slot\":\"dwn\",\"Files\":[\"" + Rel
          + "\"],\"Toggles\":{\"Bow\":\"a\"}}]}");

        var result = MeshToggleService.Revert(root);

        Assert.False(result.Ok);
        // Refused for the RIGHT reason — an unreadable record would also leave the file alone.
        Assert.Contains("first", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(model));
    }
}
