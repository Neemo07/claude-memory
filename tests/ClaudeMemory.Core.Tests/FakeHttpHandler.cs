using System.Net;
using System.Text;

namespace ClaudeMemory.Core.Tests;

/// <summary>
/// Scripted transport for the share client: no socket is opened and every response is queued
/// by the test. Requests are recorded before the request object is disposed.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _steps = new();

    public List<RecordedRequest> Requests { get; } = new();

    public FakeHttpHandler Respond(
        HttpStatusCode status, string body, string contentType = "application/json")
    {
        _steps.Enqueue(_ => Task.FromResult(Response(status, body, contentType)));
        return this;
    }

    public FakeHttpHandler Throw(Exception error)
    {
        _steps.Enqueue(_ => Task.FromException<HttpResponseMessage>(error));
        return this;
    }

    public FakeHttpHandler Wait()
    {
        _steps.Enqueue(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new RecordedRequest(
            request.RequestUri!.ToString(),
            request.Headers.UserAgent.ToString(),
            request.Headers.Accept.ToString(),
            request.Headers.Referrer?.ToString()));

        if (_steps.Count == 0) throw new InvalidOperationException("unexpected request");

        return _steps.Dequeue()(cancellationToken);
    }

    private static HttpResponseMessage Response(
        HttpStatusCode status, string body, string contentType)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    internal sealed record RecordedRequest(string Url, string UserAgent, string Accept, string? Referer);
}