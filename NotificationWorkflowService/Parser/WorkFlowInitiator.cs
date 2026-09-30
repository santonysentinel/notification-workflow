namespace ActiveAlarmsParser
{
    using NotificationWorkflowService.Parser;
    using NotificationWorkflowService.Entity;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Data;
    using System.Data.SqlClient;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    //using static System.Runtime.InteropServices.JavaScript.JSType;

    /// <summary>
    /// Defines the <see cref="Parser" />.
    /// </summary>
    internal class WorkFlowInitiator
    {
        /// <summary>
        /// Used To Update The Console title information - data about the number of points behind and
        /// the current parser sleep time.
        /// </summary>
        private String lastPointsBehind = "";

        /// <summary>
        /// Defines the platForm.
        /// </summary>
        private String platForm = "";

        /// <summary>
        /// Defines the log.
        /// </summary>
        private static readonly Logger<WorkFlowInitiator> log;

        /// <summary>
        /// Defines the activeAlarms.
        /// </summary>
        internal List<ActiveAlarm> activeAlarms = new List<ActiveAlarm>();

        /// <summary>
        /// Defines the clientProfileMapping.
        /// </summary>
        internal Dictionary<String, int> clientProfileMapping = new Dictionary<String, int>();

        /// <summary>
        /// Defines the clientHolidayProfileMapping.
        /// </summary>
        internal Dictionary<String, int> clientHolidayProfileMapping = new Dictionary<String, int>();

        /// <summary>
        /// Defines the activeProfiles.
        /// </summary>
        internal Dictionary<int, Profile> activeProfiles = new Dictionary<int, Profile>();

        /// <summary>
        /// Defines the activeHolidays.
        /// </summary>
        internal Dictionary<String, List<Holiday>> activeHolidays = new Dictionary<String, List<Holiday>>();

        /// <summary>
        /// Defines the ClearEvents.
        /// </summary>
        internal Dictionary<int, List<ProfileItemClear>> ClearEvents = new Dictionary<int, List<ProfileItemClear>>();

        /// <summary>
        /// Defines the roles.
        /// </summary>
        internal Dictionary<String, String> roles = new Dictionary<String, String>();

        /// <summary>
        /// Defines the victims.
        /// </summary>
        internal Dictionary<String, List<Victim>> victims = new Dictionary<String, List<Victim>>();

        /// <summary>
        /// Defines the victims.
        /// </summary>
        internal Dictionary<String, String> victimTypeDict = new Dictionary<String, String>();

        /// <summary>
        /// Defines the pnAlarms.
        /// </summary>
        internal Dictionary<String, HashSet<String>> pnAlarms = new Dictionary<String, HashSet<String>>();

        /// <summary>
        /// Defines the MEZVictims.
        /// </summary>
        internal Dictionary<String, HashSet<String>> MEZVictims = new Dictionary<String, HashSet<String>>();

        /// <summary>
        /// Defines the ZoneAttachedVictims.
        /// </summary>
        internal Dictionary<String, Dictionary<String, HashSet<String>>> AttachedVictimZones = new Dictionary<String, Dictionary<String, HashSet<String>>>();

        /// <summary>
        /// Defines the AlarmsDatabase.
        /// </summary>
        private String AlarmsDatabase = "";

        /// <summary>
        /// Defines the READ_POGROUP_HOLIDAYS.
        /// </summary>
        private readonly String READ_POGROUP_HOLIDAYS = "ActiveAlarms_ReadGroupHolidays";

        /// <summary>
        /// Defines the READ_ACTIVE_ALARMS.
        /// </summary>
        private readonly String READ_ACTIVE_ALARMS = "ActiveAlarms_ReadAlarms";

        /// <summary>
        /// Defines the MCAPP_CREATEAUDIT.
        /// </summary>
        private readonly String MCAPP_CREATEAUDIT = "mcapp_CreateAudit";

        /// <summary>
        /// Defines the INSERT_MCAPP.
        /// </summary>
        private readonly String INSERT_MCAPP = "ActiveAlarms_InsertIntoMCAPP";

        /// <summary>
        /// Defines the INSERT_NOTIFICATIONQUEUE.
        /// </summary>
        private readonly String INSERT_NOTIFICATIONQUEUE = "ActiveAlarms_InsertIntoNotificationQueue";

        /// <summary>
        /// Defines the INSERT_PUSH_NOTIFICATIONQUEUE.
        /// </summary>
        private readonly String INSERT_PUSH_NOTIFICATIONQUEUE = "ActiveAlarms_InsertIntoPushNotificationQueue";

        /// <summary>
        /// Defines the UPDATE_PARSER_ACTIVITY.
        /// </summary>
        private readonly String UPDATE_PARSER_ACTIVITY = "ActiveAlarms_UpdateParserActivity";

        /// <summary>
        /// Defines the INSERT_INTO_HISTORY.
        /// </summary>
        private readonly String INSERT_INTO_HISTORY = "ActiveAlarms_InsertIntoHistory";

        /// <summary>
        /// Defines the READ_AUDIT_HISTORY_EMAILS.
        /// </summary>
        private readonly String READ_AUDIT_HISTORY_EMAILS = "ActiveAlarms_ReadAuditEmails";

        /// <summary>
        /// Defines the READ_CURRENT_ACTIVE_ALARMPOINT.
        /// </summary>
        private readonly String READ_CURRENT_ACTIVE_ALARMPOINT = "ActiveAlarms_ReadLastAlarmPoint";

        /// <summary>
        /// Defines the INSERT_INTO_CURRENT_NOTIFICATION_STATE.
        /// </summary>
        private readonly String INSERT_INTO_CURRENT_NOTIFICATION_STATE = "ActiveAlarms_InsertIntoCNotificationState";

        /// <summary>
        /// Defines the INSERT_INTO_ALARM_NOTIFICATION.
        /// </summary>
        private readonly String INSERT_INTO_ALARM_NOTIFICATION = "ActiveAlarms_InsertIntoAlarmNotification";

        /// <summary>
        /// Defines the READ_ROLES.
        /// </summary>
        private readonly String READ_ROLES = "ActiveAlarms_ReadRoles";

        /// <summary>
        /// Defines the READ_VICTIMS.
        /// </summary>
        private readonly String READ_VICTIMS = "ActiveAlarms_ReadVictims";

        /// <summary>
        /// Defines the READ_VICTIMS.
        /// </summary>
        private readonly String READ_ATTACHED_VICTIMS_ZONES = "ActiveAlarms_GetZonesAttachedVictim";

        /// <summary>
        /// Defines the READ_VICTIMS_EMAIL.
        /// </summary>
        private readonly String READ_VICTIMS_EMAIL = "ActiveAlarms_ReadVictimsEmail";

        /// <summary>
        /// Defines the GET_ROLES.
        /// </summary>
        private readonly String GET_ROLES = "spGetListPOGroupRoles";

        /// <summary>
        /// Defines the GET_CIENT_PROFILE.
        /// </summary>
        private readonly String GET_CIENT_PROFILE = "ActiveAlarms_GetClientProfile";

        /// <summary>
        /// Defines the READ_PROFILE_ITEMS.
        /// </summary>
        private readonly String READ_PROFILE_ITEMS = "ActiveAlarms_ReadProfileItems";

        /// <summary>
        /// Defines the READ_PROFILE_ITEMS_CLEAR.
        /// </summary>
        private readonly String READ_PROFILE_ITEMS_CLEAR = "ActiveAlarms_ReadProfileItemsClear";

        /// <summary>
        /// Defines the CLEAR_ACTIVE_ALARMS.
        /// </summary>
        private readonly String CLEAR_ACTIVE_ALARMS = "ActiveAlarms_ClearActiveAlarm";

        /// <summary>
        /// Defines the READ_CLIENT_EMAIL.
        /// </summary>
        private readonly String READ_CLIENT_EMAIL = "ActiveAlarms_ClientEmails";

        /// <summary>
        /// Defines the READ_CLIENT_TEXT.
        /// </summary>
        private readonly String READ_CLIENT_TEXT = "ActiveAlarms_ClientCell";

        /// <summary>
        /// Defines the Platform
        /// </summary>
        /// 
        private String Platform = ConfigurationManager.AppSettings["Platform"];

        private readonly IConfiguration configuration;

        //private readonly String READ_PUSH_NOTIFICATION_SETTINGS = "ActiveAlarms_GetPushNotificationSettings";
        /// <summary>
        /// Initializes a new instance of the <see cref="Parser"/> class.
        /// </summary>
        /// <param name="connection">The connection<see cref="String"/>.</param>
        public Parser(ILogger<WorkFlowInitiator> logger, IConfiguration configuration)
        {
            setUpConnnectionStrings();
        }

        /// <summary>
        /// The setUpParser.
        /// </summary>
        /// <param name="platform">The platform<see cref="String"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        public bool setUpParser(String platform)
        {
            log4net.Config.XmlConfigurator.Configure();
            platForm = platform;

            if (!FetchClientProfile(0))
            {
                return false;
            }

            if (!FetchClientProfile(1))
            {
                return false;
            }

            if (!readAllProfiles())
            {
                return false;
            }

            if (!readAllHolidays())
            {
                return false;
            }

            if (!readAllRoles())
            {
                return false;
            }

            if (!readVictims())
            {
                return false;
            }

            if (!readMEZVictims())
            {
                return false;
            }

            if (!readAttachedVictimZones())
            {
                return false;
            }

            if (!readProfileItemsClear())
            {
                return false;
            }

            //readPNSettings(); // We do not have push notifications now. (April 2021)

            return true;
        }

        /// <summary>
        /// Reads the connection Strings in the App.config file.
        /// </summary>
        /// <param name="connection">The connection<see cref="String"/>.</param>
        private void setUpConnnectionStrings()
        {
            AlarmsDatabase = configuration.GetConnectionString("connstr")
                ?? throw new InvalidOperationException("Connection string 'connstr' is not configured.");
        }

        /// <summary>
        /// Reads into memory a set of ActiveAlarm events from the active alarms table, these events will then be processed from the 
        /// list activeAlarms.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        public bool readPoints()
        {
            bool success = true;

            int LastProcessedPoint = readLastSuccessfulProcess(Convert.ToInt32(ConfigurationManager.AppSettings[platForm + "ParserID"]));
            int pointsBehind = ReadCurrentActiveAlarmPoint() - LastProcessedPoint;

            int replaceIndex = Console.Title.IndexOf(platForm + " Parser Acvity");

            if (replaceIndex == -1)
            {
                String titleAppend = " - " + platForm + " Parser Acvity: " + pointsBehind + "/" + returnWaitTime(pointsBehind) / 1000 + " Seconds";
                lastPointsBehind = titleAppend;
                Console.Title = Console.Title + titleAppend;
            }
            else
            {
                String titleAppend = " - " + platForm + " Parser Acvity: " + pointsBehind + "/" + returnWaitTime(pointsBehind) / 1000 + " Seconds";
                Console.Title = Console.Title.Replace(lastPointsBehind, titleAppend);
                lastPointsBehind = titleAppend;
            }



            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_ACTIVE_ALARMS;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@NumberToRead", SqlDbType.Int);
                        cmd.Parameters.Add("@StartingSystemID", SqlDbType.Int);

                        cmd.Parameters["@NumberToRead"].Value = ConfigurationManager.AppSettings["NumberOfProcessPoints"];
                        cmd.Parameters["@StartingSystemID"].Value = readLastSuccessfulProcess(Convert.ToInt32(ConfigurationManager.AppSettings[platForm + "ParserID"]));

                        int retries = 3;
                        while (retries > 0)
                        {
                            activeAlarms = new List<ActiveAlarm>();

                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    ActiveAlarm a = new ActiveAlarm()
                                    {
                                        HistoryID = (int)MyDataReader["HistoryID"],
                                        AgencyID = (int)MyDataReader["AgencyID"],
                                        ClientSystemID = (int)MyDataReader["ClientID"],
                                        ClientID = MyDataReader["OID"].ToString(),
                                        ClientTZ = MyDataReader["OTZ"].ToString(),
                                        AlarmSystemID = (int)MyDataReader["AlarmSystemID"],
                                        AlarmID = MyDataReader["AlarmID"].ToString(),
                                        DeviceID = MyDataReader["DeviceID"].ToString(),
                                        StateID = Convert.ToInt32(MyDataReader["StateID"]),
                                        ReceivedDateTime = Convert.ToInt64(MyDataReader["ReceivedDateTime"]),
                                        EventDateTime = Convert.ToInt64(MyDataReader["EventDateTime"]),
                                        AlarmText = MyDataReader["EventDescription"].ToString(),
                                        SystemID = Convert.ToInt32(MyDataReader["SystemID"]),
                                        POGroupNum = MyDataReader["POGroupNum"].ToString(),
                                        POGroup1 = MyDataReader["POGroup1"].ToString(),
                                        POGroup2 = MyDataReader["POGroup2"].ToString(),
                                        POGroup3 = MyDataReader["POGroup3"].ToString(),
                                        EventDateTimeLocal = MyDataReader["EventDateTimeLocal"].ToString(),
                                        EventDateTimeUTC = MyDataReader["EventDateTimeUTC"].ToString(),
                                        ZoneID = MyDataReader["ZoneID"].ToString(),
                                        ZoneCategory = MyDataReader["ZoneCategory"].ToString(),
                                        ZoneName = MyDataReader["ZoneName"].ToString(),
                                        OffenderName = MyDataReader["OffenderName"].ToString(),
                                        ZoneAddress = MyDataReader["ZoneAddress"].ToString(),
                                        PolyZoneName = MyDataReader["PolyZoneName"].ToString(),
                                        MEZEventVictimID = MyDataReader["MEZEventVictimID"].ToString()
                                    };

                                    activeAlarms.Add(a);
                                }
                                getPriorityAndEmail(ref activeAlarms, 0);
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "SQLException on readPoints in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readPoints in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM POINTS IN PARSER ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM POINTS IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM POINTS IN PARSER ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM POINTS IN PARSER " + e);

                }
            }

            return success;
        }

        /// <summary>
        /// Sets the priority, emails and email join for each of the active alarms.
        /// </summary>
        /// <param name="activeAlarms">.</param>
        /// <param name="currentState">The currentState<see cref="int"/>.</param>
        private void getPriorityAndEmail(ref List<ActiveAlarm> activeAlarms, int currentState)
        {
            foreach (ActiveAlarm a in activeAlarms)
            {
                TimeZoneInfo zoneInfo = TimeZoneInfo.FindSystemTimeZoneById(a.ClientTZ);

                DateTime nowUTC = DateTime.UtcNow;
                DateTime cleanedUTCNow = new DateTime(nowUTC.Year, nowUTC.Month, nowUTC.Day, nowUTC.Hour, nowUTC.Minute, nowUTC.Second);

                DateTime current = TimeZoneInfo.ConvertTimeFromUtc(cleanedUTCNow, zoneInfo);

                int day = (int)current.DayOfWeek + 1;

                Boolean found = false;

                Boolean isHoliday = holidayCheck(a, current);

                Profile p = null;

                if (!isHoliday)
                {
                    if (clientProfileMapping.ContainsKey(a.ClientID))
                    {
                        if (activeProfiles.ContainsKey(clientProfileMapping[a.ClientID]))
                        {
                            p = activeProfiles[clientProfileMapping[a.ClientID]];
                        }
                    }
                }
                else
                {
                    if (clientHolidayProfileMapping.ContainsKey(a.ClientID))
                    {
                        if (activeProfiles.ContainsKey(clientHolidayProfileMapping[a.ClientID]))
                        {
                            p = activeProfiles[clientHolidayProfileMapping[a.ClientID]];
                        }
                    }
                }

                if (p != null)
                {
                    if (ClearEvents.ContainsKey(p.ProfileID))
                    {
                        List<ProfileItemClear> clearAlarms = ClearEvents[p.ProfileID];
                        List<ProfileItemClear> events = new List<ProfileItemClear>();

                        if (clearAlarms != null)
                        {
                            events = clearAlarms.FindAll(pic => pic.ClearingEvent == a.AlarmID);
                        }
                        foreach (ProfileItemClear pc in events)
                        {
                            if (p.Events.ContainsKey(pc.EventCode))
                            {
                                Dictionary<int, List<ProfileItem>> dayEvents = p.Events[pc.EventCode];

                                if (dayEvents.ContainsKey(day))
                                {
                                    foreach (ProfileItem pi in dayEvents[day])
                                    {
                                        if (pi.StartTime.TimeOfDay <= current.TimeOfDay && pi.EndTime.TimeOfDay > current.TimeOfDay)
                                        {
                                            a.ProfileID = p.ProfileID;
                                            a.IsAlarmClearingEnabled = true;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (p.Events.ContainsKey(a.AlarmID))
                    {
                        Dictionary<int, List<ProfileItem>> dayEvents = p.Events[a.AlarmID];

                        if (dayEvents.ContainsKey(day))
                        {
                            var pi = FindStepOneProfileItem(dayEvents[day], current);
                            if (pi != null)
                            {
                                found = true;
                                a.Priority = pi.Action;
                                a.EmailAddresses = pi.Email;
                                a.EmailJoin = pi.EmailJoin;
                                a.StateNo = pi.StateNo;
                                a.StateTime = pi.StateTime;
                                a.NextStateNo = pi.NextState;
                                a.Instruction = pi.Instruction;
                                a.ProfileName = p.ProfileName;
                                a.ProfileID = p.ProfileID;
                                a.RoleAction = pi.RoleAction;
                                a.RoleID = pi.RoleID;
                                a.FeedbackREQ = pi.FeedBackRequired;

                                if (pi.Action == 12)
                                {
                                    readRoles(a, pi.RoleID, pi.RoleAction);
                                }
                            }
                            else
                            {
                                try
                                {
                                    log.Info(String.Format("FindStepOneProfileItem: No Profile found meet current time AlarmSysID::{0}", a.SystemID));
                                }
                                catch (Exception exx)
                                {

                                }
                            }
                        }
                    }
                }
                else
                {
                    try
                    {
                        log.Info(String.Format("getPriorityAndEmail: No Profile found meet current time AlarmSysID::{0}", a.SystemID));
                    }
                    catch (Exception exx)
                    {

                    }

                }

                if (!found)
                {
                    try
                    {
                        if (a.AlarmID == "102" || a.AlarmID == "GPS11")
                        {
                            //log.Info(String.Format("getPriorityAndEmailPriorityIssuePI::AlarmSystemID::{0}::ProfileID::{1}::AlarmID = 102 OR GPS11:found::false", a.SystemID, a.ProfileID));
                        }
                    }
                    catch (Exception e)
                    {
                        log.Error("Error getPriorityAndEmail: ", e);
                    }

                    if (ConfigurationManager.AppSettings["DefaultMcAppFlag"].Equals("1"))
                    {
                        a.Priority = 3;
                        a.ProfileName = "";
                        a.Instruction = "";
                    }
                    else
                    {
                        a.Priority = 1;
                    }
                }
            }
        }
        public ProfileItem FindStepOneProfileItem(List<ProfileItem> profileItems, DateTime current)
        {
            try
            {
                Dictionary<int, int> indexes = new Dictionary<int, int>();
                for (int i = 0; i < profileItems.Count; i++)
                {
                    if (profileItems[i].StartTime.TimeOfDay <= current.TimeOfDay && profileItems[i].EndTime.TimeOfDay >= current.TimeOfDay)
                    {
                        //Add all StateNo, and its index in the List of profileItems that current time is in the time range
                        if (!indexes.ContainsKey(profileItems[i].StateNo))
                        {
                            indexes.Add(profileItems[i].StateNo, i);
                        }
                    }
                }

                if (indexes.Count > 0)
                {
                    if (indexes.ContainsKey(0))
                    {
                        //If there are StateNo = 0, use profileItem at StateNo = 0
                        int idx = indexes[0];
                        return profileItems[idx];
                    }
                    else
                    {
                        //Find MinStateNo
                        int minStateNoKey = indexes.Min(pi => pi.Key);
                        //Get Min State ProfileItems index, use profileItem at StateNo = minState
                        int minStateNoIndex = indexes[minStateNoKey];

                        return profileItems[minStateNoIndex];
                    }
                }
                else
                {
                    return null;
                }
            }
            catch (Exception e)
            {
                log.Error("FindStepOneProfileItem Error", e);
                return null;
            }
        }

        /// <summary>
        /// The Main active loop of the step parser, loops through all the activeAlarms and performs the necessary actions.
        /// </summary>
        public void parseAlarms()
        {
            //NotificationService.NotificationServiceSetting.GetAlarmVAPPTexT("", "", "9999");
            ActiveAlarm curr = null;

            try
            {
                Dictionary<int, String> PriorityMapping = new Dictionary<int, string>();

                PriorityMapping.Add(0, "Do Nothing");
                PriorityMapping.Add(1, "Do Nothing");
                PriorityMapping.Add(2, "Auto Email");
                PriorityMapping.Add(3, "McApp");
                PriorityMapping.Add(4, "Auto Fax");
                PriorityMapping.Add(5, "Auto Page");
                PriorityMapping.Add(6, "McApp and Auto Fax");
                PriorityMapping.Add(7, "McApp and Auto Email");
                PriorityMapping.Add(9, "McApp and Auto Page");
                PriorityMapping.Add(11, "Delay");
                PriorityMapping.Add(12, "Role Based");
                PriorityMapping.Add(13, "Email-Victims");
                PriorityMapping.Add(14, "Contact Victims");
                PriorityMapping.Add(15, "Alert Client - Email");
                PriorityMapping.Add(16, "Alert Client - Text");
                PriorityMapping.Add(17, "Text All Victims");

                Dictionary<int, String> RoleActionMapping = new Dictionary<int, String>();
                RoleActionMapping.Add(1, "Call");
                RoleActionMapping.Add(2, "E-mail");
                RoleActionMapping.Add(3, "Text");

                int? sysID = null;

                log.Info(String.Format("ParseAlarm::CurrentNumberOfActiveAlarms::{0}", activeAlarms.Count));
                foreach (ActiveAlarm a in activeAlarms)
                {
                    log.Info(String.Format("ParseAlarm::AlarmSystemID::{0}", a.SystemID));
                    try
                    {
                        curr = a;

                        try
                        {
                            PushAlertsToVictims(a);
                        }
                        catch (Exception ex)
                        {
                            log.Error("PushAlertsToVictims Error", ex);
                        }

                        try
                        {
                            //if (a.ClientID == "ID901491")
                            //{

                            //    PushNotificationToVictim(a);
                            //}

                            PushNotificationToVictim(a);
                        }
                        catch (Exception ex)
                        {
                            log.Error("PushNotificationToVictim Error", ex);
                        }


                        Boolean insert = true;

                        CreateAlarmAudit(15, "", a.HistoryID, 1);

                        StringBuilder sb = new StringBuilder();
                        sb.Append("Action as per the profile assigned:");

                        if (a.IsAlarmClearingEnabled)
                        {
                            List<ProfileItemClear> clearAlarms = ClearEvents[a.ProfileID];
                            List<ProfileItemClear> events = new List<ProfileItemClear>();

                            if (clearAlarms != null)
                            {
                                events = clearAlarms.FindAll(p => p.ClearingEvent == a.AlarmID);
                            }

                            StringBuilder listEvents = new StringBuilder();

                            foreach (ProfileItemClear p in events)
                            {
                                listEvents.Append(p.EventCode + ";");
                            }

                            String clearEvents = listEvents.ToString().TrimEnd(';');

                            ClearMcAppAlarm(clearEvents, a.ClientID);
                        }
                        log.Info(String.Format("ParserBeforeCheck::AlarmSystemID::{0}::Priority::{1}", a.SystemID, a.Priority));
                        log.Info(String.Format("ParserBeforeCheck::AlarmSystemID::{0}::CurrentStateNo::{1}::NextStateNo::{2}::ProcessNextStep::{3}::StateTime::{4}::CurrentLoopNumber::{5}::insert::{6}", a.SystemID, a.CurrentStateNo, a.NextStateNo, a.ProcessNextStep, a.StateTime, a.CurrentStateNo, insert.ToString()));

                        switch (a.Priority)
                        {
                            case 1:
                                sb.Append("Do Nothing");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Do Nothing");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Do Nothing");
                                insert = false;
                                break;
                            case 2:
                                sb.Append("Auto Email");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
                                SendNotificationsToOfficersInSameGroup(a);
                                AddToNotificationQueue(a, 3);
                                String emailAdresses = "Pages sent to: " + a.EmailAddresses;
                                if (emailAdresses.Length > 4096)
                                {
                                    emailAdresses = emailAdresses.Substring(0, 4096);
                                }
                                CreateAlarmAudit(1, emailAdresses, a.HistoryID, 1);
                                AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                                break;
                            case 3:
                                sb.Append("McApp");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp");
                                SendNotificationsToOfficersInSameGroup(a);
                                PushAlertToMcApp(a);
                                a.ProcessNextStep = 0;
                                break;
                            case 4:
                                sb.Append("Auto Fax");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Fax");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Fax");
                                SendNotificationsToOfficersInSameGroup(a);
                                break;
                            case 5:
                                sb.Append("Auto Page");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Page");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Page");
                                SendNotificationsToOfficersInSameGroup(a);
                                AddToNotificationQueue(a, 1);
                                String autoPageEmails = "Pages sent to: " + getInsertEmails(a);
                                if (autoPageEmails.Length > 4096)
                                {
                                    autoPageEmails = autoPageEmails.Substring(0, 4096);
                                }
                                CreateAlarmAudit(3, autoPageEmails, a.HistoryID, 1);
                                AddActiveAlarmActionToActivity(a.HistoryID, autoPageEmails, 0);
                                break;
                            case 6:
                                sb.Append("McApp and Auto Fax");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Fax");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Fax");
                                SendNotificationsToOfficersInSameGroup(a);
                                PushAlertToMcApp(a);
                                a.ProcessNextStep = 0;
                                break;
                            case 7:
                                sb.Append("McApp and Auto Email");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Email");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Email");
                                PushAlertToMcApp(a);
                                SendNotificationsToOfficersInSameGroup(a);
                                AddToNotificationQueue(a, 3);
                                String emailAdresses7 = "Pages sent to: " + a.EmailAddresses;
                                if (emailAdresses7.Length > 4096)
                                {
                                    emailAdresses7 = emailAdresses7.Substring(0, 4096);
                                }
                                CreateAlarmAudit(1, emailAdresses7, a.HistoryID, 1);
                                AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                                a.ProcessNextStep = 0;
                                break;
                            case 9:
                                sb.Append("McApp and Auto Page");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Page");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Page");
                                SendNotificationsToOfficersInSameGroup(a);
                                PushAlertToMcApp(a);
                                AddToNotificationQueue(a, 1);
                                String autoPageMcAppEmails = "Pages sent to: " + getInsertEmails(a);
                                if (autoPageMcAppEmails.Length > 4096)
                                {
                                    autoPageMcAppEmails = autoPageMcAppEmails.Substring(0, 4096);
                                }
                                CreateAlarmAudit(3, autoPageMcAppEmails, a.HistoryID, 1);
                                AddActiveAlarmActionToActivity(a.HistoryID, autoPageMcAppEmails, 0);
                                a.ProcessNextStep = 0;
                                break;
                            case 11:
                                sb.Append("Delay");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Delay " + "(Step " + a.StateNo + ")");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Delay " + "(Step " + a.StateNo + ")");
                                break;
                            case 12:
                                sb.Append("Role Based - ");
                                sb.Append(RoleActionMapping[a.RoleAction] + " ");
                                sb.Append(roles[a.RoleID.ToString()]);
                                switch (a.RoleAction)
                                {
                                    case 1:
                                        if (a.Instruction != "")
                                        {
                                            Console.ForegroundColor = ConsoleColor.Green;
                                            Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp - Call Officers");
                                            log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp - Call Officers");
                                            PushAlertToMcApp(a);
                                            a.ProcessNextStep = 0;
                                        }
                                        break;
                                    case 2:
                                        if (a.EmailAddresses != "")
                                        {
                                            Console.ForegroundColor = ConsoleColor.Green;
                                            Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
                                            log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
                                            AddToNotificationQueue(a, 3);
                                            String emails = "Pages sent to: " + a.EmailAddresses;
                                            if (emails.Length > 4096)
                                            {
                                                emails = emails.Substring(0, 4096);
                                            }
                                            CreateAlarmAudit(14, emails, a.HistoryID, 1);
                                            AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                                        }
                                        break;
                                    case 3:
                                        if (a.EmailAddresses != "")
                                        {
                                            Console.ForegroundColor = ConsoleColor.Green;
                                            Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
                                            log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
                                            AddToNotificationQueue(a, 3);
                                            String txtMessages = "Pages sent to: " + a.EmailAddresses;
                                            if (txtMessages.Length > 4096)
                                            {
                                                txtMessages = txtMessages.Substring(0, 4096);
                                            }
                                            CreateAlarmAudit(14, txtMessages, a.HistoryID, 1);
                                            AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                                        }
                                        break;
                                    default: break;
                                }
                                break;
                            case 13: //send victims email
                                sb.Append("Email-Victims");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Email-Victims");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Email-Victims");

                                String victimsMail = "";
                                try
                                {
                                    List<Victim> offenderVictims = victims[a.ClientID];
                                    StringBuilder victimEmail = new StringBuilder();
                                    foreach (Victim vi in offenderVictims)
                                    {
                                        if (!String.IsNullOrEmpty(vi.Email))
                                        {
                                            victimEmail.Append(vi.Email);
                                            victimEmail.Append(";");
                                        }

                                    }

                                    victimsMail = victimEmail.ToString();
                                    insertNotificationQueueVictim(a, 3, victimsMail, true);
                                }
                                catch (Exception ex)
                                {
                                    log.Error("Read Victim Emails", ex);
                                    victimsMail = "";
                                }

                                String victimEmails = "Pages sent to: " + victimsMail;
                                if (victimEmails.Length > 4096)
                                {
                                    victimEmails = victimEmails.Substring(0, 4096);
                                }
                                CreateAlarmAudit(14, victimEmails, a.HistoryID, 1);
                                AddActiveAlarmActionToActivity(a.HistoryID, victimEmails, 1);
                                break;
                            case 14:
                                sb.Append("Contact Victims");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Contact Victim");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Contact Victim");
                                SendNotificationsToOfficersInSameGroup(a);
                                PushAlertToMcApp(a);
                                a.ProcessNextStep = 0;
                                break;
                            case 15: //alert client email
                                sb.Append("Alert Client - Email");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client Email");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client Email");

                                String clientMail = "";
                                try
                                {
                                    clientMail = getClientEmail(a).Trim();
                                    if (clientMail != String.Empty)
                                    {
                                        insertNotificationQueueVictim(a, 3, clientMail);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    log.Error("clientMail", ex);
                                    clientMail = "";
                                }

                                String cMail = "Pages sent to: " + clientMail;
                                if (cMail.Length > 4096)
                                {
                                    cMail = cMail.Substring(0, 4096);
                                }
                                CreateAlarmAudit(14, cMail, a.HistoryID, 1);
                                AddActiveAlarmActionToActivity(a.HistoryID, cMail, 1);
                                break;
                            case 16: //alert client text
                                sb.Append("Alert Client - Text");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client Text");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client Text");

                                String clientText = "";
                                try
                                {
                                    clientText = getClientText(a).Trim();
                                    if (clientText != String.Empty)
                                    {
                                        insertNotificationQueueVictim(a, 4, clientText);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    log.Error("getClientText", ex);
                                    clientText = "";
                                }

                                String cText = "Pages sent to: " + clientText;
                                if (cText.Length > 4096)
                                {
                                    cText = cText.Substring(0, 4096);
                                }
                                CreateAlarmAudit(14, cText, a.HistoryID, 1);
                                AddActiveAlarmActionToActivity(a.HistoryID, cText, 1);
                                break;
                            case 17: //Send text to all victims
                                sb.Append("Text All Victims");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Text All Victims");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Text All Victims");

                                String victimsText = "";
                                try
                                {
                                    List<Victim> offenderVictims = victims[a.ClientID];
                                    StringBuilder victimText = new StringBuilder();
                                    foreach (Victim vi in offenderVictims)
                                    {
                                        if (!String.IsNullOrEmpty(vi.CellPhone))
                                        {
                                            victimText.Append(vi.CellPhone);
                                            victimText.Append(";");
                                        }

                                    }

                                    victimsText = victimText.ToString();
                                    insertNotificationQueueVictim(a, 4, victimsText, true);
                                }
                                catch (Exception ex)
                                {
                                    log.Error("victimsText", ex);
                                    victimsText = "";
                                }


                                String Msg = "Text sent to: " + victimsText;
                                if (Msg.Length > 4096)
                                {
                                    victimEmails = victimsText.Substring(0, 4096);
                                }
                                CreateAlarmAudit(14, Msg, a.HistoryID, 1);
                                AddActiveAlarmActionToActivity(a.HistoryID, Msg, 1);
                                break;
                            default:
                                sb.Append("Do Nothing");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Do Nothing");
                                log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Do Nothing");
                                insert = false;
                                break;
                        }

                        CreateAlarmAudit(14, sb.ToString(), a.HistoryID, 1);

                        DateTime EventDateTime = DateTime.Parse(a.EventRecievedDateTime());
                        DateTime ReceivedDateTime = DateTime.Parse(a.MessageReceivedDateTime());
                        DateTime ExpiryTimeByEventDateTime = EventDateTime.AddMinutes(a.StateTime);
                        DateTime ExpiryTimeByUTCNow = DateTime.UtcNow.AddMinutes(a.StateTime);

                        DateTime ExpiryTimeApplied;
                        if (ExpiryTimeByEventDateTime < ExpiryTimeByUTCNow)
                        {
                            ExpiryTimeApplied = ExpiryTimeByEventDateTime;
                        }
                        else
                        {
                            ExpiryTimeApplied = ExpiryTimeByUTCNow;
                        }

                        log.Info(String.Format("AlarmSystemID- {0} :: ExpiryTimeByEventDateTime - {1} :: ExpiryTimeByUTCNow - {2} :: ExpiryTimeApplied - {3}"
                                                , a.SystemID.ToString()
                                                , ExpiryTimeByEventDateTime.ToString()
                                                , ExpiryTimeByUTCNow.ToString()
                                                , ExpiryTimeApplied.ToString()
                                                )
                        );

                        insertIntoAlarmNotification(a.SystemID, 1, ExpiryTimeApplied, PriorityMapping[a.Priority]);
                        log.Info(String.Format("ParserAfterCheck::AlarmSystemID::{0}::CurrentStateNo::{1}::NextStateNo::{2}::ProcessNextStep::{3}::StateTime::{4}::CurrentLoopNumber::{5}::insert::{6}", a.SystemID, a.CurrentStateNo, a.NextStateNo, a.ProcessNextStep, a.StateTime, a.CurrentStateNo, insert.ToString()));

                        if (a.NextStateNo != -1 && insert)
                        {
                            insertIntoCurrentAlarmNotification(a.SystemID, a.NextStateNo, ExpiryTimeApplied, a.StateNo + 1, 0, a.ProcessNextStep);
                        }
                        else if (insert)
                        {
                            insertIntoCurrentAlarmNotification(a.SystemID, a.StateNo + 1, ExpiryTimeApplied, a.StateNo + 1, 0, 1);
                        }

                        sysID = a.SystemID;
                    }
                    catch (Exception e)
                    {
                        log.Error("Error processing alarm", e);
                        break;
                    }
                }
                if (sysID != null)
                {
                    updateParserActivty(sysID);
                }

            }
            catch (Exception ex)
            {
                log.Info("FAILED TO PARSE ALARMS " + ex);
                if (curr != null)
                {
                    log.Info("ERROR @ ActiveAlarmID - " + curr.SystemID);
                }
            }
        }

        /// <summary>
        /// When a valid alert is recieved from an offender alert all associated victims.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        public void PushAlertsToVictims(ActiveAlarm a)
        {
            if (MEZVictims.ContainsKey(a.ClientID))
            {
                List<String> victims = MEZVictims[a.ClientID].ToList();
                foreach (String v in victims)
                {
                    if (pnAlarms.ContainsKey(v) && pnAlarms[v].Contains(a.AlarmID))
                    {
                        String victim = "Push notification sent to: " + v;
                        CreateAlarmAudit(3, victim, a.HistoryID, 1);

                        insertPushNotificationQueue(a, v, a.ClientID);
                    }
                }
            }
        }

        /// <summary>
        /// Creates in memory a full dictionary of all the client profiles which is used to map OID to profile ID.
        /// </summary>
        /// <param name="profileType">The profileType<see cref="int"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool FetchClientProfile(int profileType)
        {
            bool success = true;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " Refreshing Client Profiles");
            if (profileType == 0)
            {
                clientProfileMapping = new Dictionary<String, int>();
            }
            else if (profileType == 1)
            {
                clientHolidayProfileMapping = new Dictionary<String, int>();
            }
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = GET_CIENT_PROFILE;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@HolidayProfile", SqlDbType.Int);

                        cmd.Parameters["@HolidayProfile"].Value = profileType;

                        int retries = 5;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    if (profileType == 0)
                                    {
                                        if (!clientProfileMapping.ContainsKey(MyDataReader["OID"].ToString()))
                                        {
                                            clientProfileMapping.Add(MyDataReader["OID"].ToString(), Convert.ToInt32(MyDataReader["ProfileID"]));
                                        }
                                    }
                                    if (profileType == 1)
                                    {
                                        if (!clientHolidayProfileMapping.ContainsKey(MyDataReader["OID"].ToString()))
                                        {
                                            clientHolidayProfileMapping.Add(MyDataReader["OID"].ToString(), Convert.ToInt32(MyDataReader["ProfileID"]));
                                        }
                                    }
                                }
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "SQLException on FetchClientProfile in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on FetchClientProfile in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(10000);
                                    }
                                    else
                                    {
                                        throw;

                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE CLIENT PROFILES ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE CLIENT PROFILES " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(10000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Info("[" + platForm + "] " + "FAILED TO RETRIEVE CLIENT PROFILES " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE CLIENT PROFILES " + e);

                }
            }

            return success;
        }

        /// <summary>
        /// Creates in memory a full dictionary of all the active profiles that the active alarms are used against.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool readAllProfiles()
        {
            bool success = true;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " Refreshing Client Profiles");
            activeProfiles = new Dictionary<int, Profile>();

            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_PROFILE_ITEMS;
                        cmd.CommandTimeout = 0;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        int retries = 5;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    int ProfileID = (int)MyDataReader["ProfileID"];
                                    String ProfileName = MyDataReader["ProfileName"].ToString();
                                    int currentDay = Convert.ToInt32(MyDataReader["Day"].ToString());
                                    String currentEvent = MyDataReader["EventCode"].ToString(); ;

                                    if (!activeProfiles.ContainsKey(ProfileID))
                                    {
                                        Dictionary<String, Dictionary<int, List<ProfileItem>>> profileEvents = new Dictionary<String, Dictionary<int, List<ProfileItem>>>();
                                        Dictionary<int, List<ProfileItem>> dayEvents = new Dictionary<int, List<ProfileItem>>();
                                        List<ProfileItem> profileItems = new List<ProfileItem>();


                                        ProfileItem pi = new ProfileItem()
                                        {
                                            ProfileID = ProfileID,
                                            EventCode = currentEvent,
                                            StartTime = DateTime.ParseExact(MyDataReader["StartTime"].ToString(), "HH:mm:ss", CultureInfo.InvariantCulture),
                                            EndTime = DateTime.ParseExact(MyDataReader["EndTime"].ToString(), "HH:mm:ss", CultureInfo.InvariantCulture),
                                            Day = currentDay,
                                            Action = Convert.ToInt32(MyDataReader["Action"]),
                                            HoldDuration = (int)MyDataReader["HoldDuration"],
                                            GracePeriod = (int)MyDataReader["GracePeriod"],
                                            Instruction = MyDataReader["Instruction"].ToString(),
                                            Email = MyDataReader["EmailAddress"].ToString(),
                                            EmailJoin = Convert.ToInt32(MyDataReader["EmailJoin"]),
                                            StateNo = Convert.ToInt32(MyDataReader["StateNo"]),
                                            StateTime = (int)MyDataReader["StateTime"],
                                            FeedBackRequired = Convert.ToBoolean(MyDataReader["FeedbackRequired"]),
                                            ProfileType = (int)MyDataReader["ProfileType"],
                                            TimeIntervalsID = (int)MyDataReader["TimeIntervalsID"],
                                            NextState = Convert.ToInt32(MyDataReader["NextState"]),
                                            LoopStartState = Convert.ToInt32(MyDataReader["LoopStartState"]),
                                            NumberOfLoops = Convert.ToInt32(MyDataReader["NumberOfLoops"]),
                                            RoleID = Convert.ToInt32(MyDataReader["RoleID"]),
                                            RoleAction = Convert.ToInt32(MyDataReader["RoleAction"])
                                        };
                                        profileItems.Add(pi);
                                        dayEvents.Add(currentDay, profileItems);
                                        profileEvents.Add(currentEvent, dayEvents);

                                        Profile p = new Profile()
                                        {
                                            ProfileID = ProfileID,
                                            ProfileName = ProfileName,
                                            Events = profileEvents
                                        };

                                        activeProfiles.Add(ProfileID, p);
                                    }
                                    else
                                    {
                                        Profile p = activeProfiles[ProfileID];

                                        Dictionary<String, Dictionary<int, List<ProfileItem>>> profileEvents = p.Events;

                                        if (!profileEvents.ContainsKey(currentEvent))
                                        {
                                            Dictionary<int, List<ProfileItem>> dayEvents = new Dictionary<int, List<ProfileItem>>();

                                            List<ProfileItem> profileItems = new List<ProfileItem>();

                                            ProfileItem pi = new ProfileItem()
                                            {
                                                ProfileID = ProfileID,
                                                EventCode = currentEvent,
                                                StartTime = DateTime.ParseExact(MyDataReader["StartTime"].ToString(), "HH:mm:ss", CultureInfo.InvariantCulture),
                                                EndTime = DateTime.ParseExact(MyDataReader["EndTime"].ToString(), "HH:mm:ss", CultureInfo.InvariantCulture),
                                                Day = currentDay,
                                                Action = Convert.ToInt32(MyDataReader["Action"]),
                                                HoldDuration = (int)MyDataReader["HoldDuration"],
                                                GracePeriod = (int)MyDataReader["GracePeriod"],
                                                Instruction = MyDataReader["Instruction"].ToString(),
                                                Email = MyDataReader["EmailAddress"].ToString(),
                                                EmailJoin = Convert.ToInt32(MyDataReader["EmailJoin"]),
                                                StateNo = Convert.ToInt32(MyDataReader["StateNo"]),
                                                StateTime = (int)MyDataReader["StateTime"],
                                                FeedBackRequired = Convert.ToBoolean(MyDataReader["FeedbackRequired"]),
                                                ProfileType = (int)MyDataReader["ProfileType"],
                                                TimeIntervalsID = (int)MyDataReader["TimeIntervalsID"],
                                                NextState = Convert.ToInt32(MyDataReader["NextState"]),
                                                LoopStartState = Convert.ToInt32(MyDataReader["LoopStartState"]),
                                                NumberOfLoops = Convert.ToInt32(MyDataReader["NumberOfLoops"]),
                                                RoleID = Convert.ToInt32(MyDataReader["RoleID"]),
                                                RoleAction = Convert.ToInt32(MyDataReader["RoleAction"]),
                                            };
                                            profileItems.Add(pi);

                                            dayEvents.Add(currentDay, profileItems);

                                            profileEvents.Add(currentEvent, dayEvents);
                                        }
                                        else
                                        {
                                            Dictionary<int, List<ProfileItem>> dayEvents = profileEvents[currentEvent];

                                            if (!dayEvents.ContainsKey(currentDay))
                                            {
                                                List<ProfileItem> profileItems = new List<ProfileItem>();

                                                ProfileItem pi = new ProfileItem()
                                                {
                                                    ProfileID = ProfileID,
                                                    EventCode = currentEvent,
                                                    StartTime = DateTime.ParseExact(MyDataReader["StartTime"].ToString(), "HH:mm:ss", CultureInfo.InvariantCulture),
                                                    EndTime = DateTime.ParseExact(MyDataReader["EndTime"].ToString(), "HH:mm:ss", CultureInfo.InvariantCulture),
                                                    Day = currentDay,
                                                    Action = Convert.ToInt32(MyDataReader["Action"]),
                                                    HoldDuration = (int)MyDataReader["HoldDuration"],
                                                    GracePeriod = (int)MyDataReader["GracePeriod"],
                                                    Instruction = MyDataReader["Instruction"].ToString(),
                                                    Email = MyDataReader["EmailAddress"].ToString(),
                                                    EmailJoin = Convert.ToInt32(MyDataReader["EmailJoin"]),
                                                    StateNo = Convert.ToInt32(MyDataReader["StateNo"]),
                                                    StateTime = (int)MyDataReader["StateTime"],
                                                    FeedBackRequired = Convert.ToBoolean(MyDataReader["FeedbackRequired"]),
                                                    ProfileType = (int)MyDataReader["ProfileType"],
                                                    TimeIntervalsID = (int)MyDataReader["TimeIntervalsID"],
                                                    NextState = Convert.ToInt32(MyDataReader["NextState"]),
                                                    LoopStartState = Convert.ToInt32(MyDataReader["LoopStartState"]),
                                                    NumberOfLoops = Convert.ToInt32(MyDataReader["NumberOfLoops"]),
                                                    RoleID = Convert.ToInt32(MyDataReader["RoleID"]),
                                                    RoleAction = Convert.ToInt32(MyDataReader["RoleAction"])
                                                };
                                                profileItems.Add(pi);
                                                dayEvents.Add(currentDay, profileItems);
                                            }
                                            else
                                            {
                                                List<ProfileItem> profileItems = dayEvents[currentDay];

                                                ProfileItem pi = new ProfileItem()
                                                {
                                                    ProfileID = ProfileID,
                                                    EventCode = currentEvent,
                                                    StartTime = DateTime.ParseExact(MyDataReader["StartTime"].ToString(), "HH:mm:ss", CultureInfo.InvariantCulture),
                                                    EndTime = DateTime.ParseExact(MyDataReader["EndTime"].ToString(), "HH:mm:ss", CultureInfo.InvariantCulture),
                                                    Day = currentDay,
                                                    Action = Convert.ToInt32(MyDataReader["Action"]),
                                                    HoldDuration = (int)MyDataReader["HoldDuration"],
                                                    GracePeriod = (int)MyDataReader["GracePeriod"],
                                                    Instruction = MyDataReader["Instruction"].ToString(),
                                                    Email = MyDataReader["EmailAddress"].ToString(),
                                                    EmailJoin = Convert.ToInt32(MyDataReader["EmailJoin"]),
                                                    StateNo = Convert.ToInt32(MyDataReader["StateNo"]),
                                                    StateTime = (int)MyDataReader["StateTime"],
                                                    FeedBackRequired = Convert.ToBoolean(MyDataReader["FeedbackRequired"]),
                                                    ProfileType = (int)MyDataReader["ProfileType"],
                                                    TimeIntervalsID = (int)MyDataReader["TimeIntervalsID"],
                                                    NextState = Convert.ToInt32(MyDataReader["NextState"]),
                                                    LoopStartState = Convert.ToInt32(MyDataReader["LoopStartState"]),
                                                    NumberOfLoops = Convert.ToInt32(MyDataReader["NumberOfLoops"]),
                                                    RoleID = Convert.ToInt32(MyDataReader["RoleID"]),
                                                    RoleAction = Convert.ToInt32(MyDataReader["RoleAction"])
                                                };
                                                profileItems.Add(pi);
                                            }
                                        }
                                    }
                                }

                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "SQLException on readAllProfiles in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readAllProfiles in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(10000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }

                                //System.Environment.Exit(1);
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE PROFILES ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE PROFILES IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(10000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE PROFILES ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE PROFILES IN PARSER " + e);


                    //System.Environment.Exit(1);
                }
            }

            return success;
        }

        /// <summary>
        /// Creates in memory a full dictionary of all the active profiles that the active alarms are used against.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool readAllHolidays()
        {
            bool success = true;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " Refreshing PO GRoup Holidays");
            activeHolidays = new Dictionary<String, List<Holiday>>();

            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_POGROUP_HOLIDAYS;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                List<Holiday> holidays = new List<Holiday>();
                                String currentPOGroup = "";

                                while (MyDataReader.Read())
                                {
                                    if (currentPOGroup != MyDataReader["POGroup"].ToString())
                                    {
                                        if (currentPOGroup != "")
                                        {
                                            activeHolidays.Add(currentPOGroup, holidays);
                                            holidays = new List<Holiday>();
                                        }
                                        currentPOGroup = MyDataReader["POGroup"].ToString();
                                    }

                                    DateTime startDate = Convert.ToDateTime(MyDataReader["StartDate"].ToString());
                                    DateTime endDate = Convert.ToDateTime(MyDataReader["EndDate"].ToString());
                                    String holidayName = MyDataReader["HolidayName"].ToString();

                                    Holiday h = new Holiday()
                                    {
                                        HolidayName = holidayName,
                                        StartDate = startDate,
                                        EndDate = endDate
                                    };
                                    holidays.Add(h);
                                }

                                if (holidays.Count > 0)
                                {
                                    activeHolidays.Add(currentPOGroup, holidays);
                                }

                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "SQLException on readAllHolidays in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readAllHolidays in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(5000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE HOLIDAYS ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE HOLIDAYS IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(5000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE HOLIDAYS ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE HOLIDAYS IN PARSER " + e);

                }
            }
            return success;
        }

        /// <summary>
        /// Reads in the list of all POGroup Roles.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool readAllRoles()
        {
            bool success = true;

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " Refreshing POGroup Roles");
            roles = new Dictionary<String, String>();

            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = GET_ROLES;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    roles.Add(MyDataReader["SystemID"].ToString(), MyDataReader["RoleName"].ToString());
                                }
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "SQLException on readAllRoles in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readAllRoles in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(3000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE ROLES ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ROLES IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(3000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE ROLES ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ROLES IN PARSER " + e);

                }
            }
            return success;
        }

        /// <summary>
        /// Inserts events into the McAPP queue, if the item priority numbers require it.
        /// </summary>
        /// <param name="a">.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool PushAlertToMcApp(ActiveAlarm a)
        {
            bool success = true;
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = INSERT_MCAPP;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@HistoryID", SqlDbType.Int);
                        cmd.Parameters.Add("@AgencyID", SqlDbType.Int);
                        cmd.Parameters.Add("@ClientID", SqlDbType.Int);
                        cmd.Parameters.Add("@AlarmID", SqlDbType.Int);
                        cmd.Parameters.Add("@DeviceID", SqlDbType.VarChar, 20);
                        cmd.Parameters.Add("@StateID", SqlDbType.Int);
                        cmd.Parameters.Add("@RecievedDateTime", SqlDbType.Int);
                        cmd.Parameters.Add("@EventDateTime", SqlDbType.Int);
                        cmd.Parameters.Add("@ProfileName", SqlDbType.VarChar, 100);
                        cmd.Parameters.Add("@Instruction", SqlDbType.VarChar, 1500);
                        cmd.Parameters.Add("@StepNo", SqlDbType.Int);
                        cmd.Parameters.Add("@IsUpdatedStep", SqlDbType.Bit);
                        cmd.Parameters.Add("@ProfileID", SqlDbType.Int);
                        cmd.Parameters.Add("@OID", SqlDbType.VarChar, 20);

                        cmd.Parameters["@HistoryID"].Value = a.HistoryID;
                        cmd.Parameters["@AgencyID"].Value = a.AgencyID;
                        cmd.Parameters["@ClientID"].Value = a.ClientSystemID;
                        cmd.Parameters["@AlarmID"].Value = a.AlarmSystemID;
                        cmd.Parameters["@DeviceID"].Value = a.DeviceID;
                        cmd.Parameters["@StateID"].Value = a.StateID;
                        cmd.Parameters["@RecievedDateTime"].Value = a.ReceivedDateTime;
                        cmd.Parameters["@EventDateTime"].Value = a.EventDateTime;
                        cmd.Parameters["@ProfileName"].Value = a.ProfileName;
                        cmd.Parameters["@Instruction"].Value = a.Instruction;
                        cmd.Parameters["@StepNo"].Value = a.StateNo;
                        cmd.Parameters["@IsUpdatedStep"].Value = 1;
                        cmd.Parameters["@ProfileID"].Value = a.ProfileID;
                        cmd.Parameters["@OID"].Value = a.ClientID;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "SQLException on PushAlertToMcApp in Step Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on PushAlertToMcApp in Step Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "FAILED TO RUN INSERT_MCAPP IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_MCAPP IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Info("[" + platForm + "] " + "FAILED TO RUN INSERT_MCAPP IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_MCAPP IN STEP PARSER " + e);
                    throw;
                }
            }
            return success;
        }

        /// <summary>
        /// Inserts The Active Alarm Into The Notification Queue.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="insertType">The insertType<see cref="int"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool AddToNotificationQueue(ActiveAlarm a, int insertType)
        {
            bool success = true;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = INSERT_NOTIFICATIONQUEUE;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@AlarmID", SqlDbType.Int);
                        cmd.Parameters.Add("@HistoryID", SqlDbType.Int);
                        cmd.Parameters.Add("@ClientID", SqlDbType.VarChar, 20);
                        cmd.Parameters.Add("@MsgReceivedDateTime", SqlDbType.Char, 20);
                        cmd.Parameters.Add("@EventDateTime", SqlDbType.Char, 30);
                        cmd.Parameters.Add("@MsgSubject", SqlDbType.VarChar, 128);
                        cmd.Parameters.Add("@MsgToAddress", SqlDbType.NVarChar, 1000);
                        cmd.Parameters.Add("@MsgSource", SqlDbType.VarChar, 50);
                        cmd.Parameters.Add("@InsertType", SqlDbType.Int);
                        cmd.Parameters.Add("@FeedBackReq", SqlDbType.Int);

                        cmd.Parameters["@AlarmID"].Value = a.SystemID;
                        cmd.Parameters["@HistoryID"].Value = a.HistoryID;
                        cmd.Parameters["@ClientID"].Value = a.ClientID;
                        cmd.Parameters["@MsgReceivedDateTime"].Value = a.MessageReceivedDateTime();
                        cmd.Parameters["@EventDateTime"].Value = a.EventRecievedDateTime();
                        cmd.Parameters["@MsgSubject"].Value = "Alarm Notification";
                        cmd.Parameters["@MsgToAddress"].Value = a.EmailAddresses;
                        cmd.Parameters["@MsgSource"].Value = "Sentrak Live Notify Trigger";
                        cmd.Parameters["@InsertType"].Value = insertType;
                        cmd.Parameters["@FeedBackReq"].Value = a.FeedbackREQ;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "SQLException on AddToNotificationQueue in Step Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on AddToNotificationQueue in Step Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_NOTIFICATIONQUEUE IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Info("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_NOTIFICATIONQUEUE IN STEP PARSER " + e);
                    throw;
                }
            }
            return success;
        }

        /// <summary>
        /// Inserts The Active Alarm Into The Push Notification Queue.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="VictimID">The VictimID<see cref="String"/>.</param>
        /// <param name="OffenderID">The OffenderID<see cref="String"/>.</param>
        private void insertPushNotificationQueue(ActiveAlarm a, String VictimID, String OffenderID)
        {
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = INSERT_PUSH_NOTIFICATIONQUEUE;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@HistoryID", SqlDbType.Int);
                        cmd.Parameters.Add("@ClientID", SqlDbType.VarChar, 32);
                        cmd.Parameters.Add("@OffenderID", SqlDbType.VarChar, 32);

                        cmd.Parameters["@HistoryID"].Value = a.HistoryID;
                        cmd.Parameters["@ClientID"].Value = VictimID;
                        cmd.Parameters["@OffenderID"].Value = OffenderID;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.Error("[" + platForm + "] " + "SQLException on insertPushNotificationQueue in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on insertPushNotificationQueue in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                    }
                                    else
                                    {
                                        throw ex;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                log.Error("[" + platForm + "] " + "FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE " + exc);
                                throw exc;
                            }


                        }
                    }
                }
                catch (Exception e)
                {
                    log.Error("[" + platForm + "] " + "FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE " + e);

                }
            }
        }

        /// <summary>
        /// Inserts The Active Alarm Into The Notification Queue.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="insertType">The insertType<see cref="int"/>.</param>
        /// <param name="victimsEmails">The victimsEmails<see cref="String"/>.</param>
        /// <param name="isVictimNotification">The isVictimNotification<see cref="bool"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool insertNotificationQueueVictim(ActiveAlarm a, int insertType, String victimsEmails, bool isVictimNotification = false)
        {
            bool success = true;

            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = INSERT_NOTIFICATIONQUEUE;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@AlarmID", SqlDbType.Int);
                        cmd.Parameters.Add("@HistoryID", SqlDbType.Int);
                        cmd.Parameters.Add("@ClientID", SqlDbType.VarChar, 20);
                        cmd.Parameters.Add("@MsgReceivedDateTime", SqlDbType.Char, 20);
                        cmd.Parameters.Add("@EventDateTime", SqlDbType.Char, 30);
                        cmd.Parameters.Add("@MsgSubject", SqlDbType.VarChar, 128);
                        cmd.Parameters.Add("@MsgToAddress", SqlDbType.NVarChar, 1000);
                        cmd.Parameters.Add("@MsgSource", SqlDbType.VarChar, 50);
                        cmd.Parameters.Add("@InsertType", SqlDbType.Int);
                        cmd.Parameters.Add("@FeedBackReq", SqlDbType.Int);
                        cmd.Parameters.Add("@IsVictimNotification", SqlDbType.Bit);

                        cmd.Parameters["@AlarmID"].Value = a.SystemID;
                        cmd.Parameters["@HistoryID"].Value = a.HistoryID;
                        cmd.Parameters["@ClientID"].Value = a.ClientID;
                        cmd.Parameters["@MsgReceivedDateTime"].Value = a.MessageReceivedDateTime();
                        cmd.Parameters["@EventDateTime"].Value = a.EventRecievedDateTime();
                        cmd.Parameters["@MsgSubject"].Value = "Alarm Notification";
                        cmd.Parameters["@MsgToAddress"].Value = victimsEmails;
                        cmd.Parameters["@MsgSource"].Value = "Notification Parser";
                        cmd.Parameters["@InsertType"].Value = insertType;
                        cmd.Parameters["@FeedBackReq"].Value = a.FeedbackREQ;
                        cmd.Parameters["@IsVictimNotification"].Value = isVictimNotification;


                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "SQLException on insertNotificationQueueVictim in Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on insertNotificationQueueVictim in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(3000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_NOTIFICATIONQUEUE " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(3000);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Info("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_NOTIFICATIONQUEUE " + e);
                    throw;
                }
            }

            return success;
        }

        /// <summary>
        /// Inserts The Active Alarm Into The Notification Queue.
        /// </summary>
        /// <param name="systemID">The systemID<see cref="int?"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool updateParserActivty(int? systemID)
        {
            bool success = false;
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = UPDATE_PARSER_ACTIVITY;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@ParserID", SqlDbType.Int);
                        cmd.Parameters.Add("@CurrSystemID", SqlDbType.Int);

                        cmd.Parameters["@ParserID"].Value = Convert.ToInt32(ConfigurationManager.AppSettings[platForm + "ParserID"]);
                        cmd.Parameters["@CurrSystemID"].Value = systemID;



                        int retries = 6;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                success = true;
                                log.Info("Successfully updated AAID : " + systemID);
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "SQLException on updateParserActivty in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on updateParserActivty in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(5000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "FAILED TO RUN UPDATE_PARSER_ACTIVITY ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN UPDATE_PARSER_ACTIVITY " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(5000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Error("[" + platForm + "] " + "FAILED TO RUN UPDATE_PARSER_ACTIVITY IN PARSER ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN UPDATE_PARSER_ACTIVITY IN PARSER " + e);
                    throw;
                }
            }
            return success;
        }

        /// <summary>
        /// Inserts a row into the Audit Alarms Table.
        /// </summary>
        /// <param name="type">.</param>
        /// <param name="action">.</param>
        /// <param name="historyID">.</param>
        /// <param name="StepNo">The StepNo<see cref="int"/>.</param>
        private void CreateAlarmAudit(int type, String action, int historyID, int StepNo)
        {
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = MCAPP_CREATEAUDIT;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@Login", SqlDbType.VarChar, 30);
                        cmd.Parameters.Add("@Type", SqlDbType.Int);
                        cmd.Parameters.Add("@Action", SqlDbType.VarChar, 4096);
                        cmd.Parameters.Add("@historyID", SqlDbType.Int);
                        cmd.Parameters.Add("@stepno", SqlDbType.Int);

                        cmd.Parameters["@Login"].Value = "system";
                        cmd.Parameters["@Type"].Value = type;
                        cmd.Parameters["@Action"].Value = action;
                        cmd.Parameters["@historyID"].Value = historyID;
                        cmd.Parameters["@stepno"].Value = StepNo;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.Error("[" + platForm + "] " + "SQLException on CreateAlarmAudit in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on CreateAlarmAudit in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                log.Error("[" + platForm + "] " + "FAILED TO RUN MCAPP_CREATEAUDIT IN PARSER ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CREATEAUDIT IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    log.Error("[" + platForm + "] " + "FAILED TO RUN MCAPP_CREATEAUDIT IN PARSER ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CREATEAUDIT IN PARSER " + e);

                }
            }
        }

        /// <summary>
        /// Inserts a Row Into The History Table.
        /// </summary>
        /// <param name="historyID">.</param>
        /// <param name="email">The email<see cref="String"/>.</param>
        /// <param name="type">.</param>
        private void AddActiveAlarmActionToActivity(int historyID, String email, int type)
        {
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = INSERT_INTO_HISTORY;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@HistoryID", SqlDbType.Int);
                        cmd.Parameters.Add("@emails", SqlDbType.VarChar, 1024);
                        cmd.Parameters.Add("@type", SqlDbType.Int);

                        cmd.Parameters["@HistoryID"].Value = historyID;
                        cmd.Parameters["@emails"].Value = email;
                        cmd.Parameters["@type"].Value = type;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.Error("[" + platForm + "] " + "SQLException on ActiveAlarms_InsertIntoHistory in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on ActiveAlarms_InsertIntoHistory in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                log.Error("[" + platForm + "] " + "FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    log.Error("[" + platForm + "] " + "FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER " + e);

                }
            }
        }

        /// <summary>
        /// Inserts A Row into the Alarm Notification Table - used as an archive of all processed alarms.
        /// </summary>
        /// <param name="AlarmSystemID">.</param>
        /// <param name="CurentStateNo">.</param>
        /// <param name="ExpiryTime">.</param>
        /// <param name="Action">.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool insertIntoAlarmNotification(int AlarmSystemID, int CurentStateNo, DateTime ExpiryTime, String Action)
        {
            bool success = true;
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = INSERT_INTO_ALARM_NOTIFICATION;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@AlarmSystemID", SqlDbType.Int);
                        cmd.Parameters.Add("@CurrentStateNo", SqlDbType.Int);
                        cmd.Parameters.Add("@ExpiryTime", SqlDbType.DateTime);
                        cmd.Parameters.Add("@Action", SqlDbType.VarChar, 50);

                        cmd.Parameters["@AlarmSystemID"].Value = AlarmSystemID;
                        cmd.Parameters["@CurrentStateNo"].Value = CurentStateNo;
                        cmd.Parameters["@ExpiryTime"].Value = ExpiryTime;
                        cmd.Parameters["@Action"].Value = Action;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "SQLException on insertIntoAlarmNotification in Step Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on insertIntoAlarmNotification in Step Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_ALARM_NOTIFICATION IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_ALARM_NOTIFICATION IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Info("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_ALARM_NOTIFICATION IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_ALARM_NOTIFICATION IN STEP PARSER " + e);
                    throw;
                }
            }
            return success;
        }

        /// <summary>
        /// The insertIntoCurrentAlarmNotification.
        /// </summary>
        /// <param name="AlarmSystemID">The AlarmSystemID<see cref="int"/>.</param>
        /// <param name="CurentStateNo">The CurentStateNo<see cref="int"/>.</param>
        /// <param name="ExpiryTime">The ExpiryTime<see cref="DateTime"/>.</param>
        /// <param name="DisplayStateNo">The DisplayStateNo<see cref="int"/>.</param>
        /// <param name="currentLoopNumber">The currentLoopNumber<see cref="int"/>.</param>
        /// <param name="processNext">The processNext<see cref="int"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool insertIntoCurrentAlarmNotification(int AlarmSystemID, int CurentStateNo, DateTime ExpiryTime, int DisplayStateNo, int currentLoopNumber, int processNext)
        {
            bool success = true;
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = INSERT_INTO_CURRENT_NOTIFICATION_STATE;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@AlarmSystemID", SqlDbType.Int);
                        cmd.Parameters.Add("@CurrentStateNo", SqlDbType.Int);
                        cmd.Parameters.Add("@ExpiryTime", SqlDbType.DateTime);
                        cmd.Parameters.Add("@ParserStateNo", SqlDbType.Int);
                        cmd.Parameters.Add("@ProcessNextStep", SqlDbType.Bit);
                        cmd.Parameters.Add("@CurrentLoopNumber", SqlDbType.Int);

                        cmd.Parameters["@AlarmSystemID"].Value = AlarmSystemID;
                        cmd.Parameters["@CurrentStateNo"].Value = DisplayStateNo;
                        cmd.Parameters["@ExpiryTime"].Value = ExpiryTime;
                        cmd.Parameters["@ParserStateNo"].Value = CurentStateNo;
                        cmd.Parameters["@ProcessNextStep"].Value = processNext;
                        cmd.Parameters["@CurrentLoopNumber"].Value = currentLoopNumber;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                success = true;
                                log.Info(String.Format("Parser::insertIntoCurrentAlarmNotification::AlarmSystemID::{0}::CurentStateNo::{1}::ExpiryTime::{2}::currentLoopNumber::{3}::processNext::{4}", AlarmSystemID, CurentStateNo, ExpiryTime, currentLoopNumber, processNext));
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "SQLException on insertIntoCurrentAlarmNotification in Step Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on insertIntoCurrentAlarmNotification in Step Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_CURRENT_NOTIFICATION_STATE IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_CURRENT_NOTIFICATION_STATE IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Info("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_CURRENT_NOTIFICATION_STATE IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_CURRENT_NOTIFICATION_STATE IN STEP PARSER " + e);
                    throw;
                }
            }
            return success;
        }

        /// <summary>
        /// Runs the email join logic if the active alarm state has the email join flag set.
        /// </summary>
        /// <param name="a">.</param>
        protected void SendNotificationsToOfficersInSameGroup(ActiveAlarm a)
        {
            if (a.EmailJoin == 1)
            {
                if (AddToNotificationQueue(a, 2))
                {
                    string emails = "Email also sent to: " + getInsertEmails(a);
                    if (emails.Length > 4096)
                    {
                        emails = emails.Substring(0, 4096);
                    }
                    CreateAlarmAudit(3, emails, a.HistoryID, a.CurrentStateNo);
                    AddActiveAlarmActionToActivity(a.HistoryID, getInsertEmails(a), 0);
                }
                else
                {
                    log.Info(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: Notify to all officers in group failed");
                }
            }
        }

        /// <summary>
        /// Returns the email addresses of the top 10 individuals that were contacted as the result of an alarm.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <returns>.</returns>
        private string getInsertEmails(ActiveAlarm a)
        {
            string ConnectionString = AlarmsDatabase;

            StringBuilder sb = new StringBuilder();
            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand(sb.ToString(), MyConnection))
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_AUDIT_HISTORY_EMAILS;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@ClientSystemID", SqlDbType.Int);

                        cmd.Parameters["@ClientSystemID"].Value = a.ClientSystemID;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    sb.Append(MyDataReader["POMSGAddress"].ToString());
                                    sb.Append(",");
                                }
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.Info("[" + platForm + "] " + "SQLException on getInsertEmails in Step Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on getInsertEmails in Step Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                log.Info("[" + platForm + "] " + "ERROR: Unable to Read Audit and History Emails in Step Parser " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Audit and History Emails in Step Parser " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    log.Info("[" + platForm + "] " + "ERROR: Unable to Read Audit and History Emails in Step Parser " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Audit and History Emails in Step Parser " + e);

                }
            }

            return sb.ToString().TrimEnd(',');
        }

        /// <summary>
        /// Reads the Last Processed Point SystemID from the ParserActivity table, used when the program is restarted or when reading in a set of points.
        /// </summary>
        /// <param name="parserID">The parserID<see cref="int"/>.</param>
        /// <returns>.</returns>
        private int readLastSuccessfulProcess(int parserID)
        {
            int SystemID = 0;
            string ConnectionString = AlarmsDatabase;

            StringBuilder sb = new StringBuilder();
            sb.Append("SELECT CONVERT(int, [StatusID]) AS StatusID ");
            sb.Append("FROM ParserActivity ");
            sb.AppendFormat("WHERE ParserId = {0} ", parserID);

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand(sb.ToString(), MyConnection))
                    {
                        int retries = 5;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    SystemID = (int)MyDataReader["StatusID"];
                                }

                                log.Info("[" + platForm + "] " + "Reading Last successful processed ActiveAlarms point: " + SystemID);
                                Console.ForegroundColor = ConsoleColor.White;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " Reading Last successful processed ActiveAlarms point: " + SystemID);
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.Error("[" + platForm + "] " + "SQLException on readLastSuccessfulProcess in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readLastSuccessfulProcess in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(3000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                log.Error("[" + platForm + "] " + "ERROR: Unable to Read Last successful processed ActiveAlarms point IN PARSER ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Last successful processed ActiveAlarms point IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(3000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    log.Error("[" + platForm + "] " + "ERROR: Unable to Read Last successful processed ActiveAlarms point IN PARSER ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Last successful processed ActiveAlarms point IN PARSER " + e);
                    throw;
                }
            }

            return SystemID;
        }

        /// <summary>
        /// The holidayCheck.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="current">The current<see cref="DateTime"/>.</param>
        /// <returns>The <see cref="Boolean"/>.</returns>
        private Boolean holidayCheck(ActiveAlarm a, DateTime current)
        {
            Boolean holiday = false;

            if (a.POGroupNum.Trim() != "")
            {
                if (activeHolidays.ContainsKey(a.POGroupNum))
                {
                    List<Holiday> holidays = activeHolidays[a.POGroupNum];

                    foreach (Holiday h in holidays)
                    {
                        if (h.StartDate.Date <= current.Date && h.EndDate.Date >= current.Date)
                        {
                            holiday = true;
                        }
                    }
                }
            }

            if (a.POGroup1.Trim() != "")
            {
                if (activeHolidays.ContainsKey(a.POGroup1))
                {
                    List<Holiday> holidays = activeHolidays[a.POGroup1];

                    foreach (Holiday h in holidays)
                    {
                        if (h.StartDate.Date <= current.Date && h.EndDate.Date >= current.Date)
                        {
                            holiday = true;
                        }
                    }
                }
            }

            if (a.POGroup2.Trim() != "")
            {
                if (activeHolidays.ContainsKey(a.POGroup2))
                {
                    List<Holiday> holidays = activeHolidays[a.POGroup2];

                    foreach (Holiday h in holidays)
                    {
                        if (h.StartDate.Date <= current.Date && h.EndDate.Date >= current.Date)
                        {
                            holiday = true;
                        }
                    }
                }
            }

            if (a.POGroup3.Trim() != "")
            {
                if (activeHolidays.ContainsKey(a.POGroup3))
                {
                    List<Holiday> holidays = activeHolidays[a.POGroup3];

                    foreach (Holiday h in holidays)
                    {
                        if (h.StartDate.Date <= current.Date && h.EndDate.Date >= current.Date)
                        {
                            holiday = true;
                        }
                    }
                }
            }

            return holiday;
        }

        /// <summary>
        /// The readRoles.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="RoleID">The RoleID<see cref="int"/>.</param>
        /// <param name="RoleAction">The RoleAction<see cref="int"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool readRoles(ActiveAlarm a, int RoleID, int RoleAction)
        {
            bool success = true;
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_ROLES;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@RoleID", SqlDbType.Int);
                        cmd.Parameters.Add("@POGroup", SqlDbType.VarChar, 32);

                        cmd.Parameters["@RoleID"].Value = RoleID;
                        cmd.Parameters["@POGroup"].Value = a.POGroupNum;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {

                                    if (RoleAction == 2)
                                    {
                                        a.EmailAddresses = MyDataReader["EmailAddress"].ToString();
                                    }
                                    else if (RoleAction == 3)
                                    {
                                        a.EmailAddresses = MyDataReader["MessageAddress"].ToString();
                                    }

                                    if (RoleAction == 1)
                                    {
                                        a.Instruction = a.Instruction + " Please Call " + MyDataReader["RoleName"].ToString() + " And Inform The Following Phone Number(s) " + MyDataReader["OfficePhone"].ToString();
                                    }
                                }
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "SQLException on readRoles in Step Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readRoles in Step Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Info("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM ROLES IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM ROLES IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Info("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM ROLES IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM ROLES IN STEP PARSER " + e);
                    throw;
                }
            }
            return success;
        }

        /// <summary>
        /// The readVictims.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool readVictims()
        {
            bool success = true;

            victims = new Dictionary<string, List<Victim>>();
            victimTypeDict = new Dictionary<string, string>();
            //victims.Clear();
            //victimTypeDict.Clear();
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_VICTIMS_EMAIL;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    Victim v = new Victim()
                                    {
                                        OID = MyDataReader["Victim"].ToString(),
                                        Email = MyDataReader["EmailAddress"].ToString(),
                                        CellPhone = MyDataReader["CellPhone"].ToString(),
                                        VictimType = MyDataReader["VictimType"].ToString()
                                    };
                                    String Offender = MyDataReader["Offender"].ToString();

                                    if (!victimTypeDict.ContainsKey(v.OID))
                                    {
                                        victimTypeDict.Add(v.OID, v.VictimType);
                                    }

                                    if (victims.ContainsKey(Offender))
                                    {
                                        List<Victim> vList = victims[Offender];
                                        vList.Add(v);
                                    }
                                    else
                                    {
                                        List<Victim> vList = new List<Victim>();
                                        vList.Add(v);
                                        victims.Add(Offender, vList);
                                    }
                                }

                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "SQLException on readVictims in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readVictims in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER " + e);

                }
            }
            return success;
        }

        /*
        private void readPNSettings()
        {
            pnAlarms = new Dictionary<string, HashSet<String>>();
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_PUSH_NOTIFICATION_SETTINGS;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    String OID = MyDataReader["UserName"].ToString();
                                    String Event = MyDataReader["EventCode"].ToString();
                                    if (!pnAlarms.ContainsKey(OID))
                                    {
                                        pnAlarms.Add(OID, new HashSet<String>());
                                    }
                                    pnAlarms[OID].Add(Event);

                                }

                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.Error("[" + platForm + "] " + "SQLException on readPNSettings in Parser ",ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readPNSettings in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                    }
                                    else
                                    {
                                        throw ex;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE PUSH NOTIFICATION SETTINGS IN PARSER ",exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE PUSH NOTIFICATION SETTINGS IN PARSER " + exc);
                                throw exc;
                            }
                          
                        }
                    }
                }
                catch (Exception e)
                {
                    log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE PUSH NOTIFICATION SETTINGS IN PARSER ",e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE PUSH NOTIFICATION SETTINGS IN PARSER " + e);
                    
                }
            }
        }
        */
        /// <summary>
        /// The readMEZVictims.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool readMEZVictims()
        {
            bool success = true;

            MEZVictims = new Dictionary<string, HashSet<String>>();
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_VICTIMS;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    String Offender = MyDataReader["Offender"].ToString();
                                    String Victim = MyDataReader["Victim"].ToString();
                                    if (!MEZVictims.ContainsKey(Offender))
                                    {
                                        MEZVictims.Add(Offender, new HashSet<String>());
                                    }
                                    MEZVictims[Offender].Add(Victim);

                                }
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "SQLException on readMEZVictims in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readMEZVictims in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE MEZ VICTIMS IN PARSER ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE MEZ VICTIMS IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE MEZ VICTIMS IN PARSER ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO MEZ VICTIMS IN PARSER " + e);

                }
            }
            return success;
        }

        /// <summary>
        /// The readMEZVictims.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool readAttachedVictimZones()
        {
            bool success = true;

            if (AttachedVictimZones == null)
            {
                AttachedVictimZones = new Dictionary<String, Dictionary<String, HashSet<String>>>();
            }
            else
            {
                AttachedVictimZones.Clear();
            }

            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_ATTACHED_VICTIMS_ZONES;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    String ZoneID = MyDataReader["ZoneID"].ToString();
                                    String ZoneCategory = MyDataReader["ZoneCategory"].ToString();
                                    String OffenderID = MyDataReader["OffenderID"].ToString();
                                    String VictimID = MyDataReader["VictimID"].ToString();

                                    string ZoneKey = ZoneID + "|" + ZoneCategory;

                                    if (AttachedVictimZones.ContainsKey(OffenderID))
                                    {

                                        if (AttachedVictimZones[OffenderID].ContainsKey(ZoneKey))
                                        {
                                            if (!AttachedVictimZones[OffenderID][ZoneKey].Contains(VictimID))
                                            {
                                                AttachedVictimZones[OffenderID][ZoneKey].Add(VictimID);
                                            }
                                        }
                                        else
                                        {
                                            AttachedVictimZones[OffenderID].Add(ZoneKey, new HashSet<string>());
                                            AttachedVictimZones[OffenderID][ZoneKey].Add(VictimID);
                                        }
                                    }
                                    else
                                    {
                                        AttachedVictimZones.Add(OffenderID, new Dictionary<string, HashSet<string>>());
                                        AttachedVictimZones[OffenderID].Add(ZoneKey, new HashSet<string>());
                                        AttachedVictimZones[OffenderID][ZoneKey].Add(VictimID);
                                    }

                                }
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "SQLException on readAttachedVictimZones in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readAttachedVictimZones in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "FAILED TO readAttachedVictimZonesIN PARSER ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO readAttachedVictimZones IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Error("[" + platForm + "] " + "FAILED TO readAttachedVictimZones IN PARSER ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO readAttachedVictimZones IN PARSER " + e);

                }
            }
            return success;
        }

        /// <summary>
        /// The readProfileItemsClear.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool readProfileItemsClear()
        {
            bool success = true;
            ClearEvents = new Dictionary<int, List<ProfileItemClear>>();
            //Create a connection to the SQL Server;
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_PROFILE_ITEMS_CLEAR;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    int ProfileID = Convert.ToInt32(MyDataReader["ProfileID"]);
                                    String ClearingEvent = MyDataReader["ClearingEvent"].ToString();
                                    String EventCode = MyDataReader["EventCode"].ToString();
                                    ProfileItemClear p = new ProfileItemClear()
                                    {
                                        ClearingEvent = ClearingEvent,
                                        EventCode = EventCode
                                    };

                                    if (ClearEvents.ContainsKey(ProfileID))
                                    {
                                        ClearEvents[ProfileID].Add(p);
                                    }
                                    else
                                    {
                                        List<ProfileItemClear> pl = new List<ProfileItemClear>();
                                        pl.Add(p);

                                        ClearEvents.Add(ProfileID, pl);
                                    }
                                }
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "SQLException on readProfileItemsClear in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readProfileItemsClear in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE CLEARING EVENTS IN PARSER ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE CLEARING EVENTS IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.Error("[" + platForm + "] " + "FAILED TO RETRIEVE CLEARING EVENTS IN PARSER ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE CLEARING EVENTS IN PARSER " + e);

                }
            }
            return success;
        }

        /// <summary>
        /// Returns the current ActiveAlarms point in the TRPT table.
        /// </summary>
        /// <returns>.</returns>
        private int ReadCurrentActiveAlarmPoint()
        {
            int currentAlarmPoint = 0;

            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_CURRENT_ACTIVE_ALARMPOINT;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    currentAlarmPoint = Convert.ToInt32(MyDataReader["SystemID"]);
                                }
                                break;
                            }
                            catch (SqlException ex)
                            {
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                log.Error("ReadCurrentActiveAlarmPoint", exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error("ReadCurrentActiveAlarmPoint", ex);
                    throw;
                }
            }
            return currentAlarmPoint;
        }

        /// <summary>
        /// Clears Alarms From McApp Table.
        /// </summary>
        /// <param name="alarms">The alarms<see cref="String"/>.</param>
        /// <param name="oid">The oid<see cref="String"/>.</param>
        private void ClearMcAppAlarm(String alarms, String oid)
        {
            string ConnectionString = AlarmsDatabase;

            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand())
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = CLEAR_ACTIVE_ALARMS;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@Alarms", SqlDbType.VarChar, 4096);
                        cmd.Parameters.Add("@OID", SqlDbType.VarChar, 32);

                        cmd.Parameters["@Alarms"].Value = alarms;
                        cmd.Parameters["@OID"].Value = oid;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();

                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine("[" + platForm + "] " + "Clearing Alerts For - " + oid);
                                log.Info("[" + platForm + "] " + "Clearing Alerts For - " + oid);

                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.Error("[" + platForm + "] " + "SQLException on ClearMcAppAlarm in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on ClearMcAppAlarm in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                log.Error("[" + platForm + "] " + "FAILED TO RUN MCAPP_CLEAR_ALARM IN PARSER ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CLEAR_ALARM IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (Exception e)
                {
                    log.Error("[" + platForm + "] " + "FAILED TO RUN MCAPP_CLEAR_ALARM IN PARSER ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CLEAR_ALARM IN PARSER " + e);

                }
            }
        }

        /// <summary>
        /// Returns the email addresses of the client that was contacted as the result of an alarm.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <returns>.</returns>
        private string getClientEmail(ActiveAlarm a)
        {
            string ConnectionString = AlarmsDatabase;

            StringBuilder sb = new StringBuilder();
            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand(sb.ToString(), MyConnection))
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_CLIENT_EMAIL;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@ClientSystemID", SqlDbType.Int);

                        cmd.Parameters["@ClientSystemID"].Value = a.ClientSystemID;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    sb.Append(MyDataReader["EmailAddress"].ToString());
                                }
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.Info("[" + platForm + "] " + "SQLException on getClientEmail in Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on getClientEmail in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(2000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                log.Info("[" + platForm + "] " + "ERROR: Unable to Read Client Email IN PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Client Email IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    log.Info("[" + platForm + "] " + "ERROR: Unable to Read Client Email IN PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Client Email IN PARSER " + e);
                    throw;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Returns the texting address client that was contacted as the result of an alarm.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <returns>.</returns>
        private string getClientText(ActiveAlarm a)
        {
            string ConnectionString = AlarmsDatabase;

            StringBuilder sb = new StringBuilder();
            using (SqlConnection MyConnection = new SqlConnection(ConnectionString))
            {
                try
                {
                    using (SqlCommand cmd = new SqlCommand(sb.ToString(), MyConnection))
                    {
                        // Specify which stored procedure the SqlCommand will execute
                        cmd.CommandText = READ_CLIENT_TEXT;
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@ClientSystemID", SqlDbType.Int);

                        cmd.Parameters["@ClientSystemID"].Value = a.ClientSystemID;

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                SqlDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    sb.Append(MyDataReader["Cell"].ToString());
                                }
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.Info("[" + platForm + "] " + "SQLException on getClientText in Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on getClientText in Parser " + ex);
                                if (ex.ErrorCode.Equals(1205) ||
                                   ex.Message.ToLower().Contains("deadlock"))
                                {
                                    if (retries > 0)
                                    {
                                        retries--;
                                        Thread.Sleep(3000);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                log.Info("[" + platForm + "] " + "ERROR: Unable to Read Client Text IN PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Client Text IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(1000);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    log.Info("[" + platForm + "] " + "ERROR: Unable to Read Client Text IN PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Client Text IN PARSER " + e);
                    throw;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// The returnWaitTime.
        /// </summary>
        /// <param name="pointsBehind">The pointsBehind<see cref="int"/>.</param>
        /// <returns>The <see cref="int"/>.</returns>
        protected static int returnWaitTime(int pointsBehind)
        {
            int toReturn;

            int numberOfProcessIntervals = pointsBehind / Convert.ToInt32(ConfigurationManager.AppSettings["NumberOfProcessPoints"]);

            int sleepTime = Int32.MaxValue;
            if (numberOfProcessIntervals != 0)
            {
                sleepTime = (60 / numberOfProcessIntervals) - 3;
                toReturn = sleepTime;
            }
            else
            {
                toReturn = 15;
            }

            if (sleepTime < 3)
            {
                toReturn = 3;
            }

            if (sleepTime > 15)
            {
                toReturn = 15;
            }


            return toReturn * 1000;
        }

        /// <summary>
        /// When a valid alert is recieved from an offender alert all associated victims.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        public void PushNotificationToVictim(ActiveAlarm a)
        {
            //if(a.ClientID == "ID902551")
            //{
            //    Console.WriteLine("adsa");
            //}
            if (MEZVictims.ContainsKey(a.ClientID))
            {
                ArrayList notifications = new ArrayList();

                List<String> victimList = MEZVictims[a.ClientID].ToList();

                foreach (String victimID in victimList)
                {
                    //If not VAPP victim skip; else keep going
                    if (victimTypeDict.ContainsKey(victimID) && !victimTypeDict[victimID].Equals("VAPP"))
                    {
                        continue;
                    }

                    if (a.AlarmID == "GPS20" || a.AlarmID == "GPS21" || a.AlarmID == "GPS76" || a.AlarmID == "GPS77")
                    {
                        //IF the Event is a MEZ event, check whether the event is belong to the victimid in the loop, if not go to next victim, 
                        // if yes keep going to process PN
                        if (victimID != a.MEZEventVictimID)
                        {
                            continue;
                        }
                    }

                    if (a.AlarmID == "200" || a.AlarmID == "206" || a.AlarmID == "GPS14" || a.AlarmID == "GPS17")
                    {
                        string ZoneKey = a.ZoneID + "|" + a.ZoneCategory;

                        if (!AttachedVictimZones.ContainsKey(a.ClientID) || !AttachedVictimZones[a.ClientID].ContainsKey(ZoneKey))
                        {
                            //Offender is not in AttachedVictimZones
                            //OR
                            //Offender's AttachedVictimZones dose not have the current alarm's zone
                            //skip, do not send to the victim(victimID)
                            continue;
                        }

                        if (!AttachedVictimZones[a.ClientID][ZoneKey].Contains(victimID))
                        {
                            //Offender's AttachedVictimZones dose have the current alarm's zone, but does not have victimID in the attached list
                            //skip, do not send to any the victim(victimID)
                            continue;
                        }
                    }
                    var reminder = NotificationService.NotificationServiceSetting.GetAppReminderSetting(a.AlarmID);
                    var setting = NotificationService.NotificationServiceSetting.GetNotificationServiceSetting(victimID, a.AlarmID);

                    bool reminderCheck = NotificationService.NotificationServiceSetting.CheckVictimReminderSetting(victimID, reminder);

                    if (reminder != null && reminderCheck == true)
                    {
                        string alternativeText = replaceAlarmsText(a, reminder.AlternativeText);

                        Notification reminderNotification = new Notification()
                        {
                            oid = a.ClientID,
                            victimid = victimID,
                            type = "reminder",
                            remindertype = reminder.ReminderType,
                            subtype = reminder.NotificationSubType,
                            messageto = victimID,
                            messagetoname = victimID,
                            messagedescription = alternativeText,
                            messagetitle = alternativeText,
                            messagesubtitle = alternativeText,
                            delayed = false,
                            deliverytime = a.EventDateTimeUTC,
                            additionalinfo = "",
                            source = "Active Alarms Parser",
                            activityid = a.HistoryID.ToString() + '-' + Platform + '-' + victimID,
                            eventdatetime = a.EventDateTimeUTC,
                            eventdatetimelocal = a.EventDateTimeLocal,
                            feedbackrequired = true,
                            pogroup = a.POGroupNum,
                            seenbyparticipant = false,
                            victimgenerated = false,
                            timezone = a.ClientTZ
                        };
                        notifications.Add(reminderNotification);

                        //Push Notification
                        if (setting != null)
                        {
                            Notification notif = new Notification()
                            {
                                oid = a.ClientID,
                                victimid = victimID,
                                type = "push",
                                remindertype = reminder.ReminderType,
                                subtype = reminder.NotificationSubType,
                                messageto = victimID,
                                messagetoname = victimID,
                                messagedescription = alternativeText,
                                messagetitle = alternativeText,
                                messagesubtitle = alternativeText,
                                delayed = false,
                                deliverytime = a.EventDateTimeUTC,
                                additionalinfo = "",
                                source = "Active Alarms Parser",
                                activityid = a.HistoryID.ToString() + '-' + Platform + '-' + victimID,
                                eventdatetime = a.EventDateTimeUTC,
                                eventdatetimelocal = a.EventDateTimeLocal,
                                feedbackrequired = true,
                                pogroup = a.POGroupNum,
                                seenbyparticipant = false,
                                victimgenerated = false,
                                timezone = a.ClientTZ
                            };

                            notifications.Add(notif);
                        }
                    }
                }

                //Push Notification to the cloud
                NotificationService.NotificationService.PushNotification(notifications);
            }
        }

        private string replaceAlarmsText(ActiveAlarm a, string alternativeText)
        {
            string newText = alternativeText;
            newText = newText.Replace("{ParticipantName}", a.OffenderName);

            if (a.AlarmID == "200" || a.AlarmID == "GPS14") //Enter
            {
                if (a.ZoneCategory.ToLower() == "circlezone")
                {
                    newText = newText.Replace("{ExclusionZoneTitle}", a.ZoneName);
                    newText = newText.Replace("{ExclusionZoneAddress}", a.ZoneAddress);
                }
                else
                {
                    string alText = "{ParticipantName} has entered your {ExclusionZoneTitle} zone";
                    newText = alText.Replace("{ParticipantName}", a.OffenderName);
                    newText = newText.Replace("{ExclusionZoneTitle}", a.PolyZoneName);
                }
                return newText;
            }
            else if (a.AlarmID == "206" || a.AlarmID == "GPS17") //Clear
            {
                if (a.ZoneCategory.ToLower() == "circlezone")
                {
                    newText = newText.Replace("{ExclusionZoneTitle}", a.ZoneName);
                    newText = newText.Replace("{ExclusionZoneAddress}", a.ZoneAddress);
                }
                else
                {
                    string alText = "{ParticipantName} is no longer within the range of {ExclusionZoneTitle} zone";
                    newText = alText.Replace("{ParticipantName}", a.OffenderName);
                    newText = newText.Replace("{ExclusionZoneTitle}", a.PolyZoneName);
                }
                return newText;
            }
            else
            {
                return newText;
            }
        }
    }
}
