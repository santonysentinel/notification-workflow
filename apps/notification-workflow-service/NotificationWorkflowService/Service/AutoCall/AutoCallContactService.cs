using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace NotificationWorkflowService.Service.AutoCall;

public interface IAutoCallRoutingStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
}

public interface IAutoCallPhoneRepository
{
    Task<IReadOnlyList<string>> ReadAsync(string connectionString, string oid, CancellationToken cancellationToken = default);
}

public sealed record AutoCallContacts(string Oid, string Platform, IReadOnlyList<string> PhoneNumbers);

public enum AutoCallRoutingFailure
{
    NotFound,
    InvalidRoutingData,
    DatabaseNotConfigured,
    Unavailable
}

public sealed class AutoCallRoutingException : Exception
{
    public AutoCallRoutingFailure Failure { get; }

    public AutoCallRoutingException(AutoCallRoutingFailure failure)
        : base($"Auto-call routing failed: {failure}.") => Failure = failure;
}

public sealed class AutoCallContactService
{
    private readonly IAutoCallRoutingStore routingStore;
    private readonly IAutoCallPhoneRepository phoneRepository;
    private readonly IConfiguration configuration;

    public AutoCallContactService(IAutoCallRoutingStore routingStore, IAutoCallPhoneRepository phoneRepository, IConfiguration configuration)
    {
        this.routingStore = routingStore;
        this.phoneRepository = phoneRepository;
        this.configuration = configuration;
    }

    public async Task<AutoCallContacts> ResolveAsync(string oid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOid(oid);
        string? routing;
        try
        {
            routing = await routingStore.GetAsync($"dna.client.{oid.ToUpperInvariant()}", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AutoCallRoutingException(AutoCallRoutingFailure.Unavailable);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (routing is null) throw new AutoCallRoutingException(AutoCallRoutingFailure.NotFound);
        string platform = ParsePlatform(routing);
        string? connectionString = configuration.GetConnectionString(platform);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new AutoCallRoutingException(AutoCallRoutingFailure.DatabaseNotConfigured);
        var phones = await phoneRepository.ReadAsync(connectionString, oid, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new AutoCallContacts(oid, platform, phones);
    }

    internal static void ValidateOid(string oid)
    {
        if (string.IsNullOrWhiteSpace(oid) || oid.Length > 20 || oid != oid.Trim())
            throw new ArgumentException("OID must be nonblank, at most 20 characters and without surrounding whitespace.", nameof(oid));
    }

    internal static string ParsePlatform(string routing)
    {
        string? platform;
        try
        {
            using var document = JsonDocument.Parse(routing);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("platform", out var value) || value.ValueKind != JsonValueKind.String)
                throw new AutoCallRoutingException(AutoCallRoutingFailure.InvalidRoutingData);
            platform = value.GetString();
        }
        catch (JsonException)
        {
            platform = routing;
        }
        platform = platform?.Trim();
        if (string.IsNullOrEmpty(platform) || platform.Length > 100 ||
            platform.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_' && character != '-'))
            throw new AutoCallRoutingException(AutoCallRoutingFailure.InvalidRoutingData);
        return platform;
    }
}