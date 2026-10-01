namespace ActiveAlarmsParser.Service.NotificationService;

internal sealed class NotificationTokenCache(TimeSpan lifetime, TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private string token = string.Empty;
    private DateTimeOffset expiresAt = DateTimeOffset.MinValue;

    internal async Task<string> GetAsync(Func<CancellationToken, Task<string>> authenticate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(token) && clock.GetUtcNow() < expiresAt) return token;
            token = string.Empty;
            string next = await authenticate(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(next)) return string.Empty;
            token = next;
            expiresAt = clock.GetUtcNow() + lifetime;
            return token;
        }
        finally { gate.Release(); }
    }

    internal async Task InvalidateAsync(string rejectedToken, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (token == rejectedToken)
            {
                token = string.Empty;
                expiresAt = DateTimeOffset.MinValue;
            }
        }
        finally { gate.Release(); }
    }
}