using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using NotificationWorkflowService.Parser;

namespace NotificationWorkflowService.Service
{
    internal class WorkFlowInitiatorService
    {
        /// <summary>
        /// Defines the aggressiveIterations.
        /// </summary>
        private static int aggressiveIterations = 0;

        /// <summary>
        /// Defines the log.
        /// </summary>
        private static readonly ILogger log = new SentinelLog(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Defines the READ_CURRENT_ACTIVE_ALARMPOINT.
        /// </summary>
        private static readonly String READ_CURRENT_ACTIVE_ALARMPOINT = "ActiveAlarms_ReadLastAlarmPoint";

        
        /// <summary>
        /// The thread for the normal single step parser.
        /// </summary>
        /// <param name="connectionString">The connectionString<see cref="Object"/>.</param>
        static void startParse(Object connectionString)
        {
            DateTime lastParserResetTime = DateTime.UtcNow;
            Parser p = new Parser(((ConnectionStringSettings)connectionString).ConnectionString.ToString());

            while (!p.setUpParser(((ConnectionStringSettings)connectionString).Name))
            {
                Thread.Sleep(2000);
            }


            while (true)
            {
                int pointsBehind = ReadCurrentActiveAlarmPoint(((ConnectionStringSettings)connectionString).ConnectionString.ToString()) - readLastSuccessfulProcess(Convert.ToInt32(ConfigurationManager.AppSettings[((ConnectionStringSettings)connectionString).Name + "ParserID"]), ((ConnectionStringSettings)connectionString).ConnectionString.ToString(), ((ConnectionStringSettings)connectionString).Name.ToString());

                int sleepTime = returnWaitTime(pointsBehind);

                if (DateTime.UtcNow > lastParserResetTime.AddMinutes(Convert.ToInt32(ConfigurationManager.AppSettings["ParserRefreshTime"])))
                {

                    while (!p.setUpParser(((ConnectionStringSettings)connectionString).Name))
                    {
                        Thread.Sleep(2000);
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
                    Thread.Sleep(3000);
                }

                if (processActiveAlarmsHoldQueue(pointsBehind, sleepTime))
                {
                    Thread.Sleep(sleepTime);
                }
            }
        }

        /// <summary>
        /// The thread for the multi step parser.
        /// </summary>
        /// <param name="connectionString">The connectionString<see cref="Object"/>.</param>
        static void startParseSteps(Object connectionString)
        {
            DateTime lastParserResetTime = DateTime.UtcNow;
            StepParser p = new StepParser(((ConnectionStringSettings)connectionString).ConnectionString.ToString());


            while (!p.setUpParser(((ConnectionStringSettings)connectionString).Name))
            {
                Thread.Sleep(2000);
            }


            while (true)
            {
                if (DateTime.UtcNow > lastParserResetTime.AddMinutes(Convert.ToInt32(ConfigurationManager.AppSettings["StepParserRefreshTime"])))
                {

                    while (!p.setUpParser(((ConnectionStringSettings)connectionString).Name))
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


                Thread.Sleep(Convert.ToInt32(ConfigurationManager.AppSettings["StepParserSleepTime"]));
            }
        }

        /// <summary>
        /// Returns the current ActiveAlarms point in the TRPT table.
        /// </summary>
        /// <param name="connectionString">The connectionString<see cref="String"/>.</param>
        /// <returns>.</returns>
        static int ReadCurrentActiveAlarmPoint(String connectionString)
        {
            int currentAlarmPoint = 0;

            string ConnectionString = connectionString;

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
                                        throw ex;
                                    }
                                }
                            }
                            catch (Exception exc)
                            {
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
        /// <param name="connectionString">The connectionString<see cref="String"/>.</param>
        /// <param name="platForm">The platForm<see cref="String"/>.</param>
        /// <returns>.</returns>
        private static int readLastSuccessfulProcess(int parserID, String connectionString, String platForm)
        {
            int SystemID = 0;
            string ConnectionString = connectionString;

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
                                log.LogError("[" + platForm + "] " + "SQLException on readLastSuccessfulProcess in Parser ", ex);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on readLastSuccessfulProcess in Parser " + ex);
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
                                log.LogError("[" + platForm + "] " + "ERROR: Unable to Read Last successful processed ActiveAlarms point ", exc);
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Last successful processed ActiveAlarms point " + exc);
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
                    log.LogError("[" + platForm + "] " + "ERROR: Unable to Read Last successful processed ActiveAlarms point ", e);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[" + platForm + "] " + DateTime.UtcNow.ToString("HH:mm:ss") + " ERROR: Unable to Read Last successful processed ActiveAlarms point " + e);
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
        static int returnWaitTime(int pointsBehind)
        {
            int toReturn = 3;

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
        /// Returns a status boolean on whether the alarms thread should sleep or not when processing points.
        /// </summary>
        /// <param name="pointsBehind">.</param>
        /// <param name="waitTime">.</param>
        /// <returns>.</returns>
        static bool processActiveAlarmsHoldQueue(int pointsBehind, int waitTime)
        {
            Boolean sleep = false;
            int x = 60 * Convert.ToInt32(ConfigurationManager.AppSettings["NumberOfProcessPoints"]);
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

            if (sleep == true && pointsBehind != 0 && aggressiveIterations <= Convert.ToInt32(ConfigurationManager.AppSettings["NumberOfAggressiveIterations"]))
            {
                sleep = false;
            }

            if (aggressiveIterations >= Convert.ToInt32(ConfigurationManager.AppSettings["NumberOfAggressiveIterations"]) && sleep == false)
            {
                aggressiveIterations = 0;
                sleep = true;
            }
            return sleep;
        }
    }
}
