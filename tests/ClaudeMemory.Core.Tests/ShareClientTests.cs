using System.Net;
using ClaudeMemory.Core.Parsing;
using ClaudeMemory.Core.Services;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Share-link validation and retrieval (claude_memory.py:52-61, 772-831). Every test uses the
/// scripted handler, so no socket is opened and no real backoff is waited.
/// </summary>
public sealed class ShareClientTests
{
    private const string Uuid = "abcdef12-3456-7890-abcd-ef1234567890";
    private const string ShareUrl = "https://claude.ai/share/" + Uuid;

    [Theory]
    [InlineData("https://claude.ai/share/abcdef12-3456-7890-abcd-ef1234567890")]
    [InlineData("http://www.claude.ai/share/abcdef12-3456-7890-abcd-ef1234567890")]
    [InlineData("claude.ai/share/abcdef12-3456-7890-abcd-ef1234567890")]
    [InlineData("https://claude.ai/share/abcdef12-3456-7890-abcd-ef1234567890?foo=1")]
    [InlineData("https://claude.ai/share/abcdef12-3456-7890-abcd-ef1234567890#top")]
    [InlineData("  <https://claude.ai/share/abcdef12-3456-7890-abcd-ef1234567890>  ")]
    [InlineData("abcdef12-3456-7890-abcd-ef1234567890")]
    public void Accepts_share_urls_and_bare_uuids(string value)
        => Assert.Equal(Uuid, ShareClient.ShareUuid(value));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a link")]
    [InlineData("https://evil.example/share/abcdef123456")]
    [InlineData("https://claude.ai/chat/abcdef123456")]
    public void Rejects_anything_that_is_not_a_share_link(string value)
    {
        var error = Assert.Throws<ShareException>(() => ShareClient.ShareUuid(value));

        Assert.True(
            error.Message.Contains("not a claude.ai share link", StringComparison.Ordinal)
            || error.Message == "empty share link",
            error.Message);
    }

    [Fact]
    public async Task Fetches_the_snapshot_with_the_browser_contract()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, FixtureText());
        var client = new ShareClient(handler, NoDelay);

        var payload = await client.FetchAsync(ShareUrl);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://claude.ai/api/chat_snapshots/" + Uuid
            + "?rendering_mode=messages&render_all_tools=true",
            request.Url);
        Assert.Equal(ShareClient.BrowserUserAgent, request.UserAgent);
        Assert.Equal("application/json", request.Accept);
        Assert.Equal(ShareUrl, request.Referer);

        var session = SharePayloadParser.Parse(payload);
        Assert.Equal(4, session.Turns.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Retries_rate_limit_responses_with_backoff(HttpStatusCode status)
    {
        var delays = new List<TimeSpan>();
        var notices = new List<string>();
        var handler = new FakeHttpHandler()
            .Respond(status, "no")
            .Respond(HttpStatusCode.OK, FixtureText());
        var client = new ShareClient(handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        })
        {
            RetryNotice = notices.Add,
        };

        var payload = await client.FetchAsync(ShareUrl);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays);
        var notice = Assert.Single(notices);
        Assert.StartsWith("share fetch failed (HTTP ", notice, StringComparison.Ordinal);
        Assert.EndsWith(", retrying in 2s", notice, StringComparison.Ordinal);
        Assert.Equal(5, payload.GetProperty("chat_messages").GetArrayLength());
    }

    [Fact]
    public async Task Does_not_retry_a_404_and_reports_it()
    {
        var delays = new List<TimeSpan>();
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.NotFound, "gone");
        var client = new ShareClient(handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<ShareException>(() => client.FetchAsync(ShareUrl));

        Assert.Single(handler.Requests);
        Assert.Empty(delays);
        Assert.Contains("HTTP 404", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gives_up_after_three_5xx_attempts()
    {
        var delays = new List<TimeSpan>();
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.InternalServerError, "boom")
            .Respond(HttpStatusCode.BadGateway, "boom")
            .Respond(HttpStatusCode.ServiceUnavailable, "boom");
        var client = new ShareClient(handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<ShareException>(() => client.FetchAsync(ShareUrl));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) }, delays);
        Assert.Contains("after 3 attempts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_non_json_body_without_retrying()
    {
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, "not json");
        var client = new ShareClient(handler, NoDelay);

        var error = await Assert.ThrowsAsync<ShareException>(() => client.FetchAsync(ShareUrl));

        Assert.Single(handler.Requests);
        Assert.Contains("share payload was not JSON (got 8 bytes of application/json)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retries_transport_failures()
    {
        var delays = new List<TimeSpan>();
        var handler = new FakeHttpHandler()
            .Throw(new TaskCanceledException("The request timed out."))
            .Throw(new HttpRequestException("connection reset"))
            .Respond(HttpStatusCode.OK, FixtureText());
        var client = new ShareClient(handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await client.FetchAsync(ShareUrl);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) }, delays);
    }

    [Fact]
    public async Task Surfaces_caller_cancellation()
    {
        var handler = new FakeHttpHandler().Wait();
        var client = new ShareClient(handler, NoDelay);
        using var cancellation = new CancellationTokenSource();

        var fetch = client.FetchAsync(ShareUrl, cancellation.Token);
        await Task.Yield();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    private static Task NoDelay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

    private static string FixtureText()
        => File.ReadAllText(Path.Combine(TestPaths.Fixtures, "share-branching.json"));
}