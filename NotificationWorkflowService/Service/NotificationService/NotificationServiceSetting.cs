using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Repository;
using static ActiveAlarmsParser.Service.NotificationService.NotificationServiceData;

namespace ActiveAlarmsParser.Service.NotificationService
{
    public static class NotificationServiceSetting
    {
        private static readonly object initializationLock = new object();
        private static INotificationRepository? repository;
        private static SettingsSnapshotCache<Dictionary<string, Dictionary<string, AccountPushNotificationSetting>>>? accountSettingsCache;
        private static SettingsSnapshotCache<Dictionary<string, ReminderSetting>>? reminderSettingsCache;
        private static SettingsSnapshotCache<Dictionary<string, VictimSetting>>? victimSettingsCache;
        private static bool initialized;

        public static void Initialize(IConfiguration configuration, ILogger logger)
        {
            if (Volatile.Read(ref initialized))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(logger);
            Initialize(configuration, logger, new NotificationRepository(configuration, NullLogger<NotificationRepository>.Instance));
        }

        public static void Initialize(IConfiguration configuration, ILogger logger, INotificationRepository repository)
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
                ArgumentNullException.ThrowIfNull(repository);

                NotificationServiceSetting.repository = repository;
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
            return BuildAccountSettings(repository!.ReadAccountNotificationSettingsAsync().GetAwaiter().GetResult());
        }

        private static Dictionary<string, ReminderSetting> readAccountPushNotificationTypes()
        {
            return BuildReminderSettings(repository!.ReadReminderSettingsAsync().GetAwaiter().GetResult());
        }

        private static Dictionary<string, VictimSetting> readVictimNotificationSettings()
        {
            return BuildVictimSettings(repository!.ReadVictimSettingsAsync().GetAwaiter().GetResult());
        }

        internal static Dictionary<string, Dictionary<string, AccountPushNotificationSetting>> BuildAccountSettings(IEnumerable<AccountNotificationSettingRow> rows)
        {
            var snapshot = new Dictionary<string, Dictionary<string, AccountPushNotificationSetting>>();
            foreach (AccountNotificationSettingRow row in rows)
            {
                string userName = row.UserName ?? string.Empty;
                string eventCode = row.EventCode ?? string.Empty;
                string eventType = row.EventType ?? string.Empty;
                string fieldName = row.FieldName ?? string.Empty;
                string pushNotificationToken = row.PushNotificationToken ?? string.Empty;
                string userFullname = row.UserFullname ?? string.Empty;
                string notificationSubType = row.NotificationSubType ?? string.Empty;
                string reminderType = row.ReminderType ?? string.Empty;
                // ReminderEnable remains intentionally unused; it does not filter settings.
                string alternativeText = row.AlternativeText ?? string.Empty;
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

        internal static Dictionary<string, ReminderSetting> BuildReminderSettings(IEnumerable<ReminderSettingRow> rows)
        {
            var snapshot = new Dictionary<string, ReminderSetting>();
            foreach (ReminderSettingRow row in rows)
            {
                string eventCode = row.EventCode ?? string.Empty;
                string eventName = row.EventName ?? string.Empty;
                string reminderType = row.ReminderType ?? string.Empty;
                string notificationSubType = row.NotificationSubType ?? string.Empty;
                string alternativeText = row.AlternativeText ?? string.Empty;
                string eventNotificationType = row.EventNotificationType ?? string.Empty;
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

        internal static Dictionary<string, VictimSetting> BuildVictimSettings(IEnumerable<VictimNotificationSettingRow> rows)
        {
            var snapshot = new Dictionary<string, VictimSetting>();
            foreach (VictimNotificationSettingRow row in rows)
            {
                string oid = row.OID ?? string.Empty;
                string victimproximity = row.victimproximity ?? string.Empty;
                string offbattery = row.offbattery ?? string.Empty;
                string offtamper = row.offtamper ?? string.Empty;
                string offcellgpstatus = row.offcellgpstatus ?? string.Empty;
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

    }
}
