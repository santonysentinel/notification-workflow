using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Entity;
using System.Data;
using System.Data.Common;

namespace NotificationWorkflowService.Repository
{
    /// <summary>
    /// Legacy workflow SQL contracts. No execution, mapping, retries or business policy in Prepare.
    /// The normal and step parsers currently have identical shared SQL contracts.
    /// </summary>
    public sealed class Repository : IRepository
    {
        private readonly string? connectionString;
        private readonly IConfiguration configuration;

        public Repository(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            this.configuration = configuration;
            // Do not validate here: hosted services may construct the repository before parser setup.
            connectionString = configuration.GetConnectionString("connstr");
        }

        public IWorkflowOperation PrepareFetchClientProfile(int profileType) => Prepare(() => BuildFetchClientProfile(profileType));
        public IWorkflowOperation PrepareReadAllProfiles() => Prepare(BuildReadAllProfiles);
        public IWorkflowOperation PrepareReadAllHolidays() => Prepare(BuildReadAllHolidays);
        public IWorkflowOperation PrepareReadAllRoles() => Prepare(BuildReadAllRoles);
        public IWorkflowOperation PrepareReadRoles(ActiveAlarm a, int roleID, int roleAction) => Prepare(() => BuildReadRoles(a, roleID, roleAction));
        public IWorkflowOperation PrepareReadVictims() => Prepare(BuildReadVictims);
        public IWorkflowOperation PrepareReadMEZVictims() => Prepare(BuildReadMEZVictims);
        public IWorkflowOperation PrepareReadAttachedVictimZones() => Prepare(BuildReadAttachedVictimZones);
        public IWorkflowOperation PrepareReadProfileItemsClear() => Prepare(BuildReadProfileItemsClear);
        public IWorkflowOperation PrepareGetInsertEmails(ActiveAlarm a) => Prepare(() => BuildGetInsertEmails(a));
        public IWorkflowOperation PrepareGetClientEmail(ActiveAlarm a) => Prepare(() => BuildGetClientEmail(a));
        public IWorkflowOperation PrepareGetClientText(ActiveAlarm a) => Prepare(() => BuildGetClientText(a));
        public IWorkflowOperation PrepareAddNote(string noteText, string oid) => Prepare(() => BuildAddNote(noteText, oid));
        public IWorkflowOperation PreparePushAlertToMcApp(ActiveAlarm a) => Prepare(() => BuildPushAlertToMcApp(a));
        public IWorkflowOperation PrepareAddToNotificationQueue(ActiveAlarm a, int insertType) => Prepare(() => BuildAddToNotificationQueue(a, insertType));
        public IWorkflowOperation PrepareInsertPushNotificationQueue(ActiveAlarm a, string? victimID, string? offenderID) => Prepare(() => BuildInsertPushNotificationQueue(a, victimID, offenderID));
        public IWorkflowOperation PrepareInsertNotificationQueueVictim(ActiveAlarm a, int insertType, string? victimsEmails, bool isVictimNotification = false) => Prepare(() => BuildInsertNotificationQueueVictim(a, insertType, victimsEmails, isVictimNotification));
        public IWorkflowOperation PrepareCreateAlarmAudit(int type, string? action, int historyID, int stepNo) => Prepare(() => BuildCreateAlarmAudit(type, action, historyID, stepNo));
        public IWorkflowOperation PrepareAddActiveAlarmActionToActivity(int historyID, string? email, int type) => Prepare(() => BuildAddActiveAlarmActionToActivity(historyID, email, type));
        public IWorkflowOperation PrepareClearMcAppAlarm(string? alarms, string? oid) => Prepare(() => BuildClearMcAppAlarm(alarms, oid));
        public IWorkflowOperation PrepareReadPoints(string? numberToRead, int lastSuccessfulProcess) => Prepare(() => BuildReadPoints(numberToRead, lastSuccessfulProcess));
        public IWorkflowOperation PrepareReadLastSuccessfulProcess(int parserID, bool reloadConnectionString = false) => Prepare(() => BuildReadLastSuccessfulProcess(parserID), reloadConnectionString);
        public IWorkflowOperation PrepareReadCurrentActiveAlarmPoint(bool reloadConnectionString = false) => Prepare(BuildReadCurrentActiveAlarmPoint, reloadConnectionString);
        public IWorkflowOperation PrepareUpdateParserActivty(int? systemID, string? parserId) => Prepare(() => BuildUpdateParserActivty(systemID, parserId));
        public IWorkflowOperation PrepareInsertIntoAlarmNotification(int alarmSystemID, int currentStateNo, DateTime expiryTime, string? action) => Prepare(() => BuildInsertIntoAlarmNotification(alarmSystemID, currentStateNo, expiryTime, action));
        public IWorkflowOperation PrepareInsertIntoCurrentAlarmNotification(int alarmSystemID, int currentStateNo, DateTime expiryTime, int displayStateNo, int currentLoopNumber, int processNext) => Prepare(() => BuildInsertIntoCurrentAlarmNotification(alarmSystemID, currentStateNo, expiryTime, displayStateNo, currentLoopNumber, processNext));
        public IWorkflowOperation PrepareGetExpiredAlarms(DateTime currentTime) => Prepare(() => BuildGetExpiredAlarms(currentTime));

