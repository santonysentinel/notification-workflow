using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ActiveAlarmsParser.Service.NotificationService;

internal static class NotificationHttp
{
    internal const string NotificationClient = "NotificationDelivery";
    internal const string AuthenticationClient = "NotificationAuthentication";
    // Compatibility for callers using the original two-argument constructor/Initialize.
    // The provider lives for the process lifetime; handlers are still factory-managed.
    private static readonly Lazy<ServiceProvider> fallbackProvider = new(() =>
    {
        var services = new ServiceCollection();
        RegisterClients(services);
        return services.BuildServiceProvider();
    });

    internal static IHttpClientFactory DefaultFactory => fallbackProvider.Value.GetRequiredService<IHttpClientFactory>();

    internal static void RegisterClients(IServiceCollection services)
    {
        services.AddHttpClient(NotificationClient, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(AuthenticationClient, client => client.Timeout = TimeSpan.FromSeconds(30));
    }

    internal static Uri Endpoint(string baseUrl, string relativePath)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("The API base URL must be an absolute HTTP(S) URL without credentials, query, or fragment.", nameof(baseUrl));
        }
        var directory = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        return new Uri(directory, relativePath);
    }

    internal static async Task<string> SendAsync(IHttpClientFactory factory, string clientName,
        Uri endpoint, string json, string? bearerToken, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient(clientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (bearerToken != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        // ResponseContentRead keeps body buffering covered by HttpClient's timeout.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        bool accepted = response.StatusCode == HttpStatusCode.Created ||
            (clientName == AuthenticationClient && response.StatusCode == HttpStatusCode.OK);
        if (!accepted)
        {
            var retryAfter = response.Headers.RetryAfter;
            TimeSpan? wait = retryAfter?.Delta;
            if (wait == null && retryAfter?.Date is { } date)
                wait = date - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            throw new NotificationHttpException(response.StatusCode, wait);
        }
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}