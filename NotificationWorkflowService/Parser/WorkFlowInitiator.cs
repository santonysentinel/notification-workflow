namespace ActiveAlarmsParser
{
    using ActiveAlarmsParser.Service.NotificationService;
    using NotificationSender = ActiveAlarmsParser.Service.NotificationService.NotificationService;
    using Microsoft.Data.SqlClient;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using NotificationWorkflowService.Entity;
    using NotificationWorkflowService.Repository;
    using NotificationWorkflowService.Parser;
    using NotificationWorkflowService.Parser.ReferenceData;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Data;
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
        private readonly ILogger<WorkFlowInitiator> log;

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
        /// Defines the Platform
        /// </summary>
        /// 
        private readonly String Platform;

        private readonly IConfiguration configuration;
        private readonly NotificationSender notificationService;
        private readonly IRepository repository;
        private WorkflowReferenceData? pendingReferenceData;

        /// <summary>
        /// Initializes a new instance of the <see cref="WorkFlowInitiator"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="configuration">The application configuration.</param>
        public WorkFlowInitiator(ILogger<WorkFlowInitiator> logger, IConfiguration configuration, NotificationSender notificationService)
            : this(logger, configuration, notificationService, new Repository(configuration))
        {
        }

        public WorkFlowInitiator(ILogger<WorkFlowInitiator> logger, IConfiguration configuration, NotificationSender notificationService, IRepository repository)
        {
            this.log = logger;
            this.configuration = configuration;
            this.notificationService = notificationService;
            this.repository = repository;
            Platform = configuration["Platform"] ?? "";
            setUpConnnectionStrings();
        }

        /// <summary>
        /// The setUpParser.
        /// </summary>
        /// <param name="platform">The platform<see cref="String"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        public bool setUpParser(String platform)
        {
            platForm = platform;
            pendingReferenceData = new WorkflowReferenceData();
            try
            {
                if (!SetUpReferenceData())
                {
                    return false;
                }
                PublishReferenceData(pendingReferenceData);
                return true;
            }
            finally
            {
                pendingReferenceData = null;
            }
        }

        private bool SetUpReferenceData()
        {
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

        // Workers call this parser serially; sequential publication is not a
        // concurrent-reader guarantee. The staging shell has init-only properties.
        private void StageReferenceData(
            Dictionary<string, int>? clientProfiles = null,
            Dictionary<string, int>? holidayProfiles = null,
            Dictionary<int, Profile>? profiles = null,
            Dictionary<string, List<Holiday>>? holidays = null,
            Dictionary<string, string>? roleNames = null,
            WorkflowVictimReferenceData? victimData = null,
            Dictionary<string, HashSet<string>>? mezVictims = null,
            Dictionary<string, Dictionary<string, HashSet<string>>>? attachedZones = null,
            Dictionary<int, List<ProfileItemClear>>? clearEvents = null)
        {
            var pending = pendingReferenceData ?? throw new InvalidOperationException("No reference refresh is pending.");
            pendingReferenceData = new WorkflowReferenceData
            {
                ClientProfileMapping = clientProfiles ?? pending.ClientProfileMapping,
                ClientHolidayProfileMapping = holidayProfiles ?? pending.ClientHolidayProfileMapping,
                ActiveProfiles = profiles ?? pending.ActiveProfiles,
                ActiveHolidays = holidays ?? pending.ActiveHolidays,
                Roles = roleNames ?? pending.Roles,
                Victims = victimData?.Victims ?? pending.Victims,
                VictimTypeDict = victimData?.VictimTypeDict ?? pending.VictimTypeDict,
                MEZVictims = mezVictims ?? pending.MEZVictims,
                AttachedVictimZones = attachedZones ?? pending.AttachedVictimZones,
                ClearEvents = clearEvents ?? pending.ClearEvents
            };
        }

        private void PublishReferenceData(WorkflowReferenceData data)
        {
            clientProfileMapping = data.ClientProfileMapping;
            clientHolidayProfileMapping = data.ClientHolidayProfileMapping;
            activeProfiles = data.ActiveProfiles;
            activeHolidays = data.ActiveHolidays;
            roles = data.Roles;
            victims = data.Victims;
            victimTypeDict = data.VictimTypeDict;
            MEZVictims = data.MEZVictims;
            AttachedVictimZones = data.AttachedVictimZones;
            ClearEvents = data.ClearEvents;
        }

        /// <summary>
        /// Reads the connection string from configuration.
        /// </summary>
        private void setUpConnnectionStrings()
        {
            _ = configuration.GetConnectionString("connstr")
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

            int LastProcessedPoint = readLastSuccessfulProcess(Convert.ToInt32(configuration[platForm + "ParserID"]));
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadPoints(configuration["NumberOfProcessPoints"], readLastSuccessfulProcess(Convert.ToInt32(configuration[platForm + "ParserID"])))))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            activeAlarms = new List<ActiveAlarm>();

                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

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
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on readPoints in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM POINTS IN PARSER ");
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
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM POINTS IN PARSER ");
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

                Profile? p = WorkflowProfileResolver.ResolveProfile(a, current,
                    clientProfileMapping, clientHolidayProfileMapping, activeProfiles, activeHolidays);

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
                                    log.LogInformation(String.Format("FindStepOneProfileItem: No Profile found meet current time AlarmSysID::{0}", a.SystemID));
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
                        log.LogInformation(String.Format("getPriorityAndEmail: No Profile found meet current time AlarmSysID::{0}", a.SystemID));
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
                        log.LogError(e, "Error getPriorityAndEmail: ");
                    }

                    if (configuration["DefaultMcAppFlag"] == "1")
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
        public ProfileItem? FindStepOneProfileItem(List<ProfileItem> profileItems, DateTime current)
        {
            try
            {
                return WorkflowProfileResolver.FindInitialProfileItem(profileItems, current);
            }
            catch (Exception e)
            {
                log.LogError(e, "FindStepOneProfileItem Error");
                return null;
            }
        }

        /// <summary>
        /// The Main active loop of the step parser, loops through all the activeAlarms and performs the necessary actions.
        /// </summary>
        public void parseAlarms()
        {
            //NotificationService.NotificationServiceSetting.GetAlarmVAPPTexT("", "", "9999");
            ActiveAlarm? curr = null;

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

                log.LogInformation(String.Format("ParseAlarm::CurrentNumberOfActiveAlarms::{0}", activeAlarms.Count));
                foreach (ActiveAlarm a in activeAlarms)
                {
                    log.LogInformation(String.Format("ParseAlarm::AlarmSystemID::{0}", a.SystemID));
                    try
                    {
                        curr = a;

                        try
                        {
                            PushAlertsToVictims(a);
                        }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "PushAlertsToVictims Error");
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
                            log.LogError(ex, "PushNotificationToVictim Error");
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
                        log.LogInformation(String.Format("ParserBeforeCheck::AlarmSystemID::{0}::Priority::{1}", a.SystemID, a.Priority));
                        log.LogInformation(String.Format("ParserBeforeCheck::AlarmSystemID::{0}::CurrentStateNo::{1}::NextStateNo::{2}::ProcessNextStep::{3}::StateTime::{4}::CurrentLoopNumber::{5}::insert::{6}", a.SystemID, a.CurrentStateNo, a.NextStateNo, a.ProcessNextStep, a.StateTime, a.CurrentStateNo, insert.ToString()));

                        switch (a.Priority)
                        {
                            case 1:
                                sb.Append("Do Nothing");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Do Nothing");
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Do Nothing");
                                insert = false;
                                break;
                            case 2:
                                sb.Append("Auto Email");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
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
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp");
                                SendNotificationsToOfficersInSameGroup(a);
                                PushAlertToMcApp(a);
                                a.ProcessNextStep = 0;
                                break;
                            case 4:
                                sb.Append("Auto Fax");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Fax");
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Fax");
                                SendNotificationsToOfficersInSameGroup(a);
                                break;
                            case 5:
                                sb.Append("Auto Page");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Page");
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Page");
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
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Fax");
                                SendNotificationsToOfficersInSameGroup(a);
                                PushAlertToMcApp(a);
                                a.ProcessNextStep = 0;
                                break;
                            case 7:
                                sb.Append("McApp and Auto Email");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Email");
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Email");
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
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Page");
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
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Delay " + "(Step " + a.StateNo + ")");
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
                                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp - Call Officers");
                                            PushAlertToMcApp(a);
                                            a.ProcessNextStep = 0;
                                        }
                                        break;
                                    case 2:
                                        if (a.EmailAddresses != "")
                                        {
                                            Console.ForegroundColor = ConsoleColor.Green;
                                            Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
                                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
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
                                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email");
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
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Email-Victims");

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
                                    log.LogError(ex, "Read Victim Emails");
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
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Contact Victim");
                                SendNotificationsToOfficersInSameGroup(a);
                                PushAlertToMcApp(a);
                                a.ProcessNextStep = 0;
                                break;
                            case 15: //alert client email
                                sb.Append("Alert Client - Email");
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client Email");
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client Email");

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
                                    log.LogError(ex, "clientMail");
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
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client Text");

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
                                    log.LogError(ex, "getClientText");
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
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Text All Victims");

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
                                    log.LogError(ex, "victimsText");
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
                                log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Do Nothing");
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

                        log.LogInformation(String.Format("AlarmSystemID- {0} :: ExpiryTimeByEventDateTime - {1} :: ExpiryTimeByUTCNow - {2} :: ExpiryTimeApplied - {3}"
                                                , a.SystemID.ToString()
                                                , ExpiryTimeByEventDateTime.ToString()
                                                , ExpiryTimeByUTCNow.ToString()
                                                , ExpiryTimeApplied.ToString()
                                                )
                        );

                        insertIntoAlarmNotification(a.SystemID, 1, ExpiryTimeApplied, PriorityMapping[a.Priority]);
                        log.LogInformation(String.Format("ParserAfterCheck::AlarmSystemID::{0}::CurrentStateNo::{1}::NextStateNo::{2}::ProcessNextStep::{3}::StateTime::{4}::CurrentLoopNumber::{5}::insert::{6}", a.SystemID, a.CurrentStateNo, a.NextStateNo, a.ProcessNextStep, a.StateTime, a.CurrentStateNo, insert.ToString()));

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
                        log.LogError(e, "Error processing alarm");
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
                log.LogInformation("FAILED TO PARSE ALARMS " + ex);
                if (curr != null)
                {
                    log.LogInformation("ERROR @ ActiveAlarmID - " + curr.SystemID);
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
            Dictionary<string, int> result = new();
            //Create a connection to the SQL Server;
            try
            {
                using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareFetchClientProfile(profileType)))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 5;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                result = WorkflowReferenceDataBuilders.BuildClientProfiles(MyDataReader, profileType);
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on FetchClientProfile in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE CLIENT PROFILES ");
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
            }
            catch (Exception e)
            {
                success = false;
                log.LogInformation("[" + platForm + "] " + "FAILED TO RETRIEVE CLIENT PROFILES " + e);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE CLIENT PROFILES " + e);

            }
            if (success)
            {
                if (profileType == 0)
                {
                    if (pendingReferenceData != null) StageReferenceData(clientProfiles: result);
                    else clientProfileMapping = result;
                }
                else if (profileType == 1)
                {
                    if (pendingReferenceData != null) StageReferenceData(holidayProfiles: result);
                    else clientHolidayProfileMapping = result;
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
            Dictionary<int, Profile> result = new();

            //Create a connection to the SQL Server;
            try
            {
                using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadAllProfiles()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 5;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                result = WorkflowReferenceDataBuilders.BuildProfiles(MyDataReader);

                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on readAllProfiles in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE PROFILES ");
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
            }
            catch (Exception e)
            {
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE PROFILES ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE PROFILES IN PARSER " + e);


                //System.Environment.Exit(1);
            }
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(profiles: result);
                else activeProfiles = result;
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
            Dictionary<string, List<Holiday>> result = new();

            //Create a connection to the SQL Server;
            try
            {
                using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadAllHolidays()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                result = WorkflowReferenceDataBuilders.BuildHolidays(MyDataReader);

                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on readAllHolidays in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE HOLIDAYS ");
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
            }
            catch (Exception e)
            {
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE HOLIDAYS ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE HOLIDAYS IN PARSER " + e);

            }
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(holidays: result);
                else activeHolidays = result;
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
            Dictionary<string, string> result = new();

            //Create a connection to the SQL Server;
            try
            {
                using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadAllRoles()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                result = WorkflowReferenceDataBuilders.BuildRoles(MyDataReader);
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on readAllRoles in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE ROLES ");
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
            }
            catch (Exception e)
            {
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ROLES ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ROLES IN PARSER " + e);

            }
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(roleNames: result);
                else roles = result;
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PreparePushAlertToMcApp(a)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

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
                                log.LogInformation("[" + platForm + "] " + "SQLException on PushAlertToMcApp in Step Parser " + ex);
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
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_MCAPP IN STEP PARSER " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_MCAPP IN STEP PARSER " + e);
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareAddToNotificationQueue(a, insertType)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

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
                                log.LogInformation("[" + platForm + "] " + "SQLException on AddToNotificationQueue in Step Parser " + ex);
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
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE IN STEP PARSER " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE IN STEP PARSER " + e);
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareInsertPushNotificationQueue(a, VictimID, OffenderID)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

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
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on insertPushNotificationQueue in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE " + exc);
                                throw exc;
                            }


                        }
                    }
                }
                catch (Exception e)
                {
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE ");
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

            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareInsertNotificationQueueVictim(a, insertType, victimsEmails, isVictimNotification)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();


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
                                log.LogInformation("[" + platForm + "] " + "SQLException on insertNotificationQueueVictim in Parser " + ex);
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
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE " + e);
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareUpdateParserActivty(systemID, configuration[platForm + "ParserID"])))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();



                        int retries = 6;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                success = true;
                                log.LogInformation("Successfully updated AAID : " + systemID);
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on updateParserActivty in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RUN UPDATE_PARSER_ACTIVITY ");
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
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RUN UPDATE_PARSER_ACTIVITY IN PARSER ");
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareCreateAlarmAudit(type, action, historyID, StepNo)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

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
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on CreateAlarmAudit in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RUN MCAPP_CREATEAUDIT IN PARSER ");
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
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RUN MCAPP_CREATEAUDIT IN PARSER ");
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareAddActiveAlarmActionToActivity(historyID, email, type)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

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
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on ActiveAlarms_InsertIntoHistory in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER ");
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
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER ");
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareInsertIntoAlarmNotification(AlarmSystemID, CurentStateNo, ExpiryTime, Action)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

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
                                log.LogInformation("[" + platForm + "] " + "SQLException on insertIntoAlarmNotification in Step Parser " + ex);
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
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_ALARM_NOTIFICATION IN STEP PARSER " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_ALARM_NOTIFICATION IN STEP PARSER " + e);
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareInsertIntoCurrentAlarmNotification(AlarmSystemID, CurentStateNo, ExpiryTime, DisplayStateNo, currentLoopNumber, processNext)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();
                                success = true;
                                log.LogInformation(String.Format("Parser::insertIntoCurrentAlarmNotification::AlarmSystemID::{0}::CurentStateNo::{1}::ExpiryTime::{2}::currentLoopNumber::{3}::processNext::{4}", AlarmSystemID, CurentStateNo, ExpiryTime, currentLoopNumber, processNext));
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "SQLException on insertIntoCurrentAlarmNotification in Step Parser " + ex);
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
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_CURRENT_NOTIFICATION_STATE IN STEP PARSER " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_CURRENT_NOTIFICATION_STATE IN STEP PARSER " + e);
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
                    log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: Notify to all officers in group failed");
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
            StringBuilder sb = new StringBuilder();
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareGetInsertEmails(a)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    sb.Append(MyDataReader["POMSGAddress"].ToString());
                                    sb.Append(",");
                                }
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.LogInformation("[" + platForm + "] " + "SQLException on getInsertEmails in Step Parser " + ex);
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
                                log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Audit and History Emails in Step Parser " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Audit and History Emails in Step Parser " + e);
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadLastSuccessfulProcess(parserID)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();
                        int retries = 5;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    SystemID = (int)MyDataReader["StatusID"];
                                }

                                log.LogInformation("[" + platForm + "] " + "Reading Last successful processed ActiveAlarms point: " + SystemID);
                                Console.ForegroundColor = ConsoleColor.White;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " Reading Last successful processed ActiveAlarms point: " + SystemID);
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on readLastSuccessfulProcess in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "ERROR: Unable to Read Last successful processed ActiveAlarms point IN PARSER ");
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
                    log.LogError(e, "[" + platForm + "] " + "ERROR: Unable to Read Last successful processed ActiveAlarms point IN PARSER ");
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
            return WorkflowProfileResolver.IsHoliday(a, current, activeHolidays);
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadRoles(a, RoleID, RoleAction)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

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
                                log.LogInformation("[" + platForm + "] " + "SQLException on readRoles in Step Parser " + ex);
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
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM ROLES IN STEP PARSER " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM ROLES IN STEP PARSER " + e);
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

            WorkflowVictimReferenceData result = new(new(), new());
            //Create a connection to the SQL Server;
            try
            {
                using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadVictims()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                result = WorkflowReferenceDataBuilders.BuildVictims(MyDataReader);

                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on readVictims in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER ");
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
            }
            catch (Exception e)
            {
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER " + e);

            }
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(victimData: result);
                else
                {
                    victims = result.Victims;
                    victimTypeDict = result.VictimTypeDict;
                }
            }
            return success;
        }

        // Historical readPNSettings remains disabled (April 2021); no operation is prepared.
        /// <summary>
        /// The readMEZVictims.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool readMEZVictims()
        {
            bool success = true;

            Dictionary<string, HashSet<string>> result = new();
            //Create a connection to the SQL Server;
            try
            {
                using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadMEZVictims()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                result = WorkflowReferenceDataBuilders.BuildMezVictims(MyDataReader);
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on readMEZVictims in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE MEZ VICTIMS IN PARSER ");
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
            }
            catch (Exception e)
            {
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE MEZ VICTIMS IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO MEZ VICTIMS IN PARSER " + e);

            }
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(mezVictims: result);
                else MEZVictims = result;
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

            Dictionary<string, Dictionary<string, HashSet<string>>> result = new();

            //Create a connection to the SQL Server;
            try
            {
                using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadAttachedVictimZones()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                result = WorkflowReferenceDataBuilders.BuildAttachedVictimZones(MyDataReader);
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on readAttachedVictimZones in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO readAttachedVictimZonesIN PARSER ");
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
            }
            catch (Exception e)
            {
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO readAttachedVictimZones IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO readAttachedVictimZones IN PARSER " + e);

            }
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(attachedZones: result);
                else AttachedVictimZones = result;
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
            Dictionary<int, List<ProfileItemClear>> result = new();
            //Create a connection to the SQL Server;
            try
            {
                using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadProfileItemsClear()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                result = WorkflowReferenceDataBuilders.BuildClearEvents(MyDataReader);
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on readProfileItemsClear in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE CLEARING EVENTS IN PARSER ");
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
            }
            catch (Exception e)
            {
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE CLEARING EVENTS IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE CLEARING EVENTS IN PARSER " + e);

            }
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(clearEvents: result);
                else ClearEvents = result;
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

            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadCurrentActiveAlarmPoint()))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

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
                                log.LogError(exc, "ReadCurrentActiveAlarmPoint");
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
                    log.LogError(ex, "ReadCurrentActiveAlarmPoint");
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
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareClearMcAppAlarm(alarms, oid)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                cmd.ExecuteNonQuery();

                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine("[" + platForm + "] " + "Clearing Alerts For - " + oid);
                                log.LogInformation("[" + platForm + "] " + "Clearing Alerts For - " + oid);

                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.LogError(ex, "[" + platForm + "] " + "SQLException on ClearMcAppAlarm in Parser ");
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
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RUN MCAPP_CLEAR_ALARM IN PARSER ");
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
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RUN MCAPP_CLEAR_ALARM IN PARSER ");
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
            StringBuilder sb = new StringBuilder();
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareGetClientEmail(a)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    sb.Append(MyDataReader["EmailAddress"].ToString());
                                }
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.LogInformation("[" + platForm + "] " + "SQLException on getClientEmail in Parser " + ex);
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
                                log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Client Email IN PARSER " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Client Email IN PARSER " + e);
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
            StringBuilder sb = new StringBuilder();
            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareGetClientText(a)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            try
                            {
                                MyConnection.Open();
                                IDataReader MyDataReader = cmd.ExecuteReader();

                                while (MyDataReader.Read())
                                {
                                    sb.Append(MyDataReader["Cell"].ToString());
                                }
                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.LogInformation("[" + platForm + "] " + "SQLException on getClientText in Parser " + ex);
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
                                log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Client Text IN PARSER " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Client Text IN PARSER " + e);
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
        protected int returnWaitTime(int pointsBehind)
        {
            int toReturn;

            int numberOfProcessIntervals = pointsBehind / Convert.ToInt32(configuration["NumberOfProcessPoints"]);

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
                    var reminder = NotificationServiceSetting.GetAppReminderSetting(a.AlarmID);
                    var setting = NotificationServiceSetting.GetNotificationServiceSetting(victimID, a.AlarmID);

                    bool reminderCheck = NotificationServiceSetting.CheckVictimReminderSetting(victimID, reminder);

                    if (reminder != null && reminderCheck == true)
                    {
                        string alternativeText = replaceAlarmsText(a, reminder.AlternativeText);

                        NotificationServiceData.Notification reminderNotification = new NotificationServiceData.Notification()
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
                            NotificationServiceData.Notification notif = new NotificationServiceData.Notification()
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
                notificationService.PushNotification(notifications);
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
