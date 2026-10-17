using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.Notes;
using Xunit;

namespace NotificationWorkflowService.Tests;

public sealed class ParticipantCurrentDetailsTests
{
    internal static SortedDictionary<string, JsonElement> Values(string offenderName = "Alex & Co") => new(StringComparer.OrdinalIgnoreCase)
    {
        ["offenderName"] = JsonSerializer.SerializeToElement(offenderName),
        ["lat"] = JsonSerializer.SerializeToElement(12.50m),
        ["lon"] = JsonSerializer.SerializeToElement(-45.125m),
        ["alarmname"] = JsonSerializer.SerializeToElement("Low battery"),
        ["last_location_time"] = JsonSerializer.SerializeToElement(new DateTime(2026, 10, 16, 12, 30, 0, DateTimeKind.Utc))
    };

    internal sealed class Stub : IParticipantCurrentDetailsRepository
    {
        public IReadOnlyDictionary<string, JsonElement> Details { get; set; } = Values();
        public List<(string Oid, int ActiveAlarmId, CancellationToken Token)> Calls { get; } = [];
        public Action? AfterRead { get; set; }
        public Exception? Failure { get; set; }
        public Task<IReadOnlyDictionary<string, JsonElement>> ReadAsync(string oid, int activeAlarmId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((oid, activeAlarmId, cancellationToken));
            if (Failure is not null) return Task.FromException<IReadOnlyDictionary<string, JsonElement>>(Failure);
            AfterRead?.Invoke();
            return Task.FromResult(Details);
        }
    }

    [Fact]
    public void LookupCommandCarriesOidAndIntegerActiveAlarmId()
    {
        using var command = ParticipantCurrentDetailsRepository.BuildReadCommand("oid-42", 42);
        Assert.Equal("dbo.activeAlarms_GetParticipantCurrentDetails", command.CommandText);
        Assert.Equal(CommandType.StoredProcedure, command.CommandType);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal(SqlDbType.VarChar, command.Parameters["@OID"].SqlDbType);
        Assert.Equal("oid-42", command.Parameters["@OID"].Value);
        Assert.Equal(SqlDbType.Int, command.Parameters["@ActiveAlarmID"].SqlDbType);
        Assert.Equal(42, command.Parameters["@ActiveAlarmID"].Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => ParticipantCurrentDetailsRepository.BuildReadCommand("oid", 0));
        Assert.Throws<ArgumentException>(() => ParticipantCurrentDetailsRepository.BuildReadCommand("", 42));
    }

    [Fact]
    public async Task LookupCapturesAllColumnsWithOriginalTypesAndCaseInsensitiveAccess()
    {
        var table = new DataTable();
        table.Columns.Add("offenderName", typeof(string));
        table.Columns.Add("lat", typeof(decimal));
        table.Columns.Add("lon", typeof(decimal));
        table.Columns.Add("alarmname", typeof(string));
        table.Columns.Add("last_location_time", typeof(DateTime));
        table.Columns.Add("future_variable", typeof(bool));
        var timestamp = new DateTime(2026, 10, 16, 12, 30, 0);
        table.Rows.Add("Alex & Co", 12.50m, DBNull.Value, "Low battery", timestamp, true);
        await using var reader = table.CreateDataReader();
        var values = await ParticipantCurrentDetailsRepository.ReadDetailsAsync(reader, default);
        Assert.Equal(6, values.Count);
        Assert.Equal("Alex & Co", values["OffenderName"].GetString());
        Assert.Equal(12.50m, values["LAT"].GetDecimal());
        Assert.Equal(JsonValueKind.Null, values["lon"].ValueKind);
        Assert.Equal("Low battery", values["alarmname"].GetString());
        Assert.Equal(timestamp, values["last_location_time"].GetDateTime());
        Assert.True(values["future_variable"].GetBoolean());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task MissingAndAmbiguousParticipantsAreRejected(int rows)
    {
        var table = new DataTable();
        table.Columns.Add("lat", typeof(decimal));
        for (int row = 0; row < rows; row++) table.Rows.Add(12.50m);
        await using var reader = table.CreateDataReader();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ParticipantCurrentDetailsRepository.ReadDetailsAsync(reader, default));
    }

    [Fact]
    public async Task DuplicateCaseInsensitiveColumnsAreRejected()
    {
        var table = new DataTable();
        table.Columns.Add("lat", typeof(decimal));
        table.Columns.Add("LAT", typeof(decimal));
        table.Rows.Add(1m, 2m);
        await using var reader = table.CreateDataReader();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ParticipantCurrentDetailsRepository.ReadDetailsAsync(reader, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LookupRequiresClientDatabaseWithoutFallingBackToWorkflowOrAutoCall(bool topLevel)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:connstr"] = "Server=unused",
            ["ConnectionStrings:AutoCallDB"] = "Server=unused"
        };
        if (topLevel)
        {
            settings["ClientDatabase"] = "unsupported-key=private-value";
            settings["ConnectionStrings:ClientDatabase"] = "Server=unused";
        }
        var repository = new ParticipantCurrentDetailsRepository(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        if (topLevel) await Assert.ThrowsAsync<ArgumentException>(() => repository.ReadAsync("oid", 42));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReadAsync("oid", 42));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReadAsync("oid", 42, new CancellationToken(true)));
    }

    [Fact]
    public async Task NoteLocationUsesTheSharedLookupWithOidAlarmIdAndCancellation()
    {
        var repository = new Stub();
        using var source = new CancellationTokenSource();
        var location = await new ParticipantNoteLocationProvider(repository).GetLocationAsync("oid-42", 42, source.Token);
        Assert.Equal(new NoteLocation(12.50m, -45.125m), location);
        Assert.Equal(("oid-42", 42, source.Token), Assert.Single(repository.Calls));
    }

    [Theory]
    [InlineData("null", null)]
    [InlineData("\"12.50\"", "12.50")]
    [InlineData("12.50", "12.50")]
    public async Task NoteLocationAcceptsNullOrInvariantNumericCoordinates(string json, string? expected)
    {
        var values = Values();
        values["lat"] = JsonSerializer.Deserialize<JsonElement>(json);
        var location = await new ParticipantNoteLocationProvider(new Stub { Details = values }).GetLocationAsync("oid", 42);
        Assert.Equal(expected is null ? null : 12.50m, location!.Latitude);
    }

    [Fact]
    public async Task NoteLocationDoesNotConvertLookupFailuresIntoMissingCoordinates()
    {
        var failure = new InvalidOperationException("test failure");
        var repository = new Stub { Failure = failure };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ParticipantNoteLocationProvider(repository).GetLocationAsync("oid", 42)));
        repository.Failure = null;
        using var cancellation = new CancellationTokenSource();
        repository.AfterRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ParticipantNoteLocationProvider(repository).GetLocationAsync("oid", 42, cancellation.Token));
    }
}
