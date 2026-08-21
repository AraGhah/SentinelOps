using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Reports;

// Best-effort prefill, not a substitute for a human-written report: UpdateReviewStatus
// still requires an Administrator to move it past Draft.
public static class ReportDraftBuilder
{
    public static (string Summary, string? DetectionDetails, string? ResolutionDetails) Build(
        Incident incident, List<IncidentEvent> timeline)
    {
        var ordered = timeline.OrderBy(e => e.OccurredAtUtc).ToList();

        var summary = $"Incident \"{incident.Title}\" ({incident.Severity}) was triggered at {incident.CreatedAtUtc:u}"
            + (incident.ResolvedAtUtc is { } resolved ? $" and resolved at {resolved:u}." : " and is not yet resolved.");

        var detectionEvent = ordered.FirstOrDefault(e => e.EventType is IncidentEventType.Created or IncidentEventType.NotificationSent);
        var detectionDetails = detectionEvent is null
            ? null
            : $"Detected via event \"{detectionEvent.EventType}\" at {detectionEvent.OccurredAtUtc:u}."
                + (detectionEvent.Summary is null ? "" : $" {detectionEvent.Summary}");

        var resolutionEvent = ordered.LastOrDefault(e => e.EventType == IncidentEventType.Resolved);
        var resolutionDetails = resolutionEvent is null
            ? null
            : $"Resolved at {resolutionEvent.OccurredAtUtc:u}." + (resolutionEvent.Summary is null ? "" : $" {resolutionEvent.Summary}");

        return (summary, detectionDetails, resolutionDetails);
    }
}
