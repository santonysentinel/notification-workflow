using System.Data.Common;

namespace NotificationWorkflowService.Repository
{
    /// <summary>
    /// One connection and command for the lifetime of a caller's retry loop.
    /// OpenAsync is deliberately not idempotent; failures do not reset or reopen the connection.
    /// Readers stream to the caller and are also owned by the operation.
    /// </summary>
    public interface IWorkflowOperation : IAsyncDisposable
    {
        Task OpenAsync(CancellationToken cancellationToken = default);
        Task<DbDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default);
        Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default);
        Task CloseAsync();
    }
}