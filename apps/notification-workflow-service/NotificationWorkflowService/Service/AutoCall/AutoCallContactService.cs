using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationWorkflowService.Repository;

namespace NotificationWorkflowService.Service.AutoCall;

public interface IAutoCallRoutingStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
}

public interface IAutoCallPhoneRepository
{
    Task<IReadOnlyList<AutoCallPhone>> ReadAsync(string connectionString, string oid, CancellationToken cancellationToken = default);
}

/// <summary>A participant phone from the tenant lookup: device type (HomePhone, CellPhone, WorkPhone) and number.</summary>
public sealed record AutoCallPhone(string Type, string Number);

public sealed record AutoCallContacts(string Oid, string Platform, IReadOnlyList<AutoCallPhone> Phones)
{
    public IReadOnlyList<string> PhoneNumbers => Phones.Select(phone => phone.Number).ToArray();
}

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
    private readonly ILogger<AutoCallContactService> logger;
    private readonly IAutoCallOfficerRepository? officerRepository;

    public AutoCallContactService(IAutoCallRoutingStore routingStore, IAutoCallPhoneRepository phoneRepository, IConfiguration configuration,
        ILogger<AutoCallContactService>? logger = null, IAutoCallOfficerRepository? officerRepository = null)
    {
        this.routingStore = routingStore;
        this.phoneRepository = phoneRepository;
        this.configuration = configuration;
        this.logger = logger ?? NullLogger<AutoCallContactService>.Instance;
        this.officerRepository = officerRepository;
    }

    public async Task<AutoCallContacts> ResolveAsync(string oid, CancellationToken cancellationToken = default)
        => await ResolveAsync(oid, null, cancellationToken).ConfigureAwait(false);

    public async Task<AutoCallContacts> ResolveAsync(string oid, RoboCallOfficer? officer, CancellationToken cancellationToken = default,
        bool readOfficerPhone = true)
    {
        var (platform, connectionString) = await ResolveRouteAsync(oid, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AutoCallPhone> phones;
        if (officer is null)
            phones = await phoneRepository.ReadAsync(connectionString, oid, cancellationToken).ConfigureAwait(false);
        else if (officer.Selection.Type == "Custom" || !readOfficerPhone)
            phones = [];
        else
            phones = await (officerRepository ?? throw new InvalidOperationException("Officer phone repository is required."))
                .ReadPhonesAsync(connectionString, officer.Id ?? throw new InvalidOperationException("A pinned officer ID is required."), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new AutoCallContacts(oid, platform, phones.Select(phone => phone with { Number = NormalizePhoneNumber(phone.Number) }).ToArray());
    }

    private async Task<(string Platform, string ConnectionString)> ResolveRouteAsync(string oid, CancellationToken cancellationToken)
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
        catch (Exception exception)
        {
            AutoCallDiagnostics.Failure(logger, "Redis routing lookup", exception,
                cancellationReason: exception is OperationCanceledException ? "Redis lookup deadline exceeded or dependency canceled." : null);
            throw new AutoCallRoutingException(AutoCallRoutingFailure.Unavailable);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (routing is null) throw new AutoCallRoutingException(AutoCallRoutingFailure.NotFound);
        string platform = ParsePlatform(routing);
        string? connectionString = configuration.GetConnectionString(platform);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new AutoCallRoutingException(AutoCallRoutingFailure.DatabaseNotConfigured);
        return (platform, connectionString);
    }

    internal static string NormalizePhoneNumber(string phone)
    {
        string number = string.Concat(phone.Where(character =>
            !char.IsWhiteSpace(character) && character is not ('(' or ')' or '-' or '.')));
        if (number.Length == 10 && number.All(char.IsAsciiDigit))
            return "+1" + number;
        if (number.Length == 11 && number[0] == '1' && number.All(char.IsAsciiDigit))
            return "+" + number;
        return number;
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
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new AutoCallRoutingException(AutoCallRoutingFailure.InvalidRoutingData);
            platform = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "platform", StringComparison.OrdinalIgnoreCase)) continue;
                if (platform is not null || property.Value.ValueKind != JsonValueKind.String)
                    throw new AutoCallRoutingException(AutoCallRoutingFailure.InvalidRoutingData);
                platform = property.Value.GetString();
            }
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