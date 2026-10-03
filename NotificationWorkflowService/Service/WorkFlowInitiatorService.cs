using ActiveAlarmsParser;
using NotificationWorkflowService.Parser;
using NotificationWorkflowService.Repository;
using System;
using System.Collections.Generic;
using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace NotificationWorkflowService.Service
{
    public class WorkFlowInitiatorService
    {
        /// <summary>
        /// Defines the aggressiveIterations.
        /// </summary>
        private static int aggressiveIterations = 0;

        /// <summary>
        /// Defines the log.
        /// </summary>
        private readonly ILogger<WorkFlowInitiatorService> log;

        private readonly IConfiguration configuration;
        private readonly ILoggerFactory loggerFactory;
        private readonly Func<WorkFlowCommon> parserFactory;
        private readonly IRepository repository;

        public WorkFlowInitiatorService(ILogger<WorkFlowInitiatorService> logger, IConfiguration configuration, ILoggerFactory loggerFactory, Func<WorkFlowCommon> parserFactory)
            : this(logger, configuration, loggerFactory, parserFactory, new NotificationWorkflowService.Repository.Repository(configuration))
        {
        }

        public WorkFlowInitiatorService(ILogger<WorkFlowInitiatorService> logger, IConfiguration configuration, ILoggerFactory loggerFactory, Func<WorkFlowCommon> parserFactory, IRepository repository)
        {
            this.log = logger;
            this.configuration = configuration;
            this.loggerFactory = loggerFactory;
            this.parserFactory = parserFactory;
            this.repository = repository;
        }


        /// <summary>
        /// The thread for the normal single step parser.
        /// </summary>
        /// <param name="platform">The platform name used for parser configuration.</param>
        /// <param name="cancellationToken">Signals host shutdown.</param>
        public async Task startParse(string platform, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTime lastParserResetTime = DateTime.UtcNow;
            WorkFlowCommon p = parserFactory();

            while (!await p.setUpParserAsync(platform, cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            }


            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pointsBehind = await ReadCurrentActiveAlarmPointAsync(cancellationToken).ConfigureAwait(false) - await readLastSuccessfulProcessAsync(Convert.ToInt32(configuration[platform + "ParserID"]), cancellationToken).ConfigureAwait(false);

                int sleepTime = returnWaitTime(pointsBehind);

                if (DateTime.UtcNow > lastParserResetTime.AddMinutes(Convert.ToInt32(configuration["ParserRefreshTime"])))
                {

                    while (!await p.setUpParserAsync(platform, cancellationToken).ConfigureAwait(false))
                    {
                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                    }

                    lastParserResetTime = DateTime.UtcNow;
                    Console.Title = "Active Alarms Parser: " + DateTime.UtcNow.ToString("D");
                }

                if (await p.readPointsAsync(cancellationToken).ConfigureAwait(false))
                {
                    await p.parseAlarmsAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                }

                if (processActiveAlarmsHoldQueue(pointsBehind, sleepTime))
                {
                    await Task.Delay(sleepTime, cancellationToken).ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// The thread for the multi step parser.
        /// </summary>
        /// <param name="platform">The platform name used for parser configuration.</param>
        /// <param name="cancellationToken">Signals host shutdown.</param>
        public async Task startParseSteps(string platform, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTime lastParserResetTime = DateTime.UtcNow;
            WorkFlowSteps p = new WorkFlowSteps(loggerFactory.CreateLogger<WorkFlowSteps>(), configuration, repository);


            while (!await p.setUpParserAsync(platform, cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            }


            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > lastParserResetTime.AddMinutes(Convert.ToInt32(configuration["StepParserRefreshTime"])))
                {

                    while (!await p.setUpParserAsync(platform, cancellationToken).ConfigureAwait(false))
                    {
                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                    }

                    lastParserResetTime = DateTime.UtcNow;
                }

                if (await p.getExpiredAlarmsAsync(cancellationToken).ConfigureAwait(false))
                {
                    await p.parseAlarmsAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                }


                await Task.Delay(Convert.ToInt32(configuration["StepParserSleepTime"]), cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Returns the current ActiveAlarms point in the TRPT table.
        /// </summary>
        /// <returns>.</returns>
        async Task<int> ReadCurrentActiveAlarmPointAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentAlarmPoint = 0;

            _ = configuration.GetConnectionString("connstr")
                ?? throw new InvalidOperationException("Connection string 'connstr' is not configured.");

            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadCurrentActiveAlarmPoint(reloadConnectionString: true)))
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
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
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
                catch (Exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return currentAlarmPoint;
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
            _ = configuration.GetConnectionString("connstr")
                ?? throw new InvalidOperationException("Connection string 'connstr' is not configured.");

            await using (var MyConnection = new DeferredWorkflowOperation(() => repository.PrepareReadLastSuccessfulProcess(parserID, reloadConnectionString: true)))
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
                                    SystemID = (int)MyDataReader["StatusID"];
                                }

                                break;
                            }
                            catch (SqlException ex)
                            {
                                log.LogError(ex, "SQLException on readLastSuccessfulProcess in Parser ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readLastSuccessfulProcess in Parser " + ex);
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
                                log.LogError(exc, "ERROR: Unable to Read Last successful processed ActiveAlarms point ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Last successful processed ActiveAlarms point " + exc);
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
                    log.LogError(e, "ERROR: Unable to Read Last successful processed ActiveAlarms point ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Last successful processed ActiveAlarms point " + e);
                    await MyConnection.CloseAsync().ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return SystemID;
        }

        /// <summary>
        /// The returnWaitTime.
        /// </summary>
        /// <param name="pointsBehind">The pointsBehind<see cref="int"/>.</param>
        /// <returns>The <see cref="int"/>.</returns>
        int returnWaitTime(int pointsBehind)
        {
            int toReturn = 3;

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
        /// Returns a status boolean on whether the alarms thread should sleep or not when processing points.
        /// </summary>
        /// <param name="pointsBehind">.</param>
        /// <param name="waitTime">.</param>
        /// <returns>.</returns>
        bool processActiveAlarmsHoldQueue(int pointsBehind, int waitTime)
        {

            Boolean sleep = false;
            int x = 60 * Convert.ToInt32(configuration["NumberOfProcessPoints"]);
            int y = (waitTime / 1000) + 3;

            if (pointsBehind >= x / y)
            {
                sleep = false;
                aggressiveIterations++;
            }
            else
            {
                sleep = true;
            }

            if (sleep == true && pointsBehind != 0 && aggressiveIterations <= Convert.ToInt32(configuration["NumberOfAggressiveIterations"]))
            {
                sleep = false;
            }

            if (aggressiveIterations >= Convert.ToInt32(configuration["NumberOfAggressiveIterations"]) && sleep == false)
            {
                aggressiveIterations = 0;
                sleep = true;
            }
            return sleep;
        }
    }
}
