# ADR 0003: EventBridge as the event backbone

## Status

Accepted

## Context

An alert flows through a fixed pipeline of independent stages (validate →
deduplicate → create/attach-to-incident → assign responder → notify), and
two more consumers (analytics, audit-log) need to observe every event type
without the publisher knowing they exist. New consumers get added over time
(see `AttachmentScan` reacting to GuardDuty findings on the *default* bus,
added after the original 9 workers existed). This needed pub/sub with
content-based routing, not point-to-point messaging.

## Decision

A custom EventBridge bus (`sentinelops-events`, not the account default
bus — see `event-processing-stack.ts`) as the fan-out layer, with rules
routing each `detail-type` to the SQS queue(s) that care about it. SQS sits
between EventBridge and every Lambda (rather than invoking Lambda directly
from an EventBridge target) specifically so retries/backpressure/DLQ
semantics are SQS's, not EventBridge's own (weaker) retry policy.

`infrastructure/event-schemas/*.schema.json` is the documented contract for
every `detail-type`'s wire shape, and `EventContractTests` (workers.Tests)
verifies the C# records serialize to something that actually validates
against those files — event-schema drift is caught in CI, not discovered by
a consumer at runtime.

## Consequences

- Adding a consumer is an EventBridge rule + SQS queue + Lambda, never a
  change to the publisher — `AllEventsToAnalyticsRule`/
  `AllEventsToAuditLogRule` are the clearest example, both listening to
  every event type without any producer knowing they exist.
- The custom bus is a real isolation boundary: rules here can safely use
  wildcard-ish multi-event patterns without accidentally matching unrelated
  account traffic, because nothing else publishes to this bus.
- One thing this event backbone deliberately does *not* handle: the
  deduplication → incident-creation handoff bypasses EventBridge entirely
  (a direct SQS `SendMessage`) — see the comment on `IncidentCreationRequest`
  in `EventDetails.cs` for why (avoids a create-incident race between two
  independent `alert.validated` listeners).
