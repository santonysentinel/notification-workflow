using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.AutoCall;
using NotificationWorkflowService.Service.Transcription;
using Xunit;
using WorkflowRepository = NotificationWorkflowService.Repository.Repository;

namespace NotificationWorkflowService.Tests;

public sealed class DatabaseDiagnosticsTests
{
    [Theory]
    [InlineData(18456)]
    [InlineData(2812)]
    [InlineData(-2)]
    [InlineData(1205)]
    public async Task SqlFailuresLogSafeMetadataAndRethrowOriginalException(int number)
    {
        var logger = new Logs<WorkflowRepository>();
        var failure = NotificationHistoryRetryTests.CreateSqlException(number);
        var thrown = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            DatabaseDiagnostics.ExecuteAsync<int>(logger, "connstr", "test_procedure", default,
                () => Task.FromException<int>(failure)));

        Assert.Same(failure, thrown);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("connstr", entry.Properties["Database"]);
        Assert.Equal("test_procedure", entry.Properties["Operation"]);
        Assert.Equal(number, entry.Properties["SqlNumber"]);
        Assert.Equal("procedure", entry.Properties["SqlProcedure"]);
        Assert.Equal(1, entry.Properties["SqlLine"]);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("deadlock test message", entry.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Password=private-value;unsupported-key=private-value")]
    public void WorkflowPreparationFailuresAreLogged(string? connectionString)
    {
        var logger = new Logs<WorkflowRepository>();
        var repository = new WorkflowRepository(Configuration("connstr", connectionString), logger);
        Assert.ThrowsAny<Exception>(() => repository.PrepareReadAllProfiles());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("PrepareReadAllProfiles", entry.Properties["Operation"]);
        Assert.DoesNotContain("private-value", entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task WorkflowCommandFailuresAreLoggedWithoutChangingOperationLifetime()
    {
        var logger = new Logs<WorkflowRepository>();
        var repository = new WorkflowRepository(Configuration("connstr",
            "Server=offline.invalid;Database=unused;Integrated Security=true"), logger);
        await using var operation = repository.PrepareReadAllProfiles();
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ExecuteReaderAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation.ExecuteNonQueryAsync());
        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
            Assert.Equal("ActiveAlarms_ReadProfileItems", entry.Properties["Operation"]));
        await operation.CloseAsync();
    }

    [Fact]
    public async Task QueueFlowAndEnqueueFailuresIdentifyTheirOperations()
    {
        var logger = new Logs<AutoCallQueueRepository>();
        var repository = new AutoCallQueueRepository(new ConfigurationBuilder().Build(), logger);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReadFlowAsync(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.EnqueueAsync(
            new AutoCallRequest(1, "oid", 1, "{}", Guid.NewGuid())));
        Assert.Equal(new[] { "ReadCallFlowTemplate", "dbo.activealarms_call_InsertJob" },
            logger.Entries.Select(entry => entry.Properties["Operation"]));
    }

    [Fact]
    public async Task AutoCallQueriesAndWritesLogMissingDatabase()
    {
        var logger = new Logs<AutoCallRepository>();
        var repository = new AutoCallRepository(new ConfigurationBuilder().Build(), logger);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ClaimAsync(1, "worker", 5, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.FailPreparationAsync(new AutoCallJob(), "worker", default));
        Assert.Equal(new[] { "dbo.activealarms_call_GetNextSetOfJobs", "dbo.automatedcalls_FailPreparation" },
            logger.Entries.Select(entry => entry.Properties["Operation"]));
    }

    [Fact]
    public async Task PhoneLookupLogsMalformedConnectionWithoutLeakingItsContents()
    {
        var logger = new Logs<AutoCallPhoneRepository>();
        var repository = new AutoCallPhoneRepository(logger);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.ReadAsync("unsupported-key=private-value", "oid"));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("dbo.active_alarms_GetAllParticipantPhonenumbers", entry.Properties["Operation"]);
        Assert.DoesNotContain("private-value", entry.Message);
    }

