using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
using System.Reflection;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.AutoCall;
using StackExchange.Redis;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class AutoCallContactsTests
{
    private const string TenantConnection = "Server=tenant.invalid;Database=Tenant;Integrated Security=true";

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:DEV1"] = TenantConnection,
        ["ConnectionStrings:connstr"] = "must-not-fall-back",
        ["ConnectionStrings:AutoCallDB"] = "must-not-use-central-database"
    }).Build();

    private sealed class Store : IAutoCallRoutingStore
    {
        public string? Value { get; set; } = "{\"platform\":\"DEV1\",\"OID\":\"other\",\"ClientUID\":\"ignored\"}";
        public string? Key { get; private set; }
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            Calls++;
            Key = key;
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is null ? Task.FromResult(Value) : Task.FromException<string?>(Failure);
        }
    }

    private sealed class Phones : IAutoCallPhoneRepository
    {
        public int Calls { get; private set; }
        public string? Connection { get; private set; }
        public string? Oid { get; private set; }
        public CancellationToken Token { get; private set; }
        public IReadOnlyList<string> Values { get; set; } = ["+15551234567", "+15557654321"];
        public Task<IReadOnlyList<string>> ReadAsync(string connectionString, string oid, CancellationToken cancellationToken = default)
        {
            Calls++;
            Connection = connectionString;
            Oid = oid;
            Token = cancellationToken;
            return Task.FromResult(Values);
        }
    }

    [Theory]
    [InlineData("{\"platform\":\"DEV1\",\"OID\":\"replacement\",\"ClientUID\":\"ignored\"}")]
    [InlineData("DEV1")]
    public async Task RoutesByUppercaseOidKeyWithoutRewritingTheSqlOid(string routing)
    {
        var store = new Store { Value = routing };
        var phones = new Phones();
        using var cancellation = new CancellationTokenSource();
        var service = new AutoCallContactService(store, phones, Configuration());
        var result = await service.ResolveAsync("oid-7", cancellation.Token);
        Assert.Equal("dna.client.OID-7", store.Key);
        Assert.Equal(TenantConnection, phones.Connection);
        Assert.Equal("oid-7", phones.Oid);
        Assert.Equal(cancellation.Token, phones.Token);
        Assert.Equal("oid-7", result.Oid);
        Assert.Equal("DEV1", result.Platform);
        Assert.Equal(phones.Values, result.PhoneNumbers);
    }

    [Theory]
    [InlineData(null, AutoCallRoutingFailure.NotFound)]
    [InlineData("", AutoCallRoutingFailure.InvalidRoutingData)]
    [InlineData("{broken", AutoCallRoutingFailure.InvalidRoutingData)]
    [InlineData("{}", AutoCallRoutingFailure.InvalidRoutingData)]
    [InlineData("[]", AutoCallRoutingFailure.InvalidRoutingData)]
    [InlineData("null", AutoCallRoutingFailure.InvalidRoutingData)]
    [InlineData("{\"platform\":12}", AutoCallRoutingFailure.InvalidRoutingData)]
    [InlineData("{\"platform\":\"ConnectionStrings:connstr\"}", AutoCallRoutingFailure.InvalidRoutingData)]
    [InlineData("UNKNOWN", AutoCallRoutingFailure.DatabaseNotConfigured)]
    public async Task RoutingFailureNeverCallsAnyDatabase(string? routing, AutoCallRoutingFailure failure)
    {
        var phones = new Phones();
        var service = new AutoCallContactService(new Store { Value = routing }, phones, Configuration());
        var exception = await Assert.ThrowsAsync<AutoCallRoutingException>(() => service.ResolveAsync("oid-7"));
        Assert.Equal(failure, exception.Failure);
        Assert.Equal(0, phones.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" oid")]
    [InlineData("123456789012345678901")]
    public async Task RejectsInvalidOidBeforeRedis(string oid)
    {
        var store = new Store();
        var service = new AutoCallContactService(store, new Phones(), Configuration());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveAsync(oid));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task RedisFailureIsMaskedAndDoesNotReachSql()
    {
        var phones = new Phones();
        var service = new AutoCallContactService(new Store { Failure = new TimeoutException("sensitive transport details") }, phones, Configuration());
        var exception = await Assert.ThrowsAsync<AutoCallRoutingException>(() => service.ResolveAsync("oid-7"));
        Assert.Equal(AutoCallRoutingFailure.Unavailable, exception.Failure);
        Assert.DoesNotContain("sensitive", exception.ToString());
        Assert.Equal(0, phones.Calls);
    }

    [Fact]
    public async Task NoPhoneRowsReturnsAnExplicitEmptyList()
    {
        var service = new AutoCallContactService(new Store(), new Phones { Values = [] }, Configuration());
        Assert.Empty((await service.ResolveAsync("oid-7")).PhoneNumbers);
    }

    [Fact]
    public async Task PreCanceledLookupTouchesNoDependencies()
    {
        var store = new Store();
        var phones = new Phones();
        var service = new AutoCallContactService(store, phones, Configuration());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ResolveAsync("oid-7", new CancellationToken(true)));
        Assert.Equal(0, store.Calls);
        Assert.Equal(0, phones.Calls);
    }

    [Fact]
    public void PhoneProcedureUsesTypedOidWithoutRewriting()
    {
        using var command = AutoCallPhoneRepository.BuildCommand("oid-7");
        Assert.Equal("dbo.active_alarms_GetAllParticipantPhonenumbers", command.CommandText);
        Assert.Equal(CommandType.StoredProcedure, command.CommandType);
        Assert.Equal(30, command.CommandTimeout);
        Assert.Null(command.Connection);
        var parameter = Assert.Single(command.Parameters.Cast<Microsoft.Data.SqlClient.SqlParameter>());
        Assert.Equal("@oid", parameter.ParameterName);
        Assert.Equal(SqlDbType.VarChar, parameter.SqlDbType);
        Assert.Equal(20, parameter.Size);
        Assert.Equal("oid-7", parameter.Value);
    }

    [Fact]
    public async Task ReadsEveryNonblankPhoneValueInOriginalOrder()
    {
        using var table = new DataTable();
        table.Columns.Add("phone_numbers", typeof(string));
        foreach (object value in new object[] { "+15551234567", DBNull.Value, " ", "+15557654321", "+15551234567" }) table.Rows.Add(value);
        using var reader = table.CreateDataReader();
        var result = await AutoCallPhoneRepository.ReadPhonesAsync(reader, CancellationToken.None);
        Assert.Equal(new[] { "+15551234567", "+15557654321", "+15551234567" }, result);
    }

    [Fact]
    public async Task MissingPhoneColumnFailsRatherThanPretendingThereAreNoNumbers()
    {
        using var table = new DataTable();
        table.Columns.Add("other", typeof(string));
        using var reader = table.CreateDataReader();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AutoCallPhoneRepository.ReadPhonesAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task PreCanceledPhoneRepositoryDoesNotOpenSql()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AutoCallPhoneRepository().ReadAsync("not-a-connection", "oid-7", new CancellationToken(true)));
    }

    [Fact]
    public async Task RejectsUnexpectedPhoneColumnType()
    {
        using var table = new DataTable();
        table.Columns.Add("phone_numbers", typeof(int));
        using var reader = table.CreateDataReader();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AutoCallPhoneRepository.ReadPhonesAsync(reader, CancellationToken.None));
    }

    public class RedisProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handle { get; set; } = (_, _) => throw new NotSupportedException();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handle(targetMethod!, args);
    }

    private static RedisProxy Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = (RedisProxy)(object)DispatchProxy.Create<T, RedisProxy>();
        proxy.Handle = handler;
        return proxy;
    }

    private static IConfiguration RedisConfiguration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["AutoCall:Routing:RedisConnectionString"] = "offline.invalid:6379,defaultDatabase=4" }).Build();

    [Fact]
    public async Task RedisConnectionIsLazyReusedAndDisposed()
    {
        int connections = 0, disposals = 0;
        var keys = new List<string>();
        var database = Proxy<IDatabase>((method, args) =>
        {
            Assert.Equal("StringGetAsync", method.Name);
            keys.Add(args![0]!.ToString()!);
            return Task.FromResult((RedisValue)"DEV1");
        });
        var client = Proxy<IConnectionMultiplexer>((method, _) => method.Name switch
        {
            "GetDatabase" => (IDatabase)(object)database,
            "Dispose" => CountDisposal(),
            _ => throw new NotSupportedException(method.Name)
        });
        object? CountDisposal() { disposals++; return null; }
        var store = new RedisAutoCallRoutingStore(RedisConfiguration(), options =>
        {
            connections++;
            Assert.False(options.AbortOnConnectFail);
            Assert.Equal(5000, options.ConnectTimeout);
            Assert.Equal(5000, options.AsyncTimeout);
            Assert.Equal(4, options.DefaultDatabase);
            return Task.FromResult((IConnectionMultiplexer)(object)client);
        });
        Assert.Equal(0, connections);
        Assert.Equal("DEV1", await store.GetAsync("dna.client.OID-7"));
        Assert.Equal("DEV1", await store.GetAsync("dna.client.OID-8"));
        Assert.Equal(1, connections);
        Assert.Equal(new[] { "dna.client.OID-7", "dna.client.OID-8" }, keys);
        await store.DisposeAsync();
        await store.DisposeAsync();
        Assert.Equal(1, disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetAsync("key"));
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelSharedRedisConnection()
    {
        var pending = new TaskCompletionSource<IConnectionMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Proxy<IConnectionMultiplexer>((method, _) => method.Name == "Dispose" ? null : throw new NotSupportedException());
        await using var store = new RedisAutoCallRoutingStore(RedisConfiguration(), _ => pending.Task);
        using var cancellation = new CancellationTokenSource();
        Task<string?> lookup = store.GetAsync("dna.client.OID-7", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.False(pending.Task.IsCanceled);
        pending.SetResult((IConnectionMultiplexer)(object)client);
    }

    [Fact]
    public async Task RegistrationDoesNotRequireLiveRedisOrSqlConfiguration()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Configuration());
        services.AddAutoCallContacts();
        await using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<AutoCallContactService>());
    }

    [Fact]
    public async Task FailedRedisInitializationCanRecoverOnNextLookup()
    {
        int attempts = 0;
        var database = Proxy<IDatabase>((_, _) => Task.FromResult(RedisValue.Null));
        var client = Proxy<IConnectionMultiplexer>((method, _) => method.Name switch
        {
            "GetDatabase" => (IDatabase)(object)database,
            "Dispose" => null,
            _ => throw new NotSupportedException()
        });
        await using var store = new RedisAutoCallRoutingStore(RedisConfiguration(), _ => ++attempts == 1
            ? Task.FromException<IConnectionMultiplexer>(new TimeoutException())
            : Task.FromResult((IConnectionMultiplexer)(object)client));
        await Assert.ThrowsAsync<TimeoutException>(() => store.GetAsync("dna.client.OID-7"));
        Assert.Null(await store.GetAsync("dna.client.OID-7"));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RedisGetHasABoundedDeadline()
    {
        var configuration = RedisConfiguration();
        configuration["AutoCall:Routing:LookupTimeoutSeconds"] = "1";
        var pending = new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Proxy<IDatabase>((_, _) => pending.Task);
        var client = Proxy<IConnectionMultiplexer>((method, _) => method.Name switch
        {
            "GetDatabase" => (IDatabase)(object)database,
            "Dispose" => null,
            _ => throw new NotSupportedException()
        });
        await using var store = new RedisAutoCallRoutingStore(configuration, _ => Task.FromResult((IConnectionMultiplexer)(object)client));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync("dna.client.OID-7").WaitAsync(TimeSpan.FromSeconds(5)));
        pending.SetResult(RedisValue.Null);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("bad")]
    public async Task RejectsInvalidRedisDeadlineBeforeConnection(string value)
    {
        var configuration = RedisConfiguration();
        configuration["AutoCall:Routing:LookupTimeoutSeconds"] = value;
        bool connected = false;
        await using var store = new RedisAutoCallRoutingStore(configuration, _ =>
        {
            connected = true;
            throw new InvalidOperationException();
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("dna.client.OID-7"));
        Assert.False(connected);
    }
}