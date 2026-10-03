namespace ActiveAlarmsParser
{

    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using Microsoft.Data.SqlClient;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using NotificationWorkflowService.Entity;
    using NotificationWorkflowService.Repository;
    using NotificationWorkflowService.Parser;
    using NotificationWorkflowService.Parser.ReferenceData;
    using NotificationWorkflowService.Parser.Actions;
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
        public async Task<bool> setUpParserAsync(string platform, CancellationToken cancellationToken = default)
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

            cancellationToken.ThrowIfCancellationRequested();
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
                                        throw ex;

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
                                    throw exc;
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
                                        throw ex;
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
                                    throw exc;
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
                                        throw ex;
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
                                    throw exc;
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
                                        throw ex;
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
                                    throw exc;
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
        /// Gets the active alarms that have expired from the CurrentNotificationState Table and processes their next state.
        /// </summary>
        /// <returns>.</returns>
        public async Task<bool> getExpiredAlarmsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log.LogInformation(String.Format("StepParser::getExpiredAlarms::DBCall"));
            bool success = true;
            activeAlarms = new List<ActiveAlarm>();

            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareGetExpiredAlarms(DateTime.UtcNow)))
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
                                await getPriorityAndEmailAsync(activeAlarms, cancellationToken).ConfigureAwait(false);
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
                                        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
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
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN REMOVE_EXPIRED_NOTIFICATION_ALARMS IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN REMOVE_EXPIRED_NOTIFICATION_ALARMS IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw exc;
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
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN REMOVE_EXPIRED_NOTIFICATION_ALARMS IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN REMOVE_EXPIRED_NOTIFICATION_ALARMS IN STEP PARSER " + e);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
        }

        /// <summary>
        /// Sets the priority, emails, email join, and State Numbers for each of the active alarms.
        /// </summary>
        /// <param name="activeAlarms">.</param>
        private async Task getPriorityAndEmailAsync(List<ActiveAlarm> activeAlarms, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<int> toRemove = new List<int>();
            log.LogInformation(String.Format("StepParser::getPriorityAndEmail::ActiveAlarmsCount::{0}", activeAlarms.Count));
            foreach (ActiveAlarm a in activeAlarms)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                                    await readRolesAsync(a, pi.RoleID, pi.RoleAction, cancellationToken).ConfigureAwait(false);
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
                cancellationToken.ThrowIfCancellationRequested();
                activeAlarms.RemoveAt(indice);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// The Main active loop of the step parser, loops through all the activeAlarms and performs the necessary actions.
        /// </summary>
        public async Task parseAlarmsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
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

                    WorkflowActionResult result = await WorkflowActionExecutor.ExecuteAsync(a,
                        new WorkflowActionContext(WorkflowActionMode.Step, platForm, log,
                            new ActionOperations(this), RoleActionMapping, roles, victims), cancellationToken).ConfigureAwait(false);
                    insert = result.Insert;
                    sb.Append(result.Summary);

                    await CreateAlarmAuditAsync(14, sb.ToString(), a.HistoryID, a.CurrentStateNo, cancellationToken).ConfigureAwait(false);

                    await insertIntoAlarmNotificationAsync(a.SystemID, a.CurrentStateNo, DateTime.UtcNow.AddMinutes(a.StateTime), PriorityMapping[a.Priority], cancellationToken).ConfigureAwait(false);

                    log.LogInformation(String.Format("StepParserAfterCheck::AlarmSystemID::{0}::CurrentStateNo::{1}::NextStateNo::{2}::ProcessNextStep::{3}::StateTime::{4}::CurrentLoopNumber::{5}::insert::{6}", a.SystemID, a.CurrentStateNo, a.NextStateNo, a.ProcessNextStep, a.StateTime, a.CurrentStateNo, insert.ToString()));
                    if (a.NextStateNo != -1 && insert)
                    {
                        await insertIntoCurrentAlarmNotificationAsync(a.SystemID, a.NextStateNo, DateTime.UtcNow.AddMinutes(a.StateTime), a.CurrentStateNo, a.CurrentLoopNumber, a.ProcessNextStep, cancellationToken).ConfigureAwait(false);
                    }
                    else if (insert)
                    {
                        await insertIntoCurrentAlarmNotificationAsync(a.SystemID, a.CurrentStateNo + 1, DateTime.UtcNow.AddMinutes(a.StateTime), a.CurrentStateNo, 0, 1, cancellationToken).ConfigureAwait(false);
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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    log.LogError(ex, "Step Parser Parse Error");
                }

            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private sealed class ActionOperations(WorkFlowSteps parser) : IWorkflowActionOperations
        {
            public Task SendNotificationsToOfficersInSameGroupAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => parser.SendNotificationsToOfficersInSameGroupAsync(a, cancellationToken);
            public Task<bool> PushAlertToMcAppAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => parser.PushAlertToMcAppAsync(a, cancellationToken);
            public Task<bool> AddToNotificationQueueAsync(ActiveAlarm a, int insertType, CancellationToken cancellationToken = default) => parser.AddToNotificationQueueAsync(a, insertType, cancellationToken);
            public async Task CreateAlarmAuditAsync(int type, string action, int historyID, int StepNo, CancellationToken cancellationToken = default) => await parser.CreateAlarmAuditAsync(type, action, historyID, StepNo, cancellationToken).ConfigureAwait(false);
            public async Task AddActiveAlarmActionToActivityAsync(int historyID, string email, int type, CancellationToken cancellationToken = default) => await parser.AddActiveAlarmActionToActivityAsync(historyID, email, type, cancellationToken).ConfigureAwait(false);
            public Task<bool> insertNotificationQueueVictimAsync(ActiveAlarm a, int insertType, string victimsEmails, bool isVictimNotification = false, CancellationToken cancellationToken = default) => parser.insertNotificationQueueVictimAsync(a, insertType, victimsEmails, isVictimNotification, cancellationToken);
            public Task<string> getInsertEmailsAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => parser.getInsertEmailsAsync(a, cancellationToken);
            public Task<string> getClientEmailAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => parser.getClientEmailAsync(a, cancellationToken);
            public Task<string> getClientTextAsync(ActiveAlarm a, CancellationToken cancellationToken = default) => parser.getClientTextAsync(a, cancellationToken);
        }

        /// <summary>
        /// Inserts a row into the Audit Alarms Table.
        /// </summary>
        /// <param name="type">.</param>
        /// <param name="action">.</param>
        /// <param name="historyID">.</param>
        /// <param name="StepNo">The StepNo<see cref="int"/>.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> CreateAlarmAuditAsync(int type, String action, int historyID, int StepNo, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
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
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN MCAPP_CREATEAUDIT IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CREATEAUDIT IN STEP PARSER " + exc);
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
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN MCAPP_CREATEAUDIT IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN MCAPP_CREATEAUDIT IN STEP PARSER " + e);
                }
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
        /// Inserts a Row Into The History Table.
        /// </summary>
        /// <param name="historyID">.</param>
        /// <param name="email">The email<see cref="String"/>.</param>
        /// <param name="type">.</param>
        /// <returns>The <see cref="bool"/>.</returns>
        private async Task<bool> AddActiveAlarmActionToActivityAsync(int historyID, string email, int type, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
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
                                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
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
                                success = false;
                                log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_HISTORY IN STEP PARSER " + exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_HISTORY IN STEP PARSER " + exc);
                                if (retries > 0)
                                {
                                    retries--;
                                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    throw exc;
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
                    log.LogInformation("[" + platForm + "] " + "FAILED TO RUN INSERT_INTO_HISTORY IN STEP PARSER " + e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN INSERT_INTO_HISTORY IN STEP PARSER " + e);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
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
        private async Task<bool> insertIntoAlarmNotificationAsync(int AlarmSystemID, int CurentStateNo, DateTime ExpiryTime, String Action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = true;
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
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
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
        /// Inserts The Active Alarm Into The Notification Queue.
        /// </summary>
        /// <param name="a">The a<see cref="ActiveAlarm"/>.</param>
        /// <param name="insertType">The insertType<see cref="int"/>.</param>
        /// <param name="victimsEmails">The victimsEmails<see cref="String"/>.</param>
        /// /// <param name="isVictimNotification">The isVictimNotification<see cref="bool"/>.</param>
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
            bool disposing = false;

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

                                result = await WorkflowReferenceDataBuilders.BuildVictimsAsync(MyDataReader, step: true, cancellationToken: cancellationToken).ConfigureAwait(false);

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
                    disposing = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) when (disposing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER " + e);
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                success = false;
                log.LogError(e, "[" + platForm + "] " + "FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RETRIEVE ACTIVE ALARM VICTIMS IN PARSER " + e);
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (success)
            {
                if (pendingReferenceData != null) StageReferenceData(victimData: result);
                else victims = result.Victims;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return success;
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
    }
}
