namespace NotificationWorkflowService.Service.Notes;

/// <summary>Honest default: no geocoder is implemented.</summary>
public sealed class UnavailableNoteAddressResolver : INoteAddressResolver
{
    public Task<string?> ResolveAddressAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}