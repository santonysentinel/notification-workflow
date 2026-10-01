using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Threading;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static ActiveAlarmsParser.Service.NotificationService.NotificationServiceData;

namespace ActiveAlarmsParser.Service.NotificationService
{
    public static class NotificationServiceSetting
    {
        private static readonly object initializationLock = new object();
        private static string ClientDatabase = string.Empty;
        private const string READ_ACCOUNT_PUSH_NOTIFICATION_SETTINGS = "ActiveAlarms_ReadAccountPushNotificationSettings";
        private const string READ_ACCOUNT_NOTIFICATION_TYPES = "ActiveAlarms_ReadAccountPushNotificationTypes";
        private const string READ_VICTIM_NOTIFICATION_SETTINGS = "ActiveAlarms_ReadVictimNotificationSettings";
        private static SettingsSnapshotCache<Dictionary<string, Dictionary<string, AccountPushNotificationSetting>>>? accountSettingsCache;
        private static SettingsSnapshotCache<Dictionary<string, ReminderSetting>>? reminderSettingsCache;
        private static SettingsSnapshotCache<Dictionary<string, VictimSetting>>? victimSettingsCache;
        private static ILogger logger = NullLogger.Instance;
        private static bool initialized;

        public static void Initialize(IConfiguration configuration, ILogger logger)
        {
            if (Volatile.Read(ref initialized))
            {
                return;
            }

            lock (initializationLock)
            {
                if (Volatile.Read(ref initialized))
                {
                    return;
                }

                ArgumentNullException.ThrowIfNull(configuration);
                ArgumentNullException.ThrowIfNull(logger);
                string? clientDatabase = configuration["ClientDatabase"];
                if (string.IsNullOrWhiteSpace(clientDatabase))
                {
                    clientDatabase = configuration.GetConnectionString("ClientDatabase");
                }
                if (string.IsNullOrWhiteSpace(clientDatabase))
                {
                    throw new InvalidOperationException("ClientDatabase configuration is required to initialize NotificationServiceSetting.");
                }

                NotificationServiceSetting.logger = logger;
                ClientDatabase = clientDatabase;
                accountSettingsCache = new SettingsSnapshotCache<Dictionary<string, Dictionary<string, AccountPushNotificationSetting>>>(
                    readAccountPushNotificationSettings,
                    new Dictionary<string, Dictionary<string, AccountPushNotificationSetting>>(),
                    logger, "readAccountPushNotificationSettings");
                reminderSettingsCache = new SettingsSnapshotCache<Dictionary<string, ReminderSetting>>(
                    readAccountPushNotificationTypes, new Dictionary<string, ReminderSetting>(),
                    logger, "readAccountPushNotificationTypes");
                victimSettingsCache = new SettingsSnapshotCache<Dictionary<string, VictimSetting>>(
                    readVictimNotificationSettings, new Dictionary<string, VictimSetting>(),
                    logger, "readVictimNotificationSettings");
                reminderSettingsCache.GetSnapshot();
                accountSettingsCache.GetSnapshot();
                victimSettingsCache.GetSnapshot();
                Volatile.Write(ref initialized, true);
            }
        }

        private static void EnsureInitialized()
        {
            if (Volatile.Read(ref initialized))
            {
                return;
            }
            lock (initializationLock)
            {
                if (!Volatile.Read(ref initialized))
                {
                    throw new InvalidOperationException("NotificationServiceSetting.Initialize must be called before using notification settings.");
                }
            }
        }

        public static AccountPushNotificationSetting? GetNotificationServiceSetting(String VictimID, String EventCode)
        {
            EnsureInitialized();
            var snapshot = accountSettingsCache!.GetSnapshot();
            return snapshot.TryGetValue(VictimID, out var settings) && settings.TryGetValue(EventCode, out var setting)
                ? setting : null;
        }

        public static ReminderSetting? GetAppReminderSetting(String EventCode)
        {
            EnsureInitialized();
            var snapshot = reminderSettingsCache!.GetSnapshot();
            return snapshot.TryGetValue(EventCode, out var setting) ? setting : null;
        }

        public static bool CheckVictimReminderSetting(String OID, ReminderSetting? reminder)
        {
            EnsureInitialized();
            var snapshot = victimSettingsCache!.GetSnapshot();
            if (reminder == null || !snapshot.TryGetValue(OID, out var setting))
            {
                return false;
            }
            return reminder.EventNotificationType switch
            {
                "offtamper" => setting.offtamper == "True",
                "offbattery" => setting.offbattery == "True",
                "victimproximity" => setting.victimproximity == "True",
                "offcellgpstatus" => setting.offcellgpstatus == "True",
                _ => false
            };
        }

        private static Dictionary<string, Dictionary<string, AccountPushNotificationSetting>> readAccountPushNotificationSettings()
        {
            NameValueCollection parameters = new NameValueCollection();
            parameters.Add("session", "");
            using DataTable table = GetDataTable(ClientDatabase, true, parameters, READ_ACCOUNT_PUSH_NOTIFICATION_SETTINGS);
            return BuildAccountSettings(table);
        }

        private static Dictionary<string, ReminderSetting> readAccountPushNotificationTypes()
        {
            NameValueCollection parameters = new NameValueCollection();
            parameters.Add("session", "");
            using DataTable table = GetDataTable(ClientDatabase, true, parameters, READ_ACCOUNT_NOTIFICATION_TYPES);
            return BuildReminderSettings(table);
        }

        private static Dictionary<string, VictimSetting> readVictimNotificationSettings()
        {
            NameValueCollection parameters = new NameValueCollection();
            parameters.Add("session", "");
            using DataTable table = GetDataTable(ClientDatabase, true, parameters, READ_VICTIM_NOTIFICATION_SETTINGS);
            return BuildVictimSettings(table);
        }

