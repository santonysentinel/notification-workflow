using System.Collections;
using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using ActiveAlarmsParser.Service.NotificationService;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ActiveAlarmsParser.Service.NotificationService.NotificationServiceData;
using Sender = ActiveAlarmsParser.Service.NotificationService.NotificationService;

namespace NotificationWorkflowService.Tests;

public class NotificationAsyncCancellationTests
{
    [Theory]
    [InlineData("canceled-task")]
    [InlineData("ignored-token")]
    [InlineData("other-failure")]
    public async Task CanceledRefreshPreservesOldSnapshotAndTimersAndCanRetryImmediately(string outcome)
    {
        var clock = new ManualClock();
        var original = new Dictionary<string, string> { ["old"] = "value" };
        var replacement = new Dictionary<string, string> { ["new"] = "value" };
        using var cancellation = new CancellationTokenSource();
        var entered = Gate();
        var release = Gate();
        int loads = 0;
        var cache = Create(async token =>
        {
            switch (++loads)
            {
                case 1: return original;
                case 2:
                    Assert.Equal(cancellation.Token, token);
                    entered.SetResult();
                    await release.Task;
                    clock.Advance(TimeSpan.FromMinutes(2));
                    if (outcome == "canceled-task") token.ThrowIfCancellationRequested();
                    if (outcome == "other-failure") throw new DataException("failure after cancellation");
                    return replacement; // A loader ignoring cancellation must not publish either.
                default: return replacement;
            }
        }, clock);
        Assert.Same(original, await cache.GetSnapshotAsync());
        var before = State(cache);
        clock.Advance(TimeSpan.FromMinutes(5));
        var refresh = cache.GetSnapshotAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        release.SetResult();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(before, State(cache));
        Assert.Same(original, Field(cache, "snapshot"));
        Assert.Single(original);
        Assert.False(original.ContainsKey("new"));
        Assert.Same(replacement, await cache.GetSnapshotAsync());
        Assert.Equal(3, loads); // Neither a success lifetime nor a failure cooldown was restarted.
    }

    [Fact]
    public async Task CanceledInitialLoadDoesNotStartFailureCooldown()
    {
        var clock = new ManualClock();
        using var cancellation = new CancellationTokenSource();
        int loads = 0;
        var empty = new Dictionary<string, string>();
        var cache = new SettingsSnapshotCache<Dictionary<string, string>>(token =>
        {
            if (++loads == 1)
            {
                cancellation.Cancel();
                return Task.FromCanceled<Dictionary<string, string>>(token);
            }
            return Task.FromResult(new Dictionary<string, string> { ["ready"] = "yes" });
        }, empty, NullLogger.Instance, "cancellation", clock);
        var before = State(cache);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetSnapshotAsync(cancellation.Token));
        Assert.Same(empty, Field(cache, "snapshot"));
        Assert.Equal(before, State(cache));
        Assert.Equal("yes", (await cache.GetSnapshotAsync())["ready"]);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task SemaphoreWaitingCancellationDoesNotLoadOrDisruptOwnerAndFollowers()
    {
        var release = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new Dictionary<string, string> { ["complete"] = "yes" };
        int loads = 0;
        var cache = Create(token =>
        {
            Assert.Equal(CancellationToken.None, token);
            loads++;
            return release.Task;
        }, new ManualClock());
        var owner = cache.GetSnapshotAsync();
        var followers = Enumerable.Range(0, 20).Select(_ => cache.GetSnapshotAsync()).ToArray();
        using var cancellation = new CancellationTokenSource();
        var waiter = cache.GetSnapshotAsync(cancellation.Token);
        try
        {
            Assert.False(owner.IsCompleted);
            Assert.False(waiter.IsCompleted);
            Assert.All(followers, follower => Assert.False(follower.IsCompleted));
            cancellation.Cancel();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(1, loads);
            Assert.False(owner.IsCompleted);
        }
        finally
        {
            release.TrySetResult(original);
        }
        Assert.Same(original, await owner);
        Assert.All(await Task.WhenAll(followers), snapshot => Assert.Same(original, snapshot));
        Assert.Same(original, await cache.GetSnapshotAsync());
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task CancellationDuringFailureCooldownPreservesOldSnapshotAndRetryDeadline()
    {
        var clock = new ManualClock();
        var original = new Dictionary<string, string> { ["old"] = "value" };
        int loads = 0;
        var cache = Create(_ => ++loads switch
        {
            1 => Task.FromResult(original),
            2 => Task.FromException<Dictionary<string, string>>(new DataException("refresh failed")),
            _ => Task.FromResult(new Dictionary<string, string> { ["recovered"] = "yes" })
        }, clock);
        await cache.GetSnapshotAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Same(original, await cache.GetSnapshotAsync());
        var before = State(cache);
        clock.Advance(TimeSpan.FromSeconds(29));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetSnapshotAsync(cancellation.Token));
        Assert.Equal(before, State(cache));
        Assert.Same(original, await cache.GetSnapshotAsync());
        Assert.Equal(2, loads);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("yes", (await cache.GetSnapshotAsync())["recovered"]);
        Assert.Equal(3, loads);
    }

