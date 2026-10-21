using System.Reflection;
using System.Runtime.CompilerServices;
using ActiveAlarmsParser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Entity;
using NotificationWorkflowService.Parser;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.AutoCall;
using NotificationWorkflowService.Service.Notes;
using Xunit;
using NotificationSender = ActiveAlarmsParser.Service.NotificationService.NotificationService;
using WorkflowRepository = NotificationWorkflowService.Repository.Repository;

namespace NotificationWorkflowService.Tests;

public class WorkflowOrchestrationCompatibilityTests
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [Fact]
    public void CompatibilityTypeRetainsNamespaceAccessibilityConstructorsAndInheritedPublicSurface()
    {
        Assert.Equal("ActiveAlarmsParser", typeof(WorkFlowInitiator).Namespace);
        Assert.True(typeof(WorkFlowInitiator).IsNotPublic);
        Assert.Equal(typeof(WorkFlowCommon), typeof(WorkFlowInitiator).BaseType);
        Assert.Empty(typeof(WorkFlowInitiator).GetMethods(Declared));
        Assert.Empty(typeof(WorkFlowInitiator).GetFields(Declared));
        AssertConstructors(typeof(WorkFlowCommon), typeof(ILogger<WorkFlowCommon>));
        AssertConstructors(typeof(WorkFlowInitiator), typeof(ILogger<WorkFlowInitiator>));

        (string Name, Type Return, Type[] Parameters)[] expected =
        [
            ("setUpParserAsync", typeof(Task<bool>), [typeof(string), typeof(CancellationToken)]),
            ("readPointsAsync", typeof(Task<bool>), [typeof(CancellationToken)]),
            ("FindStepOneProfileItem", typeof(ProfileItem), [typeof(List<ProfileItem>), typeof(DateTime)]),
            ("parseAlarmsAsync", typeof(Task), [typeof(CancellationToken)]),
            ("PushAlertsToVictimsAsync", typeof(Task), [typeof(ActiveAlarm), typeof(CancellationToken)]),
            ("PushNotificationToVictimAsync", typeof(Task), [typeof(ActiveAlarm), typeof(CancellationToken)])
        ];
        foreach (Type type in new[] { typeof(WorkFlowCommon), typeof(WorkFlowInitiator) })
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.DeclaringType != typeof(object)).ToArray();
            Assert.Equal(expected.Length, methods.Length);
            foreach (var contract in expected)
            {
                var method = Assert.Single(methods, m => m.Name == contract.Name);
                Assert.Equal(contract.Return, method.ReturnType);
                Assert.Equal(contract.Parameters, method.GetParameters().Select(p => p.ParameterType));
                Assert.Equal(typeof(WorkFlowCommon), method.DeclaringType);
                if (contract.Name.EndsWith("Async", StringComparison.Ordinal))
                {
                    var token = method.GetParameters()[^1];
                    Assert.Equal("cancellationToken", token.Name);
                    Assert.True(token.IsOptional);
                    Assert.Null(token.DefaultValue);
                }
            }
        }
        var bridges = typeof(WorkFlowCommon).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(3, bridges.Length);
        foreach (int arity in new[] { 4, 5, 6 })
        {
            var bridge = Assert.Single(bridges, c => c.GetParameters().Length == arity);
            Assert.True(bridge.IsFamily);
            Type[] expectedBridge = arity switch
            {
                4 => [typeof(ILogger), typeof(IConfiguration), typeof(NotificationSender), typeof(IRepository)],
                5 => [typeof(ILogger), typeof(IConfiguration), typeof(NotificationSender), typeof(IRepository), typeof(INoteService)],
                _ => [typeof(ILogger), typeof(IConfiguration), typeof(NotificationSender), typeof(IRepository), typeof(INoteService), typeof(AutoCallEnqueueService)]
            };
            Assert.Equal(expectedBridge, bridge.GetParameters().Select(p => p.ParameterType));
        }
        Assert.True(Field(typeof(WorkFlowCommon), "log").IsPrivate);
        Assert.True(Field(typeof(WorkFlowCommon), "log").IsInitOnly);
        Assert.Equal(typeof(ILogger), Field(typeof(WorkFlowCommon), "log").FieldType);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BothConstructorOverloadsPreserveOriginalLoggerCategoryAndInitializeBaseStateAsync(bool compatibility, bool injected)
    {
        var provider = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        var configuration = Configuration();
        // Never initialize shared notification authentication/settings or HTTP.
        var sender = (NotificationSender)RuntimeHelpers.GetUninitializedObject(typeof(NotificationSender));
        var spy = WorkflowRepositoryAdapterTests.RepositorySpy.Create();
        ILogger logger;
        WorkFlowCommon parser;
        if (compatibility)
        {
            var typed = factory.CreateLogger<WorkFlowInitiator>();
            logger = typed;
            parser = injected ? new WorkFlowInitiator(typed, configuration, sender, (IRepository)spy)
                : new WorkFlowInitiator(typed, configuration, sender);
        }
        else
        {
            var typed = factory.CreateLogger<WorkFlowCommon>();
            logger = typed;
            parser = injected ? new WorkFlowCommon(typed, configuration, sender, (IRepository)spy)
                : new WorkFlowCommon(typed, configuration, sender);
        }
        Assert.Same(logger, Field(parser.GetType(), "log").GetValue(parser));
        Assert.Same(configuration, Field(parser.GetType(), "configuration").GetValue(parser));
        Assert.Same(sender, Field(parser.GetType(), "notificationService").GetValue(parser));
        Assert.Equal("offline-test", Field(parser.GetType(), "Platform").GetValue(parser));
        Assert.Equal("", Field(parser.GetType(), "platForm").GetValue(parser));
        Assert.Null(Field(parser.GetType(), "pendingReferenceData").GetValue(parser));
        foreach (string name in new[] { "activeAlarms", "clientProfileMapping", "clientHolidayProfileMapping",
            "activeProfiles", "activeHolidays", "ClearEvents", "roles", "victims", "victimTypeDict",
            "pnAlarms", "MEZVictims", "AttachedVictimZones" })
            Assert.NotNull(Field(parser.GetType(), name).GetValue(parser));
        if (injected)
            Assert.Same(spy, Field(parser.GetType(), "repository").GetValue(parser));
        else
        {
            Assert.IsType<WorkflowRepository>(Field(parser.GetType(), "repository").GetValue(parser));
            // Replace only in the test before any executable call; never open SQL.
            Field(parser.GetType(), "repository").SetValue(parser, spy);
        }
        Assert.Empty(spy.Calls);
        var failure = new InvalidOperationException("offline preparation failure");
        spy.PreparationFailure = _ => failure;
        Assert.Equal(false, await WorkflowRepositoryAdapterTests.CallAsync(parser, "FetchClientProfile", 0));
        // Both the migrated refresh message and the existing preparation-failure
        // outer catch use the original parser logger category at Information.
        Assert.Equal(2, provider.Entries.Count);
        Assert.All(provider.Entries, entry =>
        {
            Assert.Equal(compatibility ? typeof(WorkFlowInitiator).FullName : typeof(WorkFlowCommon).FullName, entry.Category);
            Assert.Equal(LogLevel.Information, entry.Level);
        });
    }

    [Theory]
    [InlineData("Normal", typeof(Worker))]
    [InlineData("normal", typeof(Worker))]
    [InlineData("Step", typeof(StepWorker))]
    [InlineData("step", typeof(StepWorker))]
    [InlineData("AutoCall", typeof(AutoCallWorker))]
    [InlineData("autocall", typeof(AutoCallWorker))]
    [InlineData("Transcription", typeof(TranscriptionWorker))]
    [InlineData("transcription", typeof(TranscriptionWorker))]
    [InlineData("TenantNotes", typeof(AutoCallNoteWorker))]
    [InlineData("tenantnotes", typeof(AutoCallNoteWorker))]
    public void RealHostBuildSelectsWorkerAndRetainsNormalFactoryAndCompatibilityRegistrations(string mode, Type workerType)
    {
        ServiceDescriptor[] descriptors = [];
        using var host = Program.CreateHostBuilder([
            "--WorkerMode", mode, "--ConnectionStrings:connstr", "offline-connection-string", "--Platform", "offline-test",
            "--AutoCall:Worker:FeedbackBaseUrl", "https://feedback.example"
        ]).ConfigureServices((_, services) => descriptors = services.ToArray()).Build();
        Assert.Equal(mode, host.Services.GetRequiredService<IConfiguration>()["WorkerMode"]);
        Assert.Equal(workerType, Assert.Single(host.Services.GetServices<IHostedService>()).GetType());
        foreach (Type type in new[] { typeof(WorkFlowCommon), typeof(WorkFlowInitiator) })
        {
            var descriptor = Assert.Single(descriptors, d => d.ServiceType == type);
            Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
            Assert.Equal(type, descriptor.ImplementationType);
        }
        var factory = Assert.Single(descriptors, d => d.ServiceType == typeof(Func<WorkFlowCommon>));
        Assert.Equal(ServiceLifetime.Transient, factory.Lifetime);
        Assert.NotNull(factory.ImplementationFactory);
        Assert.NotNull(host.Services.GetRequiredService<Func<WorkFlowCommon>>());
        Assert.DoesNotContain(descriptors, d => d.ServiceType == typeof(WorkFlowSteps));
        Assert.Equal(typeof(NotificationSender), Assert.Single(descriptors, d => d.ServiceType == typeof(NotificationSender)).ImplementationType);
        // Do not start the host or invoke the parser factory: constructing the real
        // sender initializes static authentication/settings. Typed construction is
        // exercised separately above with an uninitialized sender and offline spy.
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    public void InvalidWorkerModeFailsWithSupportedModes(string mode)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Program.CreateHostBuilder(["--WorkerMode", mode]).Build());
        Assert.Equal("WorkerMode must be 'Normal', 'Step', 'AutoCall', 'Transcription' or 'TenantNotes'.", exception.Message);
    }

    [Fact]
    public void MissingWorkerModeDefaultsToNormalOnly()
    {
        using var host = Program.CreateHostBuilder([])
            .ConfigureAppConfiguration((_, builder) => builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkerMode"] = null,
                ["AutoCall:Worker:Enabled"] = "true",
                ["Transcription:Worker:Enabled"] = "true"
            })).Build();
        Assert.IsType<Worker>(Assert.Single(host.Services.GetServices<IHostedService>()));
    }

    [Fact]
    public async Task StepParserRemainsIndependentAndUsesExpiredAlarmEntryPointAsync()
    {
        var spy = WorkflowRepositoryAdapterTests.RepositorySpy.Create();
        var parser = new WorkFlowSteps(NullLogger<WorkFlowSteps>.Instance, Configuration(), (IRepository)spy);
        Assert.False(typeof(WorkFlowCommon).IsAssignableFrom(parser.GetType()));
        Assert.Equal(typeof(WorkFlowSteps), parser.GetType().GetMethod("parseAlarmsAsync")!.DeclaringType);
        Assert.Equal(typeof(WorkFlowSteps), parser.GetType().GetMethod("getExpiredAlarmsAsync")!.DeclaringType);
        Assert.Null(parser.GetType().GetMethod("readPoints"));
        Assert.Null(parser.GetType().GetMethod("readPointsAsync"));
        Assert.Null(parser.GetType().GetMethod("PushNotificationToVictim"));
        Assert.Null(parser.GetType().GetMethod("PushNotificationToVictimAsync"));
        Assert.True(await parser.getExpiredAlarmsAsync());
        Assert.Equal("PrepareGetExpiredAlarms", Assert.Single(spy.Calls).Name);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:connstr"] = "offline-connection-string", ["Platform"] = "offline-test"
    }).Build();

    private static void AssertConstructors(Type type, Type logger)
    {
        var constructors = type.GetConstructors();
        int[] arities = type == typeof(WorkFlowCommon) ? [3, 4, 5, 6] : [3, 4, 5];
        Assert.Equal(arities.Length, constructors.Length);
        foreach (int arity in arities)
        {
            var ctor = Assert.Single(constructors, c => c.GetParameters().Length == arity);
            Type[] expected = arity switch
            {
                3 => [logger, typeof(IConfiguration), typeof(NotificationSender)],
                4 => [logger, typeof(IConfiguration), typeof(NotificationSender), typeof(IRepository)],
                5 => [logger, typeof(IConfiguration), typeof(NotificationSender), typeof(IRepository), typeof(INoteService)],
                _ => [logger, typeof(IConfiguration), typeof(NotificationSender), typeof(IRepository), typeof(INoteService), typeof(AutoCallEnqueueService)]
            };
            Assert.Equal(expected, ctor.GetParameters().Select(p => p.ParameterType));
        }
    }

    private static FieldInfo Field(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
            if (current.GetField(name, Declared) is { } field) return field;
        throw new MissingFieldException(type.FullName, name);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);
        public void Dispose() { }
    }

    private sealed class RecordingLogger(string category, List<(string Category, LogLevel Level)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Add((category, logLevel));
    }
}