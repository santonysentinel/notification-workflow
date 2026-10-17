using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NotificationWorkflowService.Repository;

public interface IParticipantCurrentDetailsRepository
{
    Task<IReadOnlyDictionary<string, JsonElement>> ReadAsync(string oid, int activeAlarmId, CancellationToken cancellationToken = default);
}

public sealed class ParticipantCurrentDetailsRepository(
    IConfiguration configuration, ILogger<ParticipantCurrentDetailsRepository>? logger = null) : IParticipantCurrentDetailsRepository
{
    private readonly ILogger logger = logger ?? NullLogger<ParticipantCurrentDetailsRepository>.Instance;

    internal ParticipantCurrentDetailsRepository(IConfiguration configuration, ILogger logger) : this(configuration)
    {
        this.logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, JsonElement>> ReadAsync(
        string oid, int activeAlarmId, CancellationToken cancellationToken = default)
        => await DatabaseDiagnostics.ExecuteAsync(logger, "ClientDatabase", "dbo.activeAlarms_GetParticipantCurrentDetails",
            cancellationToken, async () =>
    {
        await using var command = BuildReadCommand(oid, activeAlarmId);
        string? connectionString = configuration["ClientDatabase"];
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = configuration.GetConnectionString("ClientDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ClientDatabase is required for participant current details.");
        await using var connection = new SqlConnection(connectionString);
        command.Connection = connection;
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDetailsAsync(reader, cancellationToken).ConfigureAwait(false);
    }).ConfigureAwait(false);

    internal static SqlCommand BuildReadCommand(string oid, int activeAlarmId)
    {
        if (string.IsNullOrWhiteSpace(oid) || oid.Length > 20)
            throw new ArgumentException("OID must be nonblank and at most 20 characters.", nameof(oid));
        if (activeAlarmId <= 0) throw new ArgumentOutOfRangeException(nameof(activeAlarmId));
        var command = new SqlCommand("dbo.activeAlarms_GetParticipantCurrentDetails")
        {
            CommandType = CommandType.StoredProcedure, CommandTimeout = 30
        };
        command.Parameters.Add("@OID", SqlDbType.VarChar, 20).Value = oid;
        command.Parameters.Add("@ActiveAlarmID", SqlDbType.Int).Value = activeAlarmId;
        return command;
    }

    internal static async Task<IReadOnlyDictionary<string, JsonElement>> ReadDetailsAsync(
        DbDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Participant current details were not returned.");
        var values = new SortedDictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < reader.FieldCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = reader.GetName(index);
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Participant current details contain an unnamed column.");
            object? value = reader.IsDBNull(index) ? null : reader.GetValue(index);
            var element = JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object));
            if (element.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Undefined)
                throw new InvalidOperationException("Participant current details must contain scalar values.");
            if (!values.TryAdd(name, element))
                throw new InvalidOperationException("Participant current details contain duplicate column names.");
        }
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Participant current details must return exactly one row.");
        return values;
    }
}
