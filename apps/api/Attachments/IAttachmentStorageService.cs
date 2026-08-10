namespace SentinelOps.Api.Attachments;

public record PresignedUpload(string StorageKey, string UploadUrl, DateTimeOffset ExpiresAtUtc);

public interface IAttachmentStorageService
{
    PresignedUpload CreateUploadUrl(Guid organizationId, Guid incidentId, string fileName, string contentType);

    (string Url, DateTimeOffset ExpiresAtUtc) CreateDownloadUrl(string storageKey, string fileName);

    Task DeleteAsync(string storageKey, CancellationToken ct);
}
