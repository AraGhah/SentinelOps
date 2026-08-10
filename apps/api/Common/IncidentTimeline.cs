using System.Text.Json;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Common;

// Static rather than an injectable service (like IAuditLogger) so it can be
// called both from API controllers, which have a DbContext from DI, and from
// worker Lambdas, which build one per message via WorkerDbContextFactory —
// neither has to stand up a second DI-registered type just for this. Callers
// are responsible for their own SaveChangesAsync; this only stages the row,
// same convention as IncidentStatusHistory inserts, so the timeline entry
// commits atomically with whatever business change it's describing.
public static class IncidentTimeline
{
    public static void Record(
        SentinelOpsDbContext db, Guid organizationId, Guid incidentId, IncidentEventType eventType,
        Guid? actorUserId, string? summary = null, object? details = null)
    {
        db.IncidentEvents.Add(new IncidentEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            IncidentId = incidentId,
            EventType = eventType,
            ActorUserId = actorUserId,
            Summary = summary,
            Details = details is null ? null : JsonSerializer.Serialize(details),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });
    }
}
