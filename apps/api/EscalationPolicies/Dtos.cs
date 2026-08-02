using System.ComponentModel.DataAnnotations;

namespace SentinelOps.Api.EscalationPolicies;

public record EscalationLevelRequest(int Order, [Range(1, 1440)] int AckTimeoutMinutes, List<Guid> TargetUserIds);

public record CreateEscalationPolicyRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description,
    Guid? ServiceId,
    Guid? FallbackAdministratorUserId,
    [Required, MinLength(1)] List<EscalationLevelRequest> Levels);

public record UpdateEscalationPolicyRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description,
    Guid? ServiceId,
    Guid? FallbackAdministratorUserId);

public record EscalationLevelResponse(Guid Id, int Order, int AckTimeoutMinutes, List<Guid> TargetUserIds);

public record EscalationPolicyResponse(
    Guid Id, string Name, string? Description, Guid? ServiceId, Guid? FallbackAdministratorUserId,
    List<EscalationLevelResponse> Levels, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
