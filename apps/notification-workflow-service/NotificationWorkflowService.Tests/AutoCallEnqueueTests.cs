using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.AutoCall;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class AutoCallEnqueueTests
{
    private static ActiveAlarm Alarm() => new()
    { SystemID = 42, HistoryID = 81, ClientID = "oid-42", ProfileID = 7, StateNo = 3, CurrentLoopNumber = 1, FlowId = 12 };
    private static AutoCallFlow Flow(string content = "{{OffenderName}} {{alarmname}} {{callId}} {{feedbackBaseUrl}} {{executionId}} {{recipient}}")
        => new(12, "{\"schemaVersion\":1}", "<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say>" + content + "</Say></Response></Step></CallFlow>");
    private static AutoCallRequest Capture(ActiveAlarm? alarm = null, string platform = "DEV1", AutoCallFlow? flow = null,
        IReadOnlyDictionary<string, JsonElement>? details = null)
        => AutoCallEnqueueService.CaptureRequest(alarm ?? Alarm(), platform, flow ?? Flow(), details ?? ParticipantCurrentDetailsTests.Values());

    private sealed class Repository : IAutoCallQueueRepository
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public AutoCallFlow Template { get; set; } = Flow();
        public AutoCallRequest? Request { get; private set; }
        public Action? AfterRead { get; set; }
        public CancellationToken ReadToken { get; private set; }
        public CancellationToken WriteToken { get; private set; }
        public Task<AutoCallFlow> ReadFlowAsync(int flowId, CancellationToken cancellationToken = default)
        { Reads++; ReadToken = cancellationToken; AfterRead?.Invoke(); return Task.FromResult(Template); }
        public Task<int> EnqueueAsync(AutoCallRequest request, CancellationToken cancellationToken = default)
        { Writes++; WriteToken = cancellationToken; Request = request; return Task.FromResult(99); }
    }

    [Fact]
    public async Task EnqueueCapturesAllParticipantValuesNotAlarmPropertiesAndPreservesIdentity()
    {
        var repository = new Repository();
        var details = new ParticipantCurrentDetailsTests.Stub();
        var alarm = Alarm();
        alarm.OffenderName = "stale alarm value";
        Assert.Equal(99, await new AutoCallEnqueueService(repository, details).EnqueueAsync(alarm, "DEV1"));
        Assert.Equal(("oid-42", 42, CancellationToken.None), Assert.Single(details.Calls));
        var request = Assert.IsType<AutoCallRequest>(repository.Request);
        Assert.Equal(42, request.ActiveAlarmId);
        Assert.Equal("oid-42", request.Oid);
        Assert.Equal(12, request.FlowId);
        using var variables = JsonDocument.Parse(request.VariablesJson);
        Assert.Equal(5, variables.RootElement.EnumerateObject().Count());
        Assert.Equal("Alex & Co", variables.RootElement.GetProperty("offenderName").GetString());
        Assert.Equal("Low battery", variables.RootElement.GetProperty("alarmname").GetString());
        Assert.Equal(12.50m, variables.RootElement.GetProperty("lat").GetDecimal());
        Assert.Equal(-45.125m, variables.RootElement.GetProperty("lon").GetDecimal());
        Assert.Equal(DateTimeKind.Utc, variables.RootElement.GetProperty("last_location_time").GetDateTime().Kind);
        Assert.NotEqual(Guid.Empty, request.EnqueueIdempotencyKey);
    }

    [Fact]
    public void SameOccurrenceGetsStableUuidButNewStepLoopFlowOrTenantDoesNot()
    {
        var alarm = Alarm();
        var original = Capture(alarm);
        Assert.Equal(original, Capture(platform: "dev1"));
        Assert.NotEqual(original.EnqueueIdempotencyKey, Capture(alarm, "DEV2").EnqueueIdempotencyKey);
        alarm.CurrentLoopNumber++;
        Assert.NotEqual(original.EnqueueIdempotencyKey, Capture(alarm).EnqueueIdempotencyKey);
        alarm = Alarm(); alarm.StateNo++;
        Assert.NotEqual(original.EnqueueIdempotencyKey, Capture(alarm).EnqueueIdempotencyKey);
        alarm = Alarm(); alarm.FlowId++;
        Assert.NotEqual(original.EnqueueIdempotencyKey, Capture(alarm, flow: Flow() with { SystemId = alarm.FlowId }).EnqueueIdempotencyKey);
        alarm = Alarm(); alarm.HistoryID++;
        Assert.NotEqual(original.EnqueueIdempotencyKey, Capture(alarm).EnqueueIdempotencyKey);
        alarm = Alarm(); alarm.ProfileID++;
        Assert.NotEqual(original.EnqueueIdempotencyKey, Capture(alarm).EnqueueIdempotencyKey);
        var changed = Capture(details: ParticipantCurrentDetailsTests.Values("New participant name"));
        Assert.Equal(original.EnqueueIdempotencyKey, changed.EnqueueIdempotencyKey);
        Assert.NotEqual(original.VariablesJson, changed.VariablesJson);
    }

    [Fact]
    public async Task MissingVariablesAreDeferredToPreparationRatherThanRequiredOnAlarm()
    {
        var repository = new Repository { Template = Flow("{{future_variable}}") };
        Assert.Equal(99, await new AutoCallEnqueueService(repository, new ParticipantCurrentDetailsTests.Stub()).EnqueueAsync(Alarm(), "DEV1"));
        Assert.Equal(1, repository.Writes);
    }

    [Theory]
    [InlineData("{{name-with-dash}}")]
    [InlineData("{{missing-close")]
    public async Task InvalidPlaceholdersDoNotEnqueue(string content)
    {
        var repository = new Repository { Template = Flow(content) };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AutoCallEnqueueService(repository, new ParticipantCurrentDetailsTests.Stub()).EnqueueAsync(Alarm(), "DEV1"));
        Assert.Equal(0, repository.Writes);
    }

    [Fact]
    public async Task MissingFlowAndCancellationTouchNoDatabase()
    {
        var repository = new Repository();
        var details = new ParticipantCurrentDetailsTests.Stub();
        var service = new AutoCallEnqueueService(repository, details);
        var alarm = Alarm(); alarm.FlowId = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(alarm, "DEV1"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnqueueAsync(Alarm(), "DEV1", new CancellationToken(true)));
        Assert.Equal(0, repository.Reads);
        Assert.Equal(0, repository.Writes);
        Assert.Empty(details.Calls);
    }

    [Fact]
    public void EnqueueCommandUsesTypedSnapshotAndUuid()
    {
        var request = Capture();
        using var command = AutoCallQueueRepository.BuildEnqueueCommand(request);
        Assert.Equal("dbo.activealarms_call_InsertJob", command.CommandText);
        Assert.Equal(CommandType.StoredProcedure, command.CommandType);
        Assert.Equal(SqlDbType.Int, command.Parameters["@ActiveAlarmID"].SqlDbType);
        Assert.Equal(42, command.Parameters["@ActiveAlarmID"].Value);
        Assert.Equal(SqlDbType.UniqueIdentifier, command.Parameters["@EnqueueIdempotencyKey"].SqlDbType);
        Assert.Equal(request.EnqueueIdempotencyKey, command.Parameters["@EnqueueIdempotencyKey"].Value);
        Assert.Equal(-1, command.Parameters["@VariablesJSON"].Size);
        Assert.Equal(request.VariablesJson, command.Parameters["@VariablesJSON"].Value);
        Assert.Equal(request.Oid, command.Parameters["@OID"].Value);
    }

    [Fact]
    public void NestedGraphsAndPlaceholdersInGraphValuesAndXmlAttributesPreserveFullSnapshot()
    {
        var flow = Flow("") with
        {
            TemplateJson = "{\"steps\":[{\"text\":\"\\u007b\\u007bOffenderName}}\",\"transitions\":{\"next\":\"end\"}}]}",
            TwiML = "<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say voice=\"{{alarmname}}\"/></Response></Step></CallFlow>"
        };
        Assert.Equal(Capture().VariablesJson, Capture(flow: flow).VariablesJson);
    }

    [Fact]
    public void NullableSnapshotValuesArePreservedButXmlEntitiesAreRejected()
    {
        var details = ParticipantCurrentDetailsTests.Values();
        details["lat"] = JsonSerializer.SerializeToElement<object?>(null);
        using var variables = JsonDocument.Parse(Capture(details: details).VariablesJson);
        Assert.Equal(JsonValueKind.Null, variables.RootElement.GetProperty("lat").ValueKind);
        var flow = Flow() with { TwiML = "<!DOCTYPE CallFlow [<!ENTITY text 'value'>]><CallFlow>&text;</CallFlow>" };
        Assert.Throws<System.Xml.XmlException>(() => Capture(flow: flow));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterEitherReadPreventsEnqueue(bool afterDetails)
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new Repository();
        var details = new ParticipantCurrentDetailsTests.Stub();
        if (afterDetails) details.AfterRead = cancellation.Cancel;
        else repository.AfterRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AutoCallEnqueueService(repository, details).EnqueueAsync(Alarm(), "DEV1", cancellation.Token));
        Assert.Equal(cancellation.Token, repository.ReadToken);
        Assert.Equal(afterDetails ? 1 : 0, details.Calls.Count);
        Assert.Equal(0, repository.Writes);
    }

    [Fact]
    public async Task LookupFailurePreventsQueueWriteAndTokensReachAllBoundaries()
    {
        var repository = new Repository();
        var failure = new InvalidOperationException("lookup failed");
        var details = new ParticipantCurrentDetailsTests.Stub { Failure = failure };
        var service = new AutoCallEnqueueService(repository, details);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(Alarm(), "DEV1")));
        Assert.Equal(0, repository.Writes);
        details.Failure = null;
        using var active = new CancellationTokenSource();
        await service.EnqueueAsync(Alarm(), "DEV1", active.Token);
        Assert.Equal(active.Token, repository.ReadToken);
        Assert.Equal(active.Token, details.Calls.Last().Token);
        Assert.Equal(active.Token, repository.WriteToken);
    }

    [Fact]
    public async Task CentralDatabaseConfigurationHasNoTenantOrDefaultFallback()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:connstr"] = "Server=unused;Database=tenant",
            ["ConnectionStrings:DEV1"] = "Server=unused;Database=tenant"
        }).Build();
        var repository = new AutoCallQueueRepository(configuration);
        var readFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReadFlowAsync(12));
        var writeFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.EnqueueAsync(Capture()));
        Assert.Contains("ConnectionStrings:AutoCallDB", readFailure.Message);
        Assert.Contains("ConnectionStrings:AutoCallDB", writeFailure.Message);
    }
}
