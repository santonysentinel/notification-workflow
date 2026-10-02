using System.Data;

namespace NotificationWorkflowService.Repository
{
    /// <summary>
    /// One connection and command for the lifetime of a caller's retry loop.
    /// Open is deliberately not idempotent; failures do not reset or reopen the connection.
    /// Readers stream to the caller and are also owned by the operation.
    /// </summary>
    public interface IWorkflowOperation : IDisposable
    {
        void Open();
        IDataReader ExecuteReader();
        int ExecuteNonQuery();
        void Close();
    }
}