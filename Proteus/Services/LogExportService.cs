using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Proteus.Interop;

namespace Proteus.Services;

/// <summary>
/// Copy Logs: runs a full refresh, records everything logged while it runs, and writes that to a file on the
/// desktop. The file carries the Proteus lines at every level plus the slice of dalamud.log from the same window,
/// which is where Penumbra's "Failed to load" lines land.
/// </summary>
public sealed class LogExportService
{
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(120);
    // Covers SchedulePostRedrawShellCheck (about 2.4 s of polling), the DONE and timeline lines after ResultChanged,
    // and Dalamud's buffered file writer.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly CapturingPluginLog capture;
    private readonly CompositorService compositor;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private readonly string dalamudLogPath;

    public LogExportService(CapturingPluginLog capture, CompositorService compositor, PenumbraBridge penumbra,
        Configuration config, string configDirectory)
    {
        this.capture = capture;
        this.compositor = compositor;
        this.penumbra = penumbra;
        this.config = config;
        // ConfigDirectory is XIVLauncher\pluginConfigs\Proteus; dalamud.log sits in XIVLauncher.
        dalamudLogPath = Path.GetFullPath(Path.Combine(configDirectory, "..", "..", "dalamud.log"));
    }

    /// <summary>Returns the written file's path. Throws only on a failed write.</summary>
    public async Task<string> ExportAsync()
    {
        var started = DateTime.Now;
        var dalamudStart = DalamudLogLength();

        capture.BeginCapture();
        string outcome;
        try
        {
            capture.Information("[Proteus] Copy Logs: full refresh starting");
            var (result, notRun) = await compositor.FullRefreshAsync(RefreshTimeout).ConfigureAwait(false);
            outcome = notRun != null ? $"not run ({notRun})"
                : result == null ? $"timed out after {RefreshTimeout.TotalSeconds:F0} s"
                : result.Success ? $"done ({result.OverlayModsUsed} mods, {result.TexturesPatched} textures)"
                : $"FAILED: {result.ErrorMessage}";
            if (notRun == null)
                await Task.Delay(Settle).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = $"refresh threw: {ex.Message}";
        }
        var lines = capture.EndCapture();

        var sb = new StringBuilder();
        sb.AppendLine("Proteus log");
        sb.AppendLine($"Build:     #{Plugin.BuildNumber} ({Plugin.BuildStamp}), v{typeof(Plugin).Assembly.GetName().Version}");
        sb.AppendLine($"Captured:  {started:yyyy-MM-dd HH:mm:ss} to {DateTime.Now:HH:mm:ss}");
        sb.AppendLine($"Enabled:   {config.PluginEnabled}   Auto redraw: {config.AutoRedraw}   Penumbra: {(penumbra.IsAvailable ? "available" : "unavailable")}");
        sb.AppendLine($"Refresh:   {outcome}");
        sb.AppendLine();
        sb.AppendLine($"==== Proteus, all levels ({lines.Count} lines) ====");
        foreach (var line in lines)
            sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("==== dalamud.log, same window ====");
        sb.AppendLine(DalamudLogSince(dalamudStart));

        var dir = DesktopFolder.Path() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var path = Path.Combine(dir, $"Proteus-log-{started:yyyyMMdd-HHmmss}.txt");
        // The file is meant to be posted, so no Windows user name leaves the machine in it.
        await File.WriteAllTextAsync(path, LogAnonymizer.Scrub(sb.ToString())).ConfigureAwait(false);
        return path;
    }

    private long? DalamudLogLength()
    {
        try { return File.Exists(dalamudLogPath) ? new FileInfo(dalamudLogPath).Length : null; }
        catch { return null; }
    }

    private string DalamudLogSince(long? start)
    {
        if (start is not { } from) return $"(not found at {dalamudLogPath})";
        try
        {
            // ReadWrite share: Dalamud's writer holds the file open.
            using var fs = new FileStream(dalamudLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Shorter than before means the log rolled over mid-capture; take all of the new one.
            fs.Seek(fs.Length < from ? 0 : from, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"(could not read {dalamudLogPath}: {ex.Message})";
        }
    }
}
