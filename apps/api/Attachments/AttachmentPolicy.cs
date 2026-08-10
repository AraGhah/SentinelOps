namespace SentinelOps.Api.Attachments;

// Server-side allowlist and size cap, enforced both when a presigned upload
// URL is minted (Content-Type is bound into the PUT signature, so the client
// can't swap it after the fact) and again when the Attachment row is created
// (in case a client skips the presign step or the two calls disagree).
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

    // Every object this org/incident is allowed to reference lives under this
    // prefix — AttachmentsController checks a StorageKey against it before
    // trusting a client-supplied key, so one org can't attach another org's
    // (or another incident's) S3 object by guessing/reusing a key.
    public static string StorageKeyPrefix(Guid orgId, Guid incidentId) => $"orgs/{orgId:N}/incidents/{incidentId:N}/";
}
