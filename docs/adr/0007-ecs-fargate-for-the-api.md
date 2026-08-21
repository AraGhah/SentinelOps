# ADR 0007: ECS Fargate for the API

## Status

Accepted

## Context

The API holds a warm Npgsql connection pool, runs a `BackgroundService`
(`DatabaseConnectionsMetricService`) that needs to keep running between
requests, and serves synchronous, latency-sensitive traffic (a user waiting
on a page load, not an async event). It also needs to sit inside the VPC
next to Aurora, reachable through an ALB with autoscaling on request count
and CPU.

## Decision

ECS Fargate (`api-stack.ts`), not Lambda-behind-API-Gateway, and not
self-managed EC2.

The deciding factor over Lambda specifically: a Lambda-per-request API would
either need a connection pooler in front of Aurora (RDS Proxy or similar) to
avoid exhausting `max_connections` under concurrent invocations, or accept
cold-start latency on every scale-up — both solvable, but both add
complexity this app doesn't need when the actual traffic shape (a
human-facing dashboard's request volume) suits a small number of long-lived
containers better than a large number of short-lived functions. Fargate over
EC2 specifically to avoid operating and patching the underlying instances —
this is a portfolio-scale system, not one with dedicated infra headcount.

## Consequences

- `NpgsqlDataSourceBuilder` builds one shared connection pool per task
  (`Program.cs`), reused across every request that task handles — the thing
  a per-request Lambda model would have made much harder to get right.
- Autoscaling is `apiDesiredCount` → `apiMaxCapacity` on CPU/request-count
  pressure (`environments.ts`), not concurrency-based the way Lambda scales
  — slower to react to a sudden spike, faster/cheaper for the API's actual
  steady-ish traffic pattern.
- The API and every worker still share one Aurora cluster
  (`DbConnectionStringResolver`), so ECS's smaller, bounded set of
  long-lived connections is easier to reason about against Aurora's
  `max_connections` ceiling than however many concurrent Lambda invocations
  an API-Gateway-fronted API might have needed.
- This is the inverse tradeoff from the workers (ADR 0008) — deliberately:
  the API's traffic is synchronous and connection-pool-sensitive; the
  workers' traffic is async, bursty, and per-message-independent.
