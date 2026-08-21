using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;

namespace SentinelOps.Workers.Shared;

public enum ClaimState
{
    // No prior row for (workerName, eventId) — proceed with business logic.
    Claimed,

    // Row exists and is Completed — publish already succeeded on a prior
    // attempt. Duplicate delivery: no-op.
    AlreadyCompleted,

    // Row exists but not Completed — a prior attempt committed business
    // writes and captured PendingOutboxJson but crashed before publishing.
    // Don't re-run business logic; replay PendingOutboxJson through
    // OutboxPublisher and call CompleteAsync.
    PendingCompletion,
}

// SQS is at-least-once, so each worker tracks its own claims by
// (workerName, eventId) — the same EventId can be new to one worker and
// already handled by another.
//
// Two-phase completion: TryClaimAsync reserves the work. SaveChangesAsync
// commits business writes + PendingOutboxJson with Completed still false.
// CompleteAsync flips it to true only after the outbound publish succeeds —
// this closes the gap where a crash between commit and publish would
// otherwise leave the publish permanently skipped.
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

    // Atomic UPDATE, independent of the change tracker — safe to call right
    // after publish succeeds without re-saving other tracked state.
    public static Task CompleteAsync(SentinelOpsDbContext db, string workerName, Guid eventId, CancellationToken ct) =>
        db.ProcessedWorkerEvents
            .Where(e => e.WorkerName == workerName && e.EventId == eventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Completed, true)
                .SetProperty(e => e.PendingOutboxJson, (string?)null), ct);
}
