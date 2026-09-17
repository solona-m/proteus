using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>Why a fetch gave up, so the caller can pick the right localized message.</summary>
public enum FetchFailure
{
    None,
    /// <summary>Every source answered with a non-success status.</summary>
    Http,
    /// <summary>The body was far smaller than expected — classically a Git LFS pointer.</summary>
    TooSmall,
    /// <summary>A transport error, a stall, or a checksum that never matched.</summary>
    Transport,
}

public readonly record struct FetchResult(
    bool Ok, FetchFailure Failure, int StatusCode, long Bytes, string Detail)
{
    public static readonly FetchResult Success = new(true, FetchFailure.None, 0, 0, "");
}

/// <summary>
/// Downloads one pinned file, resiliently: several mirrors, retries with backoff, HTTP range resume
/// across sessions, a per-read stall timeout, and a SHA-256 check before the file is promoted (a throttled
/// transfer can be a short body with a 200 on it).
/// </summary>
public sealed class ResilientDownloader(IPluginLog log, HttpMessageHandler? handler = null) : IDisposable
{
    /// <summary>Attempts per source before moving to the next one.</summary>
    private const int MaxAttempts = 5;

    /// <summary>
    /// How long a single read may stall before the attempt is abandoned; <see cref="HttpClient.Timeout"/>
    /// stops applying once the response headers are in.
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One client per downloader, disposed with it — never a static shared instance, whose pool timers would
    /// root the plugin's collectible AssemblyLoadContext past unload.
    /// Timeout is infinite; <see cref="ReadTimeout"/> is the stall detector.
    /// </summary>
    private readonly HttpClient http = CreateClient(handler);

