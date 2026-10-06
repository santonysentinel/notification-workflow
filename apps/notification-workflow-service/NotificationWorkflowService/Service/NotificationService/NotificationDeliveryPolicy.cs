using System.Net;
using Microsoft.Extensions.Logging;

namespace ActiveAlarmsParser.Service.NotificationService;

internal sealed class NotificationHttpException(HttpStatusCode statusCode, TimeSpan? retryAfter)
    : HttpRequestException("The API returned an unexpected status code.", null, statusCode)
{
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}

internal static class NotificationDeliveryPolicy
{
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan MaxInlineDelay = TimeSpan.FromSeconds(30);

    internal static TimeSpan Backoff(int attempt, double jitter)
        => TimeSpan.FromSeconds(Math.Min(16, Math.Pow(2, attempt - 1)) + Math.Clamp(jitter, 0, 1));

    internal static bool CanRetryWithIdempotency(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => true,
        _ => false
    };

    internal static async Task<bool> ExecuteAsync(Func<CancellationToken, Task<bool>> send,
        Func<CancellationToken, Task> invalidateToken, bool idempotencyConfirmed, ILogger logger,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitter = null)
    {
        delay ??= (duration, token) => Task.Delay(duration, token);
        jitter ??= () => Random.Shared.NextDouble();
        bool reauthenticated = false;
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Application-level failures/malformed acknowledgements are not transport retries.
                return await send(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                {
                    if (reauthenticated) throw;
                    reauthenticated = true;
                    await invalidateToken(cancellationToken).ConfigureAwait(false);
                    if (attempt >= MaxAttempts) throw;
                    logger.LogWarning("Notification request rejected with 401; refreshing authentication once");
                    // Refresh is part of this same attempt budget, not a nested retry policy.
                    continue;
                }

                bool throttled = exception is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
                if (attempt >= MaxAttempts || (!throttled && !(idempotencyConfirmed && CanRetryWithIdempotency(exception)))) throw;

                TimeSpan wait = Backoff(attempt, jitter());
                if (exception is NotificationHttpException { RetryAfter: { } retryAfter } && retryAfter > wait)
                    wait = retryAfter;
                // Never shorten a server's Retry-After to satisfy our inline wait limit.
                // The service records the cooldown and returns control to its caller instead.
                if (wait > MaxInlineDelay) throw;
                logger.LogWarning("Retrying notification delivery after {DelaySeconds} seconds; attempt {NextAttempt} of {MaxAttempts}", wait.TotalSeconds, attempt + 1, MaxAttempts);
                await delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}