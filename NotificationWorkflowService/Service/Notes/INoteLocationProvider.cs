namespace NotificationWorkflowService.Service.Notes;

/// <summary>
/// Supplies one location snapshot for an OID. Null means unavailable, not a provider failure.
/// Unexpected failures and cancellation must propagate rather than being converted to missing data.
/// </summary>
public interface INoteLocationProvider
{
    Task<NoteLocation?> GetLocationAsync(string oid, CancellationToken cancellationToken = default);
}