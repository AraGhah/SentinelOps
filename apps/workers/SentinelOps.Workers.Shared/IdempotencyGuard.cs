using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;

namespace SentinelOps.Workers.Shared;

public enum ClaimState
{
    // No prior row for (workerName, eventId) — proceed with business logic.
    Claimed,

    // A row exists and is Completed — the outbound publish(es) already
    // succeeded on a prior attempt. Genuine duplicate delivery: no-op.
    AlreadyCompleted,

    // A row exists but is NOT Completed — a prior attempt committed its
    // business-state writes (and captured what still needs publishing in
    // PendingOutboxJson) but crashed/failed before the outbound publish
    // succeeded. Do NOT re-run business logic (it already landed, and
    // re-running it risks creating a duplicate record); instead replay
    // PendingOutboxJson through OutboxPublisher and call CompleteAsync.
    PendingCompletion,
}

// SQS is at-least-once delivery, so every worker must tolerate seeing the same
// message more than once. Each worker checks/records against this table under
// its own name, so the same EventId can independently be "new" to one worker
// and "already handled" to another.
//
// Two-phase completion: a claim on its own only reserves the work (Claimed).
// The business SaveChangesAsync commits business writes together with the
// claim row and its PendingOutboxJson, but leaves Completed = false — the
// outbound publish/send hasn't happened yet. Only after that publish succeeds
// does CompleteAsync flip Completed to true. This closes the gap where a
// crash between "business state committed" and "publish succeeded" used to
// make TryClaimAsync see the marker as already-handled and silently skip the
// publish forever.
public static class IdempotencyGuard
{
    public static async Task<(ClaimState State, ProcessedWorkerEvent Record)> TryClaimAsync(
        SentinelOpsDbContext db, string workerName, Guid eventId, CancellationToken ct)
    {
        var existing = await db.ProcessedWorkerEvents
            .FirstOrDefaultAsync(e => e.WorkerName == workerName && e.EventId == eventId, ct);

        if (existing is not null)
        {
            return (existing.Completed ? ClaimState.AlreadyCompleted : ClaimState.PendingCompletion, existing);
        }

        var created = new ProcessedWorkerEvent
        {
            Id = Guid.NewGuid(),
            WorkerName = workerName,
            EventId = eventId,
            ProcessedAtUtc = DateTimeOffset.UtcNow,
            Completed = false,
        };
        db.ProcessedWorkerEvents.Add(created);

        return (ClaimState.Claimed, created);
    }

    // Marks the claim Completed (and clears PendingOutboxJson) with a single
    // atomic UPDATE, independent of the DbContext's change tracker — safe to
    // call right after the outbound publish(es) succeed, without re-saving
    // (and potentially re-flushing stale tracked state for) anything else.
    public static Task CompleteAsync(SentinelOpsDbContext db, string workerName, Guid eventId, CancellationToken ct) =>
        db.ProcessedWorkerEvents
            .Where(e => e.WorkerName == workerName && e.EventId == eventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Completed, true)
                .SetProperty(e => e.PendingOutboxJson, (string?)null), ct);
}
