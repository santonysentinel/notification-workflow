namespace NotificationWorkflowService.Service.Notes;

/// <summary>Honest default: no database location lookup is implemented.</summary>
public sealed class UnavailableNoteLocationProvider : INoteLocationProvider
{
    public Task<NoteLocation?> GetLocationAsync(string oid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<NoteLocation?>(null);
    }
}