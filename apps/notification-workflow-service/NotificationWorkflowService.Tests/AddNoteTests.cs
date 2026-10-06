using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.Notes;
using Xunit;
using WorkflowRepository = NotificationWorkflowService.Repository.Repository;

namespace NotificationWorkflowService.Tests;

/// <summary>Offline contracts only: no connection, geocoder, HTTP, process culture or retries.</summary>
public class AddNoteTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("A plain note — café 東京 😀", false, false, "A plain note — café 東京 😀")]
    [InlineData("{{lat}}/{{lon}}", true, false, "12.50/-45.125")]
    [InlineData("{{ \tlat\r}} {{\u00a0lon\u2003}}", true, false, "12.50 -45.125")]
    [InlineData("{{\nlat\t}} {{\u2028lon\u2029}}", true, false, "12.50 -45.125")]
    [InlineData("{{current_location_address}}", true, true, "  東京 {{lat}} {natural}  ")]
    [InlineData("{{lat}}{{lat}} {{lon}} {{current_location_address}}", true, true, "12.5012.50 -45.125   東京 {{lat}} {natural}  ")]
    public void RendererAllowsOnlyExactNamesWithDotNetWhitespaceAndDoesNotReparseValues(
        string template, bool location, bool address, string expected)
    {
        var renderer = NoteTemplateRenderer.Parse(template);
        Assert.Equal(location, renderer.RequiresLocation);
        Assert.Equal(address, renderer.RequiresAddress);
        Assert.Equal(expected, renderer.Render(12.50m, -45.125m, "  東京 {{lat}} {natural}  "));
    }

    // Intentionally retain contract regressions instead of silently accepting a runtime defect:
    // on the installed .NET runtime NonBacktracking captures an empty group for trailing LF.
    [Theory]
    [InlineData("{{lat\n}}", "12.50", false)]
    [InlineData("{{lon\r\n}}", "-45.125", false)]
    [InlineData("{{current_location_address\n}}", "address", true)]
    public void RendererPermittedTrailingLineFeedMustRetainVariableAndAddressRequirement(
        string template, string expected, bool address)
    {
        var renderer = NoteTemplateRenderer.Parse(template);
        Assert.True(renderer.RequiresLocation);
        Assert.Equal(address, renderer.RequiresAddress);
        Assert.Equal(expected, renderer.Render(12.50m, -45.125m, "address"));
    }

    public static IEnumerable<object[]> InvalidTemplates() => new string?[]
    {
        null, "", " \t\r\n", "{{LAT}}", "{{Lon}}", "{{Current_Location_Address}}", "{{unknown}}",
        "{{ lat + lon }}", "{{lat.ToString()}}", "{{lat:0.00}}", "{{}}", "{{ }}", "{{la t}}",
        "{{lаt}}", "{{lat\u200b}}", "{", "}", "{lat}", "{{lat", "lat}}", "{{{lat}}}",
        "{{lat}}}", "{{lat}{lon}}", "{{lat}}{{", "{{lat}} {natural language}", "JSON: {\"x\":1}", "{{{{lat}}}}"
    }.Select(value => new object[] { value! });

    [Theory]
    [MemberData(nameof(InvalidTemplates))]
    public async Task InvalidTemplatesAreRejectedBeforeProvidersOrPreparation(string? template)
    {
        var fixture = new Fixture();
        var error = await Record.ExceptionAsync(() => fixture.Service.AddNoteAsync(template!, "oid"));
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal("template", ((ArgumentException)error!).ParamName);
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Repository.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void RendererMissingValuesAreNA(string? address)
    {
        Assert.Equal("N/A/N/A/N/A", NoteTemplateRenderer.Parse("{{lat}}/{{lon}}/{{current_location_address}}")
            .Render(null, null, address));
    }

    [Fact]
    public void RendererUsesInvariantDecimalFormattingWithoutChangingCulture()
    {
        // Assert directly against invariant values; never mutate CurrentCulture (even temporarily).
        var original = CultureInfo.CurrentCulture;
        Assert.Equal("-12.500|180.000", NoteTemplateRenderer.Parse("{{lat}}|{{lon}}")
            .Render(-12.500m, 180.000m, null));
        Assert.Same(original, CultureInfo.CurrentCulture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RendererBoundsFinalUtf16LengthNotTemplateOrScalarCount(bool emoji)
    {
        string exact = emoji ? string.Concat(Enumerable.Repeat("😀", 500)) : new string('é', 1000);
        Assert.Equal(1000, exact.Length);
        Assert.Equal(exact, NoteTemplateRenderer.Parse(exact).Render(null, null, null));
        Assert.Throws<ArgumentException>(() => NoteTemplateRenderer.Parse(exact + "x").Render(null, null, null));
        Assert.Equal(exact, NoteTemplateRenderer.Parse("{{current_location_address}}").Render(null, null, exact));
        Assert.Throws<ArgumentException>(() => NoteTemplateRenderer.Parse("x{{current_location_address}}")
            .Render(null, null, exact));
        // A long template may contract to a short note. Repeated variable values each count.
        string longTemplate = string.Concat(Enumerable.Repeat("{{current_location_address}}", 100));
        Assert.Equal(new string('x', 100), NoteTemplateRenderer.Parse(longTemplate).Render(null, null, "x"));
        Assert.Throws<ArgumentException>(() => NoteTemplateRenderer.Parse("{{current_location_address}}{{current_location_address}}")
            .Render(null, null, new string('x', 501)));
    }

    public static IEnumerable<object[]> InvalidOids() => new string?[] { null, "", " \t", new string('x', 21), string.Concat(Enumerable.Repeat("😀", 11)) }
        .Select(value => new object[] { value! });

    [Theory]
    [MemberData(nameof(InvalidOids))]
    public async Task InvalidOidRejectedBeforeAnyProviderOrRepositoryAccess(string? oid)
    {
        var fixture = new Fixture();
        var error = await Record.ExceptionAsync(() => fixture.Service.AddNoteAsync("{{lat}}", oid!));
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal("oid", ((ArgumentException)error!).ParamName);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task PlainNoteSkipsBothProvidersPreservesValuesAndAllowsRepeatedWritesAndNoCount()
    {
        var fixture = new Fixture();
        string oid = " " + new string('é', 18) + " ";
        string text = "  café 東京 😀  ";
        using var source = new CancellationTokenSource();
        await fixture.Service.AddNoteAsync(text, oid, source.Token);
        await fixture.Service.AddNoteAsync(text, oid, source.Token);
        Assert.Equal(new[] { (text, oid), (text, oid) }, fixture.Repository.Writes);
        Assert.Equal(new[] { "prepare", "open", "execute", "dispose", "prepare", "open", "execute", "dispose" }, fixture.Events);
        Assert.All(fixture.Repository.Operations, op => Assert.True(op.Disposed));
        Assert.All(fixture.Tokens, token => Assert.Equal(source.Token, token));
    }

    [Fact]
    public async Task ServiceAcceptsExactUtf16NoteAndOidLimitsWithoutTrimmingOrTruncation()
    {
        var fixture = new Fixture();
        string text = string.Concat(Enumerable.Repeat("😀", 500));
        string oid = string.Concat(Enumerable.Repeat("😀", 10));
        await fixture.Service.AddNoteAsync(text, oid);
        Assert.Equal((text, oid), Assert.Single(fixture.Repository.Writes));
        Assert.Equal(new[] { "prepare", "open", "execute", "dispose" }, fixture.Events);
    }

    [Theory]
    [InlineData("{{lat}}/{{lon}}/{{lat}}", "12.50/-45.125/12.50", false)]
    [InlineData("{{current_location_address}} {{lat}} {{current_location_address}}", "  address   12.50   address  ", true)]
    public async Task HydrationUsesOneSnapshotAndOnlyResolvesAddressWhenNeeded(string template, string expected, bool address)
    {
        var fixture = new Fixture();
        await fixture.Service.AddNoteAsync(template, "oid");
        Assert.Equal("oid", Assert.Single(fixture.Location.Oids));
        Assert.Equal(address ? new[] { (12.50m, -45.125m) } : [], fixture.Address.Coordinates);
        Assert.Equal((expected, "oid"), Assert.Single(fixture.Repository.Writes));
        Assert.Equal(address ? new[] { "location", "address", "prepare", "open", "execute", "dispose" }
            : ["location", "prepare", "open", "execute", "dispose"], fixture.Events);
    }

    public static IEnumerable<object[]> Coordinates()
    {
        yield return [null!, null!, true, "N/A/N/A/N/A", false];
        yield return [null!, 20m, false, "N/A/20/N/A", false];
        yield return [10m, null!, false, "10/N/A/N/A", false];
        yield return [90.001m, 20m, false, "N/A/20/N/A", false];
        yield return [-90.001m, 20m, false, "N/A/20/N/A", false];
        yield return [10m, 180.001m, false, "10/N/A/N/A", false];
        yield return [10m, -180.001m, false, "10/N/A/N/A", false];
        yield return [decimal.MaxValue, decimal.MinValue, false, "N/A/N/A/N/A", false];
        yield return [90m, 180m, false, "90/180/  address  ", true];
        yield return [-90m, -180m, false, "-90/-180/  address  ", true];
        yield return [0m, 0m, false, "0/0/  address  ", true];
    }

    [Theory]
    [MemberData(nameof(Coordinates))]
    public async Task CoordinateValidationIsIndependentAndNeverGeocodesPartialPair(
        decimal? latitude, decimal? longitude, bool absent, string expected, bool resolves)
    {
        var fixture = new Fixture();
        fixture.Location.Value = absent ? null : new NoteLocation(latitude, longitude);
        await fixture.Service.AddNoteAsync("{{lat}}/{{lon}}/{{current_location_address}}", "oid");
        Assert.Equal((expected, "oid"), Assert.Single(fixture.Repository.Writes));
        Assert.Single(fixture.Location.Oids);
        Assert.Equal(resolves ? 1 : 0, fixture.Address.Coordinates.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n")]
    public async Task UnavailableAddressIsNAWithoutDiscardingValidCoordinates(string? address)
    {
        var fixture = new Fixture();
        fixture.Address.Value = address;
        await fixture.Service.AddNoteAsync("{{lat}}/{{lon}}/{{current_location_address}}", "oid");
        Assert.Equal(("12.50/-45.125/N/A", "oid"), Assert.Single(fixture.Repository.Writes));
        Assert.Single(fixture.Address.Coordinates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedHydrationOrLiteralNeverPreparesWrite(bool hydrate)
    {
        var fixture = new Fixture();
        fixture.Address.Value = new string('x', 1001);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.AddNoteAsync(
            hydrate ? "{{current_location_address}}" : new string('x', 1001), "oid"));
        Assert.Equal(hydrate ? new[] { "location", "address" } : [], fixture.Events);
        Assert.Empty(fixture.Repository.Operations);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("address")]
    [InlineData("prepare")]
    [InlineData("open")]
    [InlineData("execute")]
    [InlineData("dispose")]
    public async Task ErrorsPropagateWithoutRetryOrLaterEffectsAndOwnedOperationIsDisposed(string boundary)
    {
        var fixture = new Fixture();
        var failure = new InvalidOperationException("provider/write failure");
        fixture.Boundary = (name, _) => name == boundary ? Task.FromException(failure) : Task.CompletedTask;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.AddNoteAsync("{{current_location_address}}", "oid")));
        string[] sequence = ["location", "address", "prepare", "open", "execute", "dispose"];
        int index = Array.IndexOf(sequence, boundary);
        Assert.Equal(sequence.Take(index + 1).Concat(boundary is "open" or "execute" ? ["dispose"] : []), fixture.Events);
        Assert.Equal(boundary == "dispose" ? 1 : 0, fixture.Repository.Writes.Count);
        Assert.All(fixture.Repository.Operations, op => Assert.True(op.Disposed));
    }

    [Fact]
    public async Task PreCancellationWinsOverInvalidArgumentsWithoutSideEffects()
    {
        var fixture = new Fixture();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Canceled(fixture.Service.AddNoteAsync(null!, null!, source.Token), source.Token);
        Assert.Empty(fixture.Events);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("address")]
    [InlineData("open")]
    [InlineData("execute")]
    public async Task CancellationDuringEveryAwaitPropagatesOriginalTokenAndCleansUp(string boundary)
    {
        var fixture = new Fixture();
        var gate = new Gate();
        using var source = new CancellationTokenSource();
        fixture.Boundary = (name, token) => name == boundary ? gate.WaitAsync(token) : Task.CompletedTask;
        Task task = fixture.Service.AddNoteAsync("{{current_location_address}}", "oid", source.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.False(task.IsCompleted);
            source.Cancel();
            await Canceled(task, source.Token);
        }
        finally { source.Cancel(); gate.Release.TrySetResult(); }
        Assert.Empty(fixture.Repository.Writes);
        string[] sequence = ["location", "address", "prepare", "open", "execute"];
        Assert.Equal(sequence.Take(Array.IndexOf(sequence, boundary) + 1)
            .Concat(boundary is "open" or "execute" ? ["dispose"] : []), fixture.Events);
        Assert.All(fixture.Repository.Operations, op => Assert.True(op.Disposed));
        Assert.All(fixture.Tokens, token => Assert.Equal(source.Token, token));
    }

    [Theory]
    [InlineData("location")]
    [InlineData("address")]
    [InlineData("execute")]
    public async Task CancellationAfterSuccessfulBoundaryIsRecheckedEvenWhenProviderIgnoresToken(string boundary)
    {
        var fixture = new Fixture();
        using var source = new CancellationTokenSource();
        fixture.AfterBoundary = name => { if (name == boundary) source.Cancel(); };
        await Canceled(fixture.Service.AddNoteAsync("{{current_location_address}}", "oid", source.Token), source.Token);
        Assert.Equal(boundary == "execute" ? 1 : 0, fixture.Repository.Writes.Count);
        Assert.All(fixture.Repository.Operations, op => Assert.True(op.Disposed));
        Assert.Equal(boundary switch
        {
            "location" => new[] { "location" }, "address" => ["location", "address"],
            _ => ["location", "address", "prepare", "open", "execute", "dispose"]
        }, fixture.Events);
    }

    [Fact]
    public async Task EveryAsyncStageIsAwaitedBeforeTheNextIncludingCleanup()
    {
        var fixture = new Fixture();
        string[] sequence = ["location", "address", "open", "execute", "dispose"];
        var gates = sequence.ToDictionary(name => name, _ => new Gate());
        using var source = new CancellationTokenSource();
        fixture.Boundary = (name, token) => gates.TryGetValue(name, out var gate) ? gate.WaitAsync(token) : Task.CompletedTask;
        Task task = fixture.Service.AddNoteAsync("{{current_location_address}}", "oid", source.Token);
        try
        {
            foreach (string name in sequence)
            {
                await gates[name].Entered.Task.WaitAsync(Deadline);
                Assert.False(task.IsCompleted);
                string[] all = ["location", "address", "prepare", "open", "execute", "dispose"];
                Assert.Equal(all.Take(Array.IndexOf(all, name) + 1), fixture.Events);
                gates[name].Release.TrySetResult();
            }
            await task.WaitAsync(Deadline);
        }
        finally { source.Cancel(); foreach (var gate in gates.Values) gate.Release.TrySetResult(); }
        Assert.True(Assert.Single(fixture.Repository.Operations).Disposed);
        Assert.Single(fixture.Repository.Writes);
        Assert.All(fixture.Tokens, token => Assert.Equal(source.Token, token));
    }

    [Fact]
    public async Task AfterWriteCancellationStillAwaitsUncancelableCleanupBeforeCompleting()
    {
        var fixture = new Fixture(); var gate = new Gate();
        using var source = new CancellationTokenSource();
        fixture.AfterBoundary = name => { if (name == "execute") source.Cancel(); };
        fixture.Boundary = (name, token) => name == "dispose" ? gate.WaitAsync(token) : Task.CompletedTask;
        Task task = fixture.Service.AddNoteAsync("plain note", "oid", source.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Deadline);
            Assert.False(task.IsCompleted);
            Assert.Single(fixture.Repository.Writes);
            Assert.True(source.IsCancellationRequested);
            gate.Release.TrySetResult();
            await Canceled(task, source.Token);
        }
        finally { source.Cancel(); gate.Release.TrySetResult(); }
        Assert.Equal(new[] { "prepare", "open", "execute", "dispose" }, fixture.Events);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("address")]
    public async Task ProviderCancellationIsNeverConvertedIntoMissingDataEvenWithoutCallerCancellation(string boundary)
    {
        var fixture = new Fixture();
        using var providerSource = new CancellationTokenSource(); providerSource.Cancel();
        fixture.Boundary = (name, _) => name == boundary ? Task.FromCanceled(providerSource.Token) : Task.CompletedTask;
        await Canceled(fixture.Service.AddNoteAsync("{{current_location_address}}", "oid"), providerSource.Token);
        Assert.Equal(boundary == "location" ? new[] { "location" } : ["location", "address"], fixture.Events);
        Assert.Empty(fixture.Repository.Writes);
    }

    [Fact]
    public async Task ServiceGeneratedValidationErrorsDoNotEchoSensitiveTemplateOrOid()
    {
        const string secret = "sensitive-user-location";
        var fixture = new Fixture();
        foreach (var input in new[]
        {
            (Template: secret + "{invalid}", Oid: "oid"),
            (Template: "note", Oid: secret),
            (Template: secret + new string('x', 1001), Oid: "oid")
        })
        {
            var error = await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Service.AddNoteAsync(input.Template, input.Oid));
            Assert.DoesNotContain(secret, error.Message);
        }
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public void BuilderUsesExactStoredProcedureAndUntruncatedTypedParameters()
    {
        string text = " " + string.Concat(Enumerable.Repeat("😀", 499)) + " ";
        string oid = " " + new string('é', 18) + " ";
        using var command = WorkflowRepository.BuildAddNote(text, oid);
        Assert.Null(command.Connection);
        Assert.Equal(CommandType.StoredProcedure, command.CommandType);
        Assert.Equal("activealarms_AddNote", command.CommandText);
        Assert.Equal(2, command.Parameters.Count);
        // These are the current inferred names, not verification of a deployed SQL signature.
        Assert.Equal(new[] { "@Note", "@OID" }, command.Parameters.Cast<Microsoft.Data.SqlClient.SqlParameter>().Select(p => p.ParameterName));
        Assert.Equal(SqlDbType.NVarChar, command.Parameters["@Note"].SqlDbType);
        Assert.Equal(1000, command.Parameters["@Note"].Size);
        Assert.Equal(text, command.Parameters["@Note"].Value);
        Assert.Equal(SqlDbType.VarChar, command.Parameters["@OID"].SqlDbType);
        Assert.Equal(20, command.Parameters["@OID"].Size);
        Assert.Equal(oid, command.Parameters["@OID"].Value);
        Assert.All(command.Parameters.Cast<Microsoft.Data.SqlClient.SqlParameter>(), p => Assert.Equal(ParameterDirection.Input, p.Direction));
    }

    [Theory]
    [MemberData(nameof(InvalidOids))]
    public void BuilderRejectsEveryInvalidOidDirectly(string? oid) =>
        Assert.Equal("oid", Assert.ThrowsAny<ArgumentException>(() => WorkflowRepository.BuildAddNote("note", oid!)).ParamName);

    public static IEnumerable<object[]> InvalidNotes() => new string?[] { null, "", " \t\r\n", new string('x', 1001), string.Concat(Enumerable.Repeat("😀", 501)) }
        .Select(value => new object[] { value! });

    [Theory]
    [MemberData(nameof(InvalidNotes))]
    public void BuilderRejectsEveryInvalidNoteDirectly(string? note) =>
        Assert.Equal("noteText", Assert.ThrowsAny<ArgumentException>(() => WorkflowRepository.BuildAddNote(note!, "oid")).ParamName);

    [Fact]
    public void ConstructorRejectsMissingDependencies()
    {
        var fixture = new Fixture();
        Assert.Throws<ArgumentNullException>(() => new NoteService(null!, fixture.Location, fixture.Address));
        Assert.Throws<ArgumentNullException>(() => new NoteService((IRepository)fixture.Repository, null!, fixture.Address));
        Assert.Throws<ArgumentNullException>(() => new NoteService((IRepository)fixture.Repository, fixture.Location, null!));
        Assert.Throws<ArgumentNullException>(() => NoteServiceCollectionExtensions.AddNoteServices(null!));
    }

    [Fact]
    public async Task RegistrationIsIdempotentPreservesPreRegisteredOverridesAndResolvesDefaultsOffline()
    {
        var fixture = new Fixture();
        var services = new ServiceCollection().AddSingleton<IRepository>((IRepository)fixture.Repository);
        Assert.Same(services, services.AddNoteServices());
        services.AddNoteServices();
        using (var provider = services.BuildServiceProvider())
        {
            Assert.IsType<UnavailableNoteLocationProvider>(provider.GetRequiredService<INoteLocationProvider>());
            Assert.IsType<UnavailableNoteAddressResolver>(provider.GetRequiredService<INoteAddressResolver>());
            var service = provider.GetRequiredService<INoteService>();
            Assert.IsType<NoteService>(service);
            Assert.NotSame(service, provider.GetRequiredService<INoteService>());
            await service.AddNoteAsync("{{lat}}/{{lon}}/{{current_location_address}}", "oid");
            Assert.Equal(("N/A/N/A/N/A", "oid"), Assert.Single(fixture.Repository.Writes));
        }
        var custom = new ServiceCollection().AddSingleton<IRepository>((IRepository)fixture.Repository)
            .AddSingleton<INoteLocationProvider>(fixture.Location).AddSingleton<INoteAddressResolver>(fixture.Address)
            .AddSingleton<INoteService>(fixture.Service);
        custom.AddNoteServices().AddNoteServices();
        using var overrides = custom.BuildServiceProvider();
        Assert.Same(fixture.Location, overrides.GetRequiredService<INoteLocationProvider>());
        Assert.Same(fixture.Address, overrides.GetRequiredService<INoteAddressResolver>());
        Assert.Same(fixture.Service, overrides.GetRequiredService<INoteService>());
        Assert.Single(custom, d => d.ServiceType == typeof(INoteService));
    }

    private static async Task Canceled(Task task, CancellationToken token)
    {
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Deadline));
        Assert.Equal(token, error.CancellationToken);
        Assert.True(task.IsCanceled);
    }

    internal sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken token)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
    }

    internal sealed class Fixture
    {
        public List<string> Events { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Func<string, CancellationToken, Task> Boundary { get; set; } = (_, _) => Task.CompletedTask;
        public Action<string> AfterBoundary { get; set; } = _ => { };
        public NoteRepository Repository { get; }
        public LocationProvider Location { get; }
        public AddressResolver Address { get; }
        public NoteService Service { get; }
        public Fixture()
        {
            Repository = NoteRepository.Create(this);
            Location = new(this); Address = new(this);
            Service = new((IRepository)Repository, Location, Address);
        }
        public async Task Visit(string name, CancellationToken token, bool forwards = true)
        {
            Events.Add(name);
            if (forwards) Tokens.Add(token);
            await Boundary(name, token);
        }
    }

    internal sealed class LocationProvider(Fixture fixture) : INoteLocationProvider
    {
        public NoteLocation? Value { get; set; } = new(12.50m, -45.125m);
        public List<string> Oids { get; } = [];
        public async Task<NoteLocation?> GetLocationAsync(string oid, CancellationToken cancellationToken = default)
        {
            Oids.Add(oid); await fixture.Visit("location", cancellationToken);
            fixture.AfterBoundary("location"); return Value;
        }
    }

    internal sealed class AddressResolver(Fixture fixture) : INoteAddressResolver
    {
        public string? Value { get; set; } = "  address  ";
        public List<(decimal, decimal)> Coordinates { get; } = [];
        public async Task<string?> ResolveAddressAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken = default)
        {
            Coordinates.Add((latitude, longitude)); await fixture.Visit("address", cancellationToken);
            fixture.AfterBoundary("address"); return Value;
        }
    }

    public class NoteRepository : DispatchProxy
    {
        private Fixture fixture = null!;
        public List<(string Text, string Oid)> Writes { get; } = [];
        public List<NoteOperation> Operations { get; } = [];
        internal static NoteRepository Create(Fixture fixture)
        {
            var proxy = (NoteRepository)DispatchProxy.Create<IRepository, NoteRepository>();
            proxy.fixture = fixture; return proxy;
        }
        protected override object Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IRepository.PrepareAddNote), targetMethod!.Name);
            fixture.Events.Add("prepare");
            // Preparation is synchronous and performs no production I/O. Failure callback must complete.
            Task preparation = fixture.Boundary("prepare", CancellationToken.None);
            Assert.True(preparation.IsCompleted);
            if (preparation.Exception is { } exception) throw exception.InnerException!;
            var operation = new NoteOperation(fixture, this, (string)args![0]!, (string)args[1]!);
            Operations.Add(operation); return operation;
        }
    }

    public sealed class NoteOperation : IWorkflowOperation
    {
        private readonly Fixture fixture;
        private readonly NoteRepository repository;
        private readonly string text, oid;
        private bool opened;
        public bool Disposed { get; private set; }
        internal NoteOperation(Fixture fixture, NoteRepository repository, string text, string oid)
        { this.fixture = fixture; this.repository = repository; this.text = text; this.oid = oid; }
        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            Assert.False(Disposed); Assert.False(opened);
            await fixture.Visit("open", cancellationToken); opened = true;
            fixture.AfterBoundary("open");
        }
        public async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
        {
            Assert.True(opened); Assert.False(Disposed);
            await fixture.Visit("execute", cancellationToken);
            repository.Writes.Add((text, oid)); fixture.AfterBoundary("execute"); return -1;
        }
        public Task<DbDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No reader expected");
        public Task CloseAsync() => throw new InvalidOperationException("DisposeAsync owns cleanup");
        public async ValueTask DisposeAsync()
        {
            Assert.False(Disposed); Disposed = true;
            await fixture.Visit("dispose", CancellationToken.None, false);
        }
    }
}