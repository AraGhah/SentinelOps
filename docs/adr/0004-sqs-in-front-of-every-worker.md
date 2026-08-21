# ADR 0004: SQS in front of every worker

## Status

Accepted

## Context

EventBridge can invoke Lambda directly as a rule target, skipping a queue
entirely. That's simpler, but EventBridge's own retry policy is coarser than
SQS's, and it has no dead-letter/redrive story as good as an SQS
queue+DLQ+`maxReceiveCount` pair with a visibility timeout tuned per worker.
Alert-processing failures need to be retried a bounded number of times, then
land somewhere a human can inspect and replay them from — not silently
dropped or retried forever.

## Decision

Every EventBridge rule targets an SQS queue, and every worker Lambda
consumes from that queue via `SqsEventSource`, not `events.Rule` invoking
the Lambda directly. Each queue has its own DLQ with
`maxReceiveCount: 5` (`event-processing-stack.ts`), and every Lambda's event
source sets `reportBatchItemFailures: true` — enforced end-to-end by
`SqsBatchProcessor` (`SentinelOps.Workers.Shared`), which turns one bad
message in a batch of 10 into a single reported batch-item failure instead
of retrying (and eventually dead-lettering) the whole batch, including
messages that already succeeded.

## Consequences

- A stuck message is visible and actionable: it shows up in a specific
  queue's DLQ, with a specific `dlq-not-empty` alarm
  (`ObservabilityStack`) pointing at exactly which stage failed, and can be
  replayed from the DLQ once the underlying issue (a code bug, a DB outage)
  is fixed — see `docs/deployment/rollback.md`/the interview-demo checklist
  item "replay a failed message from the DLQ."
- Every worker pays one extra hop of latency (EventBridge → SQS → Lambda,
  not EventBridge → Lambda) in exchange for that retry/DLQ story — an
  explicit latency-for-durability tradeoff, acceptable here since nothing in
  this pipeline is synchronous from the alert-sender's point of view (the
  ingestion endpoint already returns 202 before any of this runs).
- Batch-size 10 + `reportBatchItemFailures` means one worker invocation can
  partially succeed — every worker's `HandleAsync` has to be safe to
  re-invoke per-message on retry (the idempotency-guard pattern used
  throughout `SentinelOps.Workers.Shared.IdempotencyGuard`), not just
  per-batch.
