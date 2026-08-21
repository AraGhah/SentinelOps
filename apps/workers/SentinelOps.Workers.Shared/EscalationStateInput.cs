namespace SentinelOps.Workers.Shared;

// The Step Functions execution's evolving state. Every task in the
// escalation state machine accepts and returns this same shape, so a task's
// output becomes the next state's input verbatim.
//
// Action picks which branch the Lambda runs: "CheckStatus" (has the incident
// moved past Triggered/Assigned?) or "AdvanceLevel" (notify the next level
// or fallback administrator, or stop if exhausted).
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
