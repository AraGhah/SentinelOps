namespace SentinelOps.Api.Attachments;

public record PresignedUpload(string StorageKey, string UploadUrl, DateTimeOffset ExpiresAtUtc);

public interface IAttachmentStorageService
{
    PresignedUpload CreateUploadUrl(Guid organizationId, Guid incidentId, string fileName, string contentType);

    (string Url, DateTimeOffset ExpiresAtUtc) CreateDownloadUrl(string storageKey, string fileName);

    Task DeleteAsync(string storageKey, CancellationToken ct);

    // Authoritative object size; the presigned PUT doesn't bind size, so callers
    // can't be trusted to report it. Null if no object exists at the key yet.
    Task<long?> GetObjectSizeAsync(string storageKey, CancellationToken ct);
}
