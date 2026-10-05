using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Parser.ReferenceData;
using NotificationWorkflowService.Parser.Actions;
using NotificationWorkflowService.Service.Notes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Text;
using ActiveAlarmsParser.Service.NotificationService;
using NotificationSender = ActiveAlarmsParser.Service.NotificationService.NotificationService;

namespace NotificationWorkflowService.Parser
{
    public class WorkFlowCommon
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
        private readonly ILogger log;

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

        private readonly IConfiguration configuration;
        private readonly String Platform;
        private readonly NotificationSender notificationService;
        private readonly IRepository repository;
        private readonly INoteService noteService;
        private WorkflowReferenceData? pendingReferenceData;


        /// <summary>
        /// Initializes a new instance of the <see cref="Parser"/> class.
        /// </summary>
        /// <param name="connection">The connection<see cref="String"/>.</param>
        public WorkFlowCommon(ILogger<WorkFlowCommon> logger, IConfiguration configuration, NotificationSender notificationService)
            : this(logger, configuration, notificationService, new NotificationWorkflowService.Repository.Repository(configuration))
        {
        }

        public WorkFlowCommon(ILogger<WorkFlowCommon> logger, IConfiguration configuration, NotificationSender notificationService, IRepository repository)
            : this((ILogger)logger, configuration, notificationService, repository)
        {
        }

        public WorkFlowCommon(ILogger<WorkFlowCommon> logger, IConfiguration configuration, NotificationSender notificationService, IRepository repository, INoteService noteService)
            : this((ILogger)logger, configuration, notificationService, repository, noteService)
        {
        }

        protected WorkFlowCommon(ILogger logger, IConfiguration configuration, NotificationSender notificationService, IRepository repository)
            : this(logger, configuration, notificationService, repository,
                new NoteService(repository, new UnavailableNoteLocationProvider(), new UnavailableNoteAddressResolver()))
        {
        }

        protected WorkFlowCommon(ILogger logger, IConfiguration configuration, NotificationSender notificationService, IRepository repository, INoteService noteService)
        {
            this.log = logger;
            this.configuration = configuration;
            this.notificationService = notificationService;
            this.repository = repository;
            this.noteService = noteService;
            Platform = configuration["Platform"] ?? "";
            setUpConnnectionStrings();
        }

