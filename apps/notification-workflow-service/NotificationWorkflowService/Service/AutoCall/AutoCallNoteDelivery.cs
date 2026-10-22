using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Data;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed record AutoCallNoteTask
{
    public long SystemID { get; init; }
    public Guid DeliveryId { get; init; }
    public Guid LeaseToken { get; init; }
    public int? CallId { get; init; }
    public string Kind { get; init; } = "";
    public string OID { get; init; } = "";
    public int HistoryID { get; init; }
    public DateTime OccurredAt { get; init; }
    public int? RecordingId { get; init; }
    public string? MessagesJSON { get; init; }
    public int? QueueId { get; init; }
    public int? ActiveAlarmID { get; init; }
    public string? RecordingSource { get; init; }
    public string? PhoneType { get; init; }
    public string? MessageStepId { get; init; }
}

public interface IAutoCallNoteRepository
{
    Task<AutoCallNoteTask?> ClaimAsync(CancellationToken token);
    Task PostAsync(string platform, AutoCallNoteTask task, IReadOnlyList<string> notes, CancellationToken token);
    Task ReleaseWaitAsync(string platform, AutoCallNoteTask task, CancellationToken token);
    Task FinishAsync(AutoCallNoteTask task, bool success, CancellationToken token);
}

public sealed class AutoCallNoteRepository(IConfiguration configuration) : IAutoCallNoteRepository
{
    private SqlConnection Connection(string platform) => new(configuration.GetConnectionString(platform)
        ?? throw new InvalidOperationException($"ConnectionStrings:{platform} is required for note delivery."));

    public async Task<AutoCallNoteTask?> ClaimAsync(CancellationToken token)
    {
        await using var connection = Connection("AutoCallDB");
        return await connection.QuerySingleOrDefaultAsync<AutoCallNoteTask>(new CommandDefinition(
            "dbo.automatedcalls_ClaimNote", commandType: CommandType.StoredProcedure, commandTimeout: 15,
            cancellationToken: token)).ConfigureAwait(false);
    }

    public async Task PostAsync(string platform, AutoCallNoteTask task, IReadOnlyList<string> notes, CancellationToken token)
    {
        // Never substitute AutoCallDB or the parser's tenant for failed OID routing.
        await using var connection = Connection(platform);
        await connection.ExecuteAsync(new CommandDefinition("dbo.activealarms_PostAutoCallNotes",
            new { task.DeliveryId, task.HistoryID, task.OID, NotesJSON = JsonSerializer.Serialize(notes) },
            commandType: CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: token)).ConfigureAwait(false);
    }

    public async Task ReleaseWaitAsync(string platform, AutoCallNoteTask task, CancellationToken token)
    {
        if (task.ActiveAlarmID is not > 0 || task.QueueId is not > 0)
            throw new InvalidOperationException("Invalid Robo Call wait release payload.");
        await using var connection = Connection(platform);
        await connection.ExecuteAsync(new CommandDefinition("dbo.ActiveAlarms_ReleaseAutoCallWait",
            new { task.ActiveAlarmID, task.QueueId, task.OID },
            commandType: CommandType.StoredProcedure, commandTimeout: 30, cancellationToken: token)).ConfigureAwait(false);
    }

    public async Task FinishAsync(AutoCallNoteTask task, bool success, CancellationToken token)
    {
        await using var connection = Connection("AutoCallDB");
        await connection.ExecuteAsync(new CommandDefinition("dbo.automatedcalls_FinishNote",
            new { Id = task.SystemID, task.LeaseToken, Success = success },
            commandType: CommandType.StoredProcedure, commandTimeout: 15, cancellationToken: token)).ConfigureAwait(false);
    }
}

internal static class AutoCallNoteFormatter
{
    private sealed record Message(int Sentence, int AudioChannel, int StartTimeMilliseconds, int EndTimeMilliseconds, string? TranscriptText);

    private sealed record DeviceOutcome(string? PhoneType, string? Status, string? AnsweredBy, bool MessageLeft, string? NextPhoneType);

