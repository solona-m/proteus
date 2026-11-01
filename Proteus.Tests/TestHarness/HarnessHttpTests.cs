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
