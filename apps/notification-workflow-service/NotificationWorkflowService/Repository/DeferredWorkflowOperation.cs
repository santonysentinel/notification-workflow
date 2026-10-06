using System.Data.Common;

namespace NotificationWorkflowService.Repository;

/// <summary>
/// Keeps preparation and argument evaluation inside the caller's original setup catch,
/// but owns the operation until after that catch, including explicit CloseAsync calls.
/// Preparation happens once before retries; OpenAsync never prepares or resets.
/// </summary>
internal sealed class DeferredWorkflowOperation(Func<IWorkflowOperation> prepare) : IWorkflowOperation
{
    private IWorkflowOperation? operation;

    internal IWorkflowOperation Prepare() => operation ??= prepare();

    public Task OpenAsync(CancellationToken cancellationToken = default) =>
        (operation ?? throw new InvalidOperationException("Operation has not been prepared.")).OpenAsync(cancellationToken);
    public Task<DbDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default) =>
        (operation ?? throw new InvalidOperationException("Operation has not been prepared.")).ExecuteReaderAsync(cancellationToken);
    public Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default) =>
        (operation ?? throw new InvalidOperationException("Operation has not been prepared.")).ExecuteNonQueryAsync(cancellationToken);
    public Task CloseAsync() => operation?.CloseAsync() ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => operation?.DisposeAsync() ?? ValueTask.CompletedTask;
}