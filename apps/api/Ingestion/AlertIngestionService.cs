using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Ingestion;

public enum TimestampValidity { Valid, TooFarInFuture, TooOld }

public record IngestionResult(Guid AlertId, Guid CorrelationId, bool WasDuplicate);

// Shared by both the public, API-key-authenticated ingestion endpoint (after it
// has verified the request signature) and the internal "send test event" action
// on IntegrationsController (which is already authenticated as an org
// administrator and has no signature to check). Owns everything downstream of
// "this request is who it claims to be": idempotency, correlation ids, raw
// storage, and handing the alert off to the queue.
public interface IAlertIngestionService
{
    TimestampValidity ValidateTimestamp(DateTimeOffset timestampUtc);

    Task<IngestionResult> IngestAsync(
        Guid organizationId, Guid integrationId, IngestAlertRequest request, string rawPayload,
        string idempotencyKey, CancellationToken ct);
}

public class AlertIngestionService(SentinelOpsDbContext db, IAlertQueuePublisher queuePublisher) : IAlertIngestionService
{
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public TimestampValidity ValidateTimestamp(DateTimeOffset timestampUtc)
    {
        var now = DateTimeOffset.UtcNow;
        if (timestampUtc > now + MaxFutureSkew) return TimestampValidity.TooFarInFuture;
        if (timestampUtc < now - MaxAge) return TimestampValidity.TooOld;
        return TimestampValidity.Valid;
    }

    public async Task<IngestionResult> IngestAsync(
        Guid organizationId, Guid integrationId, IngestAlertRequest request, string rawPayload,
        string idempotencyKey, CancellationToken ct)
    {
        var existing = await db.IngestionRequestRecords
            .FirstOrDefaultAsync(r => r.IntegrationId == integrationId && r.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null)
        {
            return new IngestionResult(existing.AlertId, existing.CorrelationId, WasDuplicate: true);
        }

        var alertId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var alert = new Alert
        {
            Id = alertId,
            OrganizationId = organizationId,
            IntegrationId = integrationId,
            ExternalId = request.ExternalId,
            Source = request.Source,
            Title = request.Title,
            Description = request.Description,
            Severity = request.Severity,
            TimestampUtc = request.TimestampUtc,
            Environment = request.Environment,
            Region = request.Region,
            Metadata = request.Metadata,
            RawPayload = rawPayload,
            CorrelationId = correlationId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        // Publish before persisting: if the queue is unreachable, nothing about
        // this request should be considered "stored" either, so the caller (and
        // any client-side retry) sees a clean failure rather than a stored alert
        // that silently never made it onto the queue.
        await queuePublisher.PublishAsync(new AlertQueueMessage(
            alert.Id, organizationId, integrationId, correlationId, alert.ExternalId, alert.Source, alert.Title,
            alert.Severity, alert.TimestampUtc, alert.Environment, alert.Region), ct);

        db.Alerts.Add(alert);
        db.IngestionRequestRecords.Add(new IngestionRequestRecord
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            IntegrationId = integrationId,
            IdempotencyKey = idempotencyKey,
            CorrelationId = correlationId,
            AlertId = alertId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);

        return new IngestionResult(alertId, correlationId, WasDuplicate: false);
    }
}
