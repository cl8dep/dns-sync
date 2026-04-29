using System.Net;
using System.Text;

namespace DnsSync.Tests.Helpers;

/// <summary>
/// A fake HttpMessageHandler that returns pre-queued responses and records all requests made.
/// Use <see cref="Enqueue"/> to set up responses in order before invoking provider methods.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _queue = new();

    /// <summary>All requests received in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Shorthand: the most recent request received.</summary>
    public HttpRequestMessage LastRequest => Requests[^1];

    /// <summary>Enqueue a response with the given status code and JSON body.</summary>
    public void Enqueue(HttpStatusCode code, string body, string contentType = "application/json")
    {
        _queue.Enqueue(new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        });
    }

    /// <summary>Enqueue a 200 OK response with the given body.</summary>
    public void Enqueue(string body, string contentType = "application/json") =>
        Enqueue(HttpStatusCode.OK, body, contentType);

    /// <summary>Number of responses still queued (not yet consumed).</summary>
    public int QueueDepth => _queue.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (_queue.Count == 0)
            throw new InvalidOperationException(
                $"FakeHttpHandler has no more queued responses but received a request to: {request.RequestUri}");

        return Task.FromResult(_queue.Dequeue());
    }

    /// <summary>Creates an HttpClient backed by this handler.</summary>
    public HttpClient CreateClient() => new(this);
}
