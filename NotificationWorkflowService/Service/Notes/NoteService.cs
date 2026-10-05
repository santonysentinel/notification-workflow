using NotificationWorkflowService.Repository;

namespace NotificationWorkflowService.Service.Notes;

/// <summary>
/// Hydrates then stores without a transaction, local retries or deduplication key.
/// At-least-once caller replay is supported (duplicates allowed), not guaranteed here.
/// Note text is never logged or included in service-generated exception messages.
/// </summary>
public sealed class NoteService : INoteService
{
    private readonly IRepository repository;
    private readonly INoteLocationProvider locationProvider;
    private readonly INoteAddressResolver addressResolver;

    public NoteService(IRepository repository, INoteLocationProvider locationProvider, INoteAddressResolver addressResolver)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(locationProvider);
        ArgumentNullException.ThrowIfNull(addressResolver);
        this.repository = repository;
        this.locationProvider = locationProvider;
        this.addressResolver = addressResolver;
    }

    public async Task AddNoteAsync(string template, string oid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Parse before provider access or repository configuration/operation preparation.
        var renderer = NoteTemplateRenderer.Parse(template);
        ArgumentNullException.ThrowIfNull(oid);
        if (string.IsNullOrWhiteSpace(oid) || oid.Length > 20)
            throw new ArgumentException("OID must be nonblank and at most 20 characters.", nameof(oid));

        decimal? latitude = null;
        decimal? longitude = null;
        string? address = null;
        if (renderer.RequiresLocation)
        {
            var location = await locationProvider.GetLocationAsync(oid, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Normalize each invalid coordinate independently; never geocode a partial pair.
            latitude = location?.Latitude is >= -90m and <= 90m ? location.Latitude : null;
            longitude = location?.Longitude is >= -180m and <= 180m ? location.Longitude : null;
            if (renderer.RequiresAddress && latitude.HasValue && longitude.HasValue)
            {
                address = await addressResolver.ResolveAddressAsync(latitude.Value, longitude.Value, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        var noteText = renderer.Render(latitude, longitude, address);
        cancellationToken.ThrowIfCancellationRequested();
        await using var operation = repository.PrepareAddNote(noteText, oid);
        await operation.OpenAsync(cancellationToken).ConfigureAwait(false);
        // SET NOCOUNT ON can return -1 on success: affected-row count is not a success predicate.
        _ = await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // SQL may already have committed; cancellation here can lead to duplicates on caller replay.
        cancellationToken.ThrowIfCancellationRequested();
    }
}