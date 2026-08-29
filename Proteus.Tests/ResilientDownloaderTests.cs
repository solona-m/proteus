using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NSubstitute;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Covers the paths that only fire under network conditions a live host will not reproduce on demand:
/// a resumed transfer, a 206 answered from the wrong offset, a truncated body.
/// <para/>
/// These are the paths whose failure is silent and expensive. A throttled GitHub response is not an
/// error — it is a short body with a 200 on it — so without the checksum a truncated map is promoted
/// and every composite afterwards reads garbage; and without a correct resume the user on a slow link
/// re-pulls 128 MB from zero, which is the traffic this whole subsystem exists to stop.
/// </summary>
public sealed class ResilientDownloaderTests : IDisposable
{
    private readonly string dir = Path.Combine(
        Path.GetTempPath(), "proteus-dl-" + Guid.NewGuid().ToString("N"));

    public ResilientDownloaderTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
    }

    private static IPluginLog Log => Substitute.For<IPluginLog>();

    /// <summary>
    /// Deterministic body, deliberately LARGER than the downloader's 1 MB read buffer so a transfer
    /// spans several reads. That is what makes absolute progress distinguishable from deltas: with a
    /// single-read body both end at the same number, and the test would pass against either.
    /// </summary>
    private static byte[] MakeBody(int len = 3 * 1024 * 1024)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }


    private static string Sha(byte[] b) =>
        Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Serves a scripted sequence of responses, recording the Range header of each request.</summary>
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
        : HttpMessageHandler
    {
        private int n;
        public readonly List<string?> Ranges = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Ranges.Add(request.Headers.Range?.ToString());
            var step = steps[Math.Min(n, steps.Length - 1)];
            n++;
            return Task.FromResult(step(request));
        }
    }

    private static HttpResponseMessage Full(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Partial(byte[] body, long from, long? claimFrom = null)
    {
        var slice = body.Skip((int)from).ToArray();
        var r = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice),
        };
        r.Content.Headers.ContentRange =
            new System.Net.Http.Headers.ContentRangeHeaderValue(claimFrom ?? from, body.Length - 1, body.Length);
        return r;
    }

    private static readonly string[] OneSource = ["https://example.test/tag/"];

    [Fact]
    public async Task CleanDownload_ReportsAbsoluteProgressEndingAtTheFullSize()
    {
        var body = MakeBody();
        var dest = Path.Combine(dir, "map.bin");
        var seen = new List<long>();

        var d = new ResilientDownloader(Log, new ScriptedHandler(_ => Full(body)));
        var r = await d.FetchAsync(OneSource, "map.bin", body.Length, Sha(body), dest,
                                   seen.Add, CancellationToken.None);

        Assert.True(r.Ok, r.Detail);
        Assert.Equal(body, File.ReadAllBytes(dest));

        // Absolute, not deltas: monotonically non-decreasing and ending exactly at the real size. A
        // delta-based callback ends at the size too, so the ordering assertion is what distinguishes them.
        Assert.NotEmpty(seen);
        Assert.Equal(body.Length, seen[^1]);
        Assert.True(seen.SequenceEqual(seen.OrderBy(x => x)), "progress must never go backwards mid-attempt");
        d.Dispose();
    }

    [Fact]
    public async Task RetryAfterATruncatedBody_DoesNotDoubleCountProgress()
    {
        // The throttle signature: a short body with a 200 on it. The checksum rejects it, the file is
        // re-fetched, and the caller must not be told it downloaded more than the file's real size —
        // which is what a delta-based callback did, producing "248 MB / 128 MB" on the progress pill.
        var body = MakeBody();
        // Truncated, but not so short that the LFS-pointer guard catches it first — this must reach the
        // checksum, which is the check that actually defends against a throttled transfer.
        var truncated = body.Take(body.Length * 5 / 6).ToArray();
        var dest = Path.Combine(dir, "map.bin");
        var seen = new List<long>();

        var d = new ResilientDownloader(Log, new ScriptedHandler(
            _ => Full(truncated),
            _ => Full(body)));

        var r = await d.FetchAsync(OneSource, "map.bin", body.Length, Sha(body), dest,
                                   seen.Add, CancellationToken.None);

        Assert.True(r.Ok, r.Detail);
        Assert.Equal(body, File.ReadAllBytes(dest));

        // The discriminators against a delta-based callback, which is what produced "248 MB / 128 MB":
        //   - the LAST value is the file's full size, not the size of the final chunk;
        //   - no value exceeds the real size, however many attempts it took;
        //   - the retry reported 0, i.e. the caller was told to rewind rather than keep accumulating.
        Assert.Equal(body.Length, seen[^1]);
        Assert.Equal(body.Length, seen.Max());
        Assert.Contains(0L, seen);
        d.Dispose();
    }

    [Fact]
    public async Task ResumesFromAnExistingPartial_AndDoesNotRefetchIt()
    {
        var body = MakeBody();
        var dest = Path.Combine(dir, "map.bin");
        const int have = 100 * 1024;
        File.WriteAllBytes(dest + ".tmp", body.Take(have).ToArray());
        var seen = new List<long>();

        var handler = new ScriptedHandler(req =>
            Partial(body, req.Headers.Range!.Ranges.Single().From!.Value));
        var d = new ResilientDownloader(Log, handler);

        var r = await d.FetchAsync(OneSource, "map.bin", body.Length, Sha(body), dest,
                                   seen.Add, CancellationToken.None);

        Assert.True(r.Ok, r.Detail);
        Assert.Equal(body, File.ReadAllBytes(dest));
        Assert.Equal($"bytes={have}-", handler.Ranges[0]);
        // Progress opens at the resumed position rather than counting up from zero.
        Assert.Equal(have, seen[0]);
        d.Dispose();
    }

    [Fact]
    public async Task A206FromTheWrongOffset_IsRejectedRatherThanAppended()
    {
        // A proxy that answers "bytes=102400-" with the whole file from byte 0, labelled 206 and saying
        // so in its Content-Range. Appending that onto the existing prefix builds a file out of the
        // wrong bytes. Only a header that DISAGREES with the request is detectable up front — a body
        // that contradicts its own honest-looking header is the checksum's job, not this check's.
        var body = MakeBody();
        var dest = Path.Combine(dir, "map.bin");
        const int have = 100 * 1024;
        File.WriteAllBytes(dest + ".tmp", body.Take(have).ToArray());

        var seen = new List<long>();
        var handler = new ScriptedHandler(
            // We asked for bytes=102400-; this answers from 0 and says so in Content-Range.
            _ => Partial(body, 0),
            _ => Full(body));                          // the restart then succeeds
        var d = new ResilientDownloader(Log, handler);

        var r = await d.FetchAsync(OneSource, "map.bin", body.Length, Sha(body), dest,
                                   seen.Add, CancellationToken.None);

        Assert.True(r.Ok, r.Detail);
        Assert.Equal(body, File.ReadAllBytes(dest));

        // The discriminator: the mismatched body is rejected on its HEADER, so not one byte of it is
        // written. Drop the Content-Range check and the whole thing is appended onto the 100 KB prefix
        // instead — the file transiently reaches have + body.Length, which shows up here as progress
        // overshooting the file's real size. Only the checksum would then notice, after a full wasted
        // transfer, and it would report a mismatch that blames the origin rather than the intermediary.
        Assert.All(seen, v => Assert.True(v <= body.Length,
            $"progress reported {v} for a {body.Length}-byte file — the suspect 206 was appended"));
        Assert.Equal($"bytes={have}-", handler.Ranges[0]);
        Assert.Null(handler.Ranges[1]);   // restarted from zero, not resumed onto a suspect prefix
        d.Dispose();
    }

    [Fact]
    public async Task A200AnsweringARangeRequest_RestartsInsteadOfAppending()
    {
        // A server that ignores Range replies 200 with the WHOLE file; appending it to a partial would
        // corrupt it.
        var body = MakeBody();
        var dest = Path.Combine(dir, "map.bin");
        File.WriteAllBytes(dest + ".tmp", body.Take(50 * 1024).ToArray());

        var d = new ResilientDownloader(Log, new ScriptedHandler(_ => Full(body)));
        var r = await d.FetchAsync(OneSource, "map.bin", body.Length, Sha(body), dest,
                                   null, CancellationToken.None);

        Assert.True(r.Ok, r.Detail);
        Assert.Equal(body, File.ReadAllBytes(dest));
        d.Dispose();
    }

    [Fact]
    public async Task A404IsNotRetried_AndIsReportedAsAnHttpFailure()
    {
        var dest = Path.Combine(dir, "map.bin");
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var d = new ResilientDownloader(Log, handler);

        var r = await d.FetchAsync(OneSource, "map.bin", 1024, new string('0', 64), dest,
                                   null, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(FetchFailure.Http, r.Failure);
        Assert.Equal(404, r.StatusCode);
        // Not retryable: burning five attempts plus backoff on a permanent answer would stall the
        // caller for minutes, and the tag simply is not published yet.
        Assert.Single(handler.Ranges);
        Assert.False(File.Exists(dest));
        d.Dispose();
    }

    [Fact]
    public async Task FallsBackToTheNextSourceAndKeepsTheFileIntact()
    {
        var body = MakeBody();
        var dest = Path.Combine(dir, "map.bin");
        string[] two = ["https://mirror.test/tag/", "https://origin.test/tag/"];

        var seenHosts = new List<string>();
        var handler = new ScriptedHandler(req =>
        {
            seenHosts.Add(req.RequestUri!.Host);
            return req.RequestUri.Host == "mirror.test"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Full(body);
        });

        var d = new ResilientDownloader(Log, handler);
        var r = await d.FetchAsync(two, "map.bin", body.Length, Sha(body), dest,
                                   null, CancellationToken.None);

        Assert.True(r.Ok, r.Detail);
        Assert.Equal(body, File.ReadAllBytes(dest));
        Assert.Equal(["mirror.test", "origin.test"], seenHosts);
        d.Dispose();
    }
}
