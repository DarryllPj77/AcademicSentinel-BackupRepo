using System;
using System.Collections.Generic;
using System.Linq;
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
            var violationBreakdown = ComputeViolationBreakdown(logs);

            // Historical-data sanitizer: rows written by older builds may
            // still hold the raw "CHEATING" / "Cheating" string. Defense
            // panel requires the printed report to use non-accusatory
            // wording, so any legacy value is re-mapped to
            // "POSSIBLE DISHONESTY" before it ever reaches the page.
            // New rows already arrive in the panel-approved form and
            // pass through unchanged.
            var rawRiskLevel = (student?.RiskLevel ?? "SAFE").ToUpperInvariant();
            var riskLevel = rawRiskLevel == "CHEATING" ? "POSSIBLE DISHONESTY" : rawRiskLevel;

            var riskColor = riskLevel switch
            {
                // Critical-risk band keeps the same red regardless of
                // whether the source row used the legacy or new wording.
                "POSSIBLE DISHONESTY" => "#D32F2F",
                "CHEATING"            => "#D32F2F", // safety net; raw should already be remapped above
                "SUSPICIOUS"          => "#B8860B",
                _                     => "#2E7D32"
            };
            var assessment = GenerateOverallAssessment(riskLevel, violationBreakdown);

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

                        column.Item().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(10).Column(breakdown =>
                        {
                            breakdown.Spacing(6);
                            breakdown.Item().Text("Violation Breakdown").Bold().FontSize(13);
                            foreach (var item in violationBreakdown)
                            {
                                breakdown.Item().Text($"{item.Key}: {item.Value}");
                            }
                        });

                        column.Item().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(10).Column(overall =>
                        {
                            overall.Spacing(6);
                            overall.Item().Text("Overall Assessment / Recommendation").Bold().FontSize(13);
                            overall.Item().Text("Overall Assessment").Bold();
                            overall.Item().Text(assessment.Assessment);
                            overall.Item().Text("Recommendation").Bold();
                            overall.Item().Text(assessment.Recommendation);
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

        private static Dictionary<string, int> ComputeViolationBreakdown(IEnumerable<SessionLogDto> logs)
        {
            var breakdown = new Dictionary<string, int>
            {
                ["Window Switches"] = 0,
                ["Clipboard Copy"] = 0,
                ["Clipboard Paste"] = 0,
                ["Screenshot Attempt"] = 0,
                ["Multiple Monitor Detection"] = 0,
                ["Unauthorized Process"] = 0,
                ["Other Violations"] = 0
            };

            foreach (var log in logs ?? Enumerable.Empty<SessionLogDto>())
            {
                if (log == null || log.SeverityScore <= 0)
                    continue;

                var label = FormatViolationLabel(log.EventType);
                if (label == null)
                    continue;

                breakdown[label]++;
            }

            return breakdown;
        }

        private static string FormatViolationLabel(string eventType)
        {
            switch ((eventType ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "WINDOW_SWITCH":
                    return "Window Switches";
                case "CLIPBOARD_COPY":
                    return "Clipboard Copy";
                case "CLIPBOARD_PASTE":
                    return "Clipboard Paste";
                case "SCREENSHOT":
                case "PRINTSCREEN":
                case "SNIP_TOOL":
                    return "Screenshot Attempt";
                case "MULTI_MONITOR":
                case "MULTIPLE_MONITORS":
                    return "Multiple Monitor Detection";
                case "PROCESS_DETECTED":
                    return "Unauthorized Process";
                case "SYSTEM":
                case "LEAVE_GRANTED":
                case "MONITOR_COUNT":
                case "CANVAS_RETURNED":
                case "ALLOWED_APP":
                    return null;
                default:
                    return "Other Violations";
            }
        }

        private static (string Assessment, string Recommendation) GenerateOverallAssessment(
            string riskLevel,
            Dictionary<string, int> breakdown)
        {
            var occurred = breakdown
                .Where(x => x.Value > 0 && x.Key != "Other Violations")
                .Select(x => x.Key.ToLowerInvariant())
                .ToList();

            if (breakdown.TryGetValue("Other Violations", out var otherCount) && otherCount > 0)
                occurred.Add("other suspicious activity");

            var activityText = occurred.Count > 0
                ? string.Join(", ", occurred)
                : "no major suspicious activity";

            var normalizedRisk = (riskLevel ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalizedRisk))
            {
                return (
                    "The session contains recorded monitoring events. Instructor review may be required.",
                    "The instructor should review the recorded event logs."
                );
            }

            if (normalizedRisk == "SAFE")
            {
                return (
                    "The student was classified as SAFE. No major suspicious activity was detected during the monitored exam session.",
                    "No immediate action is required."
                );
            }

            var assessment = occurred.Count > 0
                ? $"The student was classified as {normalizedRisk} due to {activityText} during the monitored exam session."
                : $"The student was classified as {normalizedRisk}. The session contains recorded monitoring events.";

            var recommendation = normalizedRisk == "POSSIBLE DISHONESTY" || normalizedRisk == "CHEATING"
                ? "Manual review is strongly recommended."
                : "The instructor should verify the flagged activities.";

            return (assessment, recommendation);
        }
    }
}
