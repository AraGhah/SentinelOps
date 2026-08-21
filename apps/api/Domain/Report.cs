namespace SentinelOps.Api.Domain;

public enum ReportReviewStatus { Draft = 0, InReview = 1, Approved = 2, Published = 3 }

public class Report : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }
    public required string Summary { get; set; }
    public string? CustomerImpact { get; set; }
    public string? RootCause { get; set; }
    public string? DetectionDetails { get; set; }
    public string? ResolutionDetails { get; set; }
    public string? PreventionActions { get; set; }
    public string? FollowUpTasks { get; set; }
    public ReportReviewStatus ReviewStatus { get; set; } = ReportReviewStatus.Draft;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    // Set together by ReportsController.Generate; always (re)generated as a pair.
    public string? HtmlStorageKey { get; set; }
    public string? PdfStorageKey { get; set; }
    public DateTimeOffset? GeneratedAtUtc { get; set; }

    public Incident? Incident { get; set; }
}
