
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActiveAlarmsParser.Service.NotificationService
{
    public static class NotificationServiceAccess
    {
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

        private static ILogger logger = NullLogger.Instance;
        private static bool initialized;
        private static IHttpClientFactory? httpClientFactory;
        private static readonly object initializationGate = new();
        private static NotificationTokenCache tokenCache = new(TimeSpan.FromMinutes(57));

        /// <summary>
        /// Initializes configuration for the <see cref="NotificationServiceAccess"/> class.
        /// </summary>
        public static void Initialize(IConfiguration configuration, ILogger logger)
            => Initialize(configuration, logger, NotificationHttp.DefaultFactory);

        public static void Initialize(IConfiguration configuration, ILogger logger, IHttpClientFactory httpClientFactory)
        {
            lock (initializationGate)
            {
                InitializeCore(configuration, logger, httpClientFactory);
            }
        }

        private static void InitializeCore(IConfiguration configuration, ILogger logger, IHttpClientFactory httpClientFactory)
        {
            if (Volatile.Read(ref initialized)) return;

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
            tokenCache = new NotificationTokenCache(TimeSpan.FromMinutes(cacheTime));
            notificationServiceUsername = username;
            notificationServiceAccess = access;
            baseURL = authApi;
            Volatile.Write(ref initialized, true);
        }

        /// <summary>
        /// The GetAuthorizationToken.
        /// </summary>
        /// <returns>The <see cref="string"/>.</returns>
        public static string GetAuthorizationToken()
            => GetAuthorizationTokenAsync().GetAwaiter().GetResult();

        public static async Task<string> GetAuthorizationTokenAsync(CancellationToken cancellationToken = default)
        {
            if (!Volatile.Read(ref initialized))
            {
                throw new InvalidOperationException("NotificationServiceAccess.Initialize must be called before requesting an authorization token.");
            }

            return await tokenCache.GetAsync(AuthenticateServiceAsync, cancellationToken).ConfigureAwait(false);
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
                var authentication = AuthServiceResponse.ParseResponse(response);
                return authentication.jwt;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                NotificationDiagnostics.Failure(logger, "Authentication", ex);
            }

            return string.Empty;
        }

        internal static Task InvalidateTokenAsync(string rejectedToken, CancellationToken cancellationToken)
            => tokenCache.InvalidateAsync(rejectedToken, cancellationToken);

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

        internal static AuthServiceResponse ParseResponse(string json)
        {
            const string invalid = "Authentication response must confirm success and contain a nonempty token.";
            try
            {
                if (Newtonsoft.Json.Linq.JToken.Parse(json, new Newtonsoft.Json.Linq.JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = Newtonsoft.Json.Linq.DuplicatePropertyNameHandling.Error
                }) is not Newtonsoft.Json.Linq.JObject response ||
                    response["isSuccessful"]?.Type != Newtonsoft.Json.Linq.JTokenType.Boolean ||
                    response["isSuccessful"]!.Value<bool>() != true ||
                    response["jwt"]?.Type != Newtonsoft.Json.Linq.JTokenType.String ||
                    string.IsNullOrWhiteSpace(response["jwt"]!.Value<string>()))
                    throw new JsonSerializationException(invalid);
                return new AuthServiceResponse { isSuccessful = true, jwt = response["jwt"]!.Value<string>()! };
            }
            catch (JsonException) { throw new JsonSerializationException(invalid); }
            catch (ArgumentException) { throw new JsonSerializationException(invalid); }
        }
    }
}
