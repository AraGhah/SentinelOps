namespace SentinelOps.Api.Domain;

public class Alert : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IntegrationId { get; set; }
    public required string ExternalId { get; set; }
    public required string Source { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public IncidentSeverity Severity { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public required string Environment { get; set; }
    public string? Region { get; set; }
    // Arbitrary source-defined JSON; stored raw rather than typed since there is no
    // ingestion/schema-validation logic behind it yet.
    public string? Metadata { get; set; }
    public Guid? IncidentId { get; set; }
    // The exact JSON body received on the ingestion endpoint, kept alongside the
    // parsed fields above for audit/replay/debugging — distinct from Metadata,
    // which is the source-defined payload as understood by the alert schema.
    public string? RawPayload { get; set; }
    public Guid CorrelationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Organization? Organization { get; set; }
    public Integration? Integration { get; set; }
    public Incident? Incident { get; set; }
}

// One row per accepted ingestion request, keyed by the caller's idempotency key
// (or, if omitted, a fallback derived from the request signature). Lets the
// ingestion endpoint recognize a retried/replayed request and return the same
// result instead of creating a duplicate alert.
public class IngestionRequestRecord : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IntegrationId { get; set; }
    public required string IdempotencyKey { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid AlertId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
