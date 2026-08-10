using System.Net;
using SentinelOps.Api.Domain;

namespace SentinelOps.Workers.Notification;

public record RenderedEmail(string Subject, string Html, string Text);

// Deliberately plain string formatting rather than a templating engine —
// three short, fixed-shape emails don't earn the dependency.
public static class EmailTemplates
{
    public static RenderedEmail Render(NotificationKind kind, Guid incidentId, string incidentTitle, IncidentSeverity severity, int? escalationLevel)
    {
        var safeTitle = WebUtility.HtmlEncode(incidentTitle);

        return kind switch
        {
            NotificationKind.IncidentAssigned => new RenderedEmail(
                Subject: $"[SentinelOps] You're assigned: {incidentTitle}",
                Html: $"<p>You've been assigned incident <strong>{safeTitle}</strong> (severity: {severity}).</p>"
                    + $"<p>Incident ID: {incidentId}</p>",
                Text: $"You've been assigned incident \"{incidentTitle}\" (severity: {severity}).\nIncident ID: {incidentId}"),

            NotificationKind.IncidentEscalated => new RenderedEmail(
                Subject: $"[SentinelOps] Escalated to you: {incidentTitle}",
                Html: $"<p>Incident <strong>{safeTitle}</strong> (severity: {severity}) was escalated"
                    + (escalationLevel is not null ? $" to level {escalationLevel}" : " to you as the fallback administrator")
                    + " because it wasn't acknowledged in time.</p>"
                    + $"<p>Incident ID: {incidentId}</p>",
                Text: $"Incident \"{incidentTitle}\" (severity: {severity}) was escalated"
                    + (escalationLevel is not null ? $" to level {escalationLevel}" : " to you as the fallback administrator")
                    + $" because it wasn't acknowledged in time.\nIncident ID: {incidentId}"),

            NotificationKind.IncidentResolved => new RenderedEmail(
                Subject: $"[SentinelOps] Resolved: {incidentTitle}",
                Html: $"<p>Incident <strong>{safeTitle}</strong> has been resolved.</p>"
                    + $"<p>Incident ID: {incidentId}</p>",
                Text: $"Incident \"{incidentTitle}\" has been resolved.\nIncident ID: {incidentId}"),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}
