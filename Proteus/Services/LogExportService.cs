using System;
using System.Collections.Generic;
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
    // How much longer it waits while a write retry, a drawn re-check or a composite is still to finish (#91), so the
    // export catches the retry and drawn-check lines that follow its refresh.
    private static readonly TimeSpan SettleCap = TimeSpan.FromSeconds(20);

    /// <summary>How long one read of the names may take: Copy Logs is pressed when things are stuck, and must never hang
    /// on a framework thread that does not answer.</summary>
    internal static readonly TimeSpan NamesTimeout = TimeSpan.FromSeconds(3);

    private readonly CapturingPluginLog capture;
    private readonly CompositorService compositor;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private readonly string dalamudLogPath;
    private readonly Func<Task<(IReadOnlyCollection<string> Collections, string? Character)>> namesNow;

    /// <param name="namesNow">The Penumbra collection names and the logged-in character's name, read on the framework
    /// thread: what the exported file must not contain.</param>
    public LogExportService(CapturingPluginLog capture, CompositorService compositor, PenumbraBridge penumbra,
        Configuration config, string configDirectory,
        Func<Task<(IReadOnlyCollection<string> Collections, string? Character)>> namesNow)
    {
        this.capture = capture;
        this.compositor = compositor;
        this.penumbra = penumbra;
        this.config = config;
        this.namesNow = namesNow;
        // ConfigDirectory is XIVLauncher\pluginConfigs\Proteus; dalamud.log sits in XIVLauncher.
        dalamudLogPath = Path.GetFullPath(Path.Combine(configDirectory, "..", "..", "dalamud.log"));
    }

    /// <summary>
    /// Waits <paramref name="settle"/>, then keeps waiting in short steps while <paramref name="pending"/> holds, for at
    /// most <paramref name="cap"/> more. Returns how long it waited beyond the base.
    /// </summary>
    internal static async Task<TimeSpan> SettleAsync(Func<bool> pending, TimeSpan settle, TimeSpan cap,
                                                    Func<TimeSpan, Task> delay)
    {
        await delay(settle).ConfigureAwait(false);
        var step = TimeSpan.FromMilliseconds(500);
        var extra = TimeSpan.Zero;
        while (extra < cap && pending())
        {
            await delay(step).ConfigureAwait(false);
            extra += step;
        }
        return extra;
    }

    /// <summary>Returns the written file's path. Throws only on a failed write.</summary>
    public async Task<string> ExportAsync()
    {
        var started = DateTime.Now;
        var dalamudStart = DalamudLogLength();
        // Read at the start and again at the end, so a character or collection switched to mid-capture is scrubbed too.
        var names = await GatherNamesAsync(namesNow, null, NamesTimeout).ConfigureAwait(false);

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
                await SettleAsync(() => compositor.DiagnosticsPending, Settle, SettleCap,
                    t => Task.Delay(t)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = $"refresh threw: {ex.Message}";
        }
        var lines = capture.EndCapture();
        names = await GatherNamesAsync(namesNow, names, NamesTimeout).ConfigureAwait(false);

        var header = new StringBuilder();
        header.AppendLine("Proteus log");
        header.AppendLine($"Build:     #{Plugin.BuildNumber} ({Plugin.BuildStamp}), v{typeof(Plugin).Assembly.GetName().Version}");
        header.AppendLine($"Captured:  {started:yyyy-MM-dd HH:mm:ss} to {DateTime.Now:HH:mm:ss}");
        header.AppendLine($"Enabled:   {config.PluginEnabled}   Auto redraw: {config.AutoRedraw}"
                    + (compositor.AutoRedrawOn != config.AutoRedraw ? $" (effective {compositor.AutoRedrawOn}, test harness override)" : "")
                    + $"   Penumbra: {(penumbra.IsAvailable ? "available" : "unavailable")}");
        header.AppendLine($"Refresh:   {outcome}");

        var dir = DesktopFolder.Path() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var path = Path.Combine(dir, $"Proteus-log-{started:yyyyMMdd-HHmmss}.txt");
        var text = ComposeExport(header.ToString(), lines, DalamudLogSince(dalamudStart),
                                 Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), names.Collections, names.Character);
        await File.WriteAllTextAsync(path, text).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// The whole exported file: <paramref name="header"/>, the captured Proteus lines and the dalamud.log slice, in that
    /// order under their headings — scrubbed as a whole, the slice included. The file is meant to be posted, so neither the
    /// Windows user name nor the character's or any collection's name leaves the machine in it
    /// (<see cref="LogAnonymizer.Scrub(string, string?, IEnumerable{string}?, string?)"/>).
    /// </summary>
    internal static string ComposeExport(string header, IReadOnlyList<string> lines, string dalamudSlice,
                                         string? profileDir, IEnumerable<string> collections, string? character)
    {
        var sb = new StringBuilder();
        sb.Append(header);
        sb.AppendLine();
        sb.AppendLine($"==== Proteus, all levels ({lines.Count} lines) ====");
        foreach (var line in lines)
            sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("==== dalamud.log, same window ====");
        sb.AppendLine(dalamudSlice);
        return LogAnonymizer.Scrub(sb.ToString(), profileDir, collections, character);
    }

    /// <summary>
    /// The names to scrub, merged with <paramref name="earlier"/>: every collection either read saw, and the latest
    /// character. Never throws and never waits past <paramref name="timeout"/>: a read that fails or takes too long
    /// keeps the earlier names. A read given up on is still observed, so its late fault is never an unobserved one.
    /// </summary>
    internal static async Task<(HashSet<string> Collections, string? Character)> GatherNamesAsync(
        Func<Task<(IReadOnlyCollection<string> Collections, string? Character)>> namesNow,
        (HashSet<string> Collections, string? Character)? earlier, TimeSpan timeout)
    {
        var collections = new HashSet<string>(earlier?.Collections ?? [], StringComparer.OrdinalIgnoreCase);
        var character = earlier?.Character;
        try
        {
            var call = namesNow();
            if (await Task.WhenAny(call, Task.Delay(timeout)).ConfigureAwait(false) != call)
            {
                _ = call.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                return (collections, character);
            }
            var (now, who) = await call.ConfigureAwait(false);
            foreach (var c in now ?? []) if (!string.IsNullOrWhiteSpace(c)) collections.Add(c);
            if (!string.IsNullOrWhiteSpace(who)) character = who;
        }
        catch (Exception)
        {
            // Unknown names: keep what the start read saw. The export itself never fails on this.
        }
        return (collections, character);
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
