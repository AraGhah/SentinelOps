using System.ComponentModel.DataAnnotations;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Ingestion;

public record IngestAlertRequest(
    [Required, MaxLength(200)] string ExternalId,
    [Required, MaxLength(200)] string Source,
    [Required, MaxLength(300)] string Title,
    string? Description,
    [EnumDataType(typeof(IncidentSeverity))] IncidentSeverity Severity,
    DateTimeOffset TimestampUtc,
    [Required, MaxLength(100)] string Environment,
    string? Region,
    string? Metadata);

public record IngestAlertResponse(Guid AlertId, Guid CorrelationId, string Status);

public record AwsSqsOptions
{
    public const string SectionName = "Aws:Sqs";

    public required string Region { get; set; }
    public required string AlertsQueueUrl { get; set; }
}
