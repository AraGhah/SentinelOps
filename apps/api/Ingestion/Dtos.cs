using System.ComponentModel.DataAnnotations;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Ingestion;

public record IngestAlertRequest(
    [Required, MaxLength(200)] string ExternalId,
    [Required, MaxLength(200)] string Source,
    [Required, MaxLength(300)] string Title,
    [MaxLength(10_000)] string? Description,
    [EnumDataType(typeof(IncidentSeverity))] IncidentSeverity Severity,
    DateTimeOffset TimestampUtc,
    [Required, MaxLength(100)] string Environment,
    [MaxLength(200)] string? Region,
    [MaxLength(10_000)] string? Metadata);

public record IngestAlertResponse(Guid AlertId, Guid CorrelationId, string Status);
