namespace SentinelOps.Events;

// Mirrors SentinelOps.Api.Domain.IncidentSeverity. Event contracts don't reference
// internal EF entity types directly; SentinelOps.Api maps between the two at the publish boundary.
public enum Severity { Critical = 0, High = 1, Medium = 2, Low = 3 }
