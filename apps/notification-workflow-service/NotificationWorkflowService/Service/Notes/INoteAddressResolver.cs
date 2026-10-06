namespace NotificationWorkflowService.Service.Notes;

/// <summary>Resolves valid coordinates; null means unavailable. Failures and cancellation propagate.</summary>
public interface INoteAddressResolver
{
    Task<string?> ResolveAddressAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken = default);
}