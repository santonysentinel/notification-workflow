using System.Data;

namespace NotificationWorkflowService.Repository;

/// <summary>
/// Keeps preparation and argument evaluation inside the caller's original setup catch,
/// but owns the operation until after that catch, including explicit Close calls.
/// Preparation happens once before retries; Open never prepares or resets.
/// </summary>
internal sealed class DeferredWorkflowOperation(Func<IWorkflowOperation> prepare) : IWorkflowOperation
{
    private IWorkflowOperation? operation;

    internal IWorkflowOperation Prepare() => operation ??= prepare();

    public void Open() => (operation ?? throw new InvalidOperationException("Operation has not been prepared.")).Open();
    public IDataReader ExecuteReader() => (operation ?? throw new InvalidOperationException("Operation has not been prepared.")).ExecuteReader();
    public int ExecuteNonQuery() => (operation ?? throw new InvalidOperationException("Operation has not been prepared.")).ExecuteNonQuery();
    public void Close() => operation?.Close();
    public void Dispose() => operation?.Dispose();
}