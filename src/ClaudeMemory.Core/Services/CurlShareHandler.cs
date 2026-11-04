using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace ClaudeMemory.Core.Services;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that performs the GET through the Windows in-box
/// <c>curl.exe</c> instead of .NET's TLS stack.
/// </summary>
/// <remarks>
/// Why it exists: claude.ai's edge rejects the request before it reaches the application —
/// with identical URL and headers (browser UA, <c>Accept: application/json</c>, share
/// <c>Referer</c>) Python 3.12/OpenSSL and the in-box Schannel-based curl.exe 8.13 get
/// through (404 on an unknown uuid, 200 on a real one), while .NET 8's HttpClient returns
/// 403 with a Cloudflare block page on every SslOptions variant probed (2026-11-05). The
/// ClientHello fingerprint is the difference, and .NET on Windows offers no way to reshape
/// it. curl.exe ships in the box since Windows 10 1803, inside the 1809+ target. Where
/// curl.exe is absent the host falls back to the plain HttpClientHandler.
/// </remarks>
public sealed class CurlShareHandler : HttpMessageHandler
{
    private readonly string _curlPath;

    private CurlShareHandler(string curlPath) => _curlPath = curlPath;

    /// <summary>Locates curl.exe (System32 first, then PATH); null when absent.</summary>
    public static CurlShareHandler? TryCreate()
    {
        var systemCopy = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (File.Exists(systemCopy)) return new CurlShareHandler(systemCopy);

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (pathVariable is null) return null;
        foreach (var directory in pathVariable.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, "curl.exe");
                if (File.Exists(candidate)) return new CurlShareHandler(candidate);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry must not break the search.
            }
        }

        return null;
    }

    /// <summary>
    /// The curl command line for one GET: silent, no Windows revocation probe (the reference's
    /// urllib does no revocation checking either), body to the scratch file, and a write-out
    /// trailer carrying the status code and content type.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(
        string url, IEnumerable<KeyValuePair<string, string>> headers, string scratchPath)
    {
        var arguments = new List<string>
        {
            "--silent",
            "--show-error",
            "--ssl-no-revoke",
            "-o", scratchPath,
            "--write-out", "%{http_code}\\t%{content_type}\\n",
        };

        foreach (var (name, value) in headers)
        {
            arguments.Add("-H");
            arguments.Add(name + ": " + value);
        }

        arguments.Add(url);
        return arguments;
    }

    /// <summary>
    /// Rebuilds one header's value. <see cref="System.Net.Http.Headers.HttpHeaders"/> stores
    /// <c>User-Agent</c> as separate product tokens ("Mozilla/5.0", "(Windows NT 10.0; ...)",
    /// …), so the naive first-value read sent a truncated UA on the wire — and a truncated
    /// browser UA is itself a bot signal. Space-joining restores the original string; other
    /// list headers join with the standard comma.
    /// </summary>
    internal static string JoinHeaderValue(KeyValuePair<string, IEnumerable<string>> header)
        => string.Join(
            header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ? " " : ", ",
            header.Value);

    /// <summary>
    /// Splits the write-out trailer: <c>&lt;code&gt;\t&lt;type&gt;</c>. Null when curl could not
    /// complete a transfer (code 000) or the trailer is missing or malformed.
    /// </summary>
    internal static (int Status, string ContentType)? ParseWriteOut(string meta)
    {
        var trimmed = meta.Trim();
        var tabIndex = trimmed.IndexOf('\t');
        var codeText = tabIndex < 0 ? trimmed : trimmed[..tabIndex];
        var contentType = tabIndex < 0 ? "" : trimmed[(tabIndex + 1)..].Trim();

        if (!int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
        {
            return null;
        }

        // 000 is curl's "no HTTP transfer happened" (DNS, TLS, timeout, bad exit).
        return status <= 0 ? null : (status, contentType);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = request.RequestUri?.ToString()
            ?? throw new InvalidOperationException("the request has no URL");

        var headers = request.Headers.ToDictionary(
            h => h.Key, h => JoinHeaderValue(h));
        var scratchPath = Path.Combine(
            Path.GetTempPath(), "claude-memory-share-" + Guid.NewGuid().ToString("N") + ".out");

        var start = new ProcessStartInfo
        {
            FileName = _curlPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in BuildArguments(url, headers, scratchPath))
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = start };
            process.Start();
            var trailerTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using (cancellationToken.Register(
                static state =>
                {
                    var process = (Process)state!;
                    if (process.HasExited) return;
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                    }
                },
                process))
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            var trailer = await trailerTask.ConfigureAwait(false);
            var stderr = await errorTask.ConfigureAwait(false);

            var parsed = ParseWriteOut(trailer)
                ?? throw new HttpRequestException(
                    "curl transfer failed (exit " + process.ExitCode + "): " + FirstLine(stderr));
            var body = File.Exists(scratchPath)
                ? await File.ReadAllBytesAsync(scratchPath, CancellationToken.None).ConfigureAwait(false)
                : Array.Empty<byte>();

            var response = new HttpResponseMessage((HttpStatusCode)parsed.Status)
            {
                ReasonPhrase = ReasonPhrase(parsed.Status),
                Content = new ByteArrayContent(body),
            };
            if (parsed.ContentType.Length > 0)
            {
                response.Content.Headers.TryAddWithoutValidation("Content-Type", parsed.ContentType);
            }

            return response;
        }
        finally
        {
            try
            {
                File.Delete(scratchPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The short reason phrases the status line prints; empty for the rest.</summary>
    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "",
    };

    private static string FirstLine(string text)
    {
        var line = text.Trim();
        var newline = line.IndexOf('\n');
        if (newline >= 0) line = line[..newline].TrimEnd();
        return line.Length > 200 ? line[..200] : line;
    }
}
