using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Service.AutoCall;

namespace NotificationWorkflowService.Repository;

public sealed class AutoCallQueueRepository(IConfiguration configuration) : IAutoCallQueueRepository
{
    private SqlConnection Connection()
    {
        string? value = configuration.GetConnectionString("AutoCallDB");
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("ConnectionStrings:AutoCallDB is required for auto-call queue access.");
        return new SqlConnection(value);
    }

    public async Task<AutoCallFlow> ReadFlowAsync(int flowId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (flowId <= 0) throw new ArgumentOutOfRangeException(nameof(flowId));
        await using var connection = Connection();
        await using var command = new SqlCommand("SELECT SystemID,TemplateJSON,TwiML FROM dbo.CallFlowTemplates WHERE SystemID=@FlowId AND DeletedDateTime IS NULL", connection) { CommandTimeout = 30 };
        command.Parameters.Add("@FlowId", SqlDbType.Int).Value = flowId;
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(1) || reader.IsDBNull(2))
            throw new InvalidOperationException("Published call flow with graph and TwiML is required.");
        return new AutoCallFlow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
    }

    public async Task<int> EnqueueAsync(AutoCallRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = Connection();
        await using var command = BuildEnqueueCommand(request);
        command.Connection = connection;
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not int jobId || jobId <= 0) throw new InvalidOperationException("Auto-call enqueue did not return a job identity.");
        return jobId;
    }

    internal static SqlCommand BuildEnqueueCommand(AutoCallRequest request)
    {
        var command = new SqlCommand("dbo.activealarms_call_InsertJob") { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
        command.Parameters.Add("@ActiveAlarmID", SqlDbType.Int).Value = request.ActiveAlarmId;
        command.Parameters.Add("@OID", SqlDbType.VarChar, 20).Value = request.Oid;
        command.Parameters.Add("@FlowId", SqlDbType.Int).Value = request.FlowId;
        command.Parameters.Add("@VariablesJSON", SqlDbType.NVarChar, -1).Value = request.VariablesJson;
        command.Parameters.Add("@EnqueueIdempotencyKey", SqlDbType.UniqueIdentifier).Value = request.EnqueueIdempotencyKey;
        return command;
    }
}