using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Service.AutoCall;

namespace NotificationWorkflowService.Repository;

public sealed class AutoCallPhoneRepository(ILogger<AutoCallPhoneRepository>? logger = null) : IAutoCallPhoneRepository
{
    private readonly ILogger logger = logger ?? NullLogger<AutoCallPhoneRepository>.Instance;

    public async Task<IReadOnlyList<AutoCallPhone>> ReadAsync(string connectionString, string oid, CancellationToken cancellationToken = default)
        => await DatabaseDiagnostics.ExecuteAsync(logger, "TenantDatabase", "dbo.activealarms_GetAllParticipantPhonenumbers", cancellationToken, async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutoCallContactService.ValidateOid(oid);
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A named tenant database connection is required.", nameof(connectionString));
        await using var connection = new SqlConnection(connectionString);
        await using var command = BuildCommand(oid);
        command.Connection = connection;
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPhonesAsync(reader, cancellationToken).ConfigureAwait(false);
    }).ConfigureAwait(false);

    internal static SqlCommand BuildCommand(string oid)
    {
        AutoCallContactService.ValidateOid(oid);
        var command = new SqlCommand("dbo.activealarms_GetAllParticipantPhonenumbers")
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        command.Parameters.Add("@oid", SqlDbType.VarChar, 20).Value = oid;
        return command;
    }

    internal static async Task<IReadOnlyList<AutoCallPhone>> ReadPhonesAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int typeOrdinal = StringOrdinal(reader, "phone_type");
        int numberOrdinal = StringOrdinal(reader, "phone_number");
        var phones = new List<AutoCallPhone>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.IsDBNull(typeOrdinal) || reader.IsDBNull(numberOrdinal)) continue;
            string type = reader.GetString(typeOrdinal).Trim();
            string value = reader.GetString(numberOrdinal);
            if (type.Length != 0 && !string.IsNullOrWhiteSpace(value)) phones.Add(new AutoCallPhone(type, value));
        }
        return phones.AsReadOnly();
    }

    private static int StringOrdinal(DbDataReader reader, string column)
    {
        int ordinal;
        try { ordinal = reader.GetOrdinal(column); }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException)
        {
            throw new InvalidOperationException($"The phone lookup must return a {column} column.");
        }
        if (reader.GetFieldType(ordinal) != typeof(string))
            throw new InvalidOperationException($"The {column} column must contain string values.");
        return ordinal;
    }
}