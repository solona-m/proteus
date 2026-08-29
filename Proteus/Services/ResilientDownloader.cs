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
/// across sessions, a per-read stall timeout, and a SHA-256 check before the file is promoted.
/// <para/>
/// The failure this exists for is a THROTTLED transfer. GitHub rate-limits anonymous release-asset
/// downloads, and a throttled response is not an error — it is a short body with a 200 on it. Without
/// the checksum a truncated map got promoted and every composite afterwards read garbage; without the
/// range resume, a user on a slow link who lost the connection at 120 MB started again from zero.
/// </summary>
public sealed class ResilientDownloader(IPluginLog log, HttpMessageHandler? handler = null) : IDisposable
{
    /// <summary>Attempts per source before moving to the next one.</summary>
    private const int MaxAttempts = 5;

    /// <summary>
    /// How long a single read may stall before the attempt is abandoned. <see cref="HttpClient.Timeout"/>
    /// stops applying once the response headers are in, so without this a connection throttled to zero
    /// bytes/sec hangs forever with the progress figure frozen mid-count.
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One client per downloader, owned and disposed with it — deliberately NOT a static shared instance.
    /// <para/>
    /// A Dalamud plugin lives in a collectible AssemblyLoadContext, and that context only unloads once
    /// nothing roots it. <c>SocketsHttpHandler</c> keeps a connection pool with cleanup timers, and the
    /// runtime's timer queue is rooted OUTSIDE the plugin's context — so a client that outlives the
    /// plugin can pin the whole context, which keeps its native libraries mapped and its file handles
    /// open. That is not hypothetical here: it is why <c>proteus_bcn.dll</c> stayed locked by the game
    /// after the plugin had been unloaded, and why a rebuild needed a full client restart.
    /// <para/>
    /// Timeout is infinite because these are 100 MB+ bodies over slow links and <see cref="ReadTimeout"/>
    /// is the real stall detector; the default 100 s would abort a perfectly healthy slow download.
    /// </summary>
    private readonly HttpClient http = CreateClient(handler);

    /// <param name="handler">
    /// Test seam only; null in the plugin. The resume, range-validation and retry paths fire only under
    /// network conditions that cannot be reproduced against a live host — a truncated body, a 206 from
    /// the wrong offset — and those are exactly the paths whose failure is silent and expensive.
    /// </param>
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
    /// Called with the total bytes of THIS file now on disk — an absolute figure, not a delta.
    /// <para/>
    /// Deltas were wrong here: an attempt that restarts from zero (a checksum mismatch, or a server that
    /// ignored a Range request) re-reports bytes the caller has already counted, so a single retry made
    /// the caller's running total exceed the real size and the progress line read "248 MB / 128 MB".
    /// An absolute figure per file resets naturally on a restart and already accounts for a resumed
    /// prefix, so a resuming download opens at the byte it actually reached rather than at zero.
    /// </param>
    public async Task<FetchResult> FetchAsync(
        string[] baseUrls, string fileName, long expectedBytes, string expectedSha,
        string destPath, Action<long>? onProgress, CancellationToken ct)
    {
        var tmp = destPath + ".tmp";
        var rng = new Random();
        var last = new FetchResult(false, FetchFailure.Transport, 0, 0, "no sources configured");

        // Waiting after the LAST attempt delays the failure by up to the cap and buys nothing — the loop
        // is about to exit either way. With two sources and a server that keeps answering 503 with a
        // Retry-After, that dead time was minutes of a frozen progress pill before the user was told
        // anything had gone wrong.
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

                // Re-hash whatever survived a previous attempt or session. This is what makes resuming
                // safe: appending to an unverified prefix yields a file of the right LENGTH and the
                // wrong CONTENT — the one failure mode a size check cannot see.
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
                        // A server that ignores Range answers 200 with the WHOLE file. Appending that to
                        // a partial would corrupt it, so start over.
                        if (haveBytes > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                        {
                            log.Information("[Proteus] {0}: range ignored, restarting from zero", fileName);
                            hasher.GetHashAndReset();   // discard prefix state; the object stays usable
                            TryDelete(tmp);
                            haveBytes = 0;
                        }
                        else if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            // Trusting the STATUS alone is not enough: a proxy may answer a range request
                            // from an offset of its own choosing, and appending that body would build a
                            // file of exactly the right length out of the wrong bytes. The checksum would
                            // still catch it, but only after a full transfer, and it would report a
                            // "checksum mismatch" that sends the reader hunting a corrupt origin instead
                            // of a misbehaving intermediary. Discard and restart, naming the real cause.
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

                    // Kept ahead of the checksum as its own case: a Git LFS pointer is a few hundred
                    // bytes of text arriving with a perfectly good 200, and saying so names the actual
                    // mistake where "checksum mismatch" would send someone hunting a network fault.
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

        // Report the starting point before any read, so a resumed file shows its real position
        // immediately instead of counting up from zero, and so a restarted attempt visibly rewinds.
        onProgress?.Invoke(fileBytes);

        while (true)
        {
            // A fresh linked token per read: cancels on teardown OR when this one read has stalled past
            // ReadTimeout, which is exactly the window HttpClient.Timeout no longer covers.
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
    /// 429 and 503 are the throttle asking us to come back; 408 and 5xx are transient. A 403 from a
    /// release-asset host is usually a rate limit rather than a real permission problem, so it earns
    /// one backoff before we fall through to the next source.
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
        // Honour the server's own number when it gives one — guessing shorter is what turns a throttle
        // into a block — but cap it so a hostile value cannot wedge the download for an hour.
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