    internal static IReadOnlyList<string> Format(AutoCallNoteTask task)
    {
        if (task.Kind == "COMPLETED")
        {
            string? phoneType = ReadPhoneType(task.MessagesJSON);
            string target = phoneType is null ? "" : " to " + phoneType;
            return [$"Robo Call{target} completed. Call connected and ended. Call ended at {task.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} UTC."];
        }
        if (task.Kind == "DEVICE") return [FormatDevice(task)];
        if (task.Kind != "TRANSCRIPT" || task.RecordingId is null || task.MessagesJSON is null)
            throw new InvalidOperationException("Invalid transcript delivery payload.");
        var messages = JsonSerializer.Deserialize<List<Message>>(task.MessagesJSON)
            ?? throw new InvalidOperationException("Transcript messages must be an array.");
        var notes = new List<string>();
        string? messageLabel = task.RecordingSource == "RecordVerb" ? MessageLabel(task) : null;
        foreach (var message in messages)
        {
            if (message.StartTimeMilliseconds < 0 || message.EndTimeMilliseconds < message.StartTimeMilliseconds || message.AudioChannel < 0)
                throw new InvalidOperationException("Invalid transcript message timestamps or channel.");
            if (string.IsNullOrWhiteSpace(message.TranscriptText)) continue;
            string range = $"[{Offset(message.StartTimeMilliseconds)} - {Offset(message.EndTimeMilliseconds)}]";
            string prefix = messageLabel is not null
                ? $"{messageLabel} {range}: "
                : FormattableString.Invariant($"Robo Call transcript (call {task.CallId}, recording {task.RecordingId}) {range} channel {message.AudioChannel}: ");
            string text = message.TranscriptText;
            for (int start = 0; start < text.Length;)
            {
                int count = Math.Min(250 - prefix.Length, text.Length - start);
                if (count <= 0) throw new InvalidOperationException("Transcript note prefix exceeds the history limit.");
                if (start + count < text.Length && char.IsHighSurrogate(text[start + count - 1]) && char.IsLowSurrogate(text[start + count]))
                    count--;
                notes.Add(prefix + text.Substring(start, count));
                start += count;
            }
        }
        if (notes.Count == 0)
            notes.Add(messageLabel is not null
                ? messageLabel + ": No speech detected."
                : FormattableString.Invariant($"Robo Call recording transcript (call {task.CallId}, recording {task.RecordingId}): No speech detected."));
        return notes;
    }

