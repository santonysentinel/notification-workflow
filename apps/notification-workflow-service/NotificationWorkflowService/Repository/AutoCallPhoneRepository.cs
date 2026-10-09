using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using NotificationWorkflowService.Service.AutoCall;

namespace NotificationWorkflowService.Repository;

public sealed class AutoCallPhoneRepository : IAutoCallPhoneRepository
{
    public async Task<IReadOnlyList<string>> ReadAsync(string connectionString, string oid, CancellationToken cancellationToken = default)
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
    }

    internal static SqlCommand BuildCommand(string oid)
    {
        AutoCallContactService.ValidateOid(oid);
        var command = new SqlCommand("dbo.active_alarms_GetAllParticipantPhonenumbers")
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        command.Parameters.Add("@oid", SqlDbType.VarChar, 20).Value = oid;
        return command;
    }

    internal static async Task<IReadOnlyList<string>> ReadPhonesAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int ordinal;
        try { ordinal = reader.GetOrdinal("phone_numbers"); }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException)
        {
            throw new InvalidOperationException("The phone lookup must return a phone_numbers column.");
        }
        if (reader.GetFieldType(ordinal) != typeof(string))
            throw new InvalidOperationException("The phone_numbers column must contain string values.");
        var phones = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.IsDBNull(ordinal)) continue;
            string value = reader.GetString(ordinal);
            if (!string.IsNullOrWhiteSpace(value)) phones.Add(value);
        }
        return phones.AsReadOnly();
    }
}