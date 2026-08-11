using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace SentinelOps.Api.Reports;

public class S3ReportStorageService(IAmazonS3 s3, IOptions<ReportStorageOptions> options) : IReportStorageService
{
    // Same rationale as S3AttachmentStorageService: short-lived so a leaked URL
    // only grants access briefly.
    private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(15);

    public async Task<string> UploadHtmlAsync(Guid organizationId, Guid incidentId, Guid reportId, string html, CancellationToken ct)
    {
        var storageKey = $"{KeyPrefix(organizationId, incidentId, reportId)}.html";
        await PutAsync(storageKey, html, "text/html; charset=utf-8", ct);
        return storageKey;
    }

    public async Task<string> UploadPdfAsync(Guid organizationId, Guid incidentId, Guid reportId, byte[] pdf, CancellationToken ct)
    {
        var storageKey = $"{KeyPrefix(organizationId, incidentId, reportId)}.pdf";
        using var stream = new MemoryStream(pdf);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Value.BucketName,
            Key = storageKey,
            InputStream = stream,
            ContentType = "application/pdf",
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
        }, ct);
        return storageKey;
    }

    public (string Url, DateTimeOffset ExpiresAtUtc) CreateDownloadUrl(string storageKey, string fileName, string contentType)
    {
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(UrlLifetime);

        var url = s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.Value.BucketName,
            Key = storageKey,
            Verb = HttpVerb.GET,
            Expires = expiresAtUtc.UtcDateTime,
            ResponseHeaderOverrides = { ContentDisposition = $"attachment; filename=\"{fileName}\"", ContentType = contentType },
        });

        return (url, expiresAtUtc);
    }

    private async Task PutAsync(string key, string content, string contentType, CancellationToken ct)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Value.BucketName,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
        }, ct);
    }

    private static string KeyPrefix(Guid organizationId, Guid incidentId, Guid reportId) =>
        $"orgs/{organizationId:N}/incidents/{incidentId:N}/reports/{reportId:N}";
}
