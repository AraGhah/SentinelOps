using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;

namespace SentinelOps.Workers.Shared;

// SQS is at-least-once delivery, so every worker must tolerate seeing the same
// message more than once. Each worker checks/records against this table under
// its own name, so the same EventId can independently be "new" to one worker
// and "already handled" to another.
public static class IdempotencyGuard
{
    // Returns false (already processed — caller should no-op) or true (not seen
    // before; a ProcessedWorkerEvent row has been added to `db`'s change tracker,
    // ready to be committed in the same SaveChangesAsync as the worker's other
    // side effects, so the "processed" marker and the actual work land atomically).
    public static async Task<bool> TryClaimAsync(SentinelOpsDbContext db, string workerName, Guid eventId, CancellationToken ct)
    {
        var alreadyProcessed = await db.ProcessedWorkerEvents
            .AnyAsync(e => e.WorkerName == workerName && e.EventId == eventId, ct);
        if (alreadyProcessed) return false;

        db.ProcessedWorkerEvents.Add(new ProcessedWorkerEvent
        {
            Id = Guid.NewGuid(),
            WorkerName = workerName,
            EventId = eventId,
            ProcessedAtUtc = DateTimeOffset.UtcNow,
        });

        return true;
    }
}
