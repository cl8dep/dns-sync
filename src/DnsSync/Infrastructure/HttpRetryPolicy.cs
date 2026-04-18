using System.Net;
using Microsoft.Extensions.Logging;

namespace DnsSync.Infrastructure;

/// <summary>
/// Shared exponential-backoff retry policy for HTTP providers.
/// Retries on transient HTTP errors (5xx, 429, timeouts).
/// Does NOT retry on user-initiated cancellation.
/// </summary>
public static class HttpRetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        ILogger logger,
        CancellationToken ct,
        int maxRetries = 3,
        string context = "")
    {
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                logger.LogWarning(
                    "Transient error{Context} on attempt {Attempt}/{Max}: {Error}. Retrying in {Delay}ms",
                    string.IsNullOrEmpty(context) ? "" : $" ({context})",
                    attempt + 1, maxRetries, ex.Message, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }
        return await action();
    }

    public static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode:
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout } => true,
        TaskCanceledException tce when !tce.CancellationToken.IsCancellationRequested => true,
        _ => false
    };
}