        private IWorkflowOperation Prepare(Func<SqlCommand> build, bool reloadConnectionString = false)
        {
            // Service checkpoint reads reload per operation; parsers retain the constructor snapshot.
            var selectedConnectionString = reloadConnectionString ? configuration.GetConnectionString("connstr") : connectionString;
            var connection = new SqlConnection(selectedConnectionString
                ?? throw new InvalidOperationException("Connection string 'connstr' is not configured."));
            SqlCommand? command = null;
            try
            {
                command = build();
                command.Connection = connection;
                return new WorkflowOperation(connection, command);
            }
            catch
            {
                command?.Dispose();
                connection.Dispose();
                throw;
            }
        }

        // Pure command factories: no connection, configuration, clock or SQL execution.
        // Deliberately retain CLR null (not DBNull), bool-to-Int and long-to-Int assignments.
        private static void Add(SqlCommand cmd, string name, SqlDbType type, object? value, int? size = null)
        {
            var parameter = size.HasValue ? cmd.Parameters.Add(name, type, size.Value) : cmd.Parameters.Add(name, type);
            parameter.Value = value!;
        }

        private static SqlCommand Build(string text, Action<SqlCommand>? configure = null, int timeout = 30)
        {
            var cmd = new SqlCommand { CommandText = text, CommandType = CommandType.StoredProcedure, CommandTimeout = timeout };
            try
            {
                configure?.Invoke(cmd);
                return cmd;
            }
            catch
            {
                cmd.Dispose();
                throw;
            }
        }

        internal static SqlCommand BuildFetchClientProfile(int profileType) => Build("ActiveAlarms_GetClientProfile", cmd => Add(cmd, "@HolidayProfile", SqlDbType.Int, profileType));
        internal static SqlCommand BuildReadAllProfiles() => Build("ActiveAlarms_ReadProfileItems", timeout: 0);
        internal static SqlCommand BuildReadAllHolidays() => Build("ActiveAlarms_ReadGroupHolidays");
        internal static SqlCommand BuildReadAllRoles() => Build("spGetListPOGroupRoles");
        internal static SqlCommand BuildReadRoles(ActiveAlarm a, int roleID, int roleAction) => Build("ActiveAlarms_ReadRoles", cmd =>
        {
            // roleAction controls caller mapping only; it is not a SQL parameter.
            Add(cmd, "@RoleID", SqlDbType.Int, roleID);
            Add(cmd, "@POGroup", SqlDbType.VarChar, a.POGroupNum, 32);
        });
        internal static SqlCommand BuildReadVictims() => Build("ActiveAlarms_ReadVictimsEmail");
        internal static SqlCommand BuildReadMEZVictims() => Build("ActiveAlarms_ReadVictims");
        internal static SqlCommand BuildReadAttachedVictimZones() => Build("ActiveAlarms_GetZonesAttachedVictim");
        internal static SqlCommand BuildReadProfileItemsClear() => Build("ActiveAlarms_ReadProfileItemsClear");

