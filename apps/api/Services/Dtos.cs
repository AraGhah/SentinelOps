using System.ComponentModel.DataAnnotations;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Services;

public record CreateServiceRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description,
    Guid OwnerUserId,
    ServiceEnvironment Environment);

public record UpdateServiceRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description,
    Guid OwnerUserId,
    ServiceEnvironment Environment,
    ServiceStatus Status);

public record ServiceResponse(
    Guid Id, string Name, string? Description, Guid OwnerUserId, ServiceEnvironment Environment,
    ServiceStatus Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public record ServiceFilters(ServiceEnvironment? Environment, ServiceStatus? Status);

public record ServiceDependencyResponse(Guid Id, Guid ServiceId, Guid DependsOnServiceId);

public record ServiceHealthResponse(
    Guid ServiceId, ServiceStatus Status, int OpenIncidentCount, int CriticalOpenIncidentCount,
    DateTimeOffset? LastIncidentCreatedAtUtc);

public record CreateAlertRuleRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description,
    [Required, MaxLength(500)] string Condition,
    IncidentSeverity Severity,
    bool IsEnabled);

public record UpdateAlertRuleRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description,
    [Required, MaxLength(500)] string Condition,
    IncidentSeverity Severity,
    bool IsEnabled);

public record AlertRuleResponse(
    Guid Id, Guid ServiceId, string Name, string? Description, string Condition, IncidentSeverity Severity,
    bool IsEnabled, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
