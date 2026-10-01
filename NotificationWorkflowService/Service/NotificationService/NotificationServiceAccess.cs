
using Newtonsoft.Json;
using System;
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
        private static IHttpClientFactory? httpClientFactory;
        private static readonly SemaphoreSlim tokenGate = new(1, 1);

        /// <summary>
        /// Initializes configuration for the <see cref="NotificationServiceAccess"/> class.
        /// </summary>
        public static void Initialize(IConfiguration configuration, ILogger logger)
            => Initialize(configuration, logger, NotificationHttp.DefaultFactory);

        public static void Initialize(IConfiguration configuration, ILogger logger, IHttpClientFactory httpClientFactory)
        {
            if (initialized)
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(httpClientFactory);

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
            NotificationHttp.Endpoint(authApi, "accounts/authenticate2");
            NotificationServiceAccess.httpClientFactory = httpClientFactory;
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
            => GetAuthorizationTokenAsync().GetAwaiter().GetResult();

        public static async Task<string> GetAuthorizationTokenAsync(CancellationToken cancellationToken = default)
        {
            if (!initialized)
            {
                throw new InvalidOperationException("NotificationServiceAccess.Initialize must be called before requesting an authorization token.");
            }

            await tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string token = GetTokenFromCache();
                return string.IsNullOrEmpty(token)
                    ? await AuthenticateServiceAsync(cancellationToken).ConfigureAwait(false)
                    : token;
            }
            finally
            {
                tokenGate.Release();
            }
        }

        /// <summary>
        /// The AuthenticateService.
        /// </summary>
        /// <returns>The <see cref="string"/>.</returns>
        private static async Task<string> AuthenticateServiceAsync(CancellationToken cancellationToken)
        {
            try
            {
                string response = await NotificationHttp.SendAsync(httpClientFactory!, NotificationHttp.AuthenticationClient,
                    NotificationHttp.Endpoint(baseURL, "accounts/authenticate2"), GetAuthBody(), null, cancellationToken).ConfigureAwait(false);
                var authentication = JsonConvert.DeserializeObject<AuthServiceResponse>(response)
                    ?? throw new JsonSerializationException("Authentication response deserialized to null.");
                if (string.IsNullOrWhiteSpace(authentication.jwt))
                    throw new JsonSerializationException("Authentication response did not contain a token.");
                SaveTokenForFutureAccess(authentication.jwt);
                return authentication.jwt;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MTService Access : AuthenticateService");
            }

            return string.Empty;
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
                sessionExpiryTime = DateTime.UtcNow.AddMinutes(NOTIFICATION_SERVICE_SESSION_CACHE_TIME);
            }
        }

        /// <summary>
        /// The GetTokenFromCache.
        /// </summary>
        /// <returns>The <see cref="string"/>.</returns>
        private static string GetTokenFromCache()
        {
            if (DateTime.UtcNow > sessionExpiryTime)
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
            return SerializeCredentials(notificationServiceUsername, notificationServiceAccess);
        }

        internal static string SerializeCredentials(string username, string password)
            => JsonConvert.SerializeObject(new { signInName = username, password });
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
