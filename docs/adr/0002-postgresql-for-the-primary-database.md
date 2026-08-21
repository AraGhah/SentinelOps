# ADR 0002: PostgreSQL for the primary database

## Status

Accepted

## Context

The domain is relational and multi-tenant: organizations, memberships,
services, integrations, incidents, escalation policies, schedules, audit
logs — all foreign-keyed to each other and all needing to be provably scoped
to one organization at a time (see `docs/security/threat-model.md`'s
cross-tenant-access-attempt scenario). Deduplication (`DynamoDB`, ADR 0006)
and event delivery (`SQS`/`EventBridge`) are handled elsewhere; this is the
system of record for everything else.

## Decision

PostgreSQL via Aurora Serverless v2, accessed through EF Core, not DynamoDB,
MySQL, or a self-managed Postgres on EC2.

Tenant isolation is enforced with EF Core global query filters
(`HasQueryFilter` scoped to the current organization id). Only a real
relational engine with a query planner makes this free to apply
transparently on every query; a NoSQL store would need that filter
re-implemented by every caller instead of centralized once. We picked
Aurora Serverless v2 over provisioned Aurora because traffic is bursty and
unpredictable across three environments with very different load
(`aurora.minCapacityAcu`/`maxCapacityAcu` per environment in
`environments.ts`), and scaling ACUs is cheaper to operate than manually
right-sizing instance classes per environment.

## Consequences

- EF Core migrations are the schema's source of truth (`apps/api/Migrations`)
  — a schema change is a migration, not an out-of-band DDL script.
- Aurora Serverless v2 has a nonzero ACU floor (see `docs/costs.md`) — it
  never scales to zero the way DynamoDB on-demand or Lambda do, which is
  part of why dev's `stop-dev-resources.sh` only pauses ECS, not the
  database.
- Every worker and the API share one cluster (`DbConnectionStringResolver`
  reads the same Secrets Manager secret) — connection count is a real
  capacity constraint, which is why `reservedConcurrentExecutions` is capped
  conservatively on every worker Lambda (`event-processing-stack.ts`) and
  why `database-connections-high` is one of the few alarms watching a
  specific numeric ceiling rather than an error-rate signal.