        internal static Dictionary<string, Dictionary<string, AccountPushNotificationSetting>> BuildAccountSettings(DataTable table)
        {
            var snapshot = new Dictionary<string, Dictionary<string, AccountPushNotificationSetting>>();
            foreach (DataRow row in table.Rows)
            {
                string userName = row["UserName"].ToString()!;
                string eventCode = row["EventCode"].ToString()!;
                string eventType = row["EventType"].ToString()!;
                string fieldName = row["FieldName"].ToString()!;
                string pushNotificationToken = row["PushNotificationToken"].ToString()!;
                string userFullname = row["UserFullname"].ToString()!;
                string notificationSubType = row["NotificationSubType"].ToString()!;
                string reminderType = row["ReminderType"].ToString()!;
                // Preserve the existing column read without changing ReminderEnable behavior.
                _ = row["ReminderEnable"].ToString();
                string alternativeText = row["AlternativeText"].ToString()!;
                if (!snapshot.TryGetValue(userName, out var settings))
                {
                    settings = new Dictionary<string, AccountPushNotificationSetting>();
                    snapshot.Add(userName, settings);
                }
                if (!settings.ContainsKey(eventCode))
                {
                    settings.Add(eventCode, new AccountPushNotificationSetting
                    {
                        UserName = userName,
                        EventCode = eventCode,
                        EventType = eventType,
                        FieldName = fieldName,
                        PushNotificationToken = pushNotificationToken,
                        UserFullname = userFullname,
                        NotificationSubType = notificationSubType,
                        ReminderType = reminderType,
                        AlternativeText = alternativeText
                    });
                }
            }
            return snapshot;
        }

        internal static Dictionary<string, ReminderSetting> BuildReminderSettings(DataTable table)
        {
            var snapshot = new Dictionary<string, ReminderSetting>();
            foreach (DataRow row in table.Rows)
            {
                string eventCode = row["EventCode"].ToString()!;
                string eventName = row["EventName"].ToString()!;
                string reminderType = row["ReminderType"].ToString()!;
                string notificationSubType = row["NotificationSubType"].ToString()!;
                string alternativeText = row["AlternativeText"].ToString()!;
                string eventNotificationType = row["EventNotificationType"].ToString()!;
                if (!snapshot.ContainsKey(eventCode))
                {
                    snapshot.Add(eventCode, new ReminderSetting
                    {
                        AlternativeText = alternativeText,
                        EventName = eventName,
                        NotificationSubType = notificationSubType,
                        ReminderType = reminderType,
                        EventNotificationType = eventNotificationType
                    });
                }
            }
            return snapshot;
        }

        internal static Dictionary<string, VictimSetting> BuildVictimSettings(DataTable table)
        {
            var snapshot = new Dictionary<string, VictimSetting>();
            foreach (DataRow row in table.Rows)
            {
                string oid = row["OID"].ToString()!;
                string victimproximity = row["victimproximity"].ToString()!;
                string offbattery = row["offbattery"].ToString()!;
                string offtamper = row["offtamper"].ToString()!;
                string offcellgpstatus = row["offcellgpstatus"].ToString()!;
                if (!snapshot.ContainsKey(oid))
                {
                    snapshot.Add(oid, new VictimSetting
                    {
                        victimproximity = victimproximity,
                        offbattery = offbattery,
                        offtamper = offtamper,
                        offcellgpstatus = offcellgpstatus
                    });
                }
            }
            return snapshot;
        }

        private static DataTable GetDataTable(String connection, bool isProc, NameValueCollection InputParams, String Stmt)
        {
            const int maxTries = 3;
            for (int currentTry = 0; currentTry < maxTries; currentTry++)
            {
                DataTable table = new DataTable();
                using SqlConnection conn = new SqlConnection(connection);
                SqlTransaction? tran = null;
                try
                {
                    conn.Open();
                    tran = conn.BeginTransaction();
                    using SqlCommand cmd = new SqlCommand(Stmt, conn, tran);
                    cmd.CommandTimeout = 600;
                    cmd.CommandType = isProc ? CommandType.StoredProcedure : CommandType.Text;
                    if (isProc)
                    {
                        SqlCommandBuilder.DeriveParameters(cmd);
                        foreach (SqlParameter p in cmd.Parameters)
                        {
                            if (p.Direction == ParameterDirection.Input)
                            {
                                if (InputParams[p.ParameterName.ToLower().Substring(1)] != null)
                                {
                                    p.Value = InputParams[p.ParameterName.ToLower().Substring(1)];
                                }
                                p.DbType = DbType.AnsiString;
                            }
                            else
                            {
                                p.Value = DBNull.Value;
                            }
                        }
                    }
                    using SqlDataAdapter adapter = new SqlDataAdapter(cmd);
                    adapter.Fill(table);
                    tran.Commit();
                    return table;
                }
                catch (Exception e)
                {
                    table.Dispose();
                    if (tran != null)
                    {
                        try
                        {
                            tran.Rollback();
                        }
                        catch (Exception rollbackException)
                        {
                            NotificationDiagnostics.Failure(logger, "SettingsRollback", rollbackException);
                        }
                    }
                    NotificationDiagnostics.Failure(logger, "SettingsQuery", e);
                    if (currentTry == maxTries - 1)
                    {
                        throw;
                    }
                }
                finally
                {
                    tran?.Dispose();
                }
            }
            throw new InvalidOperationException("SQL retry attempts exhausted.");
        }
    }
}
