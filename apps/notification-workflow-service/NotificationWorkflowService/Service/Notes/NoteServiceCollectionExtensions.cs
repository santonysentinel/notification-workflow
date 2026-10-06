using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NotificationWorkflowService.Service.Notes;

public static class NoteServiceCollectionExtensions
{
    /// <summary>
    /// Opt-in registration; the host must already register IRepository. Providers registered
    /// before this call are preserved, allowing mocks or future real implementations.
    /// Priority 18 is wired separately through parser adapters; these defaults are not
    /// a production location lookup or geocoder.
    /// </summary>
    public static IServiceCollection AddNoteServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<INoteLocationProvider, UnavailableNoteLocationProvider>();
        services.TryAddSingleton<INoteAddressResolver, UnavailableNoteAddressResolver>();
        services.TryAddTransient<INoteService, NoteService>();
        return services;
    }
}