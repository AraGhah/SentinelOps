namespace SentinelOps.Api.Attachments;

// Server-side allowlist and size cap, enforced at both upload-url and Create time.
public static class AttachmentPolicy
{
    public const long MaxSizeBytes = 25 * 1024 * 1024;

    public static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp",
        "application/pdf", "text/plain", "text/csv", "application/json", "application/zip",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    public static bool IsAllowed(string contentType, long sizeBytes) =>
        AllowedContentTypes.Contains(contentType) && sizeBytes > 0 && sizeBytes <= MaxSizeBytes;

    // Checked against client-supplied StorageKeys so one org can't reference another's S3 object.
    public static string StorageKeyPrefix(Guid orgId, Guid incidentId) => $"orgs/{orgId:N}/incidents/{incidentId:N}/";
}
