namespace SentinelOps.Api.Reports;

public interface IReportStorageService
{
    // Reports are server-generated (unlike attachments), so content is uploaded
    // directly rather than via a presigned PUT — there's no client to hand a URL to.
    Task<string> UploadHtmlAsync(Guid organizationId, Guid incidentId, Guid reportId, string html, CancellationToken ct);

    Task<string> UploadPdfAsync(Guid organizationId, Guid incidentId, Guid reportId, byte[] pdf, CancellationToken ct);

    (string Url, DateTimeOffset ExpiresAtUtc) CreateDownloadUrl(string storageKey, string fileName, string contentType);
}
