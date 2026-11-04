using System.Net;
using ClaudeMemory.Core.Services;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// The curl.exe transport added because claude.ai's edge 403s the .NET/SChannel TLS
/// fingerprint while OpenSSL (the Python reference) and libcurl pass (PRD FR-15a). Pure
/// parsing and command-line tests here — no socket, no process.
/// </summary>
public sealed class CurlShareHandlerTests
{
    [Fact]
    public void Parses_a_successful_write_out_trailer()
    {
        var parsed = CurlShareHandler.ParseWriteOut("200\tapplication/json\n");

        Assert.NotNull(parsed);
        Assert.Equal(200, parsed!.Value.Status);
        Assert.Equal("application/json", parsed.Value.ContentType);
    }

    [Fact]
    public void Parses_an_error_status_with_a_html_content_type()
    {
        var parsed = CurlShareHandler.ParseWriteOut("403\ttext/html\n");

        Assert.NotNull(parsed);
        Assert.Equal(403, parsed!.Value.Status);
        Assert.Equal("text/html", parsed.Value.ContentType);
    }

    [Theory]
    [InlineData("000\t")]        // curl's "no HTTP transfer happened"
    [InlineData("")]             // no trailer at all
    [InlineData("not a code\t")] // malformed
    [InlineData("-1\t")]
    public void A_trailer_without_a_usable_status_is_a_transport_failure(string meta)
    {
        Assert.Null(CurlShareHandler.ParseWriteOut(meta));
    }

    [Fact]
    public void The_command_line_carries_the_url_and_all_headers()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "scratch.out");
        var arguments = CurlShareHandler.BuildArguments(
            "https://claude.ai/api/chat_snapshots/abc",
            new Dictionary<string, string>
            {
                ["User-Agent"] = "test-agent",
                ["Accept"] = "application/json",
                ["Referer"] = "https://claude.ai/share/abc",
            },
            scratch);

        var expected = new List<string>
        {
            "--silent",
            "--show-error",
            "--ssl-no-revoke",
            "-o", scratch,
            "--write-out", "%{http_code}\\t%{content_type}\\n",
            "-H", "User-Agent: test-agent",
            "-H", "Accept: application/json",
            "-H", "Referer: https://claude.ai/share/abc",
            "https://claude.ai/api/chat_snapshots/abc",
        };

        Assert.Equal(expected, arguments);
    }

    [Fact]
    public void The_full_user_agent_reaches_curl_not_the_first_token()
    {
        // Regression: HttpHeaders splits User-Agent into product tokens, and a naive
        // first-value read put "User-Agent: Mozilla/5.0" on the wire - which claude.ai's
        // edge reads as a bot and challenges with 403.
        var request = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai/api/chat_snapshots/abc");
        const string agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";
        request.Headers.TryAddWithoutValidation("User-Agent", agent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        Assert.Equal(agent, CurlShareHandler.JoinHeaderValue(
            new("User-Agent", request.Headers.GetValues("User-Agent"))));
        Assert.Equal("application/json", CurlShareHandler.JoinHeaderValue(
            new("Accept", request.Headers.GetValues("Accept"))));
    }

    [Fact]
    public void The_default_client_finds_the_in_box_curl_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.NotNull(CurlShareHandler.TryCreate());

        // And the ShareClient default path resolves without throwing.
        _ = new ShareClient();
    }
}
