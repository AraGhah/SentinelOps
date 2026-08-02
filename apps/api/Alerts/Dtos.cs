using System.ComponentModel.DataAnnotations;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Alerts;

public record CreateAlertRequest(
    Guid IntegrationId,
    [Required, MaxLength(200)] string ExternalId,
    [Required, MaxLength(200)] string Source,
    [Required, MaxLength(300)] string Title,
    string? Description,
    IncidentSeverity Severity,
    DateTimeOffset TimestampUtc,
    [Required, MaxLength(100)] string Environment,
    string? Region,
    string? Metadata);

public record AlertResponse(
    Guid Id, Guid IntegrationId, string ExternalId, string Source, string Title, string? Description,
    IncidentSeverity Severity, DateTimeOffset TimestampUtc, string Environment, string? Region, string? Metadata,
    Guid? IncidentId, DateTimeOffset CreatedAtUtc);

public record AlertFilters(Guid? IntegrationId, IncidentSeverity? Severity, Guid? IncidentId);

public record LinkAlertIncidentRequest(Guid IncidentId);
