namespace SentinelOps.Api.Attachments;

public record PresignedUpload(string StorageKey, string UploadUrl, DateTimeOffset ExpiresAtUtc);

public interface IAttachmentStorageService
{
    PresignedUpload CreateUploadUrl(Guid organizationId, Guid incidentId, string fileName, string contentType);

    (string Url, DateTimeOffset ExpiresAtUtc) CreateDownloadUrl(string storageKey, string fileName);

    Task DeleteAsync(string storageKey, CancellationToken ct);

    // Authoritative size of the object actually sitting in storage — the
    // presigned PUT URL only binds Content-Type + SSE header, not size, so a
    // client can upload arbitrarily large bytes and then lie about SizeBytes
    // when calling Create. Returns null if no object exists at the key yet
    // (e.g. the client calls Create before the PUT completes).
    Task<long?> GetObjectSizeAsync(string storageKey, CancellationToken ct);
}
