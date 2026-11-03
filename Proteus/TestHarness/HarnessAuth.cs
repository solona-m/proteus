using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Proteus.TestHarness;

/// <summary>
/// Who may talk to the QA harness: a loopback client, holding this session's token, that is not a browser. The threat
/// model (ADR 0002): the token keeps out browsers (CSRF, DNS rebinding), other users and remote hosts. A process running
/// as the same Windows user can read the session file, and is out of scope.
/// </summary>
internal static class HarnessAuth
{
    /// <summary>A new session token: 32 random bytes, base64url.</summary>
    internal static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The error to answer with, or null when the request may proceed. Checked in this order, so a browser is refused
    /// for being a browser before its token is looked at: remote end, Origin, Host, token.
    /// </summary>
    internal static HarnessResponse? Check(HarnessRequest request, IPEndPoint? remote, int port, string token)
    {
        if (remote == null || !IPAddress.IsLoopback(remote.Address))
            return HarnessResponse.Error(403, "forbidden-remote", "only loopback clients are served");
        // A browser always sends Origin on a cross-origin fetch or form post; curl never does.
        if (request.Header("Origin") != null)
            return HarnessResponse.Error(403, "forbidden-origin", "browser requests are refused");
        // DNS rebinding: a page on evil.example resolved to 127.0.0.1 still says Host: evil.example.
        string? host = request.Header("Host");
        if (host == null || !(string.Equals(host, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase)))
            return HarnessResponse.Error(403, "forbidden-host", $"Host must be 127.0.0.1:{port}");
        if (!TokenMatches(request.Header("Authorization"), token))
            return HarnessResponse.Error(401, "unauthorized", "missing or wrong token");
        return null;
    }

    /// <summary>Only <c>Authorization: Bearer &lt;token&gt;</c>, compared in constant time.</summary>
    internal static bool TokenMatches(string? authorization, string token)
    {
        const string scheme = "Bearer ";
        if (authorization == null || !authorization.StartsWith(scheme, StringComparison.Ordinal)) return false;
        var given = Encoding.UTF8.GetBytes(authorization[scheme.Length..]);
        var expected = Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
