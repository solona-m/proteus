using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Proteus.TestHarness;

/// <summary>One parsed request. Header names are case-insensitive; the query is decoded.</summary>
internal sealed record HarnessRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query,
                                      IReadOnlyDictionary<string, string> Headers, byte[] Body, string RawTarget)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>One response. <see cref="HarnessHttp.Write"/> adds the headers every response carries.</summary>
internal sealed record HarnessResponse(int Status, string ContentType, byte[] Body,
                                       IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>The error envelope: <c>{"error":{"code","message"}}</c>.</summary>
    public static HarnessResponse Error(int status, string code, string message,
                                        IReadOnlyDictionary<string, string>? headers = null)
        => new(status, "application/json; charset=utf-8",
               HarnessJson.Bytes(new { error = new { code, message } }), headers);

    public static HarnessResponse Json<T>(int status, T value)
        => new(status, "application/json; charset=utf-8", HarnessJson.Bytes(value));
}

/// <summary>
/// A deliberately small HTTP/1.1 reader and writer for the QA harness: one request per connection, Content-Length
/// bodies only, hard limits on everything. Loopback-only and token-gated (see <see cref="HarnessAuth"/>), but still
/// written to survive any bytes a client sends — CodeQL never scans it, because CI compiles it out.
/// </summary>
internal static class HarnessHttp
{
    internal const int MaxHeaderBytes = 16 * 1024;
    internal const int MaxBodyBytes = 64 * 1024;

    /// <summary>A request, or the error response to send back instead; null for both when the client sent nothing.</summary>
    internal readonly record struct ParseResult(HarnessRequest? Request, HarnessResponse? Error);

    /// <summary>
    /// Read one request. Never throws for a malformed request: it answers with an error response. Throws only for IO
    /// failures and cancellation (the read timeout), which the server treats as "close the connection".
    /// </summary>
    internal static async Task<ParseResult> ReadAsync(Stream stream, CancellationToken ct)
    {
        var head = new MemoryStream();
        var buffer = new byte[4096];
        int headerEnd = -1;
        byte[] leftover = [];
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (head.Length == 0) return new ParseResult(null, null);
                return new ParseResult(null, HarnessResponse.Error(400, "bad-request", "the request ended inside its headers"));
            }
            long before = head.Length;
            head.Write(buffer, 0, n);
            headerEnd = IndexOfHeaderEnd(head.GetBuffer(), (int)head.Length, (int)Math.Max(0, before - 3));
            if (headerEnd < 0 && head.Length > MaxHeaderBytes)
                return new ParseResult(null, HarnessResponse.Error(431, "headers-too-large", $"headers over {MaxHeaderBytes} bytes"));
            if (headerEnd >= 0)
            {
                if (headerEnd > MaxHeaderBytes)
                    return new ParseResult(null, HarnessResponse.Error(431, "headers-too-large", $"headers over {MaxHeaderBytes} bytes"));
                int bodyStart = headerEnd + 4;
                leftover = head.GetBuffer().AsSpan(bodyStart, (int)head.Length - bodyStart).ToArray();
            }
        }

        string headText;
        try { headText = new UTF8Encoding(false, true).GetString(head.GetBuffer(), 0, headerEnd); }
        catch (DecoderFallbackException) { return new ParseResult(null, HarnessResponse.Error(400, "bad-request", "headers are not UTF-8")); }

        var lines = headText.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)
            || parts[0].Length == 0 || !parts[0].All(char.IsAsciiLetterUpper)
            || !parts[1].StartsWith('/'))
            return new ParseResult(null, HarnessResponse.Error(400, "bad-request", "malformed request line"));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) return new ParseResult(null, HarnessResponse.Error(400, "bad-request", "malformed header line"));
            string name = lines[i][..colon].Trim();
            if (name.Length == 0 || name.Any(c => c <= ' ' || c >= 127))
                return new ParseResult(null, HarnessResponse.Error(400, "bad-request", "malformed header name"));
            // A repeated header is refused rather than guessed at (two Hosts, two Content-Lengths).
            if (!headers.TryAdd(name, lines[i][(colon + 1)..].Trim()))
                return new ParseResult(null, HarnessResponse.Error(400, "bad-request", $"repeated header {name}"));
        }

        if (headers.ContainsKey("Transfer-Encoding"))
            return new ParseResult(null, HarnessResponse.Error(400, "unsupported-encoding", "Transfer-Encoding is not supported; send Content-Length"));

        int length = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText)
            && (!int.TryParse(lengthText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length) || length < 0))
            return new ParseResult(null, HarnessResponse.Error(400, "bad-request", "malformed Content-Length"));
        if (length > MaxBodyBytes)
            return new ParseResult(null, HarnessResponse.Error(413, "body-too-large", $"body over {MaxBodyBytes} bytes"));

        var body = new byte[length];
        int have = Math.Min(leftover.Length, length);
        Array.Copy(leftover, body, have);
        while (have < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(have, length - have), ct).ConfigureAwait(false);
            if (n == 0) return new ParseResult(null, HarnessResponse.Error(400, "bad-request", "the body ended early"));
            have += n;
        }

        string target = parts[1];
        int q = target.IndexOf('?');
        string rawPath = q < 0 ? target : target[..q];
        string path;
        try { path = Uri.UnescapeDataString(rawPath); }
        catch (UriFormatException) { return new ParseResult(null, HarnessResponse.Error(400, "bad-request", "malformed path")); }
        var query = ParseQuery(q < 0 ? "" : target[(q + 1)..]);
        if (query == null) return new ParseResult(null, HarnessResponse.Error(400, "bad-request", "malformed query"));

        return new ParseResult(new HarnessRequest(parts[0], path, query, headers, body, target), null);
    }

    private static int IndexOfHeaderEnd(byte[] data, int length, int from)
    {
        for (int i = from; i + 3 < length; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') return i;
        return -1;
    }

    private static Dictionary<string, string>? ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length == 0) return result;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            try
            {
                string key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
                string value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                result[key] = value;
            }
            catch (UriFormatException) { return null; }
        }
        return result;
    }

    /// <summary>The bytes of a response: status line, the headers every response carries, then its own, then the body.</summary>
    internal static byte[] Serialize(HarnessResponse response)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n");
        sb.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        if (response.Headers != null)
            foreach (var (name, value) in response.Headers)
                sb.Append(name).Append(": ").Append(value).Append("\r\n");
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        var all = new byte[head.Length + response.Body.Length];
        head.CopyTo(all, 0);
        response.Body.CopyTo(all, head.Length);
        return all;
    }

    internal static async Task WriteAsync(Stream stream, HarnessResponse response, CancellationToken ct)
    {
        await stream.WriteAsync(Serialize(response), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        415 => "Unsupported Media Type",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };
}
