using ActiveAlarmsParser;
using NotificationWorkflowService.Parser;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
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

        /// <summary>
        /// Defines the READ_CURRENT_ACTIVE_ALARMPOINT.
        /// </summary>
        private static readonly String READ_CURRENT_ACTIVE_ALARMPOINT = "ActiveAlarms_ReadLastAlarmPoint";

        private readonly IConfiguration configuration;
        private readonly ILoggerFactory loggerFactory;
        private readonly WorkFlowCommon parser;

        public WorkFlowInitiatorService(ILogger<WorkFlowInitiatorService> logger, IConfiguration configuration, ILoggerFactory loggerFactory, WorkFlowCommon parser)
        {
            this.log = logger;
            this.configuration = configuration;
            this.loggerFactory = loggerFactory;
            this.parser = parser;
        }


        /// <summary>
        /// The thread for the normal single step parser.
        /// </summary>
        /// <param name="platform">The platform name used for parser configuration.</param>
        /// <param name="cancellationToken">Signals host shutdown.</param>
        public async Task startParse(string platform, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTime lastParserResetTime = DateTime.UtcNow;
            WorkFlowCommon p = parser;

            while (!p.setUpParser(platform))
            {
                await Task.Delay(2000, cancellationToken);
            }


            while (!cancellationToken.IsCancellationRequested)
            {
                int pointsBehind = ReadCurrentActiveAlarmPoint() - readLastSuccessfulProcess(Convert.ToInt32(configuration[platform + "ParserID"]));

                int sleepTime = returnWaitTime(pointsBehind);

                if (DateTime.UtcNow > lastParserResetTime.AddMinutes(Convert.ToInt32(configuration["ParserRefreshTime"])))
                {

                    while (!p.setUpParser(platform))
                    {
                        await Task.Delay(2000, cancellationToken);
                    }

                    lastParserResetTime = DateTime.UtcNow;
                    Console.Title = "Active Alarms Parser: " + DateTime.UtcNow.ToString("D");
                }

                if (p.readPoints())
                {
                    p.parseAlarms();
                }
                else
                {
                    await Task.Delay(3000, cancellationToken);
                }

                if (processActiveAlarmsHoldQueue(pointsBehind, sleepTime))
                {
                    await Task.Delay(sleepTime, cancellationToken);
                }
            }
        }

        /// <summary>
        /// The thread for the multi step parser.
        /// </summary>
        /// <param name="platform">The platform name used for parser configuration.</param>
        void startParseSteps(string platform)
        {
            DateTime lastParserResetTime = DateTime.UtcNow;
            WorkFlowSteps p = new WorkFlowSteps(loggerFactory.CreateLogger<WorkFlowSteps>(), configuration);


            while (!p.setUpParser(platform))
            {
                Thread.Sleep(2000);
            }


            while (true)
            {
                if (DateTime.UtcNow > lastParserResetTime.AddMinutes(Convert.ToInt32(configuration["StepParserRefreshTime"])))
                {

                    while (!p.setUpParser(platform))
                    {
                        Thread.Sleep(2000);
                    }

                    lastParserResetTime = DateTime.UtcNow;
                }

                if (p.getExpiredAlarms())
                {
                    p.parseAlarms();
                }
                else
                {
                    Thread.Sleep(3000);
                }


                Thread.Sleep(Convert.ToInt32(configuration["StepParserSleepTime"]));
            }
        }

        /// <summary>
        /// Returns the current ActiveAlarms point in the TRPT table.
        /// </summary>
        /// <returns>.</returns>
        int ReadCurrentActiveAlarmPoint()
        {
            int currentAlarmPoint = 0;

            string ConnectionString = configuration.GetConnectionString("connstr")
                ?? throw new InvalidOperationException("Connection string 'connstr' is not configured.");

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
                            catch (Exception)
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
                    }
                }
                catch (Exception)
                {

                }
            }
            return currentAlarmPoint;
        }

        /// <summary>
        /// Reads the Last Processed Point SystemID from the ParserActivity table, used when the program is restarted or when reading in a set of points.
        /// </summary>
        /// <param name="parserID">The parserID<see cref="int"/>.</param>
        /// <returns>.</returns>
        private int readLastSuccessfulProcess(int parserID)
        {
            int SystemID = 0;
            string ConnectionString =  configuration.GetConnectionString("connstr")
                ?? throw new InvalidOperationException("Connection string 'connstr' is not configured.");
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
                        int retries = 3;
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
                                log.LogError(exc, "ERROR: Unable to Read Last successful processed ActiveAlarms point ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Last successful processed ActiveAlarms point " + exc);
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
                    log.LogError(e, "ERROR: Unable to Read Last successful processed ActiveAlarms point ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Last successful processed ActiveAlarms point " + e);
                    MyConnection.Close();
                }
            }

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
