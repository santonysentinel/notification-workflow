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

    internal static IReadOnlyList<string> Format(AutoCallNoteTask task)
    {
        if (task.Kind == "COMPLETED")
            return [$"Robo Call completed. Call connected and ended. Call ended at {task.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} UTC."];
        if (task.Kind != "TRANSCRIPT" || task.RecordingId is null || task.MessagesJSON is null)
            throw new InvalidOperationException("Invalid transcript delivery payload.");
        var messages = JsonSerializer.Deserialize<List<Message>>(task.MessagesJSON)
            ?? throw new InvalidOperationException("Transcript messages must be an array.");
        var notes = new List<string>();
        foreach (var message in messages)
        {
            if (message.StartTimeMilliseconds < 0 || message.EndTimeMilliseconds < message.StartTimeMilliseconds || message.AudioChannel < 0)
                throw new InvalidOperationException("Invalid transcript message timestamps or channel.");
            if (string.IsNullOrWhiteSpace(message.TranscriptText)) continue;
            string prefix = FormattableString.Invariant(
                $"Robo Call transcript (call {task.CallId}, recording {task.RecordingId}) [{Offset(message.StartTimeMilliseconds)} - {Offset(message.EndTimeMilliseconds)}] channel {message.AudioChannel}: ");
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
            notes.Add(FormattableString.Invariant($"Robo Call recording transcript (call {task.CallId}, recording {task.RecordingId}): No speech detected."));
        return notes;
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

public sealed class AutoCallNoteWorker(AutoCallNoteDeliveryService delivery, ILogger<AutoCallNoteWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Auto-call tenant note worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await delivery.DeliverNextAsync(stoppingToken).ConfigureAwait(false)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Auto-call tenant note worker cycle failed ({FailureType}); retrying", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
