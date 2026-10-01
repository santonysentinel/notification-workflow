
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Data;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using static ActiveAlarmsParser.Service.NotificationService.NotificationServiceData;

namespace ActiveAlarmsParser.Service.NotificationService
{
    public class NotificationService
    {
        /// <summary>
        /// Defines the logger.
        /// </summary>
        private readonly ILogger<NotificationService> logger;

        /// <summary>
        /// Defines the baseURL.
        /// </summary>
        private string baseURL;
        private String ErrorMessage = "Function:{0}, URL:{1}, PostData:{2}, Response:{3}";
        private String LogCallMessage = "RoutingCall:::{0}:: {1} :: {2} :: {3} :: {4} :: {5}"; // [Function-Request Type-Request URL-success/failure-Message-POST Data]
        private NotificationArray notifications = new NotificationArray();
        private string AlarmsDatabase;

        private bool IsSuccessStatusCode(HttpStatusCode code)
        {
            return (code == HttpStatusCode.Created);
        }

        private readonly IConfiguration configuration;
        public NotificationService(ILogger<NotificationService> logger, IConfiguration configuration)
        {
            this.logger = logger;
            this.configuration = configuration;
            AlarmsDatabase = configuration["ClientDatabase"] ?? configuration.GetConnectionString("ClientDatabase")
                ?? throw new InvalidOperationException("ClientDatabase is not configured.");
            baseURL = configuration["Notifications_Service_API_URL"]
                ?? throw new InvalidOperationException("Notifications_Service_API_URL is not configured.");
            NotificationServiceAccess.Initialize(configuration, logger);
            NotificationServiceSetting.Initialize(configuration, logger);
        }

        public void PushNotification(ArrayList nlist)
        {
            if (nlist.Count > 0)
            {
                notifications.BulkAdd(nlist);
            }

            if(notifications.Count() > 0)
            {
                bool success = PostNotification();
                for (int i = 0; i < 5; i++)
                {
                    if (success)
                    {
                        break;
                    }
                    success = PostNotification();
                }

                //Clear after each active alarm - retry to post 5 more time
                notifications.Clear();
            }
        }

        private bool HandlePostNotificationResponse(NotificationServiceResponse response)
        {
            //If all notification was sent successful, return true at the end, else return false to retry the ones was failed
            bool result = true;

            try
            {
                for (int i = 0; i < response.data.Count; i++)
                {
                    if (response.data[i].isSuccessful == false)
                    {
                        //If failed to send, keep the notification, to retry, and return false
                        //string key = response.data[i].oid + "#" + response.data[i].victimid + "#" + response.data[i].type + "#" + response.data[i].activityid;
                        //notifications.Retried(key);
                        logger.LogInformation(String.Format("Failed to send notification {0} from offender {1} to victim {2} - {3}", response.data[i].activityid, response.data[i].oid, response.data[i].victimid, response.data[i].type));
                        result = false;
                    }
                    else
                    {
                        //If succecced to send, remove the notification
                        string key = response.data[i].oid + "#" + response.data[i].victimid + "#" + response.data[i].type + "#" + response.data[i].activityid;
                        notifications.RemoveWithKey(key);
                        logger.LogInformation(String.Format("Success to send notification {0} from offender {1} to victim {2} - {3}", response.data[i].activityid, response.data[i].oid, response.data[i].victimid, response.data[i].type));

                        if(response.data[i].type == "push")
                        {
                            string? historyid = getHistoryIDFromActivityID(response.data[i].activityid);
                            if (!string.IsNullOrEmpty(historyid))
                            {
                                string note = "Push Notification ### sent to the Victim APP";
                                AddActiveAlarmActionToActivity(historyid, response.data[i].victimid, note, 2);
                            }
                        }
                        else if (response.data[i].type == "reminder")
                        {
                            string? historyid = getHistoryIDFromActivityID(response.data[i].activityid);
                            if (!string.IsNullOrEmpty(historyid))
                            {
                                string note = "Reminder Notification ### sent to the Victim APP";
                                AddActiveAlarmActionToActivity(historyid, response.data[i].victimid, note, 2);
                            }
                        }
                    }
                }

            }
            catch (Exception e )
            {
                logger.LogError(e, "HandlePostNotificationResponse Error");
            }


            return result;
        }

        public bool PostNotification()
        {
            bool Success = false;
            String requestURL = String.Empty;
            String postData = "";
            String respData = "";
            String Message = String.Empty;
            try
            {
                requestURL = String.Format("{0}notification", baseURL);

                //logger.LogInfo(requestURL);
                var request = (HttpWebRequest)WebRequest.Create(requestURL);

                postData = notifications.ToJSON();
                //logger.LogInfo(postData);
                var data = Encoding.ASCII.GetBytes(postData);
                string bearerToken = NotificationServiceAccess.GetAuthorizationToken();

                request.Method = "POST";
                request.Headers.Add("Authorization", "Bearer " + bearerToken);
                request.ContentType = "application/json";
                request.ContentLength = data.Length;

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

                var response = (HttpWebResponse)request.GetResponse();

                if (IsSuccessStatusCode(response.StatusCode))
                {
                    respData = new StreamReader(response.GetResponseStream()).ReadToEnd();
                    NotificationServiceResponse eresponse = JsonConvert.DeserializeObject<NotificationServiceResponse>(respData)
                        ?? throw new JsonSerializationException("The notification service returned a null response.");

                    Success = HandlePostNotificationResponse(eresponse);
                }
            }
            catch (WebException ex)
            {
                logger.LogError(ex, "{ErrorMessage}", String.Format(ErrorMessage, "PostNotification", requestURL, postData, respData));
                Message = ex.Message;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{ErrorMessage}", String.Format(ErrorMessage, "PostNotification", requestURL, postData, respData));
                Message = ex.Message;
            }

            //logger.LogInformation(String.Format(LogCallMessage, "PostNotification:", "POST", requestURL, Success, Message, postData));
            return Success;
        }

        private void AddActiveAlarmActionToActivity(string historyID, string victimID, String note, int type)
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
                        cmd.CommandText = "ActiveAlarms_InsertIntoHistory";
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Connection = MyConnection;

                        cmd.Parameters.Add("@HistoryID", SqlDbType.Int);
                        cmd.Parameters.Add("@emails", SqlDbType.VarChar, 1024);
                        cmd.Parameters.Add("@type", SqlDbType.Int);
                        cmd.Parameters.Add("@victimid", SqlDbType.VarChar, 30);

                        cmd.Parameters["@HistoryID"].Value = historyID;
                        cmd.Parameters["@emails"].Value = note;
                        cmd.Parameters["@type"].Value = type;
                        cmd.Parameters["@victimid"].Value = victimID;

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
                                logger.LogError(ex, "SQLException on ActiveAlarms_InsertIntoHistory in Parser ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " SQLException on ActiveAlarms_InsertIntoHistory in Parser " + ex);
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
                                logger.LogError(exc, "FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER ");
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER " + exc);
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
                    logger.LogError(e, "FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER ");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine( DateTime.UtcNow.ToString("HH:mm:ss") + " FAILED TO RUN AddActiveAlarmActionToActivity IN PARSER " + e);

                }
            }
        }

        private string? getHistoryIDFromActivityID(string activityID)
        {
            string[] strs = activityID.Split('-');
            if(strs.Length > 1)
            {
                return strs[0];
            }
            else
            {
                return null;
            }
            
        }
    }
}
