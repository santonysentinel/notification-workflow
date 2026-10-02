namespace ActiveAlarmsParser
{

    using System;
    using System.Collections.Generic;
    using System.Data;
    using Microsoft.Data.SqlClient;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using NotificationWorkflowService.Entity;
    using NotificationWorkflowService.Repository;
    using NotificationWorkflowService.Parser;
    using NotificationWorkflowService.Parser.ReferenceData;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;

    //using static System.Runtime.InteropServices.JavaScript.JSType;

    /// <summary>
    /// Defines the <see cref="WorkFlowSteps" />.
    /// </summary>
    internal class WorkFlowSteps
    {
        /// <summary>
        /// Defines the log.
        /// </summary>
        private readonly ILogger<WorkFlowSteps> log;

        /// <summary>
        /// Defines the activeProfiles.
        /// </summary>
        internal Dictionary<int, Profile> activeProfiles = new Dictionary<int, Profile>();

        /// <summary>
        /// Defines the activeHolidays.
        /// </summary>
        internal Dictionary<String, List<Holiday>> activeHolidays = new Dictionary<String, List<Holiday>>();

        /// <summary>
        /// Defines the roles.
        /// </summary>
        internal Dictionary<String, String> roles = new Dictionary<String, String>();

        /// <summary>
        /// Defines the victims.
        /// </summary>
        internal Dictionary<String, List<Victim>> victims = new Dictionary<String, List<Victim>>();

        /// <summary>
        /// Defines the clientProfileMapping.
        /// </summary>
        internal Dictionary<String, int> clientProfileMapping = new Dictionary<String, int>();

        /// <summary>
        /// Defines the clientHolidayProfileMapping.
        /// </summary>
        internal Dictionary<String, int> clientHolidayProfileMapping = new Dictionary<String, int>();

        /// <summary>
        /// Defines the activeAlarms.
        /// </summary>
        internal List<ActiveAlarm> activeAlarms = new List<ActiveAlarm>();

        /// <summary>
        /// Defines the platForm.
        /// </summary>
        private String platForm = "";

        private readonly IConfiguration configuration;
        private readonly IRepository repository;
        private WorkflowReferenceData? pendingReferenceData;

        /// <summary>
        /// Initializes a new instance of the <see cref="WorkFlowSteps"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="configuration">The application configuration.</param>
        public WorkFlowSteps(ILogger<WorkFlowSteps> logger, IConfiguration configuration)
            : this(logger, configuration, new Repository(configuration))
        {
        }

        public WorkFlowSteps(ILogger<WorkFlowSteps> logger, IConfiguration configuration, IRepository repository)
        {
            this.log = logger;
            this.configuration = configuration;
            this.repository = repository;
            setUpConnnectionStrings();
        }

        /// <summary>
        /// Performs the necessary preliminary steps to set up the parser - is ran at runtime, and periodically to refresh
        /// the updated parser state.
        /// </summary>
        /// <param name="platform">The platform<see cref="String"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        public bool setUpParser(string platform)
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

            return true;
        }

        // Workers call this parser serially; publication does not synchronize
        // concurrent readers. Rebuild the shell because its properties are init-only.
        private void StageReferenceData(
            Dictionary<string, int>? clientProfiles = null,
            Dictionary<string, int>? holidayProfiles = null,
            Dictionary<int, Profile>? profiles = null,
            Dictionary<string, List<Holiday>>? holidays = null,
            Dictionary<string, string>? roleNames = null,
            WorkflowVictimReferenceData? victimData = null)
        {
            var pending = pendingReferenceData ?? throw new InvalidOperationException("No reference refresh is pending.");
            pendingReferenceData = new WorkflowReferenceData
            {
                ClientProfileMapping = clientProfiles ?? pending.ClientProfileMapping,
                ClientHolidayProfileMapping = holidayProfiles ?? pending.ClientHolidayProfileMapping,
                ActiveProfiles = profiles ?? pending.ActiveProfiles,
                ActiveHolidays = holidays ?? pending.ActiveHolidays,
                Roles = roleNames ?? pending.Roles,
                Victims = victimData?.Victims ?? pending.Victims
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
                                        throw ex;

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
                                    throw exc;
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
                                        throw ex;
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
                                    throw exc;
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
                                        throw ex;
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
                                    throw exc;
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
                                        throw ex;
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
                                    throw exc;
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
        /// Gets the active alarms that have expired from the CurrentNotificationState Table and processes their next state.
        /// </summary>
        /// <returns>.</returns>
        public bool getExpiredAlarms()
        {
            log.LogInformation(String.Format("StepParser::getExpiredAlarms::DBCall"));
            bool success = true;
            activeAlarms = new List<ActiveAlarm>();

            using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareGetExpiredAlarms(DateTime.UtcNow)))
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
                                        ExpiryTime = MyDataReader["ExpiryTime"].ToString()
                                    };
                                    a.StateNo = Convert.ToInt32(MyDataReader["ParserStateNo"]);
                                    if (Convert.ToInt32(MyDataReader["CurrentStateNo"]) == 1)
                                    {
                                        a.CurrentStateNo = 2;
                                    }
                                    else
                                    {
                                        a.CurrentStateNo = Convert.ToInt32(MyDataReader["CurrentStateNo"]) + 1;
                                    }
                                    a.CurrentLoopNumber = Convert.ToInt32(MyDataReader["CurrentLoopNo"]);
                                    activeAlarms.Add(a);
                                }
                                getPriorityAndEmail(ref activeAlarms);
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "SQLException on getExpiredAlarms in Step Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on getExpiredAlarms in Step Parser " + ex);
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
                                        throw ex;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN REMOVE_EXPIRED_NOTIFICATION_ALARMS IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN REMOVE_EXPIRED_NOTIFICATION_ALARMS IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(3000);
                                }
                                else
                                {
                                    throw exc;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN REMOVE_EXPIRED_NOTIFICATION_ALARMS IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN REMOVE_EXPIRED_NOTIFICATION_ALARMS IN STEP PARSER " + e);
                }
            }
            return success;
        }

        /// <summary>
        /// Sets the priority, emails, email join, and State Numbers for each of the active alarms.
        /// </summary>
        /// <param name="activeAlarms">.</param>
        private void getPriorityAndEmail(ref List<ActiveAlarm> activeAlarms)
        {
            List<int> toRemove = new List<int>();
            log.LogInformation(String.Format("StepParser::getPriorityAndEmail::ActiveAlarmsCount::{0}", activeAlarms.Count));
            foreach (ActiveAlarm a in activeAlarms)
            {
                log.LogInformation(String.Format("StepParser::getPriorityAndEmail::AlarmSystemID::{0}", a.SystemID));
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
                    if (p.Events.ContainsKey(a.AlarmID))
                    {
                        Dictionary<int, List<ProfileItem>> dayEvents = p.Events[a.AlarmID];

                        if (dayEvents.ContainsKey(day))
                        {
                            // Preserve the legacy role lookup gate: it uses the
                            // original state's action even when a successor is selected.
                            int originalState = a.StateNo;
                            ProfileItem? pi = WorkflowProfileResolver.FindStepProfileItem(a, dayEvents[day], current);
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

                                ProfileItem roleSource = pi.StateNo == originalState ? pi : dayEvents[day].First(item =>
                                    item.StateNo == originalState && item.StartTime.TimeOfDay <= current.TimeOfDay
                                    && item.EndTime.TimeOfDay > current.TimeOfDay);
                                if (roleSource.Action == 12)
                                {
                                    readRoles(a, pi.RoleID, pi.RoleAction);
                                }
                            }
                        }
                    }
                }

                if (!found)
                {
                    //log.LogInfo(String.Format("StepParser::getPriorityAndEmail::AlarmSystemID::{0}::ProfileID::{1}::found::false", a.SystemID, a.ProfileID));
                    log.LogInformation(String.Format("StepParser::getPriorityAndEmail: No Profile found meet current time AlarmSysID::{0}", a.SystemID));

                    toRemove.Add(activeAlarms.IndexOf(a));
                }
            }

            foreach (int indice in toRemove.OrderByDescending(v => v))
            {
                activeAlarms.RemoveAt(indice);
            }
        }

        /// <summary>
        /// The Main active loop of the step parser, loops through all the activeAlarms and performs the necessary actions.
        /// </summary>
        public void parseAlarms()
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

            log.LogInformation(String.Format("StepParser::parseAlarms::activeAlarmsCount{0}", activeAlarms.Count));
            foreach (ActiveAlarm a in activeAlarms)
            {
                try
                {
                    bool insert = true;
                    StringBuilder sb = new StringBuilder();
                    sb.Append("Action as per the profile assigned:");
                    log.LogInformation(String.Format("StepParserBeforeCheck::AlarmSystemID::{0}::Priority::{1}", a.SystemID, a.Priority));
                    log.LogInformation(String.Format("StepParserBeforeCheck::AlarmSystemID::{0}::CurrentStateNo::{1}::NextStateNo::{2}::ProcessNextStep::{3}::StateTime::{4}::CurrentLoopNumber::{5}::insert::{6}"
                        , a.SystemID
                        , a.CurrentStateNo
                        , a.NextStateNo
                        , a.ProcessNextStep
                        , a.StateTime
                        , a.CurrentStateNo
                        , insert.ToString()
                        ));

                    switch (a.Priority)
                    {
                        case 1:
                            sb.Append("Do Nothing");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Do Nothing " + "(Step " + a.StateNo + ")");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Do Nothing " + "(Step " + a.StateNo + ")");
                            insert = false;
                            break;

                        case 2:
                            sb.Append("Auto Email");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email " + "(Step " + a.StateNo + ")");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email " + "(Step " + a.StateNo + ")");
                            SendNotificationsToOfficersInSameGroup(a);
                            AddToNotificationQueue(a, 3);
                            string emailAdresses = "Pages sent to: " + a.EmailAddresses;
                            if (emailAdresses.Length > 4096)
                            {
                                emailAdresses = emailAdresses.Substring(0, 4096);
                            }
                            CreateAlarmAudit(1, emailAdresses, a.HistoryID, a.CurrentStateNo);
                            AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                            break;
                        case 3:
                            sb.Append("McApp");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp " + "(Step " + a.StateNo + ")");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp " + "(Step " + a.StateNo + ")");
                            SendNotificationsToOfficersInSameGroup(a);
                            PushAlertToMcApp(a);
                            a.ProcessNextStep = 0;
                            break;
                        case 4:
                            sb.Append("Auto Fax");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Fax " + "(Step " + a.StateNo + ")");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Fax " + "(Step " + a.StateNo + ")");
                            SendNotificationsToOfficersInSameGroup(a);
                            break;
                        case 5:
                            sb.Append("Auto Page");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Page " + "(Step " + a.StateNo + ")");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Page " + "(Step " + a.StateNo + ")");
                            SendNotificationsToOfficersInSameGroup(a);
                            AddToNotificationQueue(a, 1);
                            string autoPageEmails = "Pages sent to: " + getInsertEmails(a);
                            if (autoPageEmails.Length > 4096)
                            {
                                autoPageEmails = autoPageEmails.Substring(0, 4096);
                            }
                            CreateAlarmAudit(3, autoPageEmails, a.HistoryID, a.CurrentStateNo);
                            AddActiveAlarmActionToActivity(a.HistoryID, autoPageEmails, 0);
                            break;
                        case 6:
                            sb.Append("McApp and Auto Fax");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Fax " + "(Step " + a.StateNo + ")");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Fax " + "(Step " + a.StateNo + ")");
                            SendNotificationsToOfficersInSameGroup(a);
                            PushAlertToMcApp(a);
                            a.ProcessNextStep = 0;
                            break;
                        case 7:
                            sb.Append("McApp and Auto Email");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Email " + "(Step " + a.StateNo + ")");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Email " + "(Step " + a.StateNo + ")");
                            PushAlertToMcApp(a);
                            SendNotificationsToOfficersInSameGroup(a);
                            AddToNotificationQueue(a, 3);
                            string emailAdresses7 = "Pages sent to: " + a.EmailAddresses;
                            if (emailAdresses7.Length > 4096)
                            {
                                emailAdresses7 = emailAdresses7.Substring(0, 4096);
                            }
                            CreateAlarmAudit(3, emailAdresses7, a.HistoryID, a.CurrentStateNo);
                            AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                            a.ProcessNextStep = 0;
                            break;
                        case 9:
                            sb.Append("McApp and Auto Page");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Page " + "(Step " + a.StateNo + ")");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp and Auto Page " + "(Step " + a.StateNo + ")");
                            SendNotificationsToOfficersInSameGroup(a);
                            PushAlertToMcApp(a);
                            AddToNotificationQueue(a, 1);
                            string autoPageMcAppEmails = "Pages sent to: " + getInsertEmails(a);
                            if (autoPageMcAppEmails.Length > 4096)
                            {
                                autoPageMcAppEmails = autoPageMcAppEmails.Substring(0, 4096);
                            }
                            CreateAlarmAudit(3, autoPageMcAppEmails, a.HistoryID, a.CurrentStateNo);
                            AddActiveAlarmActionToActivity(a.HistoryID, autoPageMcAppEmails, 0);
                            a.ProcessNextStep = 0;
                            break;
                        case 11:
                            sb.Append("Delay");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Delay " + "(Step " + a.StateNo + ")");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Delay " + "(Step " + a.StateNo + ")");
                            insert = true;
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
                                        Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp Call Officers " + "(Step " + a.StateNo + ")");
                                        log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "McApp Call Officers " + "(Step " + a.StateNo + ")");
                                        PushAlertToMcApp(a);
                                        a.ProcessNextStep = 0;
                                    }
                                    break;
                                case 2:
                                    if (a.EmailAddresses != "")
                                    {
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email " + "(Step " + a.StateNo + ")");
                                        log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email " + "(Step " + a.StateNo + ")");
                                        AddToNotificationQueue(a, 3);
                                        string emails = "Pages sent to: " + a.EmailAddresses;
                                        if (emails.Length > 4096)
                                        {
                                            emails = emails.Substring(0, 4096);
                                        }
                                        CreateAlarmAudit(14, emails, a.HistoryID, a.CurrentStateNo);
                                        AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                                    }
                                    break;
                                case 3:
                                    if (a.EmailAddresses != "")
                                    {
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email " + "(Step " + a.StateNo + ")");
                                        log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Auto Email " + "(Step " + a.StateNo + ")");
                                        AddToNotificationQueue(a, 3);
                                        string txtMessages = "Pages sent to: " + a.EmailAddresses;
                                        if (txtMessages.Length > 4096)
                                        {
                                            txtMessages = txtMessages.Substring(0, 4096);
                                        }
                                        CreateAlarmAudit(14, txtMessages, a.HistoryID, a.CurrentStateNo);
                                        AddActiveAlarmActionToActivity(a.HistoryID, a.EmailAddresses, 1);
                                    }
                                    break;
                                default: break;
                            }
                            break;
                        case 13: //send victims email

                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Email-Victims");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Email-Victims");

                            string victimsMail = "";
                            try
                            {
                                List<Victim> offenderVictims = victims[a.ClientID];
                                StringBuilder victimEmail = new StringBuilder();
                                foreach (Victim vi in offenderVictims)
                                {
                                    if (!string.IsNullOrEmpty(vi.Email))
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
                                log.LogError(ex, "victimsMail");
                                victimsMail = "";
                            }

                            string victimEmails = "Pages sent to: " + victimsMail;
                            if (victimEmails.Length > 4096)
                            {
                                victimEmails = victimEmails.Substring(0, 4096);
                            }
                            CreateAlarmAudit(14, victimEmails, a.HistoryID, 1);
                            AddActiveAlarmActionToActivity(a.HistoryID, victimEmails, 1);
                            break;
                        case 14:
                            sb.Append("Contact Victim");
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Contact Victim");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Contact Victim");
                            SendNotificationsToOfficersInSameGroup(a);
                            PushAlertToMcApp(a);
                            a.ProcessNextStep = 0;
                            break;

                        case 15: //alert client
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client");

                            string clientMail = "";
                            try
                            {
                                clientMail = getClientEmail(a).Trim();
                                if (clientMail != string.Empty)
                                {
                                    insertNotificationQueueVictim(a, 3, clientMail);
                                }
                            }
                            catch (Exception ex)
                            {
                                log.LogError(ex, "clientMail");
                                clientMail = "";
                            }

                            string cMail = "Pages sent to: " + clientMail;
                            if (cMail.Length > 4096)
                            {
                                cMail = cMail.Substring(0, 4096);
                            }
                            CreateAlarmAudit(14, cMail, a.HistoryID, 1);
                            AddActiveAlarmActionToActivity(a.HistoryID, cMail, 1);
                            break;
                        case 16: //alert client text
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client Text");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Alert Client Text");

                            string clientText = "";
                            try
                            {
                                clientText = getClientText(a).Trim();
                                if (clientText != string.Empty)
                                {
                                    insertNotificationQueueVictim(a, 4, clientText);
                                }
                            }
                            catch (Exception ex)
                            {
                                log.LogError(ex, "clientText");
                                clientText = "";
                            }

                            string cText = "Pages sent to: " + clientText;
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
                            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Text All Victims");
                            log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID.ToString() + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: " + "Text All Victims");

                            string victimsText = "";
                            try
                            {
                                List<Victim> offenderVictims = victims[a.ClientID];
                                StringBuilder victimText = new StringBuilder();
                                foreach (Victim vi in offenderVictims)
                                {
                                    if (!string.IsNullOrEmpty(vi.CellPhone))
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


                            string Msg = "Text sent to: " + victimsText;
                            if (Msg.Length > 4096)
                            {
                                Msg = Msg.Substring(0, 4096);
                            }
                            CreateAlarmAudit(14, Msg, a.HistoryID, 1);
                            AddActiveAlarmActionToActivity(a.HistoryID, Msg, 1);
                            break;
                        default: sb.Append("Do Nothing"); break;
                    }

                    CreateAlarmAudit(14, sb.ToString(), a.HistoryID, a.CurrentStateNo);

                    insertIntoAlarmNotification(a.SystemID, a.CurrentStateNo, DateTime.UtcNow.AddMinutes(a.StateTime), PriorityMapping[a.Priority]);

                    log.LogInformation(String.Format("StepParserAfterCheck::AlarmSystemID::{0}::CurrentStateNo::{1}::NextStateNo::{2}::ProcessNextStep::{3}::StateTime::{4}::CurrentLoopNumber::{5}::insert::{6}", a.SystemID, a.CurrentStateNo, a.NextStateNo, a.ProcessNextStep, a.StateTime, a.CurrentStateNo, insert.ToString()));
                    if (a.NextStateNo != -1 && insert)
                    {
                        insertIntoCurrentAlarmNotification(a.SystemID, a.NextStateNo, DateTime.UtcNow.AddMinutes(a.StateTime), a.CurrentStateNo, a.CurrentLoopNumber, a.ProcessNextStep);
                    }
                    else if (insert)
                    {
                        insertIntoCurrentAlarmNotification(a.SystemID, a.CurrentStateNo + 1, DateTime.UtcNow.AddMinutes(a.StateTime), a.CurrentStateNo, 0, 1);
                    }

                    //DateTime ExpiryTime = DateTime.Parse(a.ExpiryTime);
                    //DateTime ExpiryTimeByPreviousExpiryTime = ExpiryTime.AddMinutes(a.StateTime);
                    //DateTime ExpiryTimeByUTCNow = DateTime.UtcNow.AddMinutes(a.StateTime);

                    //DateTime ExpiryTimeApplied;
                    //if (ExpiryTimeByPreviousExpiryTime < ExpiryTimeByUTCNow)
                    //{
                    //    ExpiryTimeApplied = ExpiryTimeByPreviousExpiryTime;
                    //}
                    //else
                    //{
                    //    ExpiryTimeApplied = ExpiryTimeByUTCNow;
                    //}

                    //log.LogInfo(String.Format("ExpiryTimeByPreviousExpiryTime - {0} :: ExpiryTimeByUTCNow - {1} :: ExpiryTimeApplied - {2}"
                    //                        , ExpiryTimeByPreviousExpiryTime.ToString()
                    //                        , ExpiryTimeByUTCNow.ToString()
                    //                        , ExpiryTimeApplied.ToString()
                    //                        )
                    //);

                    //insertIntoAlarmNotification(a.SystemID, a.CurrentStateNo, ExpiryTimeApplied, PriorityMapping[a.Priority]);

                    //if (a.NextStateNo != -1 && insert)
                    //{
                    //    insertIntoCurrentAlarmNotification(a.SystemID, a.NextStateNo, ExpiryTimeApplied, a.CurrentStateNo, a.CurrentLoopNumber, a.ProcessNextStep);
                    //}
                    //else if (insert)
                    //{
                    //    insertIntoCurrentAlarmNotification(a.SystemID, a.CurrentStateNo + 1, ExpiryTimeApplied, a.CurrentStateNo, 0, 1);
                    //}
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Step Parser Parse Error");
                }

            }
        }

        /// <summary>
        /// Inserts a row into the Audit Alarms Table.
        /// </summary>
        /// <param name="type">.</param>
        /// <param name="action">.</param>
        /// <param name="historyID">.</param>
        /// <param name="StepNo">The StepNo<see cref="int"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool CreateAlarmAudit(int type, String action, int historyID, int StepNo)
        {
            bool success = true;
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
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "SQLException on CreateAlarmAudit in Step Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on CreateAlarmAudit in Step Parser " + ex);
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
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN MCAPP_CREATEAUDIT IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CREATEAUDIT IN STEP PARSER " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN MCAPP_CREATEAUDIT IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CREATEAUDIT IN STEP PARSER " + e);
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
        /// Inserts a Row Into The History Table.
        /// </summary>
        /// <param name="historyID">.</param>
        /// <param name="email">The email<see cref="String"/>.</param>
        /// <param name="type">.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private bool AddActiveAlarmActionToActivity(int historyID, string email, int type)
        {
            bool success = true;
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
                                success = true;
                                break;
                            }
                            catch (SqlException ex)
                            {
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "SQLException on ActiveAlarms_InsertIntoHistory in Step Parser " + ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on ActiveAlarms_InsertIntoHistory in Step Parser " + ex);
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
                                        throw ex;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_HISTORY IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_HISTORY IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    Thread.Sleep(2000);
                                }
                                else
                                {
                                    throw exc;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    success = false;
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_HISTORY IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_HISTORY IN STEP PARSER " + e);
                }
            }
            return success;
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
        /// Inserts a row into the Current Notification State Table, which is used to keep track of all the alarm states,
        /// when their expiry times are and the current state number that they are in.
        /// </summary>
        /// <param name="AlarmSystemID">.</param>
        /// <param name="CurentStateNo">.</param>
        /// <param name="ExpiryTime">.</param>
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
                    throw;
                }
            }

            return sb.ToString().TrimEnd(',');
        }

        /// <summary>
        /// The holidayCheck.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="current">The current<see cref="DateTime"/>.</param>
        /// <returns>The <see cref="Boolean"/>.</returns>
        private bool holidayCheck(ActiveAlarm a, DateTime current)
        {
            return WorkflowProfileResolver.IsHoliday(a, current, activeHolidays);
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
        /// Inserts The Active Alarm Into The Notification Queue.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="insertType">The insertType<see cref="int"/>.</param>
        /// <param name="victimsEmails">The victimsEmails<see cref="String"/>.</param>
        /// /// <param name="isVictimNotification">The isVictimNotification<see cref="bool"/>.</param>
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
            bool disposing = false;

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

                                result = WorkflowReferenceDataBuilders.BuildVictims(MyDataReader, step: true);

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
                    disposing = true;
                }
            }
            catch (Exception e) when (disposing)
            {
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER " + e);
                return false;
            }
            catch (Exception e)
            {
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER " + e);
                throw;
            }
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(victimData: result);
                else victims = result.Victims;
            }
            return success;
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
    }
}
