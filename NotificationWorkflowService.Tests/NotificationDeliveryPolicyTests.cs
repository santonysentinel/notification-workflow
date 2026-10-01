using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ActiveAlarmsParser.Service.NotificationService;

namespace NotificationWorkflowService.Tests;

public class NotificationDeliveryPolicyTests
{
    [Fact]
    public async Task ThrottlingUsesBoundedExponentialBackoffAndJitter()
    {
        int attempts = 0;
        var waits = new List<TimeSpan>();
        var failure = new NotificationHttpException(HttpStatusCode.TooManyRequests, null);
        Assert.Same(failure, await Assert.ThrowsAsync<NotificationHttpException>(() => Run(_ =>
        {
            attempts++;
            throw failure;
        }, waits: waits)));
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5) }, waits);
    }

    [Fact]
    public async Task RetryAfterIsMinimumWait()
    {
        int attempts = 0;
        var waits = new List<TimeSpan>();
        Assert.True(await Run(_ =>
        {
            if (++attempts == 1) throw new NotificationHttpException(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(10));
            return Task.FromResult(true);
        }, waits: waits));
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(waits));
    }

    [Fact]
    public async Task LongRetryAfterDefersInsteadOfRetryingEarly()
    {
        int attempts = 0;
        var waits = new List<TimeSpan>();
        await Assert.ThrowsAsync<NotificationHttpException>(() => Run(_ =>
        {
            attempts++;
            throw new NotificationHttpException(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(5));
        }, waits: waits));
        Assert.Equal(1, attempts);
        Assert.Empty(waits);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    [InlineData(200)] // Unexpected successful status: may already have delivered.
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task DefaultPolicyDoesNotReplayPermanentOrAmbiguousPostFailures(int status)
    {
        int attempts = 0;
        var waits = new List<TimeSpan>();
        await Assert.ThrowsAsync<NotificationHttpException>(() => Run(_ =>
        {
            attempts++;
            throw new NotificationHttpException((HttpStatusCode)status, null);
        }, waits: waits));
        Assert.Equal(1, attempts);
        Assert.Empty(waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailureRequiresConfirmedIdempotency(bool confirmed)
    {
        int attempts = 0;
        var waits = new List<TimeSpan>();
        await Assert.ThrowsAsync<HttpRequestException>(() => Run(_ =>
        {
            attempts++;
            throw new HttpRequestException("Unknown POST outcome");
        }, confirmed, waits));
        Assert.Equal(confirmed ? 3 : 1, attempts);
        Assert.Equal(confirmed ? 2 : 0, waits.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutRequiresConfirmedIdempotency(bool confirmed)
    {
        int attempts = 0;
        await Assert.ThrowsAsync<TaskCanceledException>(() => Run(_ =>
        {
            attempts++;
            throw new TaskCanceledException("Timed out");
        }, confirmed));
        Assert.Equal(confirmed ? 3 : 1, attempts);
    }

    [Fact]
    public async Task ConfirmedIdempotencyAllowsServerFailureRecovery()
    {
        int attempts = 0;
        Assert.True(await Run(_ =>
        {
            if (++attempts < 3) throw new NotificationHttpException(HttpStatusCode.ServiceUnavailable, null);
            return Task.FromResult(true);
        }, confirmed: true));
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    [InlineData(200)]
    [InlineData(501)]
    public async Task PermanentFailureIsNotRetriedEvenWhenIdempotencyConfirmed(int status)
    {
        int attempts = 0;
        await Assert.ThrowsAsync<NotificationHttpException>(() => Run(_ =>
        {
            attempts++;
            throw new NotificationHttpException((HttpStatusCode)status, null);
        }, confirmed: true));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task UnauthorizedRefreshesOnceAndStopsOnSecondUnauthorized()
    {
        int attempts = 0;
        int refreshes = 0;
        await Assert.ThrowsAsync<NotificationHttpException>(() => Run(_ =>
        {
            attempts++;
            throw new NotificationHttpException(HttpStatusCode.Unauthorized, null);
        }, refresh: _ => { refreshes++; return Task.CompletedTask; }));
        Assert.Equal(2, attempts);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task UnauthorizedRefreshCanRecoverWithoutIdempotencyConfirmation()
    {
        int attempts = 0;
        int refreshes = 0;
        Assert.True(await Run(_ =>
        {
            if (++attempts == 1) throw new NotificationHttpException(HttpStatusCode.Unauthorized, null);
            return Task.FromResult(true);
        }, refresh: _ => { refreshes++; return Task.CompletedTask; }));
        Assert.Equal(2, attempts);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task UnauthorizedAndTransientRetriesShareOneAttemptBudget()
    {
        int attempts = 0;
        int refreshes = 0;
        await Assert.ThrowsAsync<NotificationHttpException>(() => Run(_ =>
        {
            attempts++;
            throw new NotificationHttpException(attempts == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, null);
        }, refresh: _ => { refreshes++; return Task.CompletedTask; }));
        Assert.Equal(3, attempts);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task ApplicationFailureDoesNotCauseTransportRetries()
    {
        int attempts = 0;
        Assert.False(await Run(_ => { attempts++; return Task.FromResult(false); }, confirmed: true));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CallerCancellationDuringBackoffStopsRetries()
    {
        using var cancellation = new CancellationTokenSource();
        int attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NotificationDeliveryPolicy.ExecuteAsync(_ =>
        {
            attempts++;
            throw new NotificationHttpException(HttpStatusCode.TooManyRequests, null);
        }, _ => Task.CompletedTask, false, NullLogger.Instance, cancellation.Token, (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        }));
        Assert.Equal(1, attempts);
    }

    private static Task<bool> Run(Func<CancellationToken, Task<bool>> send, bool confirmed = false,
        List<TimeSpan>? waits = null, Func<CancellationToken, Task>? refresh = null)
        => NotificationDeliveryPolicy.ExecuteAsync(send, refresh ?? (_ => Task.CompletedTask), confirmed,
            NullLogger.Instance, default, (wait, _) => { waits?.Add(wait); return Task.CompletedTask; }, () => 0.5);
}