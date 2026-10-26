using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.AutoCall;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class OfficerRoboCallTests
{
    internal static string Options(string type = "RoleBased", string primary = "CellPhone", bool wait = true)
        => JsonSerializer.Serialize(new
        {
            officerRoboCall = new
            {
                FlowId = 12, FlowName = "Officer flow", ShowOfficerResponseTranscript = true,
                WaitForCallCompletion = wait, CallTimeoutSeconds = (int?)null,
                Officer = new { Type = type, RoleType = type == "RoleBased" ? "Custom duty role" : null,
                    RoleID = type == "RoleBased" ? (int?)7 : null, RolePOGroup = type == "RoleBased" ? "OTHER" : null },
                PrimaryCallDevice = new { Type = primary, PhoneNumber = primary == "CustomPhone" ? "+15555550100" : null },
                SecondaryCallDevice = new { Type = "CustomPhone", PhoneNumber = "+15555550101" }
            }
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static ActiveAlarm Alarm(string options) => new()
    {
        Priority = 20, SystemID = 42, HistoryID = 81, ClientID = "oid", POGroupNum = "TENANT",
        ProfileID = 7, StateNo = 3, CurrentLoopNumber = 1, OptionsJson = options
    };

    internal sealed class Officers : IAutoCallOfficerRepository
    {
        public int Resolutions { get; private set; }
        public List<(string Connection, int Id)> PhoneCalls { get; } = [];
        public int SelectedId { get; set; } = 91;
        public Exception? Failure { get; set; }
        public IReadOnlyList<AutoCallPhone> Phones { get; set; } = [new("CellPhone", "(555) 555-0100")];
        public Task<RoboCallOfficer> ResolveAsync(string oid, RoboCallOfficerSelection selection, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Resolutions++;
            return Failure is null ? Task.FromResult(new RoboCallOfficer(selection, SelectedId, "Officer Name"))
                : Task.FromException<RoboCallOfficer>(Failure);
        }
        public Task<IReadOnlyList<AutoCallPhone>> ReadPhonesAsync(string connectionString, int officerId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            PhoneCalls.Add((connectionString, officerId));
            return Task.FromResult(Phones);
        }
    }

    private sealed class Queue : IAutoCallQueueRepository
    {
        public AutoCallRequest? Request { get; private set; }
        public RoboCallOfficer? Saved { get; set; }
        public Task<RoboCallOfficer?> ReadQueuedOfficerAsync(Guid key, CancellationToken token) => Task.FromResult(Saved);
        public Task<AutoCallFlow> ReadFlowAsync(int flowId, CancellationToken token = default)
            => Task.FromResult(new AutoCallFlow(flowId, """{"schemaVersion":1}""",
                "<CallFlow><Step id=\"hello\"><Response><Say>Hello {{offenderName}}</Say></Response></Step></CallFlow>", 123));
        public Task<int> EnqueueAsync(AutoCallRequest request, CancellationToken token = default)
        {
            Request = request;
            Saved = RoboCallOfficer.ReadFromOptions(request.OptionsJson);
            return Task.FromResult(99);
        }
    }

    private sealed class Routing : IAutoCallRoutingStore
    {
        public Task<string?> GetAsync(string key, CancellationToken token) => Task.FromResult<string?>("TENANT");
    }

    private sealed class ParticipantPhones : IAutoCallPhoneRepository
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<AutoCallPhone>> ReadAsync(string connection, string oid, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Officer calls must not query participant phones.");
        }
    }

    [Theory]
    [InlineData("Primary", "CellPhone")]
    [InlineData("RoleBased", "CellPhone")]
    [InlineData("Custom", "CustomPhone")]
    public void ParsesOfficerSelectionAndReusesCallDefaults(string type, string device)
    {
        var options = OfficerRoboCallOptions.Parse(Options(type, device));
        Assert.Equal(type, options.Officer.Type);
        Assert.Equal(300, options.CallOptions.CallTimeoutSeconds);
        Assert.True(options.CallOptions.ShowParticipantResponseTranscript);
        Assert.True(options.CallOptions.WaitForCallCompletion);
        Assert.Equal(device == "CustomPhone" ? "Custom" : device, options.CallOptions.PrimaryCallDevice!.Type);
        Assert.Equal("Custom", options.CallOptions.SecondaryCallDevice!.Type);
        if (type == "RoleBased")
        {
            Assert.Equal("Custom duty role", options.Officer.RoleType);
            Assert.Equal(7, options.Officer.RoleID);
            Assert.Equal("OTHER", options.Officer.RolePOGroup);
        }
    }

    [Theory]
    [InlineData("\"RoleID\":7", "\"RoleID\":null")]
    [InlineData("\"RoleID\":7", "\"RoleID\":\"7\"")]
    [InlineData("\"RolePOGroup\":\"OTHER\"", "\"RolePOGroup\":\" \"")]
    [InlineData("\"RoleType\":\"Custom duty role\"", "\"RoleType\":null")]
    [InlineData("\"Type\":\"CellPhone\"", "\"Type\":\"HomePhone\"")]
    [InlineData("\"Type\":\"CellPhone\"", "\"Type\":\"Custom\"")]
    [InlineData("\"PhoneNumber\":\"+15555550101\"", "\"PhoneNumber\":\"5555550101\"")]
    [InlineData("\"ShowOfficerResponseTranscript\":true", "\"ShowOfficerResponseTranscript\":\"true\"")]
    public void RejectsIncompleteOrInvalidOfficerOptions(string before, string after)
        => Assert.Throws<InvalidOperationException>(() => OfficerRoboCallOptions.Parse(Options().Replace(before, after)));

    [Fact]
    public void CustomOfficerCannotUseCellPhone()
        => Assert.Throws<InvalidOperationException>(() => OfficerRoboCallOptions.Parse(Options("Custom", "CellPhone")));

    [Fact]
    public void PrimaryOfficerCannotCarryRoleFields()
        => Assert.Throws<InvalidOperationException>(() => OfficerRoboCallOptions.Parse(Options().Replace("\"RoleBased\"", "\"Primary\"")));

    [Fact]
    public void OfficerOptionsDoNotChangeParticipantDeviceValidation()
    {
        var json = AutoCallEnqueueTests.RoboCallOptions(primary: """{"Type":"CustomPhone","PhoneNumber":"+15555550100"}""");
        Assert.Throws<InvalidOperationException>(() => ParticipantRoboCallOptions.Parse(json));
    }

    [Fact]
    public async Task EnqueuePinsOfficerAndReplayDoesNotResolveAgain()
    {
        var queue = new Queue();
        var officers = new Officers();
        var details = new ParticipantCurrentDetailsTests.Stub();
        var service = new AutoCallEnqueueService(queue, details, officers);
        var alarm = Alarm(Options());
        Assert.Equal(99, await service.EnqueueAsync(alarm, "TENANT"));
        Assert.Equal(91, queue.Saved!.Id);
        Assert.Equal("OTHER", queue.Saved.Selection.RolePOGroup);
        var first = queue.Request!;
        officers.SelectedId = 92;
        await service.EnqueueAsync(alarm, "TENANT");
        Assert.Equal(1, officers.Resolutions);
        Assert.Equal(first, queue.Request);
        Assert.Equal(123, first.FlowVersionId);
        using var json = JsonDocument.Parse(first.OptionsJson);
        Assert.Equal(91, json.RootElement.GetProperty("officer").GetProperty("id").GetInt32());
        Assert.Equal(7, json.RootElement.GetProperty("officer").GetProperty("selection").GetProperty("roleId").GetInt32());
        Assert.True(json.RootElement.GetProperty("waitForCompletion").GetBoolean());
        Assert.True(json.RootElement.GetProperty("postRecordingTranscript").GetBoolean());
        Assert.Equal(81, json.RootElement.GetProperty("historyId").GetInt32());
    }

    [Fact]
    public async Task ChangedOfficerSelectionCannotSilentlyReuseQueuedIdentity()
    {
        var queue = new Queue();
        var service = new AutoCallEnqueueService(queue, new ParticipantCurrentDetailsTests.Stub(), new Officers());
        var alarm = Alarm(Options());
        await service.EnqueueAsync(alarm, "TENANT");
        alarm.OptionsJson = Options().Replace("\"RoleID\":7", "\"RoleID\":8");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(alarm, "TENANT"));
    }

    [Fact]
    public async Task ResolverFailureDoesNotQueue()
    {
        var queue = new Queue();
        var service = new AutoCallEnqueueService(queue, new ParticipantCurrentDetailsTests.Stub(),
            new Officers { Failure = new InvalidOperationException("No eligible officer") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(Alarm(Options()), "TENANT"));
        Assert.Null(queue.Request);
    }

    [Fact]
    public async Task CustomOfficerBypassesResolver()
    {
        var queue = new Queue();
        var service = new AutoCallEnqueueService(queue, new ParticipantCurrentDetailsTests.Stub());
        await service.EnqueueAsync(Alarm(Options("Custom", "CustomPhone")), "TENANT");
        Assert.Equal("Custom", queue.Saved!.Selection.Type);
        Assert.Null(queue.Saved.Id);
    }

    [Fact]
    public async Task ContactsUsePinnedOfficerAndNeverParticipantPhones()
    {
        var participants = new ParticipantPhones();
        var officers = new Officers();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:TENANT"] = "routed-tenant" }).Build();
        var contacts = new AutoCallContactService(new Routing(), participants, config, officerRepository: officers);
        var officer = new RoboCallOfficer(OfficerRoboCallOptions.Parse(Options()).Officer, 91, "Officer Name");
        var resolved = await contacts.ResolveAsync("oid", officer);
        Assert.Equal(("routed-tenant", 91), Assert.Single(officers.PhoneCalls));
        Assert.Equal("+15555550100", Assert.Single(resolved.Phones).Number);
        Assert.Equal(0, participants.Calls);
        var custom = new RoboCallOfficer(new("Custom", null, null, null), null, null);
        Assert.Empty((await contacts.ResolveAsync("oid", custom)).Phones);
        Assert.Single(officers.PhoneCalls);
        var job = new AutoCallJob { OptionsJSON = (await EnqueuedRequest()).OptionsJson, CallDeviceIndex = 1 };
        Assert.Equal("+15555550101", AutoCallProcessingService.SelectRecipient(job, resolved, out var type));
        Assert.Equal("Custom", type);
    }

    private static async Task<AutoCallRequest> EnqueuedRequest()
    {
        var queue = new Queue();
        await new AutoCallEnqueueService(queue, new ParticipantCurrentDetailsTests.Stub(), new Officers())
            .EnqueueAsync(Alarm(Options()), "TENANT");
        return queue.Request!;
    }

    [Fact]
    public void ResolverAndPhoneCommandsUseExactContracts()
    {
        using var command = AutoCallOfficerRepository.BuildResolveCommand("oid", OfficerRoboCallOptions.Parse(Options()).Officer);
        Assert.Equal("dbo.activealarms_ResolveRoboCallOfficer", command.CommandText);
        Assert.Equal(4, command.Parameters.Count);
        Assert.Equal("RoleBased", command.Parameters["@OfficerType"].Value);
        Assert.Equal(7, command.Parameters["@RoleID"].Value);
        Assert.Equal("OTHER", command.Parameters["@RolePOGroup"].Value);
        using var phones = AutoCallOfficerRepository.BuildPhonesCommand(91);
        Assert.Equal("dbo.activealarms_GetAllOfficerPhonenumbers", phones.CommandText);
        Assert.Equal(SqlDbType.Int, phones.Parameters["@OfficerID"].SqlDbType);
        Assert.Equal(91, phones.Parameters["@OfficerID"].Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ResolverRequiresExactlyOneEligibleOfficer(int count)
    {
        using var table = new DataTable();
        table.Columns.Add("officer_id", typeof(int));
        table.Columns.Add("officer_name", typeof(string));
        for (int i = 0; i < count; i++) table.Rows.Add(91 + i, "Officer");
        await using var reader = table.CreateDataReader();
        var selection = OfficerRoboCallOptions.Parse(Options()).Officer;
        if (count == 1) Assert.Equal(91, (await AutoCallOfficerRepository.ReadOfficerAsync(reader, selection, default)).Id);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => AutoCallOfficerRepository.ReadOfficerAsync(reader, selection, default));
    }

    [Theory]
    [InlineData("COMPLETED", null, "Officer Robo Call (Call 1) to CustomPhone completed.")]
    [InlineData("DEVICE", null, "Officer Robo Call (Call 1) to CellPhone failed (no-answer); trying CustomPhone.")]
    [InlineData("TRANSCRIPT", "RecordVerb", "Officer Response (Call 1) 1/1 [00:01-00:02] : Hello")]
    [InlineData("TRANSCRIPT", null, "Officer Call transcript (Call 1) 1/1 [00:01-00:02] : Hello")]
    public void OfficerNotesAreLabelledWithoutChangingMessageTimestamps(string kind, string? source, string expected)
    {
        var task = new AutoCallNoteTask
        {
            Kind = kind, CallId = 1, RecordingId = 2, RecordingSource = source, PhoneType = "CellPhone", MessageStepId = "message",
            OfficerJSON = """{"id":91}""",
            MessagesJSON = kind switch
            {
                "COMPLETED" => """{"phoneType":"Custom"}""",
                "DEVICE" => """{"phoneType":"CellPhone","status":"no-answer","nextPhoneType":"Custom"}""",
                _ => """[{"Sentence":0,"AudioChannel":0,"StartTimeMilliseconds":1000,"EndTimeMilliseconds":2000,"TranscriptText":"Hello"}]"""
            }
        };
        Assert.StartsWith(expected, Assert.Single(AutoCallNoteFormatter.Format(task)));
    }
}
