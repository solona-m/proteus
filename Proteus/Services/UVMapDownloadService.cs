using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

public enum UVMapDownloadState { Idle, Downloading, Done, Failed }

public class UVMapDownloadService : IDisposable
{
    private static readonly string BaseUrl =
        "https://github.com/solona-m/proteus/releases/download/uvmaps-v1/";

    private static readonly string[] MapFiles =
    [
        "bibo_to_gen3_transfer.tif",
        "gen3_to_bibo_transfer.tif",
    ];

    private readonly IPluginLog log;
    private readonly string mapsDir;
    private readonly CancellationTokenSource cts = new();

    public UVMapDownloadState State { get; private set; } = UVMapDownloadState.Idle;
    public string StatusMessage { get; private set; } = string.Empty;

    public UVMapDownloadService(IPluginLog log, string pluginDir)
    {
        this.log = log;
        mapsDir = Path.Combine(pluginDir, "uvmaps");
    }

    public bool MapsPresent()
    {
        foreach (var file in MapFiles)
        {
            var path = Path.Combine(mapsDir, file);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return false;
        }
        return true;
    }

    public void EnsureMapsAsync(Action? onComplete = null)
    {
        if (State == UVMapDownloadState.Downloading) return;
        if (MapsPresent())
        {
            State = UVMapDownloadState.Done;
            return;
        }

        State = UVMapDownloadState.Downloading;
        // Localized at PRODUCTION time, on a thread-pool thread, and then held as a plain string: switching
        // language mid-download leaves this one pill in the old language until the next progress tick.
        // Accepted deliberately — the alternative is storing a key plus boxed args and formatting at draw
        // time, which is real machinery for a string that is replaced within seconds.
        StatusMessage = Loc.Localize("Service.UVMaps.Downloading", "Downloading UV maps...");
        Task.Run(() => DownloadAll(onComplete), cts.Token);
    }

    private async Task DownloadAll(Action? onComplete)
    {
        try
        {
            Directory.CreateDirectory(mapsDir);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Proteus-Plugin");

            long totalDownloaded = 0;
            const long totalExpected = 256L * 1024 * 1024;

            foreach (var file in MapFiles)
            {
                var dest = Path.Combine(mapsDir, file);
                var tmp  = dest + ".tmp";

                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                    continue;

                if (File.Exists(tmp))
                    File.Delete(tmp);

                var url = BaseUrl + file;
                log.Information("[Proteus] Downloading {0}", url);

                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    Fail(string.Format(Loc.Localize("Service.UVMaps.HttpFailed.Fmt",
                        "Download failed: HTTP {0} for {1}"), (int)response.StatusCode, file));
                    return;
                }

                long fileBytes = 0;

                await using (var src = await response.Content.ReadAsStreamAsync(cts.Token))
                await using (var dst = File.Create(tmp))
                {
                    var buf = new byte[1024 * 1024]; // 1 MB buffer
                    long nextReport = 5L * 1024 * 1024;
                    int read;

                    while ((read = await src.ReadAsync(buf, cts.Token)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read), cts.Token);
                        fileBytes       += read;
                        totalDownloaded += read;

                        if (totalDownloaded >= nextReport)
                        {
                            var mb    = totalDownloaded / (1024 * 1024);
                            var total = totalExpected   / (1024 * 1024);
                            StatusMessage = string.Format(Loc.Localize("Service.UVMaps.Progress.Fmt",
                                "Downloading UV maps... ({0} MB / {1} MB)"), mb, total);
                            nextReport += 5L * 1024 * 1024;
                        }
                    }
                }

                if (fileBytes < 100L * 1024 * 1024)
                {
                    File.Delete(tmp);
                    Fail(string.Format(Loc.Localize("Service.UVMaps.TooSmall.Fmt",
                        "Download too small ({0} bytes) for {1} — possible LFS pointer"), fileBytes, file));
                    return;
                }

                File.Move(tmp, dest, overwrite: true);
                log.Information("[Proteus] UV map ready: {0}", file);
            }

            State = UVMapDownloadState.Done;
            StatusMessage = string.Empty;
            log.Information("[Proteus] UV maps download complete.");
            onComplete?.Invoke();
        }
        catch (OperationCanceledException)
        {
            foreach (var file in MapFiles)
            {
                var tmp = Path.Combine(mapsDir, file) + ".tmp";
                if (File.Exists(tmp)) try { File.Delete(tmp); } catch { }
            }
            State = UVMapDownloadState.Idle;
        }
        catch (Exception ex)
        {
            Fail(string.Format(Loc.Localize("Service.UVMaps.Error.Fmt", "Download error: {0}"), ex.Message));
        }
    }

    private void Fail(string message)
    {
        State = UVMapDownloadState.Failed;
        StatusMessage = message;
        log.Error("[Proteus] {0}", message);
    }

    public void Dispose() => cts.Cancel();
}
