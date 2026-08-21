using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace SentinelOps.Api.Attachments;

public class S3AttachmentStorageService(IAmazonS3 s3, IOptions<AttachmentStorageOptions> options) : IAttachmentStorageService
{
    // Short-lived on purpose: a leaked URL only grants access for a few
    // minutes, and legitimate clients use it immediately after requesting it.
    private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(15);

    public PresignedUpload CreateUploadUrl(Guid organizationId, Guid incidentId, string fileName, string contentType)
    {
        var storageKey = $"{AttachmentPolicy.StorageKeyPrefix(organizationId, incidentId)}{Guid.NewGuid():N}-{SanitizeFileName(fileName)}";
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(UrlLifetime);

        var url = s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.Value.BucketName,
            Key = storageKey,
            Verb = HttpVerb.PUT,
            Expires = expiresAtUtc.UtcDateTime,
            ContentType = contentType,
            // Requires the upload itself to be server-side encrypted, matching
            // the bucket's default-encryption + bucket-policy enforcement.
            Headers = { ["x-amz-server-side-encryption"] = "AES256" },
        });

        return new PresignedUpload(storageKey, url, expiresAtUtc);
    }

    public (string Url, DateTimeOffset ExpiresAtUtc) CreateDownloadUrl(string storageKey, string fileName)
    {
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(UrlLifetime);

        var url = s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.Value.BucketName,
            Key = storageKey,
            Verb = HttpVerb.GET,
            Expires = expiresAtUtc.UtcDateTime,
            ResponseHeaderOverrides = { ContentDisposition = $"attachment; filename=\"{SanitizeFileName(fileName)}\"" },
        });

        return (url, expiresAtUtc);
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = options.Value.BucketName, Key = storageKey }, ct);
    }

    public async Task<long?> GetObjectSizeAsync(string storageKey, CancellationToken ct)
    {
        try
        {
            var metadata = await s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = options.Value.BucketName, Key = storageKey }, ct);
            return metadata.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(fileName.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "file" : cleaned;
    }
}
