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
    { SystemID=42, HistoryID=81, ClientID="oid-42", ProfileID=7, StateNo=3, CurrentLoopNumber=1, FlowId=12, OffenderName="Alex & Co", AlarmText="Low battery" };
    private static AutoCallFlow Flow(string content="{{OffenderName}} {{AlarmText}} {{callId}} {{feedbackBaseUrl}} {{executionId}} {{recipient}}")
        => new(12,"{\"schemaVersion\":1}","<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say>"+content+"</Say></Response></Step></CallFlow>");
    private sealed class Repository : IAutoCallQueueRepository
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public AutoCallFlow Template { get; set; }=Flow();
        public AutoCallRequest? Request { get; private set; }
        public Action? AfterRead { get; set; }
        public CancellationToken ReadToken { get; private set; }
        public CancellationToken WriteToken { get; private set; }
        public Task<AutoCallFlow> ReadFlowAsync(int flowId,CancellationToken cancellationToken=default)
        { Reads++; ReadToken=cancellationToken; AfterRead?.Invoke(); return Task.FromResult(Template); }
        public Task<int> EnqueueAsync(AutoCallRequest request,CancellationToken cancellationToken=default)
        { Writes++; WriteToken=cancellationToken; Request=request; return Task.FromResult(99); }
    }

    [Fact]
    public async Task EnqueueCapturesMatchingVariableKeysAndImmutableFlowIdentity()
    {
        var repository=new Repository();
        Assert.Equal(99,await new AutoCallEnqueueService(repository).EnqueueAsync(Alarm(),"DEV1"));
        var request=Assert.IsType<AutoCallRequest>(repository.Request);
        Assert.Equal(42,request.ActiveAlarmId); Assert.Equal("oid-42",request.Oid); Assert.Equal(12,request.FlowId);
        using var variables=JsonDocument.Parse(request.VariablesJson);
        Assert.Equal(2,variables.RootElement.EnumerateObject().Count());
        Assert.Equal("Alex & Co",variables.RootElement.GetProperty("OffenderName").GetString());
        Assert.Equal("Low battery",variables.RootElement.GetProperty("AlarmText").GetString());
        Assert.NotEqual(Guid.Empty,request.EnqueueIdempotencyKey);
    }

    [Fact]
    public void SameOccurrenceGetsStableUuidButNewStepLoopFlowOrTenantDoesNot()
    {
        var alarm=Alarm(); var original=AutoCallEnqueueService.CaptureRequest(alarm,"DEV1",Flow());
        Assert.Equal(original,AutoCallEnqueueService.CaptureRequest(Alarm(),"dev1",Flow()));
        Assert.NotEqual(original.EnqueueIdempotencyKey,AutoCallEnqueueService.CaptureRequest(alarm,"DEV2",Flow()).EnqueueIdempotencyKey);
        alarm.CurrentLoopNumber++;
        Assert.NotEqual(original.EnqueueIdempotencyKey,AutoCallEnqueueService.CaptureRequest(alarm,"DEV1",Flow()).EnqueueIdempotencyKey);
        alarm=Alarm(); alarm.StateNo++;
        Assert.NotEqual(original.EnqueueIdempotencyKey,AutoCallEnqueueService.CaptureRequest(alarm,"DEV1",Flow()).EnqueueIdempotencyKey);
        alarm=Alarm(); alarm.FlowId++;
        Assert.NotEqual(original.EnqueueIdempotencyKey,AutoCallEnqueueService.CaptureRequest(alarm,"DEV1",Flow() with { SystemId=alarm.FlowId }).EnqueueIdempotencyKey);
        alarm=Alarm(); alarm.HistoryID++;
        Assert.NotEqual(original.EnqueueIdempotencyKey,AutoCallEnqueueService.CaptureRequest(alarm,"DEV1",Flow()).EnqueueIdempotencyKey);
        alarm=Alarm(); alarm.ProfileID++;
        Assert.NotEqual(original.EnqueueIdempotencyKey,AutoCallEnqueueService.CaptureRequest(alarm,"DEV1",Flow()).EnqueueIdempotencyKey);
        alarm=Alarm(); alarm.AlarmText="Changed";
        var changed=AutoCallEnqueueService.CaptureRequest(alarm,"DEV1",Flow());
        Assert.Equal(original.EnqueueIdempotencyKey,changed.EnqueueIdempotencyKey);
        Assert.NotEqual(original.VariablesJson,changed.VariablesJson);
    }

    [Theory]
    [InlineData("{{unknown}}")]
    [InlineData("{{name-with-dash}}")]
    public async Task MissingOrInvalidVariablesDoNotEnqueue(string content)
    {
        var repository=new Repository { Template=Flow(content) };
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new AutoCallEnqueueService(repository).EnqueueAsync(Alarm(),"DEV1"));
        Assert.Equal(0,repository.Writes);
    }

    [Fact]
    public async Task MissingFlowAndCancellationTouchNoDatabase()
    {
        var repository=new Repository(); var service=new AutoCallEnqueueService(repository); var alarm=Alarm(); alarm.FlowId=0;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.EnqueueAsync(alarm,"DEV1"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.EnqueueAsync(Alarm(),"DEV1",new CancellationToken(true)));
        Assert.Equal(0,repository.Reads); Assert.Equal(0,repository.Writes);
    }

    [Fact]
    public void EnqueueCommandUsesTypedSnapshotAndUuid()
    {
        var request=AutoCallEnqueueService.CaptureRequest(Alarm(),"DEV1",Flow());
        using var command=AutoCallQueueRepository.BuildEnqueueCommand(request);
        Assert.Equal("dbo.activealarms_call_InsertJob",command.CommandText);
        Assert.Equal(CommandType.StoredProcedure,command.CommandType);
        Assert.Equal(SqlDbType.UniqueIdentifier,command.Parameters["@EnqueueIdempotencyKey"].SqlDbType);
        Assert.Equal(request.EnqueueIdempotencyKey,command.Parameters["@EnqueueIdempotencyKey"].Value);
        Assert.Equal(-1,command.Parameters["@VariablesJSON"].Size);
        Assert.Equal(request.VariablesJson,command.Parameters["@VariablesJSON"].Value);
        Assert.Equal(request.Oid,command.Parameters["@OID"].Value);
    }

    [Fact]
    public void NestedGraphClosingBracesAreNotVariableTokens()
    {
        var flow=Flow() with { TemplateJson="{\"schemaVersion\":1,\"steps\":[{\"id\":\"hello\",\"transitions\":{\"next\":\"end\"}}]}" };
        Assert.Equal(12,AutoCallEnqueueService.CaptureRequest(Alarm(),"DEV1",flow).FlowId);
    }

    [Fact]
    public void EscapedGraphValuesAndXmlAttributesCaptureVariables()
    {
        var flow=Flow("") with { TemplateJson="{\"steps\":[{\"text\":\"\\u007b\\u007bOffenderName}}\"}]}", TwiML="<CallFlow initialStep=\"hello\"><Step id=\"hello\"><Response><Say voice=\"{{AlarmText}}\"/></Response></Step></CallFlow>" };
        var request=AutoCallEnqueueService.CaptureRequest(Alarm(),"DEV1",flow);
        using var variables=JsonDocument.Parse(request.VariablesJson);
        Assert.Equal("Alex & Co",variables.RootElement.GetProperty("OffenderName").GetString());
        Assert.Equal("Low battery",variables.RootElement.GetProperty("AlarmText").GetString());
    }

    [Fact]
    public void BlankRequiredValuesAndXmlEntitiesAreRejected()
    {
        var alarm=Alarm(); alarm.OffenderName=" ";
        Assert.Throws<InvalidOperationException>(()=>AutoCallEnqueueService.CaptureRequest(alarm,"DEV1",Flow()));
        var flow=Flow() with { TwiML="<!DOCTYPE CallFlow [<!ENTITY text 'value'>]><CallFlow>&text;</CallFlow>" };
        Assert.Throws<System.Xml.XmlException>(()=>AutoCallEnqueueService.CaptureRequest(Alarm(),"DEV1",flow));
    }

    [Fact]
    public async Task CancellationAfterFlowReadPreventsEnqueueAndTokensReachBothBoundaries()
    {
        using var cancellation=new CancellationTokenSource();
        var repository=new Repository { AfterRead=cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new AutoCallEnqueueService(repository).EnqueueAsync(Alarm(),"DEV1",cancellation.Token));
        Assert.Equal(cancellation.Token,repository.ReadToken);
        Assert.Equal(0,repository.Writes);
        using var active=new CancellationTokenSource();
        repository=new Repository();
        await new AutoCallEnqueueService(repository).EnqueueAsync(Alarm(),"DEV1",active.Token);
        Assert.Equal(active.Token,repository.ReadToken);
        Assert.Equal(active.Token,repository.WriteToken);
    }

    [Fact]
    public async Task CentralDatabaseConfigurationHasNoTenantOrDefaultFallback()
    {
        var configuration=new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:connstr"]="Server=unused;Database=tenant", ["ConnectionStrings:DEV1"]="Server=unused;Database=tenant" }).Build();
        var repository=new AutoCallQueueRepository(configuration);
        var readFailure=await Assert.ThrowsAsync<InvalidOperationException>(()=>repository.ReadFlowAsync(12));
        var writeFailure=await Assert.ThrowsAsync<InvalidOperationException>(()=>repository.EnqueueAsync(AutoCallEnqueueService.CaptureRequest(Alarm(),"DEV1",Flow())));
        Assert.Contains("ConnectionStrings:AutoCallDB",readFailure.Message);
        Assert.Contains("ConnectionStrings:AutoCallDB",writeFailure.Message);
    }
}