    /// <param name="handler">Test seam only; null in the plugin.</param>
    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var c = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        c.Timeout = Timeout.InfiniteTimeSpan;
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Proteus-Plugin");
        return c;
    }

    public void Dispose() => http.Dispose();

    /// <param name="baseUrls">Tried in order. A source is abandoned only once its retries are spent.</param>
    /// <param name="onProgress">
    /// Called with the total bytes of this file now on disk — an absolute figure, not a delta, so restarts
    /// and resumes report correctly.
    /// </param>
    public async Task<FetchResult> FetchAsync(
        string[] baseUrls, string fileName, long expectedBytes, string expectedSha,
        string destPath, Action<long>? onProgress, CancellationToken ct)
    {
        var tmp = destPath + ".tmp";
        var rng = new Random();
        var last = new FetchResult(false, FetchFailure.Transport, 0, 0, "no sources configured");

        // No wait after the last attempt: it would only delay the failure.
        async Task MaybeBackoff(int attempt, System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter)
        {
            if (attempt < MaxAttempts) await Backoff(attempt, retryAfter, rng, ct);
        }

        foreach (var baseUrl in baseUrls)
        {
            var url = baseUrl + fileName;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // Re-hash whatever survived a previous attempt or session, so a resume appends to verified bytes.
                var (haveBytes, h) = await RehashPartial(tmp, expectedBytes, ct);
                using var hasher = h;

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (haveBytes > 0)
                        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(haveBytes, null);

                    using var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                    bool alreadyComplete =
                        response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                        && haveBytes == expectedBytes;

                    if (!alreadyComplete && !response.IsSuccessStatusCode)
                    {
                        last = new FetchResult(false, FetchFailure.Http, (int)response.StatusCode, 0,
                                               $"HTTP {(int)response.StatusCode}");
                        log.Warning("[Proteus] HTTP {0} for <{1}> attempt {2}/{3}",
                                    (int)response.StatusCode, url, attempt, MaxAttempts);

                        if (!IsRetryable(response.StatusCode)) break;   // next source; don't burn attempts
                        await MaybeBackoff(attempt, response.Headers.RetryAfter);
                        continue;
                    }

                    if (!alreadyComplete)
                    {
                        // A server that ignores Range answers 200 with the whole file, so start over.
                        if (haveBytes > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                        {
                            log.Information("[Proteus] {0}: range ignored, restarting from zero", fileName);
                            hasher.GetHashAndReset();   // discard prefix state; the object stays usable
                            TryDelete(tmp);
                            haveBytes = 0;
                        }
                        else if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            // A 206 may start at an offset other than the one requested; discard and restart.
                            var from = response.Content.Headers.ContentRange?.From;
                            if (from != haveBytes)
                            {
                                var detail = $"{fileName}: 206 starting at {from?.ToString() ?? "?"}, " +
                                             $"expected {haveBytes}";
                                log.Warning("[Proteus] {0} <{1}> — discarding the partial", detail, url);
                                hasher.GetHashAndReset();
                                TryDelete(tmp);
                                last = new FetchResult(false, FetchFailure.Transport, 0, 0, detail);
                                await MaybeBackoff(attempt, null);
                                continue;
                            }
                        }

                        await AppendBody(response, tmp, haveBytes, hasher, onProgress, ct);
                    }

                    var got = new FileInfo(tmp).Length;

                    // Checked before the checksum so a Git LFS pointer is reported as such.
                    if (expectedBytes > 1024 * 1024 && got < expectedBytes / 2)
                    {
                        TryDelete(tmp);
                        log.Warning("[Proteus] {0}: only {1} bytes — possible LFS pointer", fileName, got);
                        last = new FetchResult(false, FetchFailure.TooSmall, 0, got, "short body");
                        break;   // a short body is a source problem, not a transient one
                    }

                    var actualSha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    if (got != expectedBytes || !string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
                    {
                        TryDelete(tmp);
                        var detail = $"checksum mismatch for {fileName} ({got} bytes, sha256 {actualSha[..16]}…)";
                        log.Warning("[Proteus] {0} <{1}>", detail, url);
                        last = new FetchResult(false, FetchFailure.Transport, 0, got, detail);
                        await MaybeBackoff(attempt, null);
                        continue;
                    }

                    File.Move(tmp, destPath, overwrite: true);
                    log.Information("[Proteus] Fetched {0} ({1:N0} bytes)", fileName, got);
                    return FetchResult.Success;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    // An OperationCanceledException that is not the caller's is the stall timeout firing.
                    log.Warning("[Proteus] {0} <{1}> attempt {2}/{3}: {4}",
                                fileName, url, attempt, MaxAttempts, ex.Message);
                    last = new FetchResult(false, FetchFailure.Transport, 0, 0, ex.Message);
                    await MaybeBackoff(attempt, null);
                }
            }

            log.Warning("[Proteus] Giving up on <{0}>", url);
        }

        return last;
    }

    /// <summary>Streams the response body onto the end of <paramref name="tmp"/>, hashing as it goes.</summary>
    private static async Task AppendBody(
        HttpResponseMessage response, string tmp, long haveBytes, IncrementalHash hasher,
        Action<long>? onProgress, CancellationToken ct)
    {
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(tmp, haveBytes > 0 ? FileMode.Append : FileMode.Create,
                                             FileAccess.Write, FileShare.None);

        var buf = new byte[1024 * 1024];   // 1 MB
        long fileBytes = haveBytes;

        // Report the starting point before any read, so a resume or restart shows its real position.
        onProgress?.Invoke(fileBytes);

        while (true)
        {
            // A fresh linked token per read: cancels on teardown or when this read stalls past ReadTimeout.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(ReadTimeout);

            int read = await src.ReadAsync(buf, stall.Token);
            if (read == 0) break;

            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            hasher.AppendData(buf, 0, read);
            fileBytes += read;
            onProgress?.Invoke(fileBytes);
        }
    }

    /// <summary>
    /// Hashes an existing <c>.tmp</c> so a resume appends to verified bytes. Returns (0, fresh hash)
    /// when there is nothing usable, and discards a partial somehow longer than the real file.
    /// </summary>
    private async Task<(long Have, IncrementalHash Hasher)> RehashPartial(
        string tmp, long expectedBytes, CancellationToken ct)
    {
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (!File.Exists(tmp)) return (0, hasher);

        try
        {
            var len = new FileInfo(tmp).Length;
            if (len == 0 || len > expectedBytes) { TryDelete(tmp); return (0, hasher); }

            await using var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None);
            var buf = new byte[1024 * 1024];
            int read;
            while ((read = await fs.ReadAsync(buf, ct)) > 0)
                hasher.AppendData(buf, 0, read);

            log.Information("[Proteus] Resuming {0} at {1:N0} bytes", Path.GetFileName(tmp), len);
            return (len, hasher);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) throw;
            log.Warning(ex, "[Proteus] Could not resume {0}, starting over", Path.GetFileName(tmp));
            hasher.GetHashAndReset();
            TryDelete(tmp);
            return (0, hasher);
        }
    }

    /// <summary>
    /// 429 and 503 are the throttle; 408 and 5xx are transient; a 403 from a release-asset host is usually a
    /// rate limit.
    /// </summary>
    private static bool IsRetryable(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
             or HttpStatusCode.Forbidden or HttpStatusCode.InternalServerError
             or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
             or HttpStatusCode.GatewayTimeout;

    private static async Task Backoff(
        int attempt, System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter,
        Random rng, CancellationToken ct)
    {
        // Honour the server's Retry-After, capped so a hostile value cannot wedge the download.
        var wait = retryAfter?.Delta
                   ?? (retryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null)
                   ?? TimeSpan.FromSeconds(Math.Pow(2, attempt) + rng.NextDouble() * 2);

        if (wait < TimeSpan.Zero) wait = TimeSpan.FromSeconds(1);
        if (wait > TimeSpan.FromMinutes(2)) wait = TimeSpan.FromMinutes(2);

        await Task.Delay(wait, ct);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
