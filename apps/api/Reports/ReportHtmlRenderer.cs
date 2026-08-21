using System.Net;
using System.Text;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Reports;

// Plain server-rendered HTML, no templating engine. Report fields are free text
// from responders, so everything goes through HtmlEncode.
public static class ReportHtmlRenderer
{
    public static string Render(Incident incident, Report report, List<IncidentEvent> timeline)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.Append($"<title>Post-Incident Report — {E(incident.Title)}</title>");
        sb.Append("<style>body{font-family:sans-serif;max-width:800px;margin:2rem auto;color:#222}");
        sb.Append("h1{font-size:1.4rem}h2{font-size:1.1rem;border-bottom:1px solid #ccc;margin-top:1.5rem}");
        sb.Append("table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:4px 8px;text-align:left;font-size:0.9rem}");
        sb.Append("</style></head><body>");

        sb.Append($"<h1>Post-Incident Report: {E(incident.Title)}</h1>");
        sb.Append("<table>");
        sb.Append(Row("Severity", incident.Severity.ToString()));
        sb.Append(Row("Status", incident.Status.ToString()));
        sb.Append(Row("Review status", report.ReviewStatus.ToString()));
        sb.Append(Row("Created", incident.CreatedAtUtc.ToString("u")));
        sb.Append(Row("Acknowledged", incident.AcknowledgedAtUtc?.ToString("u") ?? "—"));
        sb.Append(Row("Resolved", incident.ResolvedAtUtc?.ToString("u") ?? "—"));
        sb.Append("</table>");

        sb.Append(Section("Incident summary", report.Summary));
        sb.Append(Section("Customer impact", report.CustomerImpact));
        sb.Append(Section("Root cause", report.RootCause));
        sb.Append(Section("Detection details", report.DetectionDetails));
        sb.Append(Section("Resolution details", report.ResolutionDetails));
        sb.Append(Section("Prevention actions", report.PreventionActions));
        sb.Append(Section("Follow-up tasks", report.FollowUpTasks));

        sb.Append("<h2>Timeline</h2>");
        if (timeline.Count == 0)
        {
            sb.Append("<p>No timeline events recorded.</p>");
        }
        else
        {
            sb.Append("<table><tr><th>Time (UTC)</th><th>Event</th><th>Summary</th></tr>");
            foreach (var e in timeline.OrderBy(e => e.OccurredAtUtc))
            {
                sb.Append($"<tr><td>{e.OccurredAtUtc:u}</td><td>{E(e.EventType.ToString())}</td><td>{E(e.Summary ?? "")}</td></tr>");
            }
            sb.Append("</table>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Section(string title, string? content) =>
        $"<h2>{E(title)}</h2><p>{E(string.IsNullOrWhiteSpace(content) ? "—" : content).Replace("\n", "<br>")}</p>";

    private static string Row(string label, string value) => $"<tr><td>{E(label)}</td><td>{E(value)}</td></tr>";

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
