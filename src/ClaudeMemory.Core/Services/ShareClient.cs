using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeMemory.Core.Services;

/// <summary>Raised when a share link cannot be retrieved or understood (claude_memory.py:64).</summary>
public sealed class ShareException : Exception
{
    public ShareException(string message) : base(message)
    {
    }

    public ShareException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Share-link validation and retrieval, ported from claude_memory.py:52-61 and 772-831
/// (<c>SHARE_*</c>, <c>share_uuid</c>, <c>fetch_share</c>).
/// </summary>
/// <remarks>
/// The reference's default urllib user agent is rejected with 403, and the endpoint
/// rate-limits bursts with 403 as well, so the browser user agent and the retry/backoff loop
/// are both load-bearing. The handler and the delay are injectable so tests never open a
/// socket and never sleep.
/// </remarks>
public sealed class ShareClient
{
    /// <summary>Recovery advice appended to share errors (claude_memory.py:59).</summary>
    public const string ManualHint =
        "open the share link in a browser, DevTools -> Network -> filter 'chat_snapshots' "
        + "-> Copy response, then: --share-file <saved.json>";

    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    private const string ApiTemplate =
        "https://claude.ai/api/chat_snapshots/{0}?rendering_mode=messages&render_all_tools=true";

    private static readonly Regex PagePattern = new(
        @"^(?:https?://)?(?:www\.)?claude\.ai/share/([0-9a-fA-F-]{8,64})(?:[/?#].*)?$",
        RegexOptions.Compiled);

    private static readonly Regex UuidPattern =
        new(@"^[0-9a-fA-F-]{8,64}$", RegexOptions.Compiled);

    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(12),
    };

    private readonly HttpMessageHandler? _handler;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// Optional retry notice sink. <c>Core</c> must not touch the console, so the CLI host
    /// prefixes and prints these exactly like the reference's stderr line
    /// (claude_memory.py:826).</summary>
    public Action<string>? RetryNotice { get; set; }

    /// <param name="handler">Injected transport; when null a real <see cref="HttpClientHandler"/> is used.</param>
    /// <param name="delay">Injected backoff; tests pass a no-op so retries are instant.</param>
    public ShareClient(
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _handler = handler
            // Default transport: the in-box curl.exe — claude.ai's edge 403s the .NET/SChannel
            // TLS fingerprint while the same request from OpenSSL (the reference) and libcurl
            // passes (PRD §8). Plain HttpClient stays as the fallback where curl is absent.
            ?? CurlShareHandler.TryCreate() ?? (HttpMessageHandler)new HttpClientHandler();
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    /// <summary>
    /// Accepts a full share URL, a bare uuid, or nothing useful, and returns the uuid
    /// (claude_memory.py:772).
    /// </summary>
    /// <exception cref="ShareException">The value is not a claude.ai share link.</exception>
    public static string ShareUuid(string? value)
    {
        var text = (value ?? "").Trim().Trim('<', '>', '"', '\'');
        if (text.Length == 0) throw new ShareException("empty share link");

        var match = PagePattern.Match(text);
        if (match.Success) return match.Groups[1].Value;

        if (UuidPattern.IsMatch(text)) return text;

        throw new ShareException(
            $"not a claude.ai share link: {Repr(text.Length <= 80 ? text : text[..80])}\n"
            + "expected https://claude.ai/share/<uuid>");
    }

    /// <summary>GETs the public share payload, retrying the endpoint's rate-limit responses.</summary>
    /// <exception cref="ShareException">Every attempt failed, or the body was not JSON.</exception>
    public Task<JsonElement> FetchAsync(string value, CancellationToken cancellationToken = default)
        => FetchAsync(value, attempts: 3, timeout: TimeSpan.FromSeconds(30), cancellationToken);

    public async Task<JsonElement> FetchAsync(
        string value, int attempts, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var uuid = ShareUuid(value);
        var url = string.Format(ApiTemplate, uuid);
        var last = "unknown error";

        using var client = new HttpClient(
            _handler ?? new HttpClientHandler(), disposeHandler: _handler is null)
        {
            Timeout = timeout,
        };

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("Referer", $"https://claude.ai/share/{uuid}");

                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                var raw = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return ParsePayload(raw, response);

                last = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                if (!IsRetryable(response.StatusCode)) break;
            }
            catch (ShareException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // The reference catches every non-HTTPError exception here (URLError, timeout,
                // TLS, connection reset) and retries it.
                last = $"{error.GetType().Name}: {error.Message}";
            }

            if (attempt < attempts - 1)
            {
                var backoff = Backoff[Math.Min(attempt, Backoff.Length - 1)];
                RetryNotice?.Invoke(
                    $"share fetch failed ({last}), retrying in {(int)backoff.TotalSeconds}s");
                await _delay(backoff, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new ShareException(
            $"could not read share link after {attempts} attempts: {last}. "
            + "This endpoint rate-limits bursts - wait a minute, or " + ManualHint);
    }

    /// <summary>403 also means rate-limited here, so it is retried like 429 and 5xx.</summary>
    private static bool IsRetryable(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static JsonElement ParsePayload(byte[] raw, HttpResponseMessage response)
    {
        var body = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF
            ? raw[3..]
            : raw;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "?";
            throw new ShareException(
                $"share payload was not JSON (got {raw.Length} bytes of {contentType}). "
                + "Claude may have changed the page; " + ManualHint);
        }
    }

    /// <summary>Python <c>repr()</c> for the common single-line string case.</summary>
    private static string Repr(string value)
    {
        var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var body = value.Replace("\\", "\\\\").Replace(quote.ToString(), "\\" + quote);
        return quote + body + quote;
    }
}