        internal static SqlCommand BuildGetInsertEmails(ActiveAlarm a) => Build("ActiveAlarms_ReadAuditEmails", cmd => Add(cmd, "@ClientSystemID", SqlDbType.Int, a.ClientSystemID));
        internal static SqlCommand BuildGetClientEmail(ActiveAlarm a) => Build("ActiveAlarms_ClientEmails", cmd => Add(cmd, "@ClientSystemID", SqlDbType.Int, a.ClientSystemID));
        internal static SqlCommand BuildGetClientText(ActiveAlarm a) => Build("ActiveAlarms_ClientCell", cmd => Add(cmd, "@ClientSystemID", SqlDbType.Int, a.ClientSystemID));

        internal static SqlCommand BuildAddNote(string noteText, string oid)
        {
            ArgumentNullException.ThrowIfNull(noteText);
            ArgumentNullException.ThrowIfNull(oid);
            if (string.IsNullOrWhiteSpace(noteText) || noteText.Length > 1000)
                throw new ArgumentException("Note text must be nonblank and at most 1000 UTF-16 code units.", nameof(noteText));
            if (string.IsNullOrWhiteSpace(oid) || oid.Length > 20)
                throw new ArgumentException("OID must be nonblank and at most 20 characters.", nameof(oid));

            // Parameter names are inferred; verify against the deployed stored procedure signature.
            // No ASCII restriction: varchar encoding and representability depend on database collation.
            return Build("activealarms_AddNote", cmd =>
            {
                Add(cmd, "@Note", SqlDbType.NVarChar, noteText, 1000);
                Add(cmd, "@OID", SqlDbType.VarChar, oid, 20);
            });
        }

        internal static SqlCommand BuildPushAlertToMcApp(ActiveAlarm a) => Build("ActiveAlarms_InsertIntoMCAPP", cmd =>
        {
            Add(cmd, "@HistoryID", SqlDbType.Int, a.HistoryID);
            Add(cmd, "@AgencyID", SqlDbType.Int, a.AgencyID);
            Add(cmd, "@ClientID", SqlDbType.Int, a.ClientSystemID);
            Add(cmd, "@AlarmID", SqlDbType.Int, a.AlarmSystemID);
            Add(cmd, "@DeviceID", SqlDbType.VarChar, a.DeviceID, 20);
            Add(cmd, "@StateID", SqlDbType.Int, a.StateID);
            Add(cmd, "@RecievedDateTime", SqlDbType.Int, a.ReceivedDateTime);
            Add(cmd, "@EventDateTime", SqlDbType.Int, a.EventDateTime);
            Add(cmd, "@ProfileName", SqlDbType.VarChar, a.ProfileName, 100);
            Add(cmd, "@Instruction", SqlDbType.VarChar, a.Instruction, 1500);
            Add(cmd, "@StepNo", SqlDbType.Int, a.StateNo);
            Add(cmd, "@IsUpdatedStep", SqlDbType.Bit, 1);
            Add(cmd, "@ProfileID", SqlDbType.Int, a.ProfileID);
            Add(cmd, "@OID", SqlDbType.VarChar, a.ClientID, 20);
        });

