using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Service.AutoCall;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class AutoCallNoteDeliveryTests
{
    private static AutoCallNoteTask Completion() => new()
    {
        SystemID = 1, DeliveryId = Guid.NewGuid(), LeaseToken = Guid.NewGuid(), CallId = 42,
        Kind = "COMPLETED", OID = "original-oid", HistoryID = 81,
        OccurredAt = new DateTime(2026, 10, 20, 23, 50, 4, 123, DateTimeKind.Unspecified)
    };

    [Fact]
    public void CompletionContainsActualEndTimestampInUtc()
    {
        Assert.Equal("Robo Call completed. Call connected and ended. Call ended at 2026-10-20 23:50:04.123 UTC.",
            Assert.Single(AutoCallNoteFormatter.Format(Completion())));
    }

    [Fact]
    public void TranscriptPreservesEveryCharacterAndRepeatsTimestampOnBoundedUnicodeParts()
    {
        string text = "hello " + string.Concat(Enumerable.Repeat("\U0001F600", 400)) + new string('x', 700);
        var task = Completion() with
        {
            Kind = "TRANSCRIPT", RecordingId = 9,
            MessagesJSON = JsonSerializer.Serialize(new[]
            {
                new { Sentence = 0, AudioChannel = 1, StartTimeMilliseconds = 1250, EndTimeMilliseconds = 3601234, TranscriptText = text }
            })
        };
        var notes = AutoCallNoteFormatter.Format(task);
        string prefix = "Robo Call transcript (call 42, recording 9) [00:00:01.250 - 01:00:01.234] channel 1: ";
        Assert.True(notes.Count > 1);
        Assert.Contains(notes, note => note.Length == 250);
        Assert.All(notes, note =>
        {
            Assert.InRange(note.Length, prefix.Length + 1, 250);
            Assert.StartsWith(prefix, note);
            Assert.False(char.IsHighSurrogate(note[^1]));
        });
        Assert.Equal(text, string.Concat(notes.Select(note => note[prefix.Length..])));
    }

    [Fact]
    public void SeparateMessagesRetainTheirOwnTimestampsAndChannels()
    {
        var notes = AutoCallNoteFormatter.Format(Completion() with
        {
            Kind = "TRANSCRIPT", RecordingId = 9,
            MessagesJSON = JsonSerializer.Serialize(new[]
            {
                new { Sentence = 0, AudioChannel = 0, StartTimeMilliseconds = 1250, EndTimeMilliseconds = 2500, TranscriptText = "First message" },
                new { Sentence = 1, AudioChannel = 1, StartTimeMilliseconds = 3000, EndTimeMilliseconds = 4500, TranscriptText = "Second message" }
            })
        });
        Assert.Equal(2, notes.Count);
        Assert.EndsWith("[00:00:01.250 - 00:00:02.500] channel 0: First message", notes[0]);
        Assert.EndsWith("[00:00:03.000 - 00:00:04.500] channel 1: Second message", notes[1]);
    }

    [Fact]
    public void EmptyTranscriptHasExplicitNoSpeechNote()
    {
        var notes = AutoCallNoteFormatter.Format(Completion() with { Kind = "TRANSCRIPT", RecordingId = 9, MessagesJSON = "[]" });
        Assert.Contains("No speech detected.", Assert.Single(notes));
        Assert.Throws<InvalidOperationException>(() => AutoCallNoteFormatter.Format(Completion() with { Kind = "UNKNOWN" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryRoutesOriginalOidAndFinishesOnlyAfterTenantCommit(bool failPost)
    {
        var repository = new Repository { Item = Completion(), FailPost = failPost };
        var routing = new Routing();
        var service = new AutoCallNoteDeliveryService(repository, routing, NullLogger<AutoCallNoteDeliveryService>.Instance);
        Assert.True(await service.DeliverNextAsync(default));
        Assert.Equal("dna.client.ORIGINAL-OID", routing.Key);
        Assert.Equal("DEV1", repository.Platform);
        Assert.Equal(81, repository.Posted!.HistoryID);
        Assert.Equal(repository.Item.DeliveryId, repository.Posted.DeliveryId);
        Assert.Equal(new[] { "claim", "post", failPost ? "failed" : "delivered" }, repository.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitReleaseRoutesOriginalOidWithoutPostingNotes(bool failRelease)
    {
        var release = Completion() with { Kind = "RELEASE", CallId = null, QueueId = 7, ActiveAlarmID = 42 };
        var repository = new Repository { Item = release, FailPost = failRelease };
        var routing = new Routing();
        var service = new AutoCallNoteDeliveryService(repository, routing, NullLogger<AutoCallNoteDeliveryService>.Instance);
        Assert.True(await service.DeliverNextAsync(default));
        Assert.Equal("dna.client.ORIGINAL-OID", routing.Key);
        Assert.Equal("DEV1", repository.Platform);
        Assert.Same(release, repository.Released);
        Assert.Null(repository.Posted);
        Assert.Equal(new[] { "claim", "release", failRelease ? "failed" : "delivered" }, repository.Events);
    }

    [Fact]
    public async Task MissingRoutingFailsWithoutTenantFallbackAndEmptyQueueDoesNoWork()
    {
        var repository = new Repository { Item = Completion() };
        var service = new AutoCallNoteDeliveryService(repository, new Routing { Value = null }, NullLogger<AutoCallNoteDeliveryService>.Instance);
        Assert.True(await service.DeliverNextAsync(default));
        Assert.Null(repository.Posted);
        Assert.Equal(new[] { "claim", "failed" }, repository.Events);
        repository.Events.Clear();
        repository.Item = null;
        Assert.False(await service.DeliverNextAsync(default));
        Assert.Equal(new[] { "claim" }, repository.Events);
    }

    [Fact]
    public async Task UnconfirmedCentralAckRetriesTheSameDeliveryIdentity()
    {
        var repository = new Repository { Item = Completion(), FailAck = true };
        var service = new AutoCallNoteDeliveryService(repository, new Routing(), NullLogger<AutoCallNoteDeliveryService>.Instance);
        await service.DeliverNextAsync(default);
        Guid identity = repository.Posted!.DeliveryId;
        repository.FailAck = false;
        await service.DeliverNextAsync(default);
        Assert.Equal(identity, repository.Posted.DeliveryId);
        Assert.Equal(new[] { "claim", "post", "delivered", "failed", "claim", "post", "delivered" }, repository.Events);
    }

    [Fact]
    public async Task ShutdownDuringTenantPostingDoesNotAcknowledgeTheDelivery()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new Repository { Item = Completion(), OnPost = cancellation.Cancel };
        var service = new AutoCallNoteDeliveryService(repository, new Routing(), NullLogger<AutoCallNoteDeliveryService>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeliverNextAsync(cancellation.Token));
        Assert.Equal(new[] { "claim", "post" }, repository.Events);
    }

    private sealed class Routing : IAutoCallRoutingStore
    {
        public string? Key { get; private set; }
        public string? Value { get; init; } = "{\"Platform\":\"DEV1\"}";
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        { Key = key; return Task.FromResult(Value); }
    }

    private sealed class Repository : IAutoCallNoteRepository
    {
        public AutoCallNoteTask? Item { get; set; }
        public AutoCallNoteTask? Posted { get; private set; }
        public string? Platform { get; private set; }
        public List<string> Events { get; } = [];
        public bool FailPost { get; init; }
        public bool FailAck { get; set; }
        public Action? OnPost { get; init; }
        public Task<AutoCallNoteTask?> ClaimAsync(CancellationToken token)
        { Events.Add("claim"); token.ThrowIfCancellationRequested(); return Task.FromResult(Item); }
        public Task PostAsync(string platform, AutoCallNoteTask task, IReadOnlyList<string> notes, CancellationToken token)
        {
            Events.Add("post"); Platform = platform; Posted = task;
            OnPost?.Invoke();
            token.ThrowIfCancellationRequested();
            if (FailPost) throw new InvalidOperationException("offline tenant failure");
            return Task.CompletedTask;
        }
        public AutoCallNoteTask? Released { get; private set; }
        public Task ReleaseWaitAsync(string platform, AutoCallNoteTask task, CancellationToken token)
        {
            Events.Add("release"); Platform = platform; Released = task;
            token.ThrowIfCancellationRequested();
            if (FailPost) throw new InvalidOperationException("offline tenant failure");
            return Task.CompletedTask;
        }
        public Task FinishAsync(AutoCallNoteTask task, bool success, CancellationToken token)
        {
            Events.Add(success ? "delivered" : "failed");
            if (success && FailAck) throw new InvalidOperationException("central ack failed");
            return Task.CompletedTask;
        }
    }
}
