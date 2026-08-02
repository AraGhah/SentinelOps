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
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Organization? Organization { get; set; }
    public Integration? Integration { get; set; }
    public Incident? Incident { get; set; }
}
