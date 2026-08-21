# ADR 0005: Step Functions for incident escalation

## Status

Accepted

## Context

Escalation is a long-running, stateful wait loop: assign a responder, wait
up to the level's ack-timeout (configurable per escalation level, up to 24h),
check whether the incident was acknowledged/resolved, and if not, advance to
the next level (or notify a fallback administrator) and wait again — for as
long as a week in the worst case (every level maxed out). That state has to
survive for hours to days, independent of any single Lambda invocation's
15-minute ceiling, and has to be interruptible: an acknowledgement or a
reopen needs to affect an *already-running* escalation, not just a future
one.

## Decision

An AWS Step Functions standard state machine
(`EscalationStateMachine`, `event-processing-stack.ts`) — a
`Wait` → `CheckIncidentStatus` → `Choice` → `AdvanceEscalationLevel` →
`Choice` → back to `Wait` loop — rather than a Lambda scheduling its own
re-invocation (via EventBridge Scheduler or a self-requeuing SQS delay), or
a database-polled cron job.

Every task both accepts and returns `EscalationStateInput`'s shape
(`payloadResponseOnly: true`), so no state in the machine ever needs to
reshape or merge JSON paths — the Lambda (`EscalationFunction`) owns all the
actual state transition logic; Step Functions owns only the durable
wait/loop.

## Consequences

- The `Wait` state's duration is entirely data-driven
  (`sfn.WaitTime.secondsPath('$.ackTimeoutSeconds')`), so a policy's
  per-level ack-timeout can be edited without touching the state machine
  definition at all.
- An execution genuinely running for a week costs nothing extra for the
  `Wait` state itself (Standard Step Functions bills per-transition, not
  per-second-waited), which a Lambda-holds-its-own-timer approach could not
  offer without also paying for a held-open invocation.
- A reopened incident needs a *new* escalation, not a resumed old one — the
  old execution already terminated at `StoppedAcknowledgedOrResolved` when
  the incident first resolved. `EscalationRestartFunction` starts a fresh
  execution rather than trying to un-terminate the old one, which is
  simpler than it sounds precisely because Step Functions treats each
  execution as immutable once it reaches a terminal state.
- The whole execution has a 7-day `timeout` (`EscalationStateMachine`) as a
  safety net even if every level maxes out its ack-timeout — an explicit
  upper bound rather than trusting the sum of per-level timeouts to always
  be reasonable.