        /// <summary>
        /// The setUpParser.
        /// </summary>
        /// <param name="platform">The platform<see cref="String"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        public async Task<bool> setUpParserAsync(String platform, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            platForm = platform;
            pendingReferenceData = new WorkflowReferenceData();
            try
            {
                if (!await SetUpReferenceDataAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return false;
                }
                cancellationToken.ThrowIfCancellationRequested();
                PublishReferenceData(pendingReferenceData);
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }
            finally
            {
                pendingReferenceData = null;
            }
        }

        private async Task<bool> SetUpReferenceDataAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await FetchClientProfileAsync(0, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            if (!await FetchClientProfileAsync(1, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            if (!await readAllProfilesAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            if (!await readAllHolidaysAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            if (!await readAllRolesAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            if (!await readVictimsAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            if (!await readMEZVictimsAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            if (!await readAttachedVictimZonesAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            if (!await readProfileItemsClearAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            //readPNSettings(); // We do not have push notifications now. (April 2021)

            cancellationToken.ThrowIfCancellationRequested();
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
        public async Task<bool> readPointsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;

            int LastProcessedPoint = await readLastSuccessfulProcessAsync(Convert.ToInt32(configuration[platForm + "ParserID"]), cancellationToken).ConfigureAwait(false);
            int pointsBehind = await ReadCurrentActiveAlarmPointAsync(cancellationToken).ConfigureAwait(false) - LastProcessedPoint;

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
                int readPointsCheckpoint = 0;
                await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadPoints(configuration["NumberOfProcessPoints"], readPointsCheckpoint)))
            {
                try
                {
                    readPointsCheckpoint = await readLastSuccessfulProcessAsync(Convert.ToInt32(configuration[platForm + "ParserID"]), cancellationToken).ConfigureAwait(false);
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            activeAlarms = new List<ActiveAlarm>();

                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                while (await MyDataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
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
                                await getPriorityAndEmailAsync(activeAlarms, 0, cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM POINTS IN PARSER ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM POINTS IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    success = false;
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM POINTS IN PARSER ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM POINTS IN PARSER " + e);

                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Sets the priority, emails and email join for each of the active alarms.
        /// </summary>
        /// <param name="activeAlarms">.</param>
        /// <param name="currentState">The currentState<see cref="int"/>.</param>
        private async Task getPriorityAndEmailAsync(List<ActiveAlarm> activeAlarms, int currentState, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ActiveAlarm a in activeAlarms)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                                    await readRolesAsync(a, pi.RoleID, pi.RoleAction, cancellationToken).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                try
                                {
                                    log.LogInformation(String.Format("FindStepOneProfileItem: No Profile found meet current time AlarmSysID::{0}", a.SystemID));
                                }
                                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                                {
                                    throw;
                                }
                                catch (Exception exx)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
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
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exx)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
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
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
        }
        public ProfileItem? FindStepOneProfileItem(List<ProfileItem> profileItems, DateTime current)
        {
            try
            {
                return WorkflowProfileResolver.FindInitialProfileItem(profileItems, current);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                log.LogError(e, "FindStepOneProfileItem Error");
                return null;
            }
        }

        /// <summary>
        /// The Main active loop of the step parser, loops through all the activeAlarms and performs the necessary actions.
        /// </summary>
        public async Task parseAlarmsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                PriorityMapping.Add(18, "Add Note");

                Dictionary<int, String> RoleActionMapping = new Dictionary<int, String>();
                RoleActionMapping.Add(1, "Call");
                RoleActionMapping.Add(2, "E-mail");
                RoleActionMapping.Add(3, "Text");

                int? sysID = null;

                log.LogInformation(String.Format("ParseAlarm::CurrentNumberOfActiveAlarms::{0}", activeAlarms.Count));
                foreach (ActiveAlarm a in activeAlarms)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogInformation(String.Format("ParseAlarm::AlarmSystemID::{0}", a.SystemID));
                    try
                    {
                        curr = a;

                        try
                        {
                            await PushAlertsToVictimsAsync(a, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            log.LogError(ex, "PushAlertsToVictims Error");
                        }

                        try
                        {
                            //if (a.ClientID == "ID901491")
                            //{

                            //    PushNotificationToVictim(a);
                            //}

                            await PushNotificationToVictimAsync(a, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            log.LogError(ex, "PushNotificationToVictim Error");
                        }


                        Boolean insert = true;

                        await CreateAlarmAuditAsync(15, "", a.HistoryID, 1, cancellationToken).ConfigureAwait(false);

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

                            await ClearMcAppAlarmAsync(clearEvents, a.ClientID, cancellationToken).ConfigureAwait(false);
                        }
                        log.LogInformation(String.Format("ParserBeforeCheck::AlarmSystemID::{0}::Priority::{1}", a.SystemID, a.Priority));
                        log.LogInformation(String.Format("ParserBeforeCheck::AlarmSystemID::{0}::CurrentStateNo::{1}::NextStateNo::{2}::ProcessNextStep::{3}::StateTime::{4}::CurrentLoopNumber::{5}::insert::{6}", a.SystemID, a.CurrentStateNo, a.NextStateNo, a.ProcessNextStep, a.StateTime, a.CurrentStateNo, insert.ToString()));

                        WorkflowActionResult result = await WorkflowActionExecutor.ExecuteAsync(a,
                            new WorkflowActionContext(WorkflowActionMode.Normal, platForm, log,
                                new ActionOperations(this), RoleActionMapping, roles, victims), cancellationToken).ConfigureAwait(false);
                        insert = result.Insert;
                        sb.Append(result.Summary);

                        await CreateAlarmAuditAsync(14, sb.ToString(), a.HistoryID, 1, cancellationToken).ConfigureAwait(false);

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

                        await insertIntoAlarmNotificationAsync(a.SystemID, 1, ExpiryTimeApplied, PriorityMapping[a.Priority], cancellationToken).ConfigureAwait(false);
                        log.LogInformation(String.Format("ParserAfterCheck::AlarmSystemID::{0}::CurrentStateNo::{1}::NextStateNo::{2}::ProcessNextStep::{3}::StateTime::{4}::CurrentLoopNumber::{5}::insert::{6}", a.SystemID, a.CurrentStateNo, a.NextStateNo, a.ProcessNextStep, a.StateTime, a.CurrentStateNo, insert.ToString()));

                        if (a.NextStateNo != -1 && insert)
                        {
                            await insertIntoCurrentAlarmNotificationAsync(a.SystemID, a.NextStateNo, ExpiryTimeApplied, a.StateNo + 1, 0, a.ProcessNextStep, cancellationToken).ConfigureAwait(false);
                        }
                        else if (insert)
                        {
                            await insertIntoCurrentAlarmNotificationAsync(a.SystemID, a.StateNo + 1, ExpiryTimeApplied, a.StateNo + 1, 0, 1, cancellationToken).ConfigureAwait(false);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        sysID = a.SystemID;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception e)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        log.LogError(e, "Error processing alarm");
                        break;
                    }
                }
                if (sysID != null)
                {
                    await updateParserActivtyAsync(sysID, cancellationToken).ConfigureAwait(false);
                }

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                log.LogInformation("FAILED TO PARSE ALARMS " + ex);
                if (curr != null)
                {
                    log.LogInformation("ERROR @ ActiveAlarmID - " + curr.SystemID);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private sealed class ActionOperations(WorkFlowCommon parser) : IWorkflowActionOperations
        {
            public Task AddNoteAsync(string template, string oid, CancellationToken cancellationToken = default) => parser.noteService.AddNoteAsync(template, oid, cancellationToken);
            public Task SendNotificationsToOfficersInSameGroupAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => parser.SendNotificationsToOfficersInSameGroupAsync(a, cancellationToken);
            public Task<bool> PushAlertToMcAppAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => parser.PushAlertToMcAppAsync(a, cancellationToken);
            public Task<bool> AddToNotificationQueueAsync(ActiveAlarm a, int insertType, CancellationToken cancellationToken = default) => parser.AddToNotificationQueueAsync(a, insertType, cancellationToken);
            public Task CreateAlarmAuditAsync(int type, string action, int historyID, int StepNo, CancellationToken cancellationToken = default) => parser.CreateAlarmAuditAsync(type, action, historyID, StepNo, cancellationToken);
            public Task AddActiveAlarmActionToActivityAsync(int historyID, string email, int type, CancellationToken cancellationToken = default) => parser.AddActiveAlarmActionToActivityAsync(historyID, email, type, cancellationToken);
            public Task<bool> insertNotificationQueueVictimAsync(ActiveAlarm a, int insertType, string victimsEmails, bool isVictimNotification = false, CancellationToken cancellationToken = default) => parser.insertNotificationQueueVictimAsync(a, insertType, victimsEmails, isVictimNotification, cancellationToken);
            public Task<string> getInsertEmailsAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => parser.getInsertEmailsAsync(a, cancellationToken);
            public Task<string> getClientEmailAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => parser.getClientEmailAsync(a, cancellationToken);
            public Task<string> getClientTextAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => parser.getClientTextAsync(a, cancellationToken);
        }

        /// <summary>
        /// When a valid alert is recieved from an offender alert all associated victims.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        public async Task PushAlertsToVictimsAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MEZVictims.ContainsKey(a.ClientID))
            {
                List<String> victims = MEZVictims[a.ClientID].ToList();
                foreach (String v in victims)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pnAlarms.ContainsKey(v) && pnAlarms[v].Contains(a.AlarmID))
                    {
                        String victim = "Push notification sent to: " + v;
                        await CreateAlarmAuditAsync(3, victim, a.HistoryID, 1, cancellationToken).ConfigureAwait(false);

                        await insertPushNotificationQueueAsync(a, v, a.ClientID, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Creates in memory a full dictionary of all the client profiles which is used to map OID to profile ID.
        /// </summary>
        /// <param name="profileType">The profileType<see cref="int"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> FetchClientProfileAsync(int profileType, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " Refreshing Client Profiles");
            Dictionary<string, int> result = new();
            //Create a connection to the SQL Server;
            try
            {
                await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareFetchClientProfile(profileType)))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 5;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                result = await WorkflowReferenceDataBuilders.BuildClientProfilesAsync(MyDataReader, profileType, cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(10000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;

                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE CLIENT PROFILES ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE CLIENT PROFILES " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(10000, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                success = false;
                log.LogInformation("[" + platForm + "] " + "FAILED TO RETRIEVE CLIENT PROFILES " + e);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE CLIENT PROFILES " + e);

            }
            cancellationToken.ThrowIfCancellationRequested();
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

            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Creates in memory a full dictionary of all the active profiles that the active alarms are used against.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> readAllProfilesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " Refreshing Client Profiles");
            Dictionary<int, Profile> result = new();

            //Create a connection to the SQL Server;
            try
            {
                await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadAllProfiles()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 5;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                result = await WorkflowReferenceDataBuilders.BuildProfilesAsync(MyDataReader, cancellationToken).ConfigureAwait(false);

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
                                        await Task.Delay(10000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }

                                //System.Environment.Exit(1);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE PROFILES ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE PROFILES IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(10000, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE PROFILES ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE PROFILES IN PARSER " + e);


                //System.Environment.Exit(1);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(profiles: result);
                else activeProfiles = result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Creates in memory a full dictionary of all the active profiles that the active alarms are used against.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> readAllHolidaysAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " Refreshing PO GRoup Holidays");
            Dictionary<string, List<Holiday>> result = new();

            //Create a connection to the SQL Server;
            try
            {
                await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadAllHolidays()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                result = await WorkflowReferenceDataBuilders.BuildHolidaysAsync(MyDataReader, cancellationToken).ConfigureAwait(false);

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
                                        await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE HOLIDAYS ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE HOLIDAYS IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE HOLIDAYS ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE HOLIDAYS IN PARSER " + e);

            }
            cancellationToken.ThrowIfCancellationRequested();
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(holidays: result);
                else activeHolidays = result;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Reads in the list of all POGroup Roles.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> readAllRolesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " Refreshing POGroup Roles");
            Dictionary<string, string> result = new();

            //Create a connection to the SQL Server;
            try
            {
                await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadAllRoles()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                result = await WorkflowReferenceDataBuilders.BuildRolesAsync(MyDataReader, cancellationToken: cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE ROLES ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ROLES IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ROLES ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ROLES IN PARSER " + e);

            }
            cancellationToken.ThrowIfCancellationRequested();
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(roleNames: result);
                else roles = result;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Inserts events into the McAPP queue, if the item priority numbers require it.
        /// </summary>
        /// <param name="a">.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> PushAlertToMcAppAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
            //Create a connection to the SQL Server;
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PreparePushAlertToMcApp(a)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_MCAPP IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_MCAPP IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    success = false;
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_MCAPP IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_MCAPP IN STEP PARSER " + e);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Inserts The Active Alarm Into The Notification Queue.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="insertType">The insertType<see cref="int"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> AddToNotificationQueueAsync(ActiveAlarm a, int insertType, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareAddToNotificationQueue(a, insertType)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_NOTIFICATIONQUEUE IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    success = false;
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_NOTIFICATIONQUEUE IN STEP PARSER " + e);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Inserts The Active Alarm Into The Push Notification Queue.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="VictimID">The VictimID<see cref="String"/>.</param>
        /// <param name="OffenderID">The OffenderID<see cref="String"/>.</param>
        private async Task insertPushNotificationQueueAsync(ActiveAlarm a, String VictimID, String OffenderID, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            //Create a connection to the SQL Server;
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareInsertPushNotificationQueue(a, VictimID, OffenderID)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE " + exc);
                                throw exc;
                            }


                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_PUSH_NOTIFICATIONQUEUE " + e);

                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Inserts The Active Alarm Into The Notification Queue.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="insertType">The insertType<see cref="int"/>.</param>
        /// <param name="victimsEmails">The victimsEmails<see cref="String"/>.</param>
        /// <param name="isVictimNotification">The isVictimNotification<see cref="bool"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> insertNotificationQueueVictimAsync(ActiveAlarm a, int insertType, String victimsEmails, bool isVictimNotification = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;

            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareInsertNotificationQueueVictim(a, insertType, victimsEmails, isVictimNotification)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();


                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_NOTIFICATIONQUEUE " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    success = false;
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_NOTIFICATIONQUEUE " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_NOTIFICATIONQUEUE " + e);
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Inserts The Active Alarm Into The Notification Queue.
        /// </summary>
        /// <param name="systemID">The systemID<see cref="int?"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> updateParserActivtyAsync(int? systemID, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = false;
            //Create a connection to the SQL Server;
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareUpdateParserActivty(systemID, configuration[platForm + "ParserID"])))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();



                        int retries = 6;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RUN UPDATE_PARSER_ACTIVITY ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN UPDATE_PARSER_ACTIVITY " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    success = false;
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RUN UPDATE_PARSER_ACTIVITY IN PARSER ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN UPDATE_PARSER_ACTIVITY IN PARSER " + e);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Inserts a row into the Audit Alarms Table.
        /// </summary>
        /// <param name="type">.</param>
        /// <param name="action">.</param>
        /// <param name="historyID">.</param>
        /// <param name="StepNo">The StepNo<see cref="int"/>.</param>
        private async Task CreateAlarmAuditAsync(int type, String action, int historyID, int StepNo, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            //Create a connection to the SQL Server;
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareCreateAlarmAudit(type, action, historyID, StepNo)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RUN MCAPP_CREATEAUDIT IN PARSER ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CREATEAUDIT IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RUN MCAPP_CREATEAUDIT IN PARSER ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CREATEAUDIT IN PARSER " + e);

                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Inserts a Row Into The History Table.
        /// </summary>
        /// <param name="historyID">.</param>
        /// <param name="email">The email<see cref="String"/>.</param>
        /// <param name="type">.</param>
        private async Task AddActiveAlarmActionToActivityAsync(int historyID, String email, int type, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            //Create a connection to the SQL Server;
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareAddActiveAlarmActionToActivity(historyID, email, type)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER " + e);

                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Inserts A Row into the Alarm Notification Table - used as an archive of all processed alarms.
        /// </summary>
        /// <param name="AlarmSystemID">.</param>
        /// <param name="CurentStateNo">.</param>
        /// <param name="ExpiryTime">.</param>
        /// <param name="Action">.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> insertIntoAlarmNotificationAsync(int AlarmSystemID, int CurentStateNo, DateTime ExpiryTime, String Action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
            //Create a connection to the SQL Server;
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareInsertIntoAlarmNotification(AlarmSystemID, CurentStateNo, ExpiryTime, Action)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_ALARM_NOTIFICATION IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_ALARM_NOTIFICATION IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    success = false;
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_ALARM_NOTIFICATION IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_ALARM_NOTIFICATION IN STEP PARSER " + e);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
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
        private async Task<bool> insertIntoCurrentAlarmNotificationAsync(int AlarmSystemID, int CurentStateNo, DateTime ExpiryTime, int DisplayStateNo, int currentLoopNumber, int processNext, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
            //Create a connection to the SQL Server;
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareInsertIntoCurrentAlarmNotification(AlarmSystemID, CurentStateNo, ExpiryTime, DisplayStateNo, currentLoopNumber, processNext)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_CURRENT_NOTIFICATION_STATE IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_CURRENT_NOTIFICATION_STATE IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    success = false;
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_CURRENT_NOTIFICATION_STATE IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_CURRENT_NOTIFICATION_STATE IN STEP PARSER " + e);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Runs the email join logic if the active alarm state has the email join flag set.
        /// </summary>
        /// <param name="a">.</param>
        protected async Task SendNotificationsToOfficersInSameGroupAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (a.EmailJoin == 1)
            {
                if (await AddToNotificationQueueAsync(a, 2, cancellationToken).ConfigureAwait(false))
                {
                    string emails = "Email also sent to: " + await getInsertEmailsAsync(a, cancellationToken).ConfigureAwait(false);
                    if (emails.Length > 4096)
                    {
                        emails = emails.Substring(0, 4096);
                    }
                    await CreateAlarmAuditAsync(3, emails, a.HistoryID, a.CurrentStateNo, cancellationToken).ConfigureAwait(false);
                    await AddActiveAlarmActionToActivityAsync(a.HistoryID, await getInsertEmailsAsync(a, cancellationToken).ConfigureAwait(false), 0, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    log.LogInformation(DateTime.UtcNow.ToString("HH:mm:ss") + " AAID:" + a.SystemID + " " + a.ClientID.Trim() + " in POGroup " + a.POGroupNum + " - " + "[" + a.AlarmID + "]" + " Action: Notify to all officers in group failed");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Returns the email addresses of the top 10 individuals that were contacted as the result of an alarm.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <returns>.</returns>
        private async Task<string> getInsertEmailsAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StringBuilder sb = new StringBuilder();
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareGetInsertEmails(a)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                while (await MyDataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Audit and History Emails in Step Parser " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Audit and History Emails in Step Parser " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Audit and History Emails in Step Parser " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Audit and History Emails in Step Parser " + e);

                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return sb.ToString().TrimEnd(',');
        }

        /// <summary>
        /// Reads the Last Processed Point SystemID from the ParserActivity table, used when the program is restarted or when reading in a set of points.
        /// </summary>
        /// <param name="parserID">The parserID<see cref="int"/>.</param>
        /// <returns>.</returns>
        private async Task<int> readLastSuccessfulProcessAsync(int parserID, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int SystemID = 0;
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadLastSuccessfulProcess(parserID)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();
                        int retries = 5;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                while (await MyDataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
                                        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                log.LogError(exc, "[" + platForm + "] " + "ERROR: Unable to Read Last successful processed ActiveAlarms point IN PARSER ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Last successful processed ActiveAlarms point IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogError(e, "[" + platForm + "] " + "ERROR: Unable to Read Last successful processed ActiveAlarms point IN PARSER ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Last successful processed ActiveAlarms point IN PARSER " + e);
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
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
        private async Task<bool> readRolesAsync(ActiveAlarm a, int RoleID, int RoleAction, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
            //Create a connection to the SQL Server;
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadRoles(a, RoleID, RoleAction)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                while (await MyDataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM ROLES IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM ROLES IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    success = false;
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM ROLES IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM ROLES IN STEP PARSER " + e);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// The readVictims.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> readVictimsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;

            WorkflowVictimReferenceData result = new(new(), new());
            //Create a connection to the SQL Server;
            try
            {
                await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadVictims()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                result = await WorkflowReferenceDataBuilders.BuildVictimsAsync(MyDataReader, cancellationToken: cancellationToken).ConfigureAwait(false);

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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER " + e);

            }
            cancellationToken.ThrowIfCancellationRequested();
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(victimData: result);
                else
                {
                    victims = result.Victims;
                    victimTypeDict = result.VictimTypeDict;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        // Historical readPNSettings remains disabled (April 2021); no operation is prepared.
        /// <summary>
        /// The readMEZVictims.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> readMEZVictimsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;

            Dictionary<string, HashSet<string>> result = new();
            //Create a connection to the SQL Server;
            try
            {
                await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadMEZVictims()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                result = await WorkflowReferenceDataBuilders.BuildMezVictimsAsync(MyDataReader, cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE MEZ VICTIMS IN PARSER ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE MEZ VICTIMS IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE MEZ VICTIMS IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO MEZ VICTIMS IN PARSER " + e);

            }
            cancellationToken.ThrowIfCancellationRequested();
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(mezVictims: result);
                else MEZVictims = result;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// The readMEZVictims.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> readAttachedVictimZonesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;

            Dictionary<string, Dictionary<string, HashSet<string>>> result = new();

            //Create a connection to the SQL Server;
            try
            {
                await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadAttachedVictimZones()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                result = await WorkflowReferenceDataBuilders.BuildAttachedVictimZonesAsync(MyDataReader, cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO readAttachedVictimZonesIN PARSER ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO readAttachedVictimZones IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO readAttachedVictimZones IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO readAttachedVictimZones IN PARSER " + e);

            }
            cancellationToken.ThrowIfCancellationRequested();
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(attachedZones: result);
                else AttachedVictimZones = result;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// The readProfileItemsClear.
        /// </summary>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> readProfileItemsClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
            Dictionary<int, List<ProfileItemClear>> result = new();
            //Create a connection to the SQL Server;
            try
            {
                await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadProfileItemsClear()))
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                result = await WorkflowReferenceDataBuilders.BuildClearEventsAsync(MyDataReader, cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                success = false;
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RETRIEVE CLEARING EVENTS IN PARSER ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE CLEARING EVENTS IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE CLEARING EVENTS IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE CLEARING EVENTS IN PARSER " + e);

            }
            cancellationToken.ThrowIfCancellationRequested();
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
        private async Task<int> ReadCurrentActiveAlarmPointAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentAlarmPoint = 0;

            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadCurrentActiveAlarmPoint()))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                while (await MyDataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                log.LogError(exc, "ReadCurrentActiveAlarmPoint");
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogError(ex, "ReadCurrentActiveAlarmPoint");
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return currentAlarmPoint;
        }

        /// <summary>
        /// Clears Alarms From McApp Table.
        /// </summary>
        /// <param name="alarms">The alarms<see cref="String"/>.</param>
        /// <param name="oid">The oid<see cref="String"/>.</param>
        private async Task ClearMcAppAlarmAsync(String alarms, String oid, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareClearMcAppAlarm(alarms, oid)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                log.LogError(exc, "[" + platForm + "] " + "FAILED TO RUN MCAPP_CLEAR_ALARM IN PARSER ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CLEAR_ALARM IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }

                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogError(e, "[" + platForm + "] " + "FAILED TO RUN MCAPP_CLEAR_ALARM IN PARSER ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CLEAR_ALARM IN PARSER " + e);

                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Returns the email addresses of the client that was contacted as the result of an alarm.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <returns>.</returns>
        private async Task<string> getClientEmailAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StringBuilder sb = new StringBuilder();
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareGetClientEmail(a)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                while (await MyDataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Client Email IN PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Client Email IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Client Email IN PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Client Email IN PARSER " + e);
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return sb.ToString();
        }

        /// <summary>
        /// Returns the texting address client that was contacted as the result of an alarm.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <returns>.</returns>
        private async Task<string> getClientTextAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StringBuilder sb = new StringBuilder();
            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareGetClientText(a)))
            {
                try
                {
                    {
                        IWorkflowOperation cmd = MyConnection.Prepare();

                        int retries = 3;
                        while (retries > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await MyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                                await using DbDataReader MyDataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                                while (await MyDataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
                                        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        throw;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception exc)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Client Text IN PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Client Text IN PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogInformation("[" + platForm + "] " + "ERROR: Unable to Read Client Text IN PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Client Text IN PARSER " + e);
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
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
        public async Task PushNotificationToVictimAsync(ActiveAlarm a, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    cancellationToken.ThrowIfCancellationRequested();
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
                    var reminder = await NotificationServiceSetting.GetAppReminderSettingAsync(a.AlarmID, cancellationToken).ConfigureAwait(false);
                    var setting = await NotificationServiceSetting.GetNotificationServiceSettingAsync(victimID, a.AlarmID, cancellationToken).ConfigureAwait(false);

                    bool reminderCheck = await NotificationServiceSetting.CheckVictimReminderSettingAsync(victimID, reminder, cancellationToken).ConfigureAwait(false);

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
                await notificationService.PushNotificationAsync(notifications, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
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
