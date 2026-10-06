using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;
using ActiveAlarmsParser.Service.NotificationService;

namespace NotificationWorkflowService.Tests;

public class NotificationHttpTests
{
    [Theory]
    [InlineData("https://example.test/api", "https://example.test/api/notification")]
    [InlineData("https://example.test/api/", "https://example.test/api/notification")]
    public void EndpointPreservesBasePath(string baseUrl, string expected)
        => Assert.Equal(expected, NotificationHttp.Endpoint(baseUrl, "notification").AbsoluteUri);

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://example.test/")]
    [InlineData("https://example.test/?query=1")]
    [InlineData("https://example.test/#fragment")]
    public void EndpointRejectsInvalidBaseUrls(string baseUrl)
        => Assert.Throws<ArgumentException>(() => NotificationHttp.Endpoint(baseUrl, "notification"));

    [Theory]
    [InlineData(NotificationHttp.AuthenticationClient, 200, true)]
    [InlineData(NotificationHttp.AuthenticationClient, 201, true)]
    [InlineData(NotificationHttp.AuthenticationClient, 202, false)]
    [InlineData(NotificationHttp.NotificationClient, 201, true)]
    [InlineData(NotificationHttp.NotificationClient, 200, false)]
    [InlineData(NotificationHttp.NotificationClient, 204, false)]
    [InlineData(NotificationHttp.NotificationClient, 401, false)]
    [InlineData(NotificationHttp.NotificationClient, 500, false)]
    public async Task StatusRulesAndDisposalArePreserved(string name, int status, bool accepted)
    {
        var content = new TrackingContent("response");
        HttpRequestMessage? sent = null;
        var factory = new FakeFactory((request, _) =>
        {
            sent = request;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content });
        });
        var operation = NotificationHttp.SendAsync(factory, name, new Uri("https://example.test/api/notification"), "{}", "token", default);
        if (accepted) Assert.Equal("response", await operation);
        else
        {
            var failure = await Assert.ThrowsAnyAsync<HttpRequestException>(() => operation);
            Assert.Equal((HttpStatusCode)status, failure.StatusCode);
        }
        Assert.Equal(name, factory.ClientName);
        Assert.True(content.Disposed);
        Assert.Equal("Bearer", sent!.Headers.Authorization!.Scheme);
        Assert.Equal("token", sent.Headers.Authorization.Parameter);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sent.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task CredentialsAreSerializedAndPostedAsUtf8WithoutBearerHeader()
    {
        const string username = "用户\"\\name";
        const string password = "påss\"\\\n密碼";
        var factory = new FakeFactory(async (request, cancellationToken) =>
        {
            Assert.Equal("https://example.test/auth/accounts/authenticate2", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("utf-8", request.Content.Headers.ContentType.CharSet);
            var body = JObject.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(username, body.Value<string>("signInName"));
            Assert.Equal(password, body.Value<string>("password"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"isSuccessful\":true,\"jwt\":\"test-token\"}")
            };
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AuthAPI"] = "https://example.test/auth",
            ["notificationservice-login"] = username,
            ["notificationservice-access"] = password
        }).Build();
        NotificationServiceAccess.Initialize(configuration, NullLogger.Instance, factory);
        Assert.Equal("test-token", await NotificationServiceAccess.GetAuthorizationTokenAsync());
        Assert.Equal("test-token", await NotificationServiceAccess.GetAuthorizationTokenAsync());
        Assert.Equal(1, factory.Calls);
        await NotificationServiceAccess.InvalidateTokenAsync("stale-token", default);
        Assert.Equal("test-token", await NotificationServiceAccess.GetAuthorizationTokenAsync());
        Assert.Equal(1, factory.Calls);
        await NotificationServiceAccess.InvalidateTokenAsync("test-token", default);
        Assert.Equal("test-token", await NotificationServiceAccess.GetAuthorizationTokenAsync());
        Assert.Equal(2, factory.Calls);

        // Run service-level cases in this test to share the isolated static authentication
        // configuration without racing another fixture's initialization.
        await VerifyServiceDeliveryPolicy(factory);
    }

    [Fact]
    public async Task NotificationTextRetainsUnicode()
    {
        var factory = new FakeFactory(async (request, token) =>
        {
            Assert.Equal("[{\"text\":\"提醒 — café\"}]", await request.Content!.ReadAsStringAsync(token));
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("[]") };
        });
        await NotificationHttp.SendAsync(factory, NotificationHttp.NotificationClient,
            new Uri("https://example.test/notification"), "[{\"text\":\"提醒 — café\"}]", "token", default);
    }

    [Fact]
    public async Task CallerCancellationReachesTransport()
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new FakeFactory(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NotificationHttp.SendAsync(factory,
            NotificationHttp.NotificationClient, new Uri("https://example.test/notification"), "{}", "token", cancellation.Token));
    }

    [Fact]
    public async Task TimeoutInterruptsTransport()
    {
        var factory = new FakeFactory(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }) { Timeout = TimeSpan.FromMilliseconds(50) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NotificationHttp.SendAsync(factory,
            NotificationHttp.AuthenticationClient, new Uri("https://example.test/auth"), "{}", null, default));
    }

    [Fact]
    public void NamedFactoryClientsHaveExplicitTimeouts()
    {
        var services = new ServiceCollection();
        NotificationHttp.RegisterClients(services);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var delivery = factory.CreateClient(NotificationHttp.NotificationClient);
        using var auth = factory.CreateClient(NotificationHttp.AuthenticationClient);
        Assert.Equal(TimeSpan.FromSeconds(30), delivery.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), auth.Timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportCapturesRetryAfterBeforeDisposingResponse(bool dateFormat)
    {
        var factory = new FakeFactory((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = dateFormat
                ? new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(20))
                : new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return Task.FromResult(response);
        });
        var failure = await Assert.ThrowsAsync<NotificationHttpException>(() => NotificationHttp.SendAsync(factory,
            NotificationHttp.NotificationClient, new Uri("https://example.test/notification"), "{}", "token", default));
        Assert.InRange(failure.RetryAfter!.Value.TotalSeconds, 18, 20);
    }

    private static async Task VerifyServiceDeliveryPolicy(FakeFactory authentication)
    {
        var permanent = new FakeFactory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var rejected = IsolatedSender(permanent);
        await rejected.PushNotificationAsync(Batch());
        Assert.True(rejected.RequiresDeliveryReview);
        await rejected.PushNotificationAsync(Batch());
        Assert.Equal(1, permanent.Calls); // Neither the outer call nor a later batch multiplies attempts.
        rejected.ResumePendingDeliveryAfterReview();
        await rejected.PushNotificationAsync(new System.Collections.ArrayList());
        Assert.Equal(2, permanent.Calls);

        var timeout = new FakeFactory((_, _) => throw new TaskCanceledException("Ambiguous POST timeout"));
        var uncertain = IsolatedSender(timeout);
        await uncertain.PushNotificationAsync(Batch());
        Assert.True(uncertain.RequiresDeliveryReview);
        await uncertain.PushNotificationAsync(Batch());
        Assert.Equal(1, timeout.Calls);

        var throttled = new FakeFactory((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        });
        var deferred = IsolatedSender(throttled);
        await deferred.PushNotificationAsync(Batch());
        Assert.False(deferred.RequiresDeliveryReview);
        await deferred.PushNotificationAsync(Batch());
        Assert.Equal(1, throttled.Calls); // Retry-After also applies to subsequent calls.

        int posts = 0;
        int previousAuthentications = authentication.Calls;
        var recovering = new FakeFactory((request, _) =>
        {
            Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            return Task.FromResult(++posts == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"object\":[{\"oid\":\"o\",\"victimid\":\"v\",\"type\":\"test\",\"activityid\":\"1\",\"isSuccessful\":true}]}")
                });
        });
        var refreshed = IsolatedSender(recovering);
        await refreshed.PushNotificationAsync(Batch());
        Assert.Equal(2, recovering.Calls);
        Assert.Equal(previousAuthentications + 1, authentication.Calls);
        Assert.False(refreshed.RequiresDeliveryReview);
        Assert.True(await refreshed.PostNotificationAsync()); // Queue was acknowledged; no HTTP call.
        Assert.Equal(2, recovering.Calls);

        // Keep public delivery cancellation cases here: this is the only fixture that
        // owns static authentication. The isolated history tests never reset globals.
        foreach (string outcome in new[] { "canceled-task", "ignored-token", "other-failure" })
        {
            using var cancellation = new CancellationTokenSource();
            var repository = new FakeNotificationRepository
            {
                InsertHistory = token =>
                {
                    Assert.Equal(cancellation.Token, token);
                    cancellation.Cancel();
                    return outcome switch
                    {
                        "canceled-task" => Task.FromCanceled(token),
                        "other-failure" => Task.FromException(new InvalidOperationException("history failed after cancellation")),
                        _ => Task.CompletedTask
                    };
                }
            };
            var confirmed = new FakeFactory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"object\":[{\"oid\":\"o\",\"victimid\":\"v\",\"type\":\"push\",\"activityid\":\"1-platform-v\",\"isSuccessful\":true}]}")
            }));
            var canceledHistory = IsolatedSender(confirmed, repository);
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledHistory.PushNotificationAsync(new()
            {
                new NotificationServiceData.Notification { oid = "o", victimid = "v", type = "push", activityid = "1-platform-v" }
            }, cancellation.Token));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(cancellation.Token, repository.HistoryToken);
            Assert.Single(repository.History);
            Assert.False(canceledHistory.RequiresDeliveryReview);
            Assert.True(await canceledHistory.PostNotificationAsync()); // Confirmed delivery is not requeued.
            Assert.Equal(1, confirmed.Calls);
        }

        static System.Collections.ArrayList Batch() => new()
        {
            new NotificationServiceData.Notification { oid = "o", victimid = "v", type = "test", activityid = "1" }
        };
    }

    private static NotificationService IsolatedSender(IHttpClientFactory factory, FakeNotificationRepository? repository = null)
    {
        // Avoid shared static settings/authentication initialization; all HTTP uses fake transports.
        var sender = (NotificationService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(NotificationService));
        void Set(string name, object value) => typeof(NotificationService).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(sender, value);
        Set("httpClientFactory", factory);
        Set("logger", NullLogger<NotificationService>.Instance);
        Set("baseURL", "https://example.test/api");
        Set("notifications", new NotificationServiceData.NotificationArray());
        if (repository != null) Set("repository", repository);
        return sender;
    }

    private sealed class FakeFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory
    {
        public string? ClientName { get; private set; }
        public int Calls { get; private set; }
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
        public HttpClient CreateClient(string name)
        {
            ClientName = name;
            return new HttpClient(new FakeHandler((request, token) =>
            {
                Calls++;
                return send(request, token);
            })) { Timeout = Timeout };
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class TrackingContent(string text) : StringContent(text)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}