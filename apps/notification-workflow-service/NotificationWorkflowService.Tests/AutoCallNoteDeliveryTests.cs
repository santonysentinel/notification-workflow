using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
        Assert.Equal("Robo Call (Call 42) completed. Call connected and ended. Call ended at 2026-10-20 23:50:04.123 UTC.",
            Assert.Single(AutoCallNoteFormatter.Format(Completion())));
    }

    [Fact]
    public void CompletionNamesTheDialledDevice()
    {
        Assert.Equal("Robo Call (Call 42) to HomePhone completed. Call connected and ended. Call ended at 2026-10-20 23:50:04.123 UTC.",
            Assert.Single(AutoCallNoteFormatter.Format(Completion() with { MessagesJSON = "{\"phoneType\":\"HomePhone\"}" })));
    }

    [Theory]
    [InlineData("{\"phoneType\":\"CellPhone\",\"status\":\"no-answer\",\"answeredBy\":null,\"messageLeft\":false,\"nextPhoneType\":\"HomePhone\"}",
        "Robo Call to CellPhone failed (no-answer); trying HomePhone.")]
    [InlineData("{\"phoneType\":\"HomePhone\",\"status\":\"busy\",\"answeredBy\":null,\"messageLeft\":false,\"nextPhoneType\":null}",
        "Robo Call to HomePhone failed (busy); no more devices.")]
    [InlineData("{\"phoneType\":\"CellPhone\",\"status\":\"completed\",\"answeredBy\":\"machine_end_beep\",\"messageLeft\":true,\"nextPhoneType\":\"Custom\"}",
        "Robo Call to CellPhone reached voicemail; message left; trying Custom.")]
    [InlineData("{\"phoneType\":\"CellPhone\",\"status\":\"completed\",\"answeredBy\":\"machine_start\",\"messageLeft\":false,\"nextPhoneType\":null}",
        "Robo Call to CellPhone reached an answering machine; no message left; no more devices.")]
    [InlineData("{\"phoneType\":\"WorkPhone\",\"status\":\"completed\",\"answeredBy\":\"fax\",\"messageLeft\":false,\"nextPhoneType\":null}",
        "Robo Call to WorkPhone reached a fax machine; no more devices.")]
    [InlineData("{\"phoneType\":\"WorkPhone\",\"status\":\"no-phone-number\",\"answeredBy\":null,\"messageLeft\":false,\"nextPhoneType\":\"CellPhone\"}",
        "Robo Call to WorkPhone skipped (no phone number on file); trying CellPhone.")]
    public void DeviceOutcomeNamesTheDeviceAndWhatHappensNext(string messages, string expected)
        => Assert.Equal(expected, Assert.Single(AutoCallNoteFormatter.Format(Completion() with { Kind = "DEVICE", CallId = null, MessagesJSON = messages })));

    [Fact]
    public void DeviceOutcomeWithoutAPayloadIsRejected()
        => Assert.Throws<InvalidOperationException>(() => AutoCallNoteFormatter.Format(Completion() with { Kind = "DEVICE" }));

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
        Assert.True(notes.Count > 1);
        Assert.Contains(notes, note => note.Length == 250);
        string restored = "";
        for (int i = 0; i < notes.Count; i++)
        {
            string note = notes[i];
            string prefix = $"Participant Call transcript (Call 42) {i + 1}/{notes.Count} [00:01-60:01] : ";
            Assert.InRange(note.Length, prefix.Length + 1, 250);
            Assert.StartsWith(prefix, note);
            Assert.False(char.IsHighSurrogate(note[^1]));
            restored += note[prefix.Length..];
        }
        Assert.Equal(text, restored);
    }

    [Fact]
    public void SeparateMessagesRetainTheirOwnTimestampsAndAreNumberedWithoutChannels()
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
        Assert.Equal("Participant Call transcript (Call 42) 1/2 [00:01-00:02] : First message", notes[0]);
        Assert.Equal("Participant Call transcript (Call 42) 2/2 [00:03-00:04] : Second message", notes[1]);
    }

    [Fact]
    public void EmptyTranscriptHasExplicitNoSpeechNote()
    {
        var notes = AutoCallNoteFormatter.Format(Completion() with { Kind = "TRANSCRIPT", RecordingId = 9, MessagesJSON = "[]" });
        Assert.Equal("Participant Call transcript (Call 42) 1/1 : No speech detected.", Assert.Single(notes));
        Assert.Throws<InvalidOperationException>(() => AutoCallNoteFormatter.Format(Completion() with { Kind = "UNKNOWN" }));
    }

    [Theory]
    [InlineData("CellPhone", "leave-message")]
    [InlineData(null, "leave-message")]
    [InlineData("CellPhone", null)]
    [InlineData(null, null)]
    public void RecordStepMessageUsesResponseLabel(string? phoneType, string? stepId)
    {
        var task = Completion() with { Kind = "TRANSCRIPT", RecordingId = 9, RecordingSource = "RecordVerb", PhoneType = phoneType, MessageStepId = stepId };
        var notes = AutoCallNoteFormatter.Format(task with
        {
            MessagesJSON = JsonSerializer.Serialize(new[]
            {
                new { Sentence = 0, AudioChannel = 0, StartTimeMilliseconds = 1250, EndTimeMilliseconds = 6900, TranscriptText = "I'll be there in 10 minutes" }
            })
        });
        Assert.Equal("Participant Response (Call 42) 1/1 [00:01-00:06] : I'll be there in 10 minutes", Assert.Single(notes));
        Assert.Equal("Participant Response (Call 42) 1/1 : No speech detected.", Assert.Single(AutoCallNoteFormatter.Format(task with { MessagesJSON = "[]" })));
    }

    [Fact]
    public void RecordStepMessageOmitsStepIdsAndSplitsWithinTheLimit()
    {
        string text = new string('x', 600);
        var notes = AutoCallNoteFormatter.Format(Completion() with
        {
            Kind = "TRANSCRIPT", RecordingId = 9, RecordingSource = "RecordVerb", PhoneType = "CellPhone", MessageStepId = new string('s', 200),
            MessagesJSON = JsonSerializer.Serialize(new[] { new { Sentence = 0, AudioChannel = 0, StartTimeMilliseconds = 0, EndTimeMilliseconds = 1000, TranscriptText = text } })
        });
        string restored = "";
        for (int i = 0; i < notes.Count; i++)
        {
            string prefix = $"Participant Response (Call 42) {i + 1}/{notes.Count} [00:00-00:01] : ";
            Assert.StartsWith(prefix, notes[i]);
            Assert.InRange(notes[i].Length, prefix.Length + 1, 250);
            restored += notes[i][prefix.Length..];
        }
        Assert.Equal(text, restored);
    }

    [Fact]
    public void WholeCallTranscriptKeepsItsFormatEvenWithDeviceAndStep()
    {
        var notes = AutoCallNoteFormatter.Format(Completion() with
        {
            Kind = "TRANSCRIPT", RecordingId = 9, RecordingSource = "OutboundAPI", PhoneType = "CellPhone",
            MessagesJSON = JsonSerializer.Serialize(new[] { new { Sentence = 0, AudioChannel = 1, StartTimeMilliseconds = 0, EndTimeMilliseconds = 1000, TranscriptText = "Hi" } })
        });
        Assert.Equal("Participant Call transcript (Call 42) 1/1 [00:00-00:01] : Hi", Assert.Single(notes));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(99)]
    [InlineData(100)]
    public void ChunkTotalsAccountForNumberingDigitBoundaries(int expectedChunks)
    {
        int length = Enumerable.Range(1, expectedChunks).Sum(index =>
            250 - $"Participant Call transcript (Call 42) {index}/{expectedChunks} [00:00-00:01] : ".Length);
        string text = new('x', length);
        var notes = AutoCallNoteFormatter.Format(Completion() with
        {
            Kind = "TRANSCRIPT", RecordingId = 9,
            MessagesJSON = JsonSerializer.Serialize(new[] { new { Sentence = 0, AudioChannel = 0, StartTimeMilliseconds = 0, EndTimeMilliseconds = 1000, TranscriptText = text } })
        });
        Assert.Equal(expectedChunks, notes.Count);
        Assert.All(notes, note => Assert.InRange(note.Length, 1, 250));
        for (int i = 0; i < notes.Count; i++)
            Assert.StartsWith($"Participant Call transcript (Call 42) {i + 1}/{notes.Count} ", notes[i]);
        Assert.Equal(text, string.Concat(notes.Select(note => note[(note.IndexOf(" : ", StringComparison.Ordinal) + 3)..])));
    }

    [Fact]
    public void BlankMessagesDoNotCountAsChunksAndMissingCallIdIsRejected()
    {
        var task = Completion() with
        {
            Kind = "TRANSCRIPT", RecordingId = 9,
            MessagesJSON = """[{"Sentence":0,"AudioChannel":0,"StartTimeMilliseconds":0,"EndTimeMilliseconds":560,"TranscriptText":"hello"},{"Sentence":1,"AudioChannel":0,"StartTimeMilliseconds":1000,"EndTimeMilliseconds":2000,"TranscriptText":" "}]"""
        };
        Assert.Equal("Participant Call transcript (Call 42) 1/1 [00:00-00:00] : hello", Assert.Single(AutoCallNoteFormatter.Format(task)));
        Assert.Throws<InvalidOperationException>(() => AutoCallNoteFormatter.Format(task with { CallId = null }));
    }

    [Fact]
    public void DeviceOutcomeIncludesCallIdWhenAnAttemptExists()
        => Assert.Equal("Robo Call (Call 42) to CellPhone failed (busy); no more devices.",
            Assert.Single(AutoCallNoteFormatter.Format(Completion() with
            {
                Kind = "DEVICE", MessagesJSON = """{"phoneType":"CellPhone","status":"busy"}"""
            })));

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerHeartbeatsWhileIdleOrBusyWithoutLoggingEveryPollAtInformation(bool busy)
    {
        var repository = new Repository { Item = busy ? Completion() : null };
        var service = new AutoCallNoteDeliveryService(repository, new Routing(), NullLogger<AutoCallNoteDeliveryService>.Instance);
        var logs = new AutoCallDiagnosticsTests.Logs<AutoCallNoteWorker>();
        var clock = new FakeTimeProvider();
        using var worker = new AutoCallNoteWorker(service, logs, clock);
        Assert.Equal(busy, await worker.RunCycleAsync(default));
        Assert.Single(logs.Entries.Where(entry => entry.Level == LogLevel.Information));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(busy, await worker.RunCycleAsync(default));
        Assert.Single(logs.Entries.Where(entry => entry.Level == LogLevel.Information));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(busy, await worker.RunCycleAsync(default));
        var heartbeat = logs.Entries.Last(entry => entry.Level == LogLevel.Information);
        Assert.Equal(2, logs.Entries.Count(entry => entry.Level == LogLevel.Information));
        Assert.Equal(3L, heartbeat.Properties["SuccessfulPolls"]);
        Assert.Equal(busy ? 3L : 0L, heartbeat.Properties["TaskAttempts"]);
        Assert.Equal(0L, heartbeat.Properties["PollFailures"]);
        Assert.Equal(clock.GetUtcNow(), heartbeat.Properties["LastSuccessfulPollUtc"]);
        Assert.DoesNotContain("original-oid", heartbeat.Message);
    }

    [Fact]
    public async Task WorkerHeartbeatDistinguishesFailedPollsAndRecovery()
    {
        var repository = new Repository { FailClaim = true };
        var service = new AutoCallNoteDeliveryService(repository, new Routing(), NullLogger<AutoCallNoteDeliveryService>.Instance);
        var logs = new AutoCallDiagnosticsTests.Logs<AutoCallNoteWorker>();
        var clock = new FakeTimeProvider();
        using var worker = new AutoCallNoteWorker(service, logs, clock);
        Assert.False(await worker.RunCycleAsync(default));
        var heartbeat = logs.Entries.Last();
        Assert.Equal(0L, heartbeat.Properties["SuccessfulPolls"]);
        Assert.Equal(1L, heartbeat.Properties["PollFailures"]);
        Assert.Null(heartbeat.Properties["LastSuccessfulPollUtc"]);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.All(logs.Entries, entry => Assert.DoesNotContain("private failure", entry.Message));

        repository.FailClaim = false;
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.False(await worker.RunCycleAsync(default));
        heartbeat = logs.Entries.Last();
        Assert.Equal(1L, heartbeat.Properties["SuccessfulPolls"]);
        Assert.Equal(1L, heartbeat.Properties["PollFailures"]);
        Assert.Equal(clock.GetUtcNow(), heartbeat.Properties["LastSuccessfulPollUtc"]);
    }

    [Fact]
    public async Task WorkerLogsStartupAndShutdownWithoutTreatingCancellationAsFailure()
    {
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Repository { OnClaim = () => polled.TrySetResult() };
        var service = new AutoCallNoteDeliveryService(repository, new Routing(), NullLogger<AutoCallNoteDeliveryService>.Instance);
        var logs = new AutoCallDiagnosticsTests.Logs<AutoCallNoteWorker>();
        using var worker = new AutoCallNoteWorker(service, logs, new FakeTimeProvider());
        await worker.StartAsync(default);
        await polled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("worker started"));
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("worker stopped"));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Error);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunCycleAsync(cancellation.Token));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Error);
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
        public bool FailClaim { get; set; }
        public Action? OnClaim { get; init; }
        public Action? OnPost { get; init; }
        public Task<AutoCallNoteTask?> ClaimAsync(CancellationToken token)
        {
            Events.Add("claim");
            OnClaim?.Invoke();
            token.ThrowIfCancellationRequested();
            if (FailClaim) throw new InvalidOperationException("private failure");
            return Task.FromResult(Item);
        }
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
