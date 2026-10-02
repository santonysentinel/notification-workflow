using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Entity;
using WorkflowRepository = NotificationWorkflowService.Repository.Repository;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class WorkflowRepositoryTests
{
    private static readonly DateTime Time = new(2026, 10, 1, 12, 34, 56, DateTimeKind.Utc);

    private static ActiveAlarm Alarm() => new()
    {
        SystemID = 11, HistoryID = 12, AgencyID = 13, ClientSystemID = 14,
        AlarmSystemID = 15, StateID = 16, StateNo = 17, ProfileID = 18,
        ClientID = "client", DeviceID = "device", POGroupNum = "group",
        ProfileName = "profile", Instruction = "instruction", EmailAddresses = "officer@example.test",
        ReceivedDateTime = 1_700_000_000L, EventDateTime = 1_700_000_001L, FeedbackREQ = true
    };

    private sealed record Parameter(string Name, SqlDbType Type, int Size, object? Value);
    private static Parameter P(string name, SqlDbType type, object? value, int size = 0) => new(name, type, size, value);

    private static void Contract(SqlCommand command, string text, params Parameter[] parameters)
    {
        using (command)
        {
            Assert.Equal(text, command.CommandText);
            Assert.Equal(CommandType.StoredProcedure, command.CommandType);
            Assert.Equal(30, command.CommandTimeout);
            Assert.Null(command.Connection);
            Assert.Equal(parameters.Length, command.Parameters.Count);
            for (int i = 0; i < parameters.Length; i++)
            {
                var expected = parameters[i];
                var actual = command.Parameters[i];
                Assert.Equal(expected.Name, actual.ParameterName);
                Assert.Equal(expected.Type, actual.SqlDbType);
                Assert.Equal(expected.Size, actual.Size);
                Assert.Equal(ParameterDirection.Input, actual.Direction);
                Assert.Equal(expected.Value, actual.Value);
                Assert.Equal(expected.Value?.GetType(), actual.Value?.GetType());
            }
        }
    }

    [Fact]
    public void AllReferenceAndContactReadsMatchOriginalContracts()
    {
        var a = Alarm();
        Contract(WorkflowRepository.BuildFetchClientProfile(1), "ActiveAlarms_GetClientProfile", P("@HolidayProfile", SqlDbType.Int, 1));
        using (var profiles = WorkflowRepository.BuildReadAllProfiles())
        {
            Assert.Equal("ActiveAlarms_ReadProfileItems", profiles.CommandText);
            Assert.Equal(CommandType.StoredProcedure, profiles.CommandType);
            Assert.Equal(0, profiles.CommandTimeout);
            Assert.Empty(profiles.Parameters.Cast<SqlParameter>());
            Assert.Null(profiles.Connection);
        }
        Contract(WorkflowRepository.BuildReadAllHolidays(), "ActiveAlarms_ReadGroupHolidays");
        Contract(WorkflowRepository.BuildReadAllRoles(), "spGetListPOGroupRoles");
        Contract(WorkflowRepository.BuildReadRoles(a, 19, 3), "ActiveAlarms_ReadRoles",
            P("@RoleID", SqlDbType.Int, 19), P("@POGroup", SqlDbType.VarChar, "group", 32));
        Contract(WorkflowRepository.BuildReadVictims(), "ActiveAlarms_ReadVictimsEmail");
        Contract(WorkflowRepository.BuildReadMEZVictims(), "ActiveAlarms_ReadVictims");
        Contract(WorkflowRepository.BuildReadAttachedVictimZones(), "ActiveAlarms_GetZonesAttachedVictim");
        Contract(WorkflowRepository.BuildReadProfileItemsClear(), "ActiveAlarms_ReadProfileItemsClear");
        Contract(WorkflowRepository.BuildGetInsertEmails(a), "ActiveAlarms_ReadAuditEmails", P("@ClientSystemID", SqlDbType.Int, 14));
        Contract(WorkflowRepository.BuildGetClientEmail(a), "ActiveAlarms_ClientEmails", P("@ClientSystemID", SqlDbType.Int, 14));
        Contract(WorkflowRepository.BuildGetClientText(a), "ActiveAlarms_ClientCell", P("@ClientSystemID", SqlDbType.Int, 14));
    }

    [Fact]
    public void McAppAndPushQueueMatchOriginalContractsIncludingLegacyClrTypes()
    {
        var a = Alarm();
        Contract(WorkflowRepository.BuildPushAlertToMcApp(a), "ActiveAlarms_InsertIntoMCAPP",
            P("@HistoryID", SqlDbType.Int, 12), P("@AgencyID", SqlDbType.Int, 13),
            P("@ClientID", SqlDbType.Int, 14), P("@AlarmID", SqlDbType.Int, 15),
            P("@DeviceID", SqlDbType.VarChar, "device", 20), P("@StateID", SqlDbType.Int, 16),
            P("@RecievedDateTime", SqlDbType.Int, a.ReceivedDateTime), P("@EventDateTime", SqlDbType.Int, a.EventDateTime),
            P("@ProfileName", SqlDbType.VarChar, "profile", 100), P("@Instruction", SqlDbType.VarChar, "instruction", 1500),
            P("@StepNo", SqlDbType.Int, 17), P("@IsUpdatedStep", SqlDbType.Bit, 1),
            P("@ProfileID", SqlDbType.Int, 18), P("@OID", SqlDbType.VarChar, "client", 20));
        Contract(WorkflowRepository.BuildInsertPushNotificationQueue(a, "victim", "offender"), "ActiveAlarms_InsertIntoPushNotificationQueue",
            P("@HistoryID", SqlDbType.Int, 12), P("@ClientID", SqlDbType.VarChar, "victim", 32), P("@OffenderID", SqlDbType.VarChar, "offender", 32));
    }

    [Fact]
    public void NotificationQueuesPreserveExactSourcesAddressesAndFeedbackTypes()
    {
        var a = Alarm();
        Parameter[] Expected(string address, string source) =>
        [
            P("@AlarmID", SqlDbType.Int, 11), P("@HistoryID", SqlDbType.Int, 12),
            P("@ClientID", SqlDbType.VarChar, "client", 20),
            P("@MsgReceivedDateTime", SqlDbType.Char, a.MessageReceivedDateTime(), 20),
            P("@EventDateTime", SqlDbType.Char, a.EventRecievedDateTime(), 30),
            P("@MsgSubject", SqlDbType.VarChar, "Alarm Notification", 128),
            P("@MsgToAddress", SqlDbType.NVarChar, address, 1000),
            P("@MsgSource", SqlDbType.VarChar, source, 50),
            P("@InsertType", SqlDbType.Int, 4), P("@FeedBackReq", SqlDbType.Int, true)
        ];
        Contract(WorkflowRepository.BuildAddToNotificationQueue(a, 4), "ActiveAlarms_InsertIntoNotificationQueue",
            Expected(a.EmailAddresses, "Sentrak Live Notify Trigger"));
        Contract(WorkflowRepository.BuildInsertNotificationQueueVictim(a, 4, "victim@example.test", true), "ActiveAlarms_InsertIntoNotificationQueue",
            [.. Expected("victim@example.test", "Notification Parser"), P("@IsVictimNotification", SqlDbType.Bit, true)]);
        using var defaults = WorkflowRepository.BuildInsertNotificationQueueVictim(a, 4, "client@example.test");
        Assert.Equal(false, defaults.Parameters["@IsVictimNotification"].Value);
    }

    [Fact]
    public void AuditHistoryAndClearWritesPreserveSizesAndUntruncatedValues()
    {
        var longText = new string('x', 5000);
        Contract(WorkflowRepository.BuildCreateAlarmAudit(3, longText, 12, 17), "mcapp_CreateAudit",
            P("@Login", SqlDbType.VarChar, "system", 30), P("@Type", SqlDbType.Int, 3),
            P("@Action", SqlDbType.VarChar, longText, 4096), P("@historyID", SqlDbType.Int, 12), P("@stepno", SqlDbType.Int, 17));
        Contract(WorkflowRepository.BuildAddActiveAlarmActionToActivity(12, longText, 1), "ActiveAlarms_InsertIntoHistory",
            P("@HistoryID", SqlDbType.Int, 12), P("@emails", SqlDbType.VarChar, longText, 1024), P("@type", SqlDbType.Int, 1));
        Contract(WorkflowRepository.BuildClearMcAppAlarm(longText, "client"), "ActiveAlarms_ClearActiveAlarm",
            P("@Alarms", SqlDbType.VarChar, longText, 4096), P("@OID", SqlDbType.VarChar, "client", 32));
    }

    [Fact]
    public void ProgressAndStateContractsPreserveConversionsAndStateNumberInversion()
    {
        Contract(WorkflowRepository.BuildReadPoints("0025", 21), "ActiveAlarms_ReadAlarms",
            // SqlParameter infers Size from a string Value even when SqlDbType is Int.
            P("@NumberToRead", SqlDbType.Int, "0025", 4), P("@StartingSystemID", SqlDbType.Int, 21));
        Contract(WorkflowRepository.BuildReadCurrentActiveAlarmPoint(), "ActiveAlarms_ReadLastAlarmPoint");
        Contract(WorkflowRepository.BuildUpdateParserActivty(21, "0022"), "ActiveAlarms_UpdateParserActivity",
            P("@ParserID", SqlDbType.Int, 22), P("@CurrSystemID", SqlDbType.Int, 21));
        Contract(WorkflowRepository.BuildInsertIntoAlarmNotification(11, 23, Time, "Delay"), "ActiveAlarms_InsertIntoAlarmNotification",
            P("@AlarmSystemID", SqlDbType.Int, 11), P("@CurrentStateNo", SqlDbType.Int, 23),
            P("@ExpiryTime", SqlDbType.DateTime, Time), P("@Action", SqlDbType.VarChar, "Delay", 50));
        Contract(WorkflowRepository.BuildInsertIntoCurrentAlarmNotification(11, 23, Time, 24, 25, 1), "ActiveAlarms_InsertIntoCNotificationState",
            P("@AlarmSystemID", SqlDbType.Int, 11), P("@CurrentStateNo", SqlDbType.Int, 24),
            P("@ExpiryTime", SqlDbType.DateTime, Time), P("@ParserStateNo", SqlDbType.Int, 23),
            P("@ProcessNextStep", SqlDbType.Bit, 1), P("@CurrentLoopNumber", SqlDbType.Int, 25));
        Contract(WorkflowRepository.BuildGetExpiredAlarms(Time), "ActiveAlarms_RemoveExpiredNotificationAlarms",
            P("@CurrentTime", SqlDbType.DateTime, Time));
        using var last = WorkflowRepository.BuildReadLastSuccessfulProcess(int.MaxValue);
        Assert.Equal("SELECT CONVERT(int, [StatusID]) AS StatusID FROM ParserActivity WHERE ParserId = @ParserID ", last.CommandText);
        Assert.Equal(CommandType.Text, last.CommandType);
        Assert.Equal(30, last.CommandTimeout);
        Assert.Null(last.Connection);
        var parameter = Assert.Single(last.Parameters.Cast<SqlParameter>());
        Assert.Equal("@ParserID", parameter.ParameterName);
        Assert.Equal(SqlDbType.Int, parameter.SqlDbType);
        Assert.Equal(0, parameter.Size);
        Assert.Equal(int.MaxValue, parameter.Value);
    }

    [Fact]
    public void NullsAreNotModernizedToDbNullAndParserIdUsesOriginalConversion()
    {
        Contract(WorkflowRepository.BuildUpdateParserActivty(null, null), "ActiveAlarms_UpdateParserActivity",
            P("@ParserID", SqlDbType.Int, 0), P("@CurrSystemID", SqlDbType.Int, null));
        Contract(WorkflowRepository.BuildReadPoints(null, 0), "ActiveAlarms_ReadAlarms",
            P("@NumberToRead", SqlDbType.Int, null), P("@StartingSystemID", SqlDbType.Int, 0));
        Assert.Throws<FormatException>(() => WorkflowRepository.BuildUpdateParserActivty(1, "invalid"));
        Assert.Throws<OverflowException>(() => WorkflowRepository.BuildUpdateParserActivty(1, "2147483648"));
    }

    private static IConfiguration Configuration(string? connectionString = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:connstr"] = connectionString }).Build();

    [Fact]
    public void ConfigurationIsValidatedWhenPreparedNotWhenConstructed()
    {
        var missing = new WorkflowRepository(Configuration());
        Assert.Equal("Connection string 'connstr' is not configured.",
            Assert.Throws<InvalidOperationException>(() => missing.PrepareReadAllProfiles()).Message);
        var invalid = new WorkflowRepository(Configuration("not a connection string"));
        Assert.Throws<ArgumentException>(() => invalid.PrepareReadAllProfiles());
    }

    [Fact]
    public void ServiceCheckpointReadsReloadConnectionStringWhileParserReadsRetainSnapshot()
    {
        const string initial = "Server=offline.invalid;Database=Initial;Integrated Security=true";
        const string next = "Server=offline.invalid;Database=Next;Integrated Security=true";
        var configuration = Configuration(initial);
        var repo = new WorkflowRepository(configuration);
        configuration["ConnectionStrings:connstr"] = next;

        using var parserCurrent = Assert.IsType<WorkflowRepository.WorkflowOperation>(repo.PrepareReadCurrentActiveAlarmPoint());
        using var parserLast = Assert.IsType<WorkflowRepository.WorkflowOperation>(repo.PrepareReadLastSuccessfulProcess(47));
        using var serviceCurrent = Assert.IsType<WorkflowRepository.WorkflowOperation>(repo.PrepareReadCurrentActiveAlarmPoint(reloadConnectionString: true));
        using var serviceLast = Assert.IsType<WorkflowRepository.WorkflowOperation>(repo.PrepareReadLastSuccessfulProcess(47, reloadConnectionString: true));
        Assert.Equal(initial, parserCurrent.Command.Connection!.ConnectionString);
        Assert.Equal(initial, parserLast.Command.Connection!.ConnectionString);
        Assert.Equal(next, serviceCurrent.Command.Connection!.ConnectionString);
        Assert.Equal(next, serviceLast.Command.Connection!.ConnectionString);

        configuration["ConnectionStrings:connstr"] = initial;
        using var reloadedCurrent = Assert.IsType<WorkflowRepository.WorkflowOperation>(repo.PrepareReadCurrentActiveAlarmPoint(reloadConnectionString: true));
        using var reloadedLast = Assert.IsType<WorkflowRepository.WorkflowOperation>(repo.PrepareReadLastSuccessfulProcess(47, reloadConnectionString: true));
        Assert.Equal(initial, reloadedCurrent.Command.Connection!.ConnectionString);
        Assert.Equal(initial, reloadedLast.Command.Connection!.ConnectionString);
        Assert.Equal(next, serviceCurrent.Command.Connection!.ConnectionString);
        Assert.Equal(next, serviceLast.Command.Connection!.ConnectionString);
    }

    [Fact]
    public void MissingReloadedConnectionStringThrowsWhileParserSnapshotRemainsUsable()
    {
        const string initial = "Server=offline.invalid;Database=Initial;Integrated Security=true";
        var configuration = Configuration(initial);
        var repo = new WorkflowRepository(configuration);
        configuration["ConnectionStrings:connstr"] = null;

        Assert.Equal("Connection string 'connstr' is not configured.",
            Assert.Throws<InvalidOperationException>(() => repo.PrepareReadCurrentActiveAlarmPoint(reloadConnectionString: true)).Message);
        Assert.Equal("Connection string 'connstr' is not configured.",
            Assert.Throws<InvalidOperationException>(() => repo.PrepareReadLastSuccessfulProcess(47, reloadConnectionString: true)).Message);
        using var parserCurrent = Assert.IsType<WorkflowRepository.WorkflowOperation>(repo.PrepareReadCurrentActiveAlarmPoint());
        using var parserLast = Assert.IsType<WorkflowRepository.WorkflowOperation>(repo.PrepareReadLastSuccessfulProcess(47));
        Assert.Equal(initial, parserCurrent.Command.Connection!.ConnectionString);
        Assert.Equal(initial, parserLast.Command.Connection!.ConnectionString);
    }

    [Fact]
    public void EveryPrepareCreatesAClosedIndependentOperationWithoutExecutingSql()
    {
        var repo = new WorkflowRepository(Configuration("Server=localhost;Database=unused;Integrated Security=true"));
        var a = Alarm();
        Func<NotificationWorkflowService.Repository.IWorkflowOperation>[] prepares =
        [
            () => repo.PrepareFetchClientProfile(0), repo.PrepareReadAllProfiles, repo.PrepareReadAllHolidays,
            repo.PrepareReadAllRoles, () => repo.PrepareReadRoles(a, 1, 2), repo.PrepareReadVictims,
            repo.PrepareReadMEZVictims, repo.PrepareReadAttachedVictimZones, repo.PrepareReadProfileItemsClear,
            () => repo.PrepareGetInsertEmails(a), () => repo.PrepareGetClientEmail(a),
            () => repo.PrepareGetClientText(a), () => repo.PreparePushAlertToMcApp(a),
            () => repo.PrepareAddToNotificationQueue(a, 1), () => repo.PrepareInsertPushNotificationQueue(a, "v", "o"),
            () => repo.PrepareInsertNotificationQueueVictim(a, 1, "v"), () => repo.PrepareCreateAlarmAudit(1, "action", 2, 3),
            () => repo.PrepareAddActiveAlarmActionToActivity(1, "email", 2), () => repo.PrepareClearMcAppAlarm("alarms", "oid"),
            () => repo.PrepareReadPoints("25", 1), () => repo.PrepareReadLastSuccessfulProcess(1),
            () => repo.PrepareReadCurrentActiveAlarmPoint(), () => repo.PrepareUpdateParserActivty(1, "2"),
            () => repo.PrepareInsertIntoAlarmNotification(1, 2, Time, "action"),
            () => repo.PrepareInsertIntoCurrentAlarmNotification(1, 2, Time, 3, 4, 1), () => repo.PrepareGetExpiredAlarms(Time)
        ];
        foreach (var prepare in prepares)
        {
            using var first = Assert.IsType<WorkflowRepository.WorkflowOperation>(prepare());
            using var second = Assert.IsType<WorkflowRepository.WorkflowOperation>(prepare());
            Assert.NotSame(first.Command, second.Command);
            Assert.NotSame(first.Command.Connection, second.Command.Connection);
            Assert.Equal(ConnectionState.Closed, first.Command.Connection!.State);
            var command = first.Command;
            var connection = command.Connection;
            Assert.Throws<InvalidOperationException>(() => first.ExecuteReader());
            Assert.Throws<InvalidOperationException>(() => first.ExecuteNonQuery());
            first.Close();
            Assert.Same(command, first.Command);
            Assert.Same(connection, first.Command.Connection);
        }
    }

    [Fact]
    public void PreparedValuesAreCapturedOnceAndDisposedOperationsCannotBeUsed()
    {
        var repo = new WorkflowRepository(Configuration("Server=localhost;Database=unused;Integrated Security=true"));
        var a = Alarm();
        var operation = Assert.IsType<WorkflowRepository.WorkflowOperation>(repo.PrepareAddToNotificationQueue(a, 3));
        a.EmailAddresses = "changed";
        a.SystemID = 99;
        Assert.Equal("officer@example.test", operation.Command.Parameters["@MsgToAddress"].Value);
        Assert.Equal(11, operation.Command.Parameters["@AlarmID"].Value);
        operation.Dispose();
        operation.Dispose();
        Assert.Throws<ObjectDisposedException>(() => operation.Open());
        Assert.Throws<ObjectDisposedException>(() => operation.Close());
        Assert.Throws<ObjectDisposedException>(() => operation.ExecuteReader());
        Assert.Throws<ObjectDisposedException>(() => operation.ExecuteNonQuery());
    }
}