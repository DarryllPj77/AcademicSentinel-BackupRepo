using System;
using System.Collections.Generic;
using AcademicSentinel.Client.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AcademicSentinel.Client.Services
{
    public static class PdfReportGenerator
    {
        public static void GenerateStudentReport(SessionStudentDto student, string sessionDate, string savePath)
        {
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

            var logs = student?.Logs ?? new List<SessionLogDto>();
            var studentName = student?.Name ?? "Unknown";
            var studentEmail = student?.Email ?? "Unknown";
            var riskScore = student?.RiskScore ?? 0;
            var violationCount = student?.ViolationCount ?? 0;
            var riskLevel = (student?.RiskLevel ?? "SAFE").ToUpperInvariant();

            var riskColor = riskLevel switch
            {
                "CHEATING" => "#D32F2F",
                "SUSPICIOUS" => "#B8860B",
                _ => "#2E7D32"
            };

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(30);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header().Column(column =>
                    {
                        column.Item().Text("Academic Sentinel - Violation Report")
                            .Bold()
                            .FontSize(20)
                            .FontColor("#1B5E20");

                        column.Item().PaddingTop(4).Text($"Session Date: {sessionDate}")
                            .FontColor(Colors.Grey.Darken2);
                    });

                    page.Content().PaddingTop(12).Column(column =>
                    {
                        column.Spacing(14);

                        column.Item().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(10).Column(details =>
                        {
                            details.Spacing(6);
                            details.Item().Text("Student Details").Bold().FontSize(13);
                            details.Item().Text($"Name: {studentName}");
                            details.Item().Text($"Email: {studentEmail}");
                        });

                        column.Item().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(10).Column(summary =>
                        {
                            summary.Spacing(6);
                            summary.Item().Text("Risk Summary").Bold().FontSize(13);
                            summary.Item().Text($"Total Violations: {violationCount}");
                            summary.Item().Text($"Final Risk Score: {riskScore}");
                            summary.Item().Text(text =>
                            {
                                text.Span("Risk Level: ");
                                text.Span(riskLevel).Bold().FontColor(riskColor);
                            });
                        });

                        column.Item().Text("Event Logs").Bold().FontSize(13);

                        column.Item().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.RelativeColumn(1.3f);
                                columns.RelativeColumn(1.2f);
                                columns.RelativeColumn(0.8f);
                                columns.RelativeColumn(3.0f);
                            });

                            table.Header(header =>
                            {
                                header.Cell().Element(HeaderCell).Text("Timestamp").SemiBold();
                                header.Cell().Element(HeaderCell).Text("Event Type").SemiBold();
                                header.Cell().Element(HeaderCell).Text("Severity").SemiBold();
                                header.Cell().Element(HeaderCell).Text("Description").SemiBold();
                            });

                            if (logs.Count == 0)
                            {
                                table.Cell().ColumnSpan(4).Element(RowCell).Text("No logs found for this student.");
                            }
                            else
                            {
                                foreach (var log in logs)
                                {
                                    table.Cell().Element(RowCell).Text(log.Timestamp.ToLocalTime().ToString("HH:mm:ss"));
                                    table.Cell().Element(RowCell).Text(log.EventType ?? string.Empty);
                                    table.Cell().Element(RowCell).Text(log.SeverityScore.ToString());
                                    table.Cell().Element(RowCell).Text(log.Description ?? string.Empty);
                                }
                            }
                        });
                    });

                    page.Footer().AlignRight().Text(text =>
                    {
                        text.Span("Page ");
                        text.CurrentPageNumber();
                        text.Span(" of ");
                        text.TotalPages();
                    });
                });
            })
            .GeneratePdf(savePath);
        }

        private static IContainer HeaderCell(IContainer container)
        {
            return container
                .Background(Colors.Grey.Lighten3)
                .BorderBottom(1)
                .BorderColor(Colors.Grey.Medium)
                .PaddingVertical(6)
                .PaddingHorizontal(6);
        }

        private static IContainer RowCell(IContainer container)
        {
            return container
                .BorderBottom(1)
                .BorderColor(Colors.Grey.Lighten2)
                .PaddingVertical(6)
                .PaddingHorizontal(6);
        }
    }
}