    /// <summary>Labels a record-step message, e.g. "Robo Call message from participant (CellPhone, step leave-message)".</summary>
    private static string MessageLabel(AutoCallNoteTask task)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(task.PhoneType)) parts.Add(task.PhoneType.Trim());
        if (!string.IsNullOrWhiteSpace(task.MessageStepId))
        {
            string step = task.MessageStepId.Trim();
            parts.Add("step " + (step.Length > 60 ? step[..60] + "..." : step));
        }
        return parts.Count == 0 ? "Robo Call message from participant" : $"Robo Call message from participant ({string.Join(", ", parts)})";
    }

    private static string? ReadPhoneType(string? messagesJson)
    {
        if (string.IsNullOrWhiteSpace(messagesJson)) return null;
        using var document = JsonDocument.Parse(messagesJson);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("phoneType", out var type) &&
            type.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(type.GetString()) ? type.GetString() : null;
    }

    private static string FormatDevice(AutoCallNoteTask task)
    {
        var outcome = task.MessagesJSON is null ? null : JsonSerializer.Deserialize<DeviceOutcome>(task.MessagesJSON,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (outcome is null || string.IsNullOrWhiteSpace(outcome.PhoneType) || string.IsNullOrWhiteSpace(outcome.Status))
            throw new InvalidOperationException("Invalid call device delivery payload.");
        string result = outcome switch
        {
            { Status: "no-phone-number" } => "skipped (no phone number on file)",
            { AnsweredBy: "fax" } => "reached a fax machine",
            { AnsweredBy: { } machine } when machine.StartsWith("machine_", StringComparison.Ordinal) =>
                outcome.MessageLeft ? "reached voicemail; message left" : "reached an answering machine; no message left",
            _ => $"failed ({outcome.Status})"
        };
        string next = string.IsNullOrWhiteSpace(outcome.NextPhoneType) ? "no more devices" : "trying " + outcome.NextPhoneType;
        return $"Robo Call to {outcome.PhoneType} {result}; {next}.";
    }

    private static string Offset(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return FormattableString.Invariant($"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }
}

public sealed class AutoCallNoteDeliveryService(IAutoCallNoteRepository repository, IAutoCallRoutingStore routing,
    ILogger<AutoCallNoteDeliveryService> logger)
{
    public async Task<bool> DeliverNextAsync(CancellationToken token)
    {
        var task = await repository.ClaimAsync(token).ConfigureAwait(false);
        if (task is null) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            AutoCallContactService.ValidateOid(task.OID);
            string value = await routing.GetAsync($"dna.client.{task.OID.ToUpperInvariant()}", deadline.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Tenant routing was not found.");
            string platform = AutoCallContactService.ParsePlatform(value);
            if (task.Kind == "RELEASE")
                await repository.ReleaseWaitAsync(platform, task, deadline.Token).ConfigureAwait(false);
            else
                await repository.PostAsync(platform, task, AutoCallNoteFormatter.Format(task), deadline.Token).ConfigureAwait(false);
            await repository.FinishAsync(task, true, deadline.Token).ConfigureAwait(false);
            logger.LogInformation("Auto-call tenant {Kind} task {DeliveryId} delivered for call {CallId}", task.Kind, task.DeliveryId, task.CallId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Log identifiers and exception type, never note text, OID or provider response.
            logger.LogError("Auto-call tenant note {DeliveryId} for call {CallId} failed ({FailureType}, SQL {SqlErrorNumber}); delivery remains retryable or dead-lettered",
                task.DeliveryId, task.CallId, exception.GetType().Name, (exception as SqlException)?.Number);
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
            cleanup.CancelAfter(TimeSpan.FromSeconds(15));
            await repository.FinishAsync(task, false, cleanup.Token).ConfigureAwait(false);
        }
        return true;
    }
}

public sealed class AutoCallNoteWorker(AutoCallNoteDeliveryService delivery, ILogger<AutoCallNoteWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long? lastHeartbeat;
    private long successfulPolls;
    private long taskAttempts;
    private long pollFailures;
    private DateTimeOffset? lastSuccessfulPoll;

    internal async Task<bool> RunCycleAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool hadTask = false;
        try
        {
            hadTask = await delivery.DeliverNextAsync(token).ConfigureAwait(false);
            successfulPolls++;
            if (hadTask) taskAttempts++;
            lastSuccessfulPoll = clock.GetUtcNow();
            logger.LogDebug("Auto-call tenant note worker poll completed; task attempted {HadTask}", hadTask);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            pollFailures++;
            logger.LogError("Auto-call tenant note worker cycle failed ({FailureType}, SQL {SqlErrorNumber}); retrying",
                exception.GetType().Name, (exception as SqlException)?.Number);
        }
        long now = clock.GetTimestamp();
        if (lastHeartbeat is null || clock.GetElapsedTime(lastHeartbeat.Value, now) >= TimeSpan.FromSeconds(60))
        {
            logger.LogInformation(
                "Auto-call tenant note worker heartbeat; successful polls {SuccessfulPolls}, task attempts {TaskAttempts}, poll failures {PollFailures}, last successful poll {LastSuccessfulPollUtc}",
                successfulPolls, taskAttempts, pollFailures, lastSuccessfulPoll);
            lastHeartbeat = now;
        }
        return hadTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Auto-call tenant note worker started; idle poll 5 seconds, heartbeat 60 seconds");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await RunCycleAsync(stoppingToken).ConfigureAwait(false)) continue;
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            logger.LogInformation(
                "Auto-call tenant note worker stopped; successful polls {SuccessfulPolls}, task attempts {TaskAttempts}, poll failures {PollFailures}",
                successfulPolls, taskAttempts, pollFailures);
        }
    }
}
