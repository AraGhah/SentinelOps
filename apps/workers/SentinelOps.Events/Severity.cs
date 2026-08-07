namespace SentinelOps.Events;

// Standalone mirror of SentinelOps.Api.Domain.IncidentSeverity. Event contracts
// intentionally don't reference internal EF entity/enum types directly, so this
// can drift independently of the API's persistence model; SentinelOps.Api maps
// between the two once, at the publish boundary.
public enum Severity { Critical = 0, High = 1, Medium = 2, Low = 3 }
