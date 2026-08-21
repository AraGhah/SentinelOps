# ADR 0008: Lambda for event-processing workers

## Status

Accepted

## Context

Every worker (alert-validation, deduplication, incident-creation,
responder-assignment, notification, analytics, audit-log, attachment-scan,
escalation-restart, dashboard-broadcast) is triggered by SQS messages
arriving in bursts (a single alert can fan out to 10 messages across the
pipeline; 100 near-simultaneous alerts fan out to hundreds), with idle
periods in between. Each invocation is short, stateless, and independent of
every other — the opposite traffic shape from the API (ADR 0007).

## Decision

AWS Lambda (`.NET 10` runtime, `event-processing-stack.ts`), one function
per worker, each with its own SQS trigger — not a long-lived
ECS-service-per-worker consuming from the same queues.

`reservedConcurrentExecutions` is capped conservatively per worker
(5, most workers; 10 for the lighter observer workers) specifically because
every worker shares the same Aurora cluster the API uses — Lambda's
scale-to-the-backlog behavior is exactly what's wanted for bursty traffic,
capped low enough that a burst of messages can't open more Postgres
connections than the cluster can handle.

## Consequences

- Scales to zero between bursts — no idle compute cost for a worker that
  might process nothing for hours, unlike an always-on ECS service per
  worker would (see `docs/costs.md` — Lambda's share of the monthly
  estimate is under $1 even with 10 functions).
- Cold starts matter here in a way they don't for the API: `.NET 10` on
  Lambda has a real (if small) cold-start cost, mitigated by keeping each
  function's dependency surface narrow (worker-specific projects reference
  only `SentinelOps.Workers.Shared`/`SentinelOps.Events`, not the full API
  project) — see `SqsBatchProcessor`'s partial-batch-failure handling (ADR
  0004) for how a cold-start-induced slow invocation on one message doesn't
  take a whole batch down with it.
- `reservedConcurrentExecutions` is a hard ceiling — a large burst queues in
  SQS rather than opening unbounded concurrent Postgres connections. It's a
  throughput-vs-latency tradeoff: messages wait longer in the queue during a
  big burst so the shared database stays healthy, rather than processing
  everything instantly and risking connection exhaustion.
