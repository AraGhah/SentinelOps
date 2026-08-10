namespace SentinelOps.Workers.Shared;

// The Step Functions execution's evolving state. Every task in the escalation
// state machine (SentinelOps.Workers.Escalation) both accepts and returns this
// same shape, so the ASL definition never needs to restructure JSON between
// states — a task's output becomes the next state's input verbatim.
//
// Action is set by the state machine's task definition (not by the caller
// that starts the execution) to pick which branch the Lambda runs:
// "CheckStatus" (has the incident moved past Triggered/Assigned?) or
// "AdvanceLevel" (notify the next escalation level, or the fallback
// administrator, or stop if both are exhausted).
public record EscalationStateInput(
    string Action,
    Guid OrganizationId,
    Guid IncidentId,
    Guid CorrelationId,
    Guid EscalationPolicyId,
    int CurrentLevelOrder,
    int AckTimeoutSeconds,
    bool FallbackNotified,
    bool AcknowledgedOrResolved = false,
    bool Stop = false);
