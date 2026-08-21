using System.Text.Json;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Common;

// Static so it works from both API controllers (DI-provided DbContext) and worker
// Lambdas (WorkerDbContextFactory). Only stages the row; callers call SaveChangesAsync
// so the timeline entry commits atomically with the business change it describes.
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