        private static void AddNotificationParameters(SqlCommand cmd, ActiveAlarm a, int insertType, string? address, string source)
        {
            Add(cmd, "@AlarmID", SqlDbType.Int, a.SystemID);
            Add(cmd, "@HistoryID", SqlDbType.Int, a.HistoryID);
            Add(cmd, "@ClientID", SqlDbType.VarChar, a.ClientID, 20);
            Add(cmd, "@MsgReceivedDateTime", SqlDbType.Char, a.MessageReceivedDateTime(), 20);
            Add(cmd, "@EventDateTime", SqlDbType.Char, a.EventRecievedDateTime(), 30);
            Add(cmd, "@MsgSubject", SqlDbType.VarChar, "Alarm Notification", 128);
            Add(cmd, "@MsgToAddress", SqlDbType.NVarChar, address, 1000);
            Add(cmd, "@MsgSource", SqlDbType.VarChar, source, 50);
            Add(cmd, "@InsertType", SqlDbType.Int, insertType);
            Add(cmd, "@FeedBackReq", SqlDbType.Int, a.FeedbackREQ);
        }

        internal static SqlCommand BuildAddToNotificationQueue(ActiveAlarm a, int insertType) => Build("ActiveAlarms_InsertIntoNotificationQueue", cmd => AddNotificationParameters(cmd, a, insertType, a.EmailAddresses, "Sentrak Live Notify Trigger"));
        internal static SqlCommand BuildInsertNotificationQueueVictim(ActiveAlarm a, int insertType, string? victimsEmails, bool isVictimNotification = false) => Build("ActiveAlarms_InsertIntoNotificationQueue", cmd =>
        {
            AddNotificationParameters(cmd, a, insertType, victimsEmails, "Notification Parser");
            Add(cmd, "@IsVictimNotification", SqlDbType.Bit, isVictimNotification);
        });
        internal static SqlCommand BuildInsertPushNotificationQueue(ActiveAlarm a, string? victimID, string? offenderID) => Build("ActiveAlarms_InsertIntoPushNotificationQueue", cmd =>
        {
            Add(cmd, "@HistoryID", SqlDbType.Int, a.HistoryID);
            Add(cmd, "@ClientID", SqlDbType.VarChar, victimID, 32);
            Add(cmd, "@OffenderID", SqlDbType.VarChar, offenderID, 32);
        });
        internal static SqlCommand BuildCreateAlarmAudit(int type, string? action, int historyID, int stepNo) => Build("mcapp_CreateAudit", cmd =>
        {
            Add(cmd, "@Login", SqlDbType.VarChar, "system", 30);
            Add(cmd, "@Type", SqlDbType.Int, type);
            Add(cmd, "@Action", SqlDbType.VarChar, action, 4096);
            Add(cmd, "@historyID", SqlDbType.Int, historyID);
            Add(cmd, "@stepno", SqlDbType.Int, stepNo);
        });
        internal static SqlCommand BuildAddActiveAlarmActionToActivity(int historyID, string? email, int type) => Build("ActiveAlarms_InsertIntoHistory", cmd =>
        {
            Add(cmd, "@HistoryID", SqlDbType.Int, historyID);
            Add(cmd, "@emails", SqlDbType.VarChar, email, 1024);
            Add(cmd, "@type", SqlDbType.Int, type);
        });
        internal static SqlCommand BuildClearMcAppAlarm(string? alarms, string? oid) => Build("ActiveAlarms_ClearActiveAlarm", cmd =>
        {
            Add(cmd, "@Alarms", SqlDbType.VarChar, alarms, 4096);
            Add(cmd, "@OID", SqlDbType.VarChar, oid, 32);
        });