    [Theory]
    [InlineData("push", "canceled-task")]
    [InlineData("push", "ignored-token")]
    [InlineData("push", "other-failure")]
    [InlineData("reminder", "canceled-task")]
    [InlineData("reminder", "ignored-token")]
    [InlineData("reminder", "other-failure")]
    public async Task HistoryCancellationAfterAcknowledgementPropagatesWithoutRequeueOrReview(string type, string outcome)
    {
        var queue = new NotificationArray();
        queue.BulkAdd(new ArrayList
        {
            new Notification { oid = "o", victimid = "v", type = type, activityid = "1-platform-v" },
            new Notification { oid = "o", victimid = "v", type = type, activityid = "2-platform-v" }
        });
        var submitted = queue.GetSnapshot();
        var response = new NotificationServiceResponse
        {
            data = submitted.Values.Select(item => new NotificationServiceDataResponse
            {
                oid = item.oid, victimid = item.victimid, type = item.type,
                activityid = item.activityid, isSuccessful = true
            }).ToList()
        };
        using var cancellation = new CancellationTokenSource();
        var entered = Gate();
        var release = Gate();
        var repository = new FakeNotificationRepository
        {
            InsertHistory = async token =>
            {
                Assert.Equal(cancellation.Token, token);
                Assert.Equal(0, queue.Count()); // All confirmed deliveries are removed before history I/O.
                entered.SetResult();
                await release.Task;
                if (outcome == "canceled-task") token.ThrowIfCancellationRequested();
                if (outcome == "other-failure") throw new DataException("history failure after cancellation");
            }
        };
        // Instance-only seam: never initialize or reset static authentication/settings.
        var sender = (Sender)RuntimeHelpers.GetUninitializedObject(typeof(Sender));
        void Set(string name, object value) => typeof(Sender).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, value);
        Set("notifications", queue);
        Set("logger", NullLogger<Sender>.Instance);
        Set("repository", repository);
        var handler = typeof(Sender).GetMethod("HandlePostNotificationResponseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handling = (Task<bool>)handler.Invoke(sender, [response, submitted, cancellation.Token])!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        release.SetResult();
        // Async reflection returns a faulted task, not a TargetInvocationException wrapper.
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handling);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(cancellation.Token, repository.HistoryToken);
        Assert.Single(repository.History);
        Assert.Equal(0, queue.Count());
        Assert.False(sender.RequiresDeliveryReview);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SettingsSnapshotCache<Dictionary<string, string>> Create(
        Func<CancellationToken, Task<Dictionary<string, string>>> load, TimeProvider clock)
        => new(load, new(), NullLogger.Instance, "cancellation", clock);

    private static object Field(object target, string name) => target.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static (long Success, long Failure, bool Loaded, bool Failed) State(object cache)
        => ((long)Field(cache, "lastSuccessfulLoad"), (long)Field(cache, "lastFailure"),
            (bool)Field(cache, "hasSuccessfulLoad"), (bool)Field(cache, "failed"));

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
}