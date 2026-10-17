using System.Globalization;
using System.Text.Json;
using NotificationWorkflowService.Repository;

namespace NotificationWorkflowService.Service.Notes;

public sealed class ParticipantNoteLocationProvider(IParticipantCurrentDetailsRepository repository) : INoteLocationProvider
{
    public async Task<NoteLocation?> GetLocationAsync(string oid, int activeAlarmId, CancellationToken cancellationToken = default)
    {
        var details = await repository.ReadAsync(oid, activeAlarmId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new NoteLocation(Coordinate(details, "lat"), Coordinate(details, "lon"));
    }

    private static decimal? Coordinate(IReadOnlyDictionary<string, JsonElement> details, string name)
    {
        if (!details.TryGetValue(name, out var value))
            throw new InvalidOperationException($"Participant current details are missing '{name}'.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        throw new InvalidOperationException($"Participant current details contain an invalid '{name}'.");
    }
}
