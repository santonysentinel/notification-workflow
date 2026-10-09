using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationWorkflowService.Repository;
using StackExchange.Redis;

namespace NotificationWorkflowService.Service.AutoCall;

public sealed class RedisAutoCallRoutingStore : IAutoCallRoutingStore, IAsyncDisposable
{
    private readonly IConfiguration configuration;
    private readonly Func<ConfigurationOptions, Task<IConnectionMultiplexer>> connect;
    private readonly object gate = new();
    private Task<IConnectionMultiplexer>? connection;
    private bool disposed;

    public RedisAutoCallRoutingStore(IConfiguration configuration)
        : this(configuration, async options => await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false)) { }

    internal RedisAutoCallRoutingStore(IConfiguration configuration, Func<ConfigurationOptions, Task<IConnectionMultiplexer>> connect)
    {
        this.configuration = configuration;
        this.connect = connect;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int seconds = 5;
        string? configuredTimeout = configuration["AutoCall:Routing:LookupTimeoutSeconds"];
        if (configuredTimeout is not null && (!int.TryParse(configuredTimeout, out seconds) || seconds < 1 || seconds > 60))
            throw new InvalidOperationException("AutoCall routing lookup timeout must be between 1 and 60 seconds.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        Task<IConnectionMultiplexer> pending;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (connection is null || connection.IsFaulted || connection.IsCanceled)
            {
                string? connectionString = configuration["AutoCall:Routing:RedisConnectionString"];
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new InvalidOperationException("AutoCall routing Redis connection is not configured.");
                var options = ConfigurationOptions.Parse(connectionString);
                options.AbortOnConnectFail = false;
                options.ConnectRetry = 0;
                options.ConnectTimeout = seconds * 1000;
                options.AsyncTimeout = seconds * 1000;
                connection = connect(options);
            }
            pending = connection;
        }
        var client = await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
        var value = await client.GetDatabase().StringGetAsync(key).WaitAsync(deadline.Token).ConfigureAwait(false);
        return value.IsNull ? null : value.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        Task<IConnectionMultiplexer>? pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending = connection;
        }
        if (pending is null) return;
        IConnectionMultiplexer client;
        try { client = await pending.ConfigureAwait(false); }
        catch { return; }
        client.Dispose();
    }
}

public static class AutoCallServiceCollectionExtensions
{
    public static IServiceCollection AddAutoCallContacts(this IServiceCollection services)
    {
        services.AddSingleton<IAutoCallRoutingStore, RedisAutoCallRoutingStore>();
        services.AddSingleton<IAutoCallPhoneRepository, AutoCallPhoneRepository>();
        services.AddSingleton<AutoCallContactService>();
        services.AddSingleton<IAutoCallQueueRepository, AutoCallQueueRepository>();
        services.AddSingleton<AutoCallEnqueueService>();
        services.AddSingleton<IAutoCallRepository, AutoCallRepository>();
        services.AddSingleton(provider => AutoCallWorkerOptions.Read(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IAutoCallProcessingService, AutoCallProcessingService>();
        return services;
    }
}