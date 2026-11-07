using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class HarnessHttpTests
{
    private static Task<HarnessHttp.ParseResult> Parse(string raw) => Parse(Encoding.UTF8.GetBytes(raw));
    private static Task<HarnessHttp.ParseResult> Parse(byte[] raw) => HarnessHttp.ReadAsync(new MemoryStream(raw), CancellationToken.None);

    [Fact]
    public async Task WellFormed_ParsesMethodPathQueryHeadersBody()
    {
        var r = await Parse("POST /actions/refit?a=1&b=x%20y HTTP/1.1\r\nHost: 127.0.0.1:5\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}");
        Assert.Null(r.Error);
        Assert.Equal("POST", r.Request!.Method);
        Assert.Equal("/actions/refit", r.Request.Path);
        Assert.Equal("1", r.Request.Query["a"]);
        Assert.Equal("x y", r.Request.Query["b"]);
        Assert.Equal("application/json", r.Request.Header("content-type"));
        Assert.Equal("{}", Encoding.UTF8.GetString(r.Request.Body));
    }

    [Fact]
    [Trait("Plan", "AC-issue-sweep-c-5")]
    public async Task RepeatedQueryKey_Is400()
    {
        var r = await Parse("GET /bindings?mod=A&mod=B HTTP/1.1\r\nHost: 127.0.0.1:5\r\n\r\n");
        Assert.Null(r.Request);
        Assert.Equal(400, r.Error!.Status);
        Assert.Contains("repeated query parameter mod", Encoding.UTF8.GetString(r.Error.Body));
    }

    [Fact]
    [Trait("Plan", "EDGE-issue-sweep-c-13")]
    public async Task RepeatedQueryKey_OnLog_400_EmptyValueStillAbsent()
    {
        var r = await Parse("GET /log?since=1&since=2 HTTP/1.1\r\nHost: 127.0.0.1:5\r\n\r\n");
        Assert.Equal(400, r.Error!.Status);
        Assert.Contains("bad-request", Encoding.UTF8.GetString(r.Error.Body));

        var ok = await Parse("GET /bindings?mod= HTTP/1.1\r\nHost: 127.0.0.1:5\r\n\r\n");
        Assert.Null(ok.Error);
        Assert.Equal("", ok.Request!.Query["mod"]);   // an empty value: the route treats it as absent, as before
    }

    [Theory]
    [InlineData("GARBAGE\r\n\r\n")]
    [InlineData("GET /x\r\n\r\n")]
    [InlineData("get /x HTTP/1.1\r\n\r\n")]
    [InlineData("GET x HTTP/1.1\r\n\r\n")]
    [InlineData("GET /x HTTP/1.1\r\nNoColon\r\n\r\n")]
    [InlineData("GET /x HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n")]
    public async Task MalformedRequestLine_400(string raw)
    {
        // [EDGE-qa-harness-15] Malformed input gets 400, never an exception.
        var r = await Parse(raw);
        Assert.Equal(400, r.Error!.Status);
    }

    [Fact]
    public async Task NonHttpBytes_400()
    {
        // [EDGE-qa-harness-15]
        var r = await Parse([0xFF, 0xFE, 0x00, 0x01, (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n']);
        Assert.Equal(400, r.Error!.Status);
    }

    [Fact]
    public async Task HeadersTooLarge_431()
    {
        // [EDGE-qa-harness-15]
        var r = await Parse("GET /x HTTP/1.1\r\nX-Big: " + new string('a', HarnessHttp.MaxHeaderBytes + 10) + "\r\n\r\n");
        Assert.Equal(431, r.Error!.Status);
    }

    [Fact]
    public async Task BodyTooLarge_413()
    {
        // [EDGE-qa-harness-15]
        var r = await Parse($"POST /x HTTP/1.1\r\nContent-Length: {HarnessHttp.MaxBodyBytes + 1}\r\n\r\n");
        Assert.Equal(413, r.Error!.Status);
    }

    [Fact]
    public async Task TransferEncoding_400()
    {
        // [EDGE-qa-harness-15] Chunked bodies are not supported.
        var r = await Parse("POST /x HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
        Assert.Equal(400, r.Error!.Status);
        Assert.Contains("unsupported-encoding", Encoding.UTF8.GetString(r.Error.Body));
    }

    [Fact]
    public async Task BodyEndsEarly_400_EmptyConnection_Nothing()
    {
        // [EDGE-qa-harness-15]
        Assert.Equal(400, (await Parse("POST /x HTTP/1.1\r\nContent-Length: 10\r\n\r\nabc")).Error!.Status);
        var empty = await Parse("");
        Assert.Null(empty.Request);
        Assert.Null(empty.Error);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData(" 5x")]
    [InlineData("99999999999")]
    public async Task MalformedContentLength_400(string length)
    {
        // [EDGE-qa-harness-15]
        var r = await Parse($"POST /x HTTP/1.1\r\nContent-Length: {length}\r\n\r\n");
        Assert.Equal(400, r.Error!.Status);
    }

    [Fact]
    public async Task BodyLargerThanFirstRead_ReadInFull()
    {
        // [EDGE-qa-harness-15] A body past the first read's buffer comes through the read loop whole.
        string body = "{\"x\":\"" + new string('a', 20_000) + "\"}";
        var r = await Parse($"POST /x HTTP/1.1\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
        Assert.Null(r.Error);
        Assert.Equal(body, Encoding.UTF8.GetString(r.Request!.Body));
    }

    /// <summary>A stream that hands out a few bytes per read, so a terminator lands across reads.</summary>
    private sealed class Trickle(byte[] data, int step) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, System.Math.Min(count, step));
        public override System.Threading.Tasks.ValueTask<int> ReadAsync(System.Memory<byte> buffer, CancellationToken ct = default)
            => base.ReadAsync(buffer[..System.Math.Min(buffer.Length, step)], ct);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task HeaderEndSplitAcrossReads_Parsed(int step)
    {
        // [EDGE-qa-harness-15] The blank line found however the reads split it.
        var raw = Encoding.ASCII.GetBytes("POST /a HTTP/1.1\r\nHost: h\r\nContent-Length: 3\r\n\r\nabc");
        var r = await HarnessHttp.ReadAsync(new Trickle(raw, step), CancellationToken.None);
        Assert.Null(r.Error);
        Assert.Equal("abc", Encoding.ASCII.GetString(r.Request!.Body));
        Assert.Equal("h", r.Request.Header("Host"));
    }

    [Fact]
    public async Task AtLimits_Accepted_OneOver_Refused()
    {
        // [EDGE-qa-harness-15] A body of exactly the limit and a header block ending exactly at it are accepted.
        var body = new string('b', HarnessHttp.MaxBodyBytes);
        Assert.Null((await Parse($"POST /x HTTP/1.1\r\nContent-Length: {body.Length}\r\n\r\n{body}")).Error);

        const string start = "GET /x HTTP/1.1\r\nX-Pad: ";
        string exact = start + new string('p', HarnessHttp.MaxHeaderBytes - start.Length) + "\r\n\r\n";
        Assert.Equal(HarnessHttp.MaxHeaderBytes, exact.IndexOf("\r\n\r\n", System.StringComparison.Ordinal));
        Assert.Null((await Parse(exact)).Error);
        string over = start + new string('p', HarnessHttp.MaxHeaderBytes - start.Length + 1) + "\r\n\r\n";
        Assert.Equal(431, (await Parse(over)).Error!.Status);
    }

    [Fact]
    public void Serialize_CarriesNoStoreAndClose_NoCors()
    {
        // [EDGE-qa-harness-12] No CORS header is ever sent.
        var text = Encoding.ASCII.GetString(HarnessHttp.Serialize(HarnessResponse.Json(200, new { ok = true })));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", text);
        Assert.Contains("Connection: close\r\n", text);
        Assert.Contains("Cache-Control: no-store\r\n", text);
        Assert.DoesNotContain("Access-Control", text);
    }
}
