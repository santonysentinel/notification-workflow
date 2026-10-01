
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Data;
using Microsoft.Data.SqlClient;
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
        private NotificationArray notifications = new NotificationArray();
        private string AlarmsDatabase;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly bool idempotencyConfirmed;
        private DateTimeOffset retryNotBefore;

        /// <summary>Pending delivery is paused after a permanent or ambiguous failure.</summary>
        public bool RequiresDeliveryReview { get; private set; }

        /// <summary>
        /// Explicitly allow pending delivery after checking the remote outcome/correcting a rejection.
        /// This can duplicate notifications if the remote outcome has not been reconciled.
        /// </summary>
        public void ResumePendingDeliveryAfterReview() => RequiresDeliveryReview = false;

        private readonly IConfiguration configuration;
        public NotificationService(ILogger<NotificationService> logger, IConfiguration configuration)
            : this(logger, configuration, NotificationHttp.DefaultFactory)
        {
        }

        public NotificationService(ILogger<NotificationService> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            this.httpClientFactory = httpClientFactory;
            this.logger = logger;
            this.configuration = configuration;
            idempotencyConfirmed = configuration.GetValue<bool>("NotificationDelivery:IdempotencyConfirmed");
            AlarmsDatabase = configuration["ClientDatabase"] ?? configuration.GetConnectionString("ClientDatabase")
                ?? throw new InvalidOperationException("ClientDatabase is not configured.");
            baseURL = configuration["Notifications_Service_API_URL"]
                ?? throw new InvalidOperationException("Notifications_Service_API_URL is not configured.");
            NotificationHttp.Endpoint(baseURL, "notification");
            NotificationServiceAccess.Initialize(configuration, logger, httpClientFactory);
            NotificationServiceSetting.Initialize(configuration, logger);
        }

        public void PushNotification(ArrayList nlist)
            => PushNotificationAsync(nlist).GetAwaiter().GetResult();

        public async Task PushNotificationAsync(ArrayList nlist, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nlist.Count > 0)
            {
                notifications.BulkAdd(nlist);
            }

            if(notifications.Count() > 0)
            {
                await PostNotificationAsync(cancellationToken).ConfigureAwait(false);

                if (notifications.Count() > 0)
                {
                    logger.LogWarning("Retaining {PendingCount} unacknowledged notifications; delivery review required: {RequiresReview}", notifications.Count(), RequiresDeliveryReview);
                }
            }
        }

        private bool HandlePostNotificationResponse(NotificationServiceResponse response, IReadOnlyDictionary<string, Notification> submitted)
        {
            IReadOnlyList<Notification> delivered;
            try
            {
                // Validate the entire response before changing the queue. Missing acknowledgements stay pending.
                delivered = notifications.ApplyAcknowledgements(response.data, submitted);
            }
            catch (Exception e)
            {
                NotificationDiagnostics.Failure(logger, "AcknowledgementValidation", e);
                return false;
            }

            // Confirmed deliveries have already been removed. History failures must not resend them.
            foreach (Notification notification in delivered)
            {
                logger.LogInformation("Notification delivery confirmed");
                try
                {
                    if (notification.type == "push" || notification.type == "reminder")
                    {
                        string? historyid = getHistoryIDFromActivityID(notification.activityid);
                        if (!string.IsNullOrEmpty(historyid))
                        {
                            string note = notification.type == "push"
                                ? "Push Notification ### sent to the Victim APP"
                                : "Reminder Notification ### sent to the Victim APP";
                            AddActiveAlarmActionToActivity(historyid, notification.victimid, note, 2);
                        }
                    }
                }
                catch (Exception e)
                {
                    NotificationDiagnostics.Failure(logger, "DeliveredNotificationHistory", e);
                }
            }

            return notifications.Count() == 0;
        }

        public bool PostNotification()
            => PostNotificationAsync().GetAwaiter().GetResult();

        public async Task<bool> PostNotificationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RequiresDeliveryReview || DateTimeOffset.UtcNow < retryNotBefore) return false;
            bool deliveryStarted = false;
            try
            {
                var endpoint = NotificationHttp.Endpoint(baseURL, "notification");

                var submitted = notifications.GetSnapshot();
                if (submitted.Count == 0)
                {
                    return true;
                }
                string bearerToken = string.Empty;
                string json = JsonConvert.SerializeObject(submitted.Values);
                return await NotificationDeliveryPolicy.ExecuteAsync(async token =>
                {
                    bearerToken = await NotificationServiceAccess.GetAuthorizationTokenAsync(token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(bearerToken))
                    {
                        logger.LogWarning("Notification posting skipped because authentication did not provide a token");
                        return false;
                    }
                    deliveryStarted = true;
                    string response = await NotificationHttp.SendAsync(httpClientFactory, NotificationHttp.NotificationClient,
                        endpoint, json, bearerToken, token).ConfigureAwait(false);
                    var acknowledgement = NotificationServiceResponse.ParseResponse(response);
                    bool success = HandlePostNotificationResponse(acknowledgement, submitted);
                    // Unknown/missing acknowledgements or application rejections need review,
                    // not blind retries (even if transport idempotency has been confirmed).
                    if (!success) RequiresDeliveryReview = true;
                    return success;
                }, token => NotificationServiceAccess.InvalidateTokenAsync(bearerToken, token),
                    idempotencyConfirmed, logger, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (deliveryStarted && !idempotencyConfirmed) RequiresDeliveryReview = true;
                throw;
            }
            catch (Exception ex)
            {
                bool throttled = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
                if (throttled || (idempotencyConfirmed && NotificationDeliveryPolicy.CanRetryWithIdempotency(ex)))
                {
                    TimeSpan cooldown = NotificationDeliveryPolicy.Backoff(NotificationDeliveryPolicy.MaxAttempts, Random.Shared.NextDouble());
                    if (ex is NotificationHttpException { RetryAfter: { } retryAfter } && retryAfter > cooldown)
                        cooldown = retryAfter;
                    var now = DateTimeOffset.UtcNow;
                    retryNotBefore = cooldown > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + cooldown;
                }
                if (deliveryStarted && !throttled && !(idempotencyConfirmed && NotificationDeliveryPolicy.CanRetryWithIdempotency(ex)))
                    RequiresDeliveryReview = true;
                NotificationDiagnostics.Failure(logger, "Delivery", ex);
                return false;
            }
        }

        private void AddActiveAlarmActionToActivity(string historyID, string victimID, String note, int type)
        {
            ExecuteHistoryWithRetry(() =>
            {
                // Each attempt owns its connection and command, including failed opens/executions.
                using var connection = new SqlConnection(AlarmsDatabase);
                using var command = new SqlCommand("ActiveAlarms_InsertIntoHistory", connection)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 30
                };
                command.Parameters.Add("@HistoryID", SqlDbType.Int).Value = historyID;
                command.Parameters.Add("@emails", SqlDbType.VarChar, 1024).Value = note;
                command.Parameters.Add("@type", SqlDbType.Int).Value = type;
                command.Parameters.Add("@victimid", SqlDbType.VarChar, 30).Value = victimID;
                connection.Open();
                command.ExecuteNonQuery();
            }, delay => Thread.Sleep(delay), logger);
        }

        internal static void ExecuteHistoryWithRetry(Action execute, Action<TimeSpan> delay, ILogger logger)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    execute();
                    return;
                }
                // A deadlock victim's transaction is rolled back. Timeouts/connection loss
                // may follow a committed insert, so don't replay those without idempotency.
                catch (SqlException exception) when (exception.Number == 1205 && attempt < maxAttempts)
                {
                    logger.LogWarning("History insert deadlocked (SQL 1205) on attempt {Attempt} of {MaxAttempts}; retrying", attempt, maxAttempts);
                    delay(TimeSpan.FromSeconds(attempt * 2));
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
