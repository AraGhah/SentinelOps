using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SentinelOps.Api.Common;
using SentinelOps.Api.Data;
using SentinelOps.Api.Domain;
using SentinelOps.Api.Tenancy;

namespace SentinelOps.Api.Reports;

[ApiController]
[Authorize]
[Route("api/v1/organizations/{orgId:guid}/incidents/{incidentId:guid}/reports")]
public class ReportsController(
    SentinelOpsDbContext db, ICurrentUserService currentUserService, IAuditLogger auditLogger, IReportStorageService storage)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<List<ReportResponse>>> List(Guid orgId, Guid incidentId, CancellationToken ct)
    {
        var reports = await db.Reports
            .Where(r => r.OrganizationId == orgId && r.IncidentId == incidentId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync(ct);

        return Ok(reports.Select(ToResponse).ToList());
    }

    [HttpGet("{reportId:guid}")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<ReportResponse>> Get(Guid orgId, Guid incidentId, Guid reportId, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        return Ok(ToResponse(report));
    }

    [HttpPost]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<ReportResponse>> Create(
        Guid orgId, Guid incidentId, CreateReportRequest request, CancellationToken ct)
    {
        var incidentExists = await db.Incidents.AnyAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        if (!incidentExists) return NotFound();

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var report = new Report
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IncidentId = incidentId,
            Summary = request.Summary,
            CustomerImpact = request.CustomerImpact,
            RootCause = request.RootCause,
            DetectionDetails = request.DetectionDetails,
            ResolutionDetails = request.ResolutionDetails,
            PreventionActions = request.PreventionActions,
            FollowUpTasks = request.FollowUpTasks,
            ReviewStatus = ReportReviewStatus.Draft,
            CreatedByUserId = actor.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("report.created", nameof(Report), report.Id, null, ct);

        return Ok(ToResponse(report));
    }

    // Prefills a new Draft report from the incident's record and timeline. Still goes
    // through the same Draft -> InReview -> Approved -> Published pipeline as any other report.
    [HttpPost("draft")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<ReportResponse>> CreateDraft(Guid orgId, Guid incidentId, CancellationToken ct)
    {
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        if (incident is null) return NotFound();

        var timeline = await db.IncidentEvents
            .Where(e => e.OrganizationId == orgId && e.IncidentId == incidentId)
            .ToListAsync(ct);

        var (summary, detectionDetails, resolutionDetails) = ReportDraftBuilder.Build(incident, timeline);

        var actor = await currentUserService.GetOrProvisionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var report = new Report
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            IncidentId = incidentId,
            Summary = summary,
            DetectionDetails = detectionDetails,
            ResolutionDetails = resolutionDetails,
            ReviewStatus = ReportReviewStatus.Draft,
            CreatedByUserId = actor.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("report.created", nameof(Report), report.Id, new { Source = "timeline_draft" }, ct);

        return Ok(ToResponse(report));
    }

    [HttpPut("{reportId:guid}")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<ReportResponse>> Update(
        Guid orgId, Guid incidentId, Guid reportId, UpdateReportRequest request, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        report.Summary = request.Summary;
        report.CustomerImpact = request.CustomerImpact;
        report.RootCause = request.RootCause;
        report.DetectionDetails = request.DetectionDetails;
        report.ResolutionDetails = request.ResolutionDetails;
        report.PreventionActions = request.PreventionActions;
        report.FollowUpTasks = request.FollowUpTasks;
        report.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("report.updated", nameof(Report), report.Id, null, ct);

        return Ok(ToResponse(report));
    }

    [HttpPut("{reportId:guid}/review-status")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<ActionResult<ReportResponse>> UpdateReviewStatus(
        Guid orgId, Guid incidentId, Guid reportId, UpdateReportReviewStatusRequest request, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        // Human approval before publication: must go through Approved first.
        if (request.ReviewStatus == ReportReviewStatus.Published && report.ReviewStatus != ReportReviewStatus.Approved)
        {
            return Problem(
                title: "Invalid transition", detail: "A report must be Approved before it can be Published.", statusCode: 400);
        }

        report.ReviewStatus = request.ReviewStatus;
        report.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync(
            "report.review_status_changed", nameof(Report), report.Id, new { report.ReviewStatus }, ct);

        return Ok(ToResponse(report));
    }

    // Renders HTML and PDF, uploads both to S3, and returns short-lived download URLs.
    [HttpPost("{reportId:guid}/generate")]
    [Authorize(Policy = OrgPolicies.Responder)]
    public async Task<ActionResult<GenerateReportResponse>> Generate(Guid orgId, Guid incidentId, Guid reportId, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        if (incident is null) return NotFound();

        var timeline = await db.IncidentEvents
            .Where(e => e.OrganizationId == orgId && e.IncidentId == incidentId)
            .ToListAsync(ct);

        var html = ReportHtmlRenderer.Render(incident, report, timeline);
        var pdf = ReportPdfRenderer.Render(incident, report, timeline);

        report.HtmlStorageKey = await storage.UploadHtmlAsync(orgId, incidentId, reportId, html, ct);
        report.PdfStorageKey = await storage.UploadPdfAsync(orgId, incidentId, reportId, pdf, ct);
        report.GeneratedAtUtc = DateTimeOffset.UtcNow;
        report.UpdatedAtUtc = report.GeneratedAtUtc.Value;

        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("report.generated", nameof(Report), report.Id, null, ct);

        var (htmlUrl, expiresAtUtc) = storage.CreateDownloadUrl(report.HtmlStorageKey, $"{ReportFileName(incident)}.html", "text/html");
        var (pdfUrl, _) = storage.CreateDownloadUrl(report.PdfStorageKey, $"{ReportFileName(incident)}.pdf", "application/pdf");

        return Ok(new GenerateReportResponse(htmlUrl, pdfUrl, expiresAtUtc, report.GeneratedAtUtc.Value));
    }

    [HttpGet("{reportId:guid}/download")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<DownloadReportResponse>> Download(
        Guid orgId, Guid incidentId, Guid reportId, [FromQuery] string format, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        var (storageKey, contentType) = format.ToLowerInvariant() switch
        {
            "html" => (report.HtmlStorageKey, "text/html"),
            "pdf" => (report.PdfStorageKey, "application/pdf"),
            _ => (null, null),
        };

        if (contentType is null)
        {
            return Problem(title: "Invalid request", detail: "format must be 'html' or 'pdf'.", statusCode: 400);
        }

        if (storageKey is null)
        {
            return Problem(
                title: "Report not generated", detail: "Call generate before downloading this report.", statusCode: 409);
        }

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        var fileName = $"{(incident is null ? "report" : ReportFileName(incident))}.{format.ToLowerInvariant()}";

        var (url, expiresAtUtc) = storage.CreateDownloadUrl(storageKey, fileName, contentType);
        await auditLogger.LogAsync("report.exported", nameof(Report), report.Id, new { Format = format }, ct);

        return Ok(new DownloadReportResponse(url, expiresAtUtc));
    }

    // Internal sharing: every org member with Viewer+ access can already List/
    // Get/Download this report, so "sharing" here means handing out a set of
    // download links to reference (e.g. paste into a Slack thread) while
    // recording who requested it, rather than granting any new access.
    [HttpPost("{reportId:guid}/share")]
    [Authorize(Policy = OrgPolicies.Viewer)]
    public async Task<ActionResult<GenerateReportResponse>> Share(
        Guid orgId, Guid incidentId, Guid reportId, ShareReportRequest request, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        if (report.HtmlStorageKey is null || report.PdfStorageKey is null)
        {
            return Problem(
                title: "Report not generated", detail: "Call generate before sharing this report.", statusCode: 409);
        }

        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.OrganizationId == orgId && i.Id == incidentId, ct);
        var fileName = incident is null ? "report" : ReportFileName(incident);

        var (htmlUrl, expiresAtUtc) = storage.CreateDownloadUrl(report.HtmlStorageKey, $"{fileName}.html", "text/html");
        var (pdfUrl, _) = storage.CreateDownloadUrl(report.PdfStorageKey, $"{fileName}.pdf", "application/pdf");

        await auditLogger.LogAsync("report.shared", nameof(Report), report.Id, new { request.Note }, ct);

        return Ok(new GenerateReportResponse(htmlUrl, pdfUrl, expiresAtUtc, report.GeneratedAtUtc ?? DateTimeOffset.UtcNow));
    }

    [HttpDelete("{reportId:guid}")]
    [Authorize(Policy = OrgPolicies.Administrator)]
    public async Task<IActionResult> Delete(Guid orgId, Guid incidentId, Guid reportId, CancellationToken ct)
    {
        var report = await Find(orgId, incidentId, reportId, ct);
        if (report is null) return NotFound();

        db.Reports.Remove(report);
        await db.SaveChangesAsync(ct);
        await auditLogger.LogAsync("report.deleted", nameof(Report), reportId, null, ct);

        return NoContent();
    }

    private Task<Report?> Find(Guid orgId, Guid incidentId, Guid reportId, CancellationToken ct) =>
        db.Reports.FirstOrDefaultAsync(r => r.OrganizationId == orgId && r.IncidentId == incidentId && r.Id == reportId, ct);

    private static string ReportFileName(Incident incident) =>
        string.Join('-', incident.Title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            is { Length: > 0 } slug ? slug : "report";

    private static ReportResponse ToResponse(Report r) => new(
        r.Id, r.IncidentId, r.Summary, r.CustomerImpact, r.RootCause, r.DetectionDetails, r.ResolutionDetails,
        r.PreventionActions, r.FollowUpTasks, r.ReviewStatus, r.CreatedByUserId, r.CreatedAtUtc, r.UpdatedAtUtc, r.GeneratedAtUtc);
}