        internal static SqlCommand BuildReadPoints(string? numberToRead, int lastSuccessfulProcess) => Build("ActiveAlarms_ReadAlarms", cmd =>
        {
            // Keep the configuration string; conversion to Int is the provider's responsibility.
            Add(cmd, "@NumberToRead", SqlDbType.Int, numberToRead);
            Add(cmd, "@StartingSystemID", SqlDbType.Int, lastSuccessfulProcess);
        });
        internal static SqlCommand BuildReadLastSuccessfulProcess(int parserID)
        {
            // Only change from original ad hoc SQL: bind the already-int parser ID rather than interpolate it.
            var cmd = Build("SELECT CONVERT(int, [StatusID]) AS StatusID FROM ParserActivity WHERE ParserId = @ParserID ",
                cmd => Add(cmd, "@ParserID", SqlDbType.Int, parserID));
            cmd.CommandType = CommandType.Text;
            return cmd;
        }
        internal static SqlCommand BuildReadCurrentActiveAlarmPoint() => Build("ActiveAlarms_ReadLastAlarmPoint");
        internal static SqlCommand BuildUpdateParserActivty(int? systemID, string? parserId) => Build("ActiveAlarms_UpdateParserActivity", cmd =>
        {
            Add(cmd, "@ParserID", SqlDbType.Int, Convert.ToInt32(parserId));
            Add(cmd, "@CurrSystemID", SqlDbType.Int, systemID);
        });
        internal static SqlCommand BuildInsertIntoAlarmNotification(int alarmSystemID, int currentStateNo, DateTime expiryTime, string? action) => Build("ActiveAlarms_InsertIntoAlarmNotification", cmd =>
        {
            Add(cmd, "@AlarmSystemID", SqlDbType.Int, alarmSystemID);
            Add(cmd, "@CurrentStateNo", SqlDbType.Int, currentStateNo);
            Add(cmd, "@ExpiryTime", SqlDbType.DateTime, expiryTime);
            Add(cmd, "@Action", SqlDbType.VarChar, action, 50);
        });
        internal static SqlCommand BuildInsertIntoCurrentAlarmNotification(int alarmSystemID, int currentStateNo, DateTime expiryTime, int displayStateNo, int currentLoopNumber, int processNext) => Build("ActiveAlarms_InsertIntoCNotificationState", cmd =>
        {
            Add(cmd, "@AlarmSystemID", SqlDbType.Int, alarmSystemID);
            // Deliberate legacy inversion: display number goes to CurrentStateNo.
            Add(cmd, "@CurrentStateNo", SqlDbType.Int, displayStateNo);
            Add(cmd, "@ExpiryTime", SqlDbType.DateTime, expiryTime);
            Add(cmd, "@ParserStateNo", SqlDbType.Int, currentStateNo);
            Add(cmd, "@ProcessNextStep", SqlDbType.Bit, processNext);
            Add(cmd, "@CurrentLoopNumber", SqlDbType.Int, currentLoopNumber);
        });
        internal static SqlCommand BuildGetExpiredAlarms(DateTime currentTime) => Build("ActiveAlarms_RemoveExpiredNotificationAlarms", cmd => Add(cmd, "@CurrentTime", SqlDbType.DateTime, currentTime));

        internal sealed class WorkflowOperation : IWorkflowOperation
        {
            private readonly SqlConnection connection;
            private readonly List<DbDataReader> readers = new();
            private bool disposed;

            internal WorkflowOperation(SqlConnection connection, SqlCommand command)
            {
                this.connection = connection;
                Command = command;
            }

            // Offline contract-test seam; never expose connection strings or command snapshots publicly.
            internal SqlCommand Command { get; }

            public async Task OpenAsync(CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            public async Task<DbDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var reader = await Command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                readers.Add(reader);
                return reader;
            }

            public async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                return await Command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            public Task CloseAsync()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                // Caller-directed close only; do not dispose readers or reset command before a retry.
                return connection.CloseAsync();
            }

            public async ValueTask DisposeAsync()
            {
                if (disposed) return;
                disposed = true;
                try
                {
                    Exception? readerDisposalException = null;
                    foreach (var reader in readers)
                    {
                        try
                        {
                            await reader.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            // A failed reader cleanup must not skip the remaining readers.
                            readerDisposalException ??= exception;
                        }
                    }
                    if (readerDisposalException is not null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(readerDisposalException).Throw();
                    }
                }
                finally
                {
                    try { await Command.DisposeAsync().ConfigureAwait(false); }
                    finally { await connection.DisposeAsync().ConfigureAwait(false); }
                }
            }
        }
    }
}