    [Fact]
    public async Task NotificationReadsAndHistoryLogTheirFailures()
    {
        var logger = new Logs<NotificationRepository>();
        var repository = new NotificationRepository(Configuration("ClientDatabase",
            "unsupported-key=private-value"), logger);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReadAccountNotificationSettingsAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => repository.InsertNotificationHistoryAsync("1", "v", "note", 1));
        Assert.Equal(new[] { "ActiveAlarms_ReadAccountPushNotificationSettings", "ActiveAlarms_InsertIntoHistory" },
            logger.Entries.Select(entry => entry.Properties["Operation"]));
        Assert.All(logger.Entries, entry => Assert.DoesNotContain("private-value", entry.Message));
    }

    [Fact]
    public async Task TranscriptionLogsBeforePreservingItsSanitizedFailureContract()
    {
        var logger = new Logs<TranscriptionSubmissionRepository>();
        var repository = new TranscriptionSubmissionRepository(new ConfigurationBuilder().Build(), logger);
        var single = await Assert.ThrowsAsync<TranscriptionException>(() => repository.GetMetricsAsync(default));
        var many = await Assert.ThrowsAsync<TranscriptionException>(() => repository.RecoverAsync(1, default));
        Assert.Equal("TRANSCRIPTION_DATABASE_UNAVAILABLE", single.ErrorCode);
        Assert.Equal(single.ErrorCode, many.ErrorCode);
        Assert.Equal(new[] { "dbo.transcription_GetQueueMetrics", "dbo.transcription_RecoverTimeouts" },
            logger.Entries.Select(entry => entry.Properties["Operation"]));
    }

    [Fact]
    public async Task ExpectedCancellationIsNotLoggedButUnexpectedCancellationIs()
    {
        var logger = new Logs<WorkflowRepository>();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseDiagnostics.ExecuteAsync<int>(logger, "connstr", "test", cancellation.Token, () =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<int>(cancellation.Token);
            }));
        Assert.Empty(logger.Entries);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseDiagnostics.ExecuteAsync<int>(logger, "connstr", "test", default,
                () => Task.FromException<int>(new OperationCanceledException())));
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task PreCanceledOperationsDoNotExecuteOrLog()
    {
        var logger = new Logs<WorkflowRepository>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool executed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseDiagnostics.ExecuteAsync(logger, "connstr", "test", cancellation.Token, () =>
            {
                executed = true;
                return Task.CompletedTask;
            }));
        Assert.False(executed);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task SuccessfulOperationsPreserveResultsWithoutLoggingErrors()
    {
        var logger = new Logs<WorkflowRepository>();
        Assert.Equal(42, DatabaseDiagnostics.Execute(logger, "connstr", "test", () => 42));
        Assert.Equal(43, await DatabaseDiagnostics.ExecuteAsync(logger, "connstr", "test",
            default, () => Task.FromResult(43)));
        await DatabaseDiagnostics.ExecuteAsync(logger, "connstr", "test", default, () => Task.CompletedTask);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task StreamingReaderPreservesRowsAndLogsLateReadFailures()
    {
        var logger = new Logs<WorkflowRepository>();
        var table = new DataTable();
        table.Columns.Add("value", typeof(int));
        table.Rows.Add(42);
        var underlying = table.CreateDataReader();
        await using var reader = new LoggingDbDataReader(underlying, logger, "test_procedure");
        Assert.True(await reader.ReadAsync(default));
        Assert.Equal(42, reader.GetInt32(0));
        Assert.Equal("value", reader.GetName(0));
        Assert.False(await reader.ReadAsync(default));
        Assert.Empty(logger.Entries);
        underlying.Close();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(default));
        Assert.Throws<InvalidOperationException>(() => reader.NextResult());
        Assert.Equal(new[] { "test_procedure:Read", "test_procedure:NextResult" },
            logger.Entries.Select(entry => entry.Properties["Operation"]));
    }

    private static IConfiguration Configuration(string key, string? value)
        => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:" + key] = value }).Build();

    private sealed record Entry(LogLevel Level, string Message, Exception? Exception,
        Dictionary<string, object?> Properties);

    private sealed class Logs<T> : ILogger<T>
    {
        internal List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new(logLevel, formatter(state, exception), exception,
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
    }
}
