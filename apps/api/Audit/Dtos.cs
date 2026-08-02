namespace SentinelOps.Api.Audit;

public record AuditLogFilters(string? Action, string? EntityType, Guid? ActorUserId, DateTimeOffset? From, DateTimeOffset? To);

public record AuditLogResponse(
    Guid Id, Guid? ActorUserId, string Action, string? EntityType, Guid? EntityId, string? IpAddress, string? UserAgent,
    string? Details, DateTimeOffset CreatedAtUtc);
