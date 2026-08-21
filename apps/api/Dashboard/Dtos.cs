using SentinelOps.Api.Domain;
using SentinelOps.Api.Services;

namespace SentinelOps.Api.Dashboard;

// Rehydrates a stale dashboard after a WebSocket reconnect or initial page load;
// the REST counterpart to what BroadcastFunction pushes incrementally.
public record DashboardSummaryResponse(
    int ActiveIncidentCount,
    IReadOnlyDictionary<string, int> ActiveIncidentsBySeverity,
    IReadOnlyList<IncidentSummary> RecentlyResolvedIncidents,
    double? MeanAcknowledgementTimeSeconds,
    double? MeanResolutionTimeSeconds,
    int AlertVolumeLast24Hours,
    IReadOnlyList<ServiceHealthResponse> ServiceHealth);

public record IncidentSummary(
    Guid Id, string Title, IncidentSeverity Severity, IncidentStatus Status, Guid? ServiceId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? AcknowledgedAtUtc, DateTimeOffset? ResolvedAtUtc);
