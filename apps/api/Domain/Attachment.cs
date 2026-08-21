namespace SentinelOps.Api.Domain;

// Pending until the GuardDuty Malware Protection finding arrives; download presigning
// refuses anything other than Clean.
public enum AttachmentScanStatus { Pending, Clean, Infected, Failed }

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
    public AttachmentScanStatus ScanStatus { get; set; } = AttachmentScanStatus.Pending;
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Incident? Incident { get; set; }
}
