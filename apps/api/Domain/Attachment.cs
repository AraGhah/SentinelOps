namespace SentinelOps.Api.Domain;

// Metadata only. StorageKey is an opaque placeholder string standing in for a
// future S3 object key; no actual file bytes are handled by this scaffolding.
public class Attachment : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string StorageKey { get; set; }
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Incident? Incident { get; set; }
}
