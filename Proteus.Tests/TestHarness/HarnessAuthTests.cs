using System;
using System.Collections.Generic;
using System.Net;
using Proteus.TestHarness;
using Xunit;

namespace Proteus.Tests.TestHarness;

public class HarnessAuthTests
{
    private const string Token = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";
    private static readonly IPEndPoint Loopback = new(IPAddress.Loopback, 50000);

    private static HarnessRequest Request(params (string, string)[] headers)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers) h[k] = v;
        return new HarnessRequest("GET", "/health", new Dictionary<string, string>(), h, [], "/health");
    }

    private static (string, string) Host => ("Host", "127.0.0.1:5555");
    private static (string, string) Bearer => ("Authorization", "Bearer " + Token);

    [Fact]
    public void Valid_Passes_LocalhostHostToo()
    {
        Assert.Null(HarnessAuth.Check(Request(Host, Bearer), Loopback, 5555, Token));
        Assert.Null(HarnessAuth.Check(Request(("Host", "localhost:5555"), Bearer), Loopback, 5555, Token));
    }

    [Fact]
    public void Origin_403()
    {
        // [AC-qa-harness-3] A browser's request is refused, token or not.
        var e = HarnessAuth.Check(Request(Host, Bearer, ("Origin", "http://evil.example")), Loopback, 5555, Token);
        Assert.Equal(403, e!.Status);
        Assert.Contains("forbidden-origin", System.Text.Encoding.UTF8.GetString(e.Body));
    }

    [Fact]
    public void ForeignHost_403()
    {
        // [AC-qa-harness-3] DNS rebinding: the Host names the attacker's domain.
        var e = HarnessAuth.Check(Request(("Host", "evil.example:5555"), Bearer), Loopback, 5555, Token);
        Assert.Equal(403, e!.Status);
        Assert.Equal(403, HarnessAuth.Check(Request(("Host", "127.0.0.1:1"), Bearer), Loopback, 5555, Token)!.Status);
        Assert.Equal(403, HarnessAuth.Check(Request(Bearer), Loopback, 5555, Token)!.Status);
    }

    [Fact]
    public void NonLoopbackRemote_403()
    {
        // [AC-qa-harness-3]
        Assert.Equal(403, HarnessAuth.Check(Request(Host, Bearer), new IPEndPoint(IPAddress.Parse("192.168.1.5"), 1), 5555, Token)!.Status);
    }

    [Fact]
    public void NonLoopbackRemote_403_Ipv4LoopbackOk()
    {
        // [EDGE-qa-harness-14] Non-loopback (IPv4 and IPv6) refused; 127.0.0.1 accepted.
        Assert.Equal(403, HarnessAuth.Check(Request(Host, Bearer), new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1), 5555, Token)!.Status);
        Assert.Equal(403, HarnessAuth.Check(Request(Host, Bearer), new IPEndPoint(IPAddress.Parse("fe80::1"), 1), 5555, Token)!.Status);
        Assert.Equal(403, HarnessAuth.Check(Request(Host, Bearer), null, 5555, Token)!.Status);
        Assert.Null(HarnessAuth.Check(Request(Host, Bearer), Loopback, 5555, Token));
    }

    [Fact]
    public void SameLengthLastByteDiffers_Rejected()
    {
        // [AC-qa-harness-3] The constant-time compare rejects a near miss; only the Bearer scheme is accepted.
        string near = Token[..^1] + (Token[^1] == 'G' ? 'H' : 'G');
        Assert.False(HarnessAuth.TokenMatches("Bearer " + near, Token));
        Assert.True(HarnessAuth.TokenMatches("Bearer " + Token, Token));
        Assert.False(HarnessAuth.TokenMatches("bearer " + Token, Token));
        Assert.False(HarnessAuth.TokenMatches("Basic " + Token, Token));
        Assert.False(HarnessAuth.TokenMatches(null, Token));
        Assert.Equal(401, HarnessAuth.Check(Request(Host, ("Authorization", "Bearer " + near)), Loopback, 5555, Token)!.Status);
    }

    [Fact]
    public void NewToken_IsLongAndUrlSafe()
    {
        var t = HarnessAuth.NewToken();
        Assert.True(t.Length >= 43);
        Assert.DoesNotContain('+', t);
        Assert.DoesNotContain('/', t);
        Assert.NotEqual(t, HarnessAuth.NewToken());
    }
}
