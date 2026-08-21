# ADR 0006: DynamoDB for alert deduplication

## Status

Accepted

## Context

100 identical alerts arriving within seconds of each other (the exact
scenario the load tests and the interview demo both exercise — see
`tests/load/alert-ingestion.js` and the "final interview demonstration"
checklist) must produce exactly one incident, not 100. That requires a
"claim this fingerprint, atomically, even under concurrent invocations of
possibly-different warm Lambda instances" primitive — a genuine
compare-and-swap/atomic-increment, not "check then write" with a race window
between the two steps.

## Decision

A DynamoDB table (`fingerprintTable`, `storage-stack.ts`) keyed by the
alert's computed fingerprint (`AlertFingerprint.Compute` —
org+title+error-code+service+environment), using an atomic `UpdateItem`
(`ADD AlertCount :incr`) to claim/increment in one call — not a PostgreSQL
row with `SELECT ... FOR UPDATE`, and not an in-memory/ElastiCache cache.

DynamoDB specifically because a single `UpdateItem` on one item is
atomic without any explicit locking, which is exactly the guarantee needed:
the first of N concurrent invocations to touch a fingerprint sees
`AlertCount == 1` and becomes the "winner" that creates the incident; every
other concurrent invocation sees `AlertCount > 1` with the incident ID still
`Pending` and reports itself as a batch item failure for redelivery (see
ADR 0004) rather than racing to create a second incident. A Postgres-based
version of this same guarantee would need an explicit row lock held across
the whole "check, maybe create incident, write back" sequence, which is far
easier to get subtly wrong under real concurrency than one atomic DynamoDB
call.

## Consequences

- `FakeFingerprintStore` (`workers.Tests/TestSupport.cs`) deliberately
  mirrors this atomicity with a single lock around every operation — not
  because DynamoDB needs a lock, but because the *test double* needs to be
  just as atomic as the real thing for
  `Handle_100SimultaneousIdenticalAlerts_ExactlyOneIsTreatedAsUnique` to be
  a meaningful test at all.
- Fingerprint records need their own TTL/lifecycle independent of the
  Postgres `Alert`/`Incident` rows they reference — DynamoDB's native TTL
  attribute handles automatic expiry without a separate cleanup job.
- This is the one piece of primary state that lives outside PostgreSQL
  (ADR 0002) — a deliberate exception because the atomicity guarantee it
  needs is DynamoDB's whole reason for existing here, not a general
  preference for NoSQL over relational storage in this system.
