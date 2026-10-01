using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;
using static ActiveAlarmsParser.Service.NotificationService.NotificationServiceData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveAlarmsParser.Service.NotificationService
{
    public static class NotificationServiceSetting
    {

        /// <summary>
        /// Defines the log.
        /// </summary>
        

        private static DateTime cacheExpiryTime1 = DateTime.MinValue;
        private static DateTime cacheExpiryTime2 = DateTime.MinValue;
        private static DateTime cacheExpiryTime3 = DateTime.MinValue;

        private static String ClientDatabase = string.Empty;

        private static String READ_ACCOUNT_PUSH_NOTIFICATION_SETTINGS = "ActiveAlarms_ReadAccountPushNotificationSettings";
        private static String READ_ACCOUNT_NOTIFICATION_TYPES = "ActiveAlarms_ReadAccountPushNotificationTypes";
        private static String READ_VICTIM_NOTIFICATION_SETTINGS = "ActiveAlarms_ReadVictimNotificationSettings";

        static Dictionary<string, Dictionary<string, AccountPushNotificationSetting>> AccountPushNotificationSettings = new Dictionary<string, Dictionary<string, AccountPushNotificationSetting>>();

        static Dictionary<string, ReminderSetting> AppReminderDictionary = new Dictionary<string, ReminderSetting>();
        static Dictionary<string, VictimSetting> VictimSettingDictionary = new Dictionary<string, VictimSetting>();

        private static ILogger logger = NullLogger.Instance;
        private static bool initialized;

        public static void Initialize(IConfiguration configuration, ILogger logger)
        {
            if (initialized)
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
            initialized = true;
            readAccountPushNotificationTypes();
            readAccountPushNotificationSettings();
            readVictimNotificationSettings();
        }

        private static void EnsureInitialized()
        {
            if (!initialized)
            {
                throw new InvalidOperationException("NotificationServiceSetting.Initialize must be called before using notification settings.");
            }
        }

        public static AccountPushNotificationSetting? GetNotificationServiceSetting(String VictimID, String EventCode)
        {
            EnsureInitialized();

            if (DateTime.Now > cacheExpiryTime1)
            {
                readAccountPushNotificationSettings();
                cacheExpiryTime1 = DateTime.Now.AddMinutes(5);
            }

            if (AccountPushNotificationSettings.ContainsKey(VictimID))
            {
                if (AccountPushNotificationSettings[VictimID].ContainsKey(EventCode))
                {
                    return AccountPushNotificationSettings[VictimID][EventCode];
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }

        }

        public static ReminderSetting? GetAppReminderSetting(String EventCode)
        {
            EnsureInitialized();

            if (DateTime.Now > cacheExpiryTime2)
            {
                readAccountPushNotificationTypes();
                cacheExpiryTime2 = DateTime.Now.AddMinutes(5);
            }

            if (AppReminderDictionary.ContainsKey(EventCode))
            {
                return AppReminderDictionary[EventCode];
            }
            else
            {
                return null;
            }

        }

        public static bool CheckVictimReminderSetting(String OID, ReminderSetting? reminder)
        {
            EnsureInitialized();
            bool result = false;

            if (DateTime.Now > cacheExpiryTime3)
            {
                readVictimNotificationSettings();
                cacheExpiryTime3 = DateTime.Now.AddMinutes(5);
            }

            if(reminder == null)
            {
                return result;
            }

            if (VictimSettingDictionary.ContainsKey(OID))
            {
                var setting =  VictimSettingDictionary[OID];

                if(reminder.EventNotificationType == "offtamper")
                {
                    if(setting.offtamper == "True")
                    {
                        result = true;
                    }
                } 
                else if (reminder.EventNotificationType == "offbattery")
                {
                    if (setting.offbattery == "True")
                    {
                        result = true;
                    }
                }
                else if (reminder.EventNotificationType == "victimproximity")
                {
                    if (setting.victimproximity == "True")
                    {
                        result = true;
                    }
                }
                else if (reminder.EventNotificationType == "offcellgpstatus")
                {
                    if (setting.offcellgpstatus == "True")
                    {
                        result = true;
                    }
                }
            }

            return result;
        }

        private static void readAccountPushNotificationSettings()
        {
            try
            {
                string connectionString = ClientDatabase;
                NameValueCollection nvm = new NameValueCollection();
                nvm.Add("session", "");
                DataTable dt = GetDataTable(connectionString, true, nvm, READ_ACCOUNT_PUSH_NOTIFICATION_SETTINGS);
                if (dt != null && dt.Rows.Count > 0)
                {
                    AccountPushNotificationSettings.Clear();
                    for (int i = 0; i < dt.Rows.Count; i++)
                    {
                        String UserName = dt.Rows[i]["UserName"].ToString();
                        String EventCode = dt.Rows[i]["EventCode"].ToString();
                        String EventType = dt.Rows[i]["EventType"].ToString();
                        String FieldName = dt.Rows[i]["FieldName"].ToString();
                        String PushNotificationToken = dt.Rows[i]["PushNotificationToken"].ToString();
                        String UserFullname = dt.Rows[i]["UserFullname"].ToString();
                        String NotificationSubType = dt.Rows[i]["NotificationSubType"].ToString();
                        String ReminderType = dt.Rows[i]["ReminderType"].ToString();
                        String ReminderEnable = dt.Rows[i]["ReminderEnable"].ToString();
                        String AlternativeText = dt.Rows[i]["AlternativeText"].ToString();
 
                        //if(UserName == "ID901983")
                        //{
                        //    Console.WriteLine("ID901983");
                        //}

                        if (!AccountPushNotificationSettings.ContainsKey(UserName))
                        {
                            AccountPushNotificationSettings.Add(UserName, new Dictionary<string, AccountPushNotificationSetting>());
                            AccountPushNotificationSettings[UserName].Add(EventCode, new AccountPushNotificationSetting()
                            {
                                UserName = UserName,
                                EventCode = EventCode,
                                EventType = EventType,
                                FieldName = FieldName,
                                PushNotificationToken = PushNotificationToken,
                                UserFullname = UserFullname,
                                NotificationSubType = NotificationSubType,
                                ReminderType = ReminderType,
                                AlternativeText = AlternativeText
                            });
                        }
                        else
                        {
                            if (!AccountPushNotificationSettings[UserName].ContainsKey(EventCode))
                            {
                                AccountPushNotificationSettings[UserName].Add(EventCode, new AccountPushNotificationSetting()
                                {
                                    UserName = UserName,
                                    EventCode = EventCode,
                                    EventType = EventType,
                                    FieldName = FieldName,
                                    PushNotificationToken = PushNotificationToken,
                                    UserFullname = UserFullname,
                                    NotificationSubType = NotificationSubType,
                                    ReminderType = ReminderType,
                                    AlternativeText = AlternativeText
                                });
                            }
                        }
                        
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "readAccountPushNotificationSettings");
            }
        }

        private static void readAccountPushNotificationTypes()
        {
            try
            {
                string connectionString = ClientDatabase;
                NameValueCollection nvm = new NameValueCollection();
                nvm.Add("session", "");
                DataTable dt = GetDataTable(connectionString, true, nvm, READ_ACCOUNT_NOTIFICATION_TYPES);
                if (dt != null && dt.Rows.Count > 0)
                {
                    AppReminderDictionary.Clear();
                    for (int i = 0; i < dt.Rows.Count; i++)
                    {
                        String EventCode = dt.Rows[i]["EventCode"].ToString();
                        String EventName = dt.Rows[i]["EventName"].ToString();
                        String ReminderType = dt.Rows[i]["ReminderType"].ToString();
                        String NotificationSubType = dt.Rows[i]["NotificationSubType"].ToString();
                        String AlternativeText = dt.Rows[i]["AlternativeText"].ToString();
                        String EventNotificationType = dt.Rows[i]["EventNotificationType"].ToString();

                        if (!AppReminderDictionary.ContainsKey(EventCode))
                        {
                            AppReminderDictionary.Add(EventCode, new ReminderSetting { 
                                            AlternativeText = AlternativeText,
                                            EventName = EventName,
                                            NotificationSubType = NotificationSubType,
                                            ReminderType = ReminderType,
                                            EventNotificationType = EventNotificationType
                            });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "readAccountPushNotificationTypes");
            }
        }

        private static void readVictimNotificationSettings()
        {
            try
            {
                string connectionString = ClientDatabase;
                NameValueCollection nvm = new NameValueCollection();
                nvm.Add("session", "");
                DataTable dt = GetDataTable(connectionString, true, nvm, READ_VICTIM_NOTIFICATION_SETTINGS);
                if (dt != null && dt.Rows.Count > 0)
                {
                    VictimSettingDictionary.Clear();
                    for (int i = 0; i < dt.Rows.Count; i++)
                    {
                        String OID = dt.Rows[i]["OID"].ToString();
                        String victimproximity = dt.Rows[i]["victimproximity"].ToString();
                        String offbattery = dt.Rows[i]["offbattery"].ToString();
                        String offtamper = dt.Rows[i]["offtamper"].ToString();
                        String offcellgpstatus = dt.Rows[i]["offcellgpstatus"].ToString();

                        if (!VictimSettingDictionary.ContainsKey(OID))
                        {
                            VictimSettingDictionary.Add(OID, new VictimSetting
                            {
                                victimproximity = victimproximity,
                                offbattery = offbattery,
                                offtamper = offtamper,
                                offcellgpstatus = offcellgpstatus
                            });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "readVictimNotificationSettings");
            }
        }

        private static DataTable GetDataTable(String connection, bool isProc, NameValueCollection InputParams, String Stmt)
        {
            bool Success = false;
            DataTable dt = new DataTable();

            int maxTries = 3;
            int currentTry = 0;

            do
            {
                using (SqlConnection conn = new SqlConnection(connection))
                {
                    SqlTransaction? tran = null;

                    try
                    {
                        conn.Open();
                        tran = conn.BeginTransaction();
                        using (SqlCommand cmd = new SqlCommand(Stmt, conn, tran))
                        {
                            cmd.CommandTimeout = 600;
                            if (isProc)
                            {

                                cmd.CommandType = CommandType.StoredProcedure;
                            }
                            else
                            {
                                cmd.CommandType = CommandType.Text;
                            }

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
                                    else p.Value = DBNull.Value;

                                }
                            }
                            SqlDataAdapter da = new SqlDataAdapter(cmd);
                            da.Fill(dt);
                            Success = true;

                        }
                        tran.Commit();
                    }
                    catch (Exception e)
                    {
                        if (tran != null)
                        {
                            tran.Rollback();
                        }
                        //logger.Error("Error in DataLayer: Trial:" + currentTry + " SP:" + Stmt, e);
                        logger.LogError(e, "Error in DataLayer: Trial:{Trial} SP:{Statement}", currentTry, Stmt);

                        /**********************************************
                        * Throw exception only after the third try
                        * For third try, currentTry = 2
                        * ********************************************/
                        if (currentTry == 2)
                        {
                            throw;
                        }
                    }
                    finally
                    {
                        if (conn != null)
                        {
                            conn.Close();
                        }
                    }
                }
            } while (++currentTry <= maxTries && !Success);

            return dt;

        }
    }
}
