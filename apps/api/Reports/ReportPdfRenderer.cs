using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SentinelOps.Api.Domain;

namespace SentinelOps.Api.Reports;

// Composed directly with QuestPDF rather than converting ReportHtmlRenderer's output:
// no headless-browser/wkhtmltopdf dependency in this Lambda-friendly stack.
public static class ReportPdfRenderer
{
    public static byte[] Render(Incident incident, Report report, List<IncidentEvent> timeline)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Text($"Post-Incident Report: {incident.Title}").FontSize(16).Bold();

                page.Content().Column(column =>
                {
                    column.Spacing(8);

                    column.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn();
                            c.RelativeColumn(2);
                        });

                        AddRow(table, "Severity", incident.Severity.ToString());
                        AddRow(table, "Status", incident.Status.ToString());
                        AddRow(table, "Review status", report.ReviewStatus.ToString());
                        AddRow(table, "Created", incident.CreatedAtUtc.ToString("u"));
                        AddRow(table, "Acknowledged", incident.AcknowledgedAtUtc?.ToString("u") ?? "—");
                        AddRow(table, "Resolved", incident.ResolvedAtUtc?.ToString("u") ?? "—");
                    });

                    AddSection(column, "Incident summary", report.Summary);
                    AddSection(column, "Customer impact", report.CustomerImpact);
                    AddSection(column, "Root cause", report.RootCause);
                    AddSection(column, "Detection details", report.DetectionDetails);
                    AddSection(column, "Resolution details", report.ResolutionDetails);
                    AddSection(column, "Prevention actions", report.PreventionActions);
                    AddSection(column, "Follow-up tasks", report.FollowUpTasks);

                    column.Item().Text("Timeline").FontSize(12).Bold();
                    if (timeline.Count == 0)
                    {
                        column.Item().Text("No timeline events recorded.");
                    }
                    else
                    {
                        column.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn(3);
                            });

                            table.Header(header =>
                            {
                                header.Cell().Text("Time (UTC)").Bold();
                                header.Cell().Text("Event").Bold();
                                header.Cell().Text("Summary").Bold();
                            });

                            foreach (var e in timeline.OrderBy(e => e.OccurredAtUtc))
                            {
                                table.Cell().Text(e.OccurredAtUtc.ToString("u"));
                                table.Cell().Text(e.EventType.ToString());
                                table.Cell().Text(e.Summary ?? "");
                            }
                        });
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Generated ");
                    x.Span(DateTimeOffset.UtcNow.ToString("u"));
                });
            });
        });

        return document.GeneratePdf();
    }

    private static void AddRow(TableDescriptor table, string label, string value)
    {
        table.Cell().Text(label).Bold();
        table.Cell().Text(value);
    }

    private static void AddSection(ColumnDescriptor column, string title, string? content)
    {
        column.Item().Text(title).FontSize(12).Bold();
        column.Item().Text(string.IsNullOrWhiteSpace(content) ? "—" : content);
    }
}
