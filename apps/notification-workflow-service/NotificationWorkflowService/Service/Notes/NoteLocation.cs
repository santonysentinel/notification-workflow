namespace NotificationWorkflowService.Service.Notes;

/// <summary>A single immutable location snapshot; null coordinates mean unavailable data.</summary>
public sealed record NoteLocation(decimal? Latitude, decimal? Longitude);