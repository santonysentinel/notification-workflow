
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveAlarmsParser.Service.NotificationService
{
    public static class NotificationServiceAccess
    {
        /// <summary>
        /// Defines the MT_SERVICE_SESSION_CACHE_TIME.
        /// </summary>
        private static int NOTIFICATION_SERVICE_SESSION_CACHE_TIME = 57;

        /// <summary>
        /// Defines the baseURL.
        /// </summary>
        private static string baseURL = string.Empty;

        /// <summary>
        /// Defines the mtServiceUsername.
        /// </summary>
        private static string notificationServiceUsername = string.Empty;

        /// <summary>
        /// Defines the mtServiceAccess.
        /// </summary>
        private static string notificationServiceAccess = string.Empty;

        /// <summary>
        /// Defines the sessionToken.
        /// </summary>
        private static string sessionToken = string.Empty;

        /// <summary>
        /// Defines the sessionExpiryTime.
        /// </summary>
        private static DateTime sessionExpiryTime = DateTime.MinValue;

        /// <summary>
        /// Defines the log.
        /// </summary>
        

        private static ILogger logger = NullLogger.Instance;
        private static bool initialized;

        /// <summary>
        /// Initializes configuration for the <see cref="NotificationServiceAccess"/> class.
        /// </summary>
        public static void Initialize(IConfiguration configuration, ILogger logger)
        {
            if (initialized)
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(logger);

            string? cacheTimeValue = configuration["notificationservice-session-cache-time"];
            int cacheTime = 57;
            if (cacheTimeValue != null && (!int.TryParse(cacheTimeValue, out cacheTime) || cacheTime <= 0))
            {
                throw new InvalidOperationException("notificationservice-session-cache-time must be a positive integer when supplied.");
            }

            string? username = configuration["notificationservice-login"];
            string? access = configuration["notificationservice-access"];
            string? authApi = configuration["AuthAPI"];

            if (string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException("notificationservice-login configuration is required to initialize NotificationServiceAccess.");
            }

            if (string.IsNullOrWhiteSpace(access))
            {
                throw new InvalidOperationException("notificationservice-access configuration is required to initialize NotificationServiceAccess.");
            }

            if (string.IsNullOrWhiteSpace(authApi))
            {
                throw new InvalidOperationException("AuthAPI configuration is required to initialize NotificationServiceAccess.");
            }

            NotificationServiceAccess.logger = logger;
            NOTIFICATION_SERVICE_SESSION_CACHE_TIME = cacheTime;
            notificationServiceUsername = username;
            notificationServiceAccess = access;
            baseURL = authApi;
            initialized = true;
        }

        /// <summary>
        /// The GetAuthorizationToken.
        /// </summary>
        /// <returns>The <see cref="string"/>.</returns>
        public static string GetAuthorizationToken()
        {
            if (!initialized)
            {
                throw new InvalidOperationException("NotificationServiceAccess.Initialize must be called before requesting an authorization token.");
            }

            string token = GetTokenFromCache();

            if (string.IsNullOrEmpty(token))
            {
                return AuthenticateService();
            }

            return token;
        }

        /// <summary>
        /// The AuthenticateService.
        /// </summary>
        /// <returns>The <see cref="string"/>.</returns>
        private static string AuthenticateService()
        {
            string token = string.Empty;
            string requestURL = string.Empty;
            string respData = string.Empty;
            try
            {
                requestURL = string.Format("{0}accounts/authenticate2", baseURL);
                logger.LogInformation(requestURL);
                var request = (HttpWebRequest)WebRequest.Create(requestURL);
                string body = GetAuthBody();
                request.Method = "POST";

                var data = Encoding.ASCII.GetBytes(body);

                request.ContentType = "application/json";
                request.ContentLength = data.Length;

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }

                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;

                var response = (HttpWebResponse)request.GetResponse();

                if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created)
                {
                    respData = new StreamReader(response.GetResponseStream()).ReadToEnd();
                    AuthServiceResponse eresponse = JsonConvert.DeserializeObject<AuthServiceResponse>(respData)
                        ?? throw new JsonSerializationException("Authentication response deserialized to null.");

                    token = eresponse.jwt;
                    SaveTokenForFutureAccess(token);
                }
            }
            catch (WebException ex)
            {
                logger.LogError(ex, "MTService Access : AuthenticateService");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MTService Access : AuthenticateService");
            }

            return token;
        }

        /// <summary>
        /// The SaveTokenForFutureAccess.
        /// </summary>
        /// <param name="token">The token<see cref="string"/>.</param>
        private static void SaveTokenForFutureAccess(string token)
        {
            if (!string.IsNullOrEmpty(token))
            {
                sessionToken = token;
                sessionExpiryTime = DateTime.Now.AddMinutes(NOTIFICATION_SERVICE_SESSION_CACHE_TIME);
            }
        }

        /// <summary>
        /// The GetTokenFromCache.
        /// </summary>
        /// <returns>The <see cref="string"/>.</returns>
        private static string GetTokenFromCache()
        {
            if (DateTime.Now > sessionExpiryTime)
            {
                sessionToken = string.Empty;
            }

            return sessionToken;
        }

        /// <summary>
        /// The GetAuthBody.
        /// </summary>
        /// <returns>The <see cref="string"/>.</returns>
        private static string GetAuthBody()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{");
            builder.Append("\"signInName\":\"" + notificationServiceUsername + "\",");
            builder.Append("\"password\":\"" + notificationServiceAccess + "\"");
            builder.Append("}");
            return builder.ToString();
        }
    }

    /// <summary>
    /// Defines the <see cref="AuthServiceResponse" />.
    /// </summary>
    public class AuthServiceResponse
    {
        /// <summary>
        /// Gets or sets a value indicating whether isSuccessful.
        /// </summary>
        public bool isSuccessful { get; set; }

        /// <summary>
        /// Gets or sets the jwt.
        /// </summary>
        public string jwt { get; set; } = string.Empty;
    }
}
