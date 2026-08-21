namespace SentinelOps.Api.Reports;

public interface IReportStorageService
{
    // Reports are server-generated, so content is uploaded directly, no presigned PUT.
    Task<string> UploadHtmlAsync(Guid organizationId, Guid incidentId, Guid reportId, string html, CancellationToken ct);

    Task<string> UploadPdfAsync(Guid organizationId, Guid incidentId, Guid reportId, byte[] pdf, CancellationToken ct);

    (string Url, DateTimeOffset ExpiresAtUtc) CreateDownloadUrl(string storageKey, string fileName, string contentType);
}
