using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Service.AutoCall;

namespace NotificationWorkflowService.Repository;

public interface IAutoCallOfficerRepository
{
    Task<RoboCallOfficer> ResolveAsync(string oid, RoboCallOfficerSelection selection, CancellationToken cancellationToken);
    Task<IReadOnlyList<AutoCallPhone>> ReadPhonesAsync(string connectionString, int officerId, CancellationToken cancellationToken);
}

public sealed class AutoCallOfficerRepository(IConfiguration configuration, ILogger<AutoCallOfficerRepository>? logger = null) : IAutoCallOfficerRepository
{
    private readonly ILogger logger = logger ?? NullLogger<AutoCallOfficerRepository>.Instance;

    public async Task<RoboCallOfficer> ResolveAsync(string oid, RoboCallOfficerSelection selection, CancellationToken cancellationToken)
        => await DatabaseDiagnostics.ExecuteAsync(logger, "ClientDatabase", "dbo.activealarms_ResolveRoboCallOfficer", cancellationToken, async () =>
        {
            string? connectionString = configuration["ClientDatabase"];
            if (string.IsNullOrWhiteSpace(connectionString)) connectionString = configuration.GetConnectionString("ClientDatabase");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("ClientDatabase is required for officer selection.");
            await using var connection = new SqlConnection(connectionString);
            await using var command = BuildResolveCommand(oid, selection);
            command.Connection = connection;
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await ReadOfficerAsync(reader, selection, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

    internal static SqlCommand BuildResolveCommand(string oid, RoboCallOfficerSelection selection)
    {
        AutoCallContactService.ValidateOid(oid);
        if (selection.Type is not ("Primary" or "RoleBased"))
            throw new InvalidOperationException("Only Primary or RoleBased officers require resolution.");
        var command = new SqlCommand("dbo.activealarms_ResolveRoboCallOfficer") { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
        command.Parameters.Add("@oid", SqlDbType.VarChar, 20).Value = oid;
        command.Parameters.Add("@OfficerType", SqlDbType.VarChar, 20).Value = selection.Type;
        command.Parameters.Add("@RoleID", SqlDbType.Int).Value = selection.RoleID is int id ? id : DBNull.Value;
        command.Parameters.Add("@RolePOGroup", SqlDbType.VarChar, 20).Value = (object?)selection.RolePOGroup ?? DBNull.Value;
        return command;
    }

    internal static async Task<RoboCallOfficer> ReadOfficerAsync(DbDataReader reader, RoboCallOfficerSelection selection, CancellationToken token)
    {
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("No eligible officer was returned for Officer Robo Call.");
        int id = reader.GetInt32(reader.GetOrdinal("officer_id"));
        int nameOrdinal = reader.GetOrdinal("officer_name");
        string? name = reader.IsDBNull(nameOrdinal) ? null : reader.GetString(nameOrdinal);
        if (id <= 0 || string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new InvalidOperationException("Officer lookup requires a positive officer_id and nonblank officer_name (at most 200 characters).");
        if (await reader.ReadAsync(token).ConfigureAwait(false) || await reader.NextResultAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Officer lookup must return exactly one result set with one officer.");
        return new RoboCallOfficer(selection, id, name);
    }

    public async Task<IReadOnlyList<AutoCallPhone>> ReadPhonesAsync(string connectionString, int officerId, CancellationToken cancellationToken)
        => await DatabaseDiagnostics.ExecuteAsync(logger, "TenantDatabase", "dbo.activealarms_GetAllOfficerPhonenumbers", cancellationToken, async () =>
        {
            if (officerId <= 0) throw new ArgumentOutOfRangeException(nameof(officerId));
            if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("A routed tenant database is required for officer phones.");
            await using var connection = new SqlConnection(connectionString);
            await using var command = BuildPhonesCommand(officerId);
            command.Connection = connection;
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var phones = await AutoCallPhoneRepository.ReadPhonesAsync(reader, cancellationToken).ConfigureAwait(false);
            if (phones.Count > 1 || phones.Any(phone => phone.Type != "CellPhone") || await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Officer phones must return a single result set with at most one CellPhone.");
            return phones;
        }).ConfigureAwait(false);

    internal static SqlCommand BuildPhonesCommand(int officerId)
    {
        if (officerId <= 0) throw new ArgumentOutOfRangeException(nameof(officerId));
        var command = new SqlCommand("dbo.activealarms_GetAllOfficerPhonenumbers") { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
        command.Parameters.Add("@OfficerID", SqlDbType.Int).Value = officerId;
        return command;
    }
}
