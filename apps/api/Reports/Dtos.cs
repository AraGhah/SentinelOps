using System.ComponentModel.DataAnnotations;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Reports;

public record CreateReportRequest(
    [Required] string Summary, string? CustomerImpact, string? RootCause, string? DetectionDetails,
    string? ResolutionDetails, string? PreventionActions, string? FollowUpTasks);

public record UpdateReportRequest(
    [Required] string Summary, string? CustomerImpact, string? RootCause, string? DetectionDetails,
    string? ResolutionDetails, string? PreventionActions, string? FollowUpTasks);

public record UpdateReportReviewStatusRequest(ReportReviewStatus ReviewStatus);

public record ShareReportRequest(string? Note);

public record ReportResponse(
    Guid Id, Guid IncidentId, string Summary, string? CustomerImpact, string? RootCause, string? DetectionDetails,
    string? ResolutionDetails, string? PreventionActions, string? FollowUpTasks, ReportReviewStatus ReviewStatus,
    Guid CreatedByUserId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? GeneratedAtUtc);

public record GenerateReportResponse(string HtmlDownloadUrl, string PdfDownloadUrl, DateTimeOffset ExpiresAtUtc, DateTimeOffset GeneratedAtUtc);

public record DownloadReportResponse(string Url, DateTimeOffset ExpiresAtUtc);
