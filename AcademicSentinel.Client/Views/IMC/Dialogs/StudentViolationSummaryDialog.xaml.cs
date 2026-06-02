using AcademicSentinel.Client.Views.IMC;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace AcademicSentinel.Client.Views.IMC.Dialogs
{
    public partial class StudentViolationSummaryDialog : Window
    {
        public record ViolationCategorySummary(string CategoryName, int Count, int TotalScore);

        public string DialogTitle { get; }
        public int TotalViolationsCount { get; }
        public int TotalRiskScore { get; }
        public string RiskLevelText { get; }
        // Score line — shown beneath the bold risk label as a smaller,
        // secondary readout (e.g. "60 pts"). Splitting it off the main
        // label lets "Possible Dishonesty" wrap cleanly in a half-width
        // card without colliding with the cumulative-score figure.
        public string RiskLevelScoreText { get; }
        public string RiskLevelColorHex { get; }
        public ObservableCollection<ViolationCategorySummary> ViolationSummaries { get; }

        public StudentViolationSummaryDialog(string studentName, int totalRiskScore, IEnumerable<StudentMonitoringEvent> logs)
        {
            var safeLogs = logs?.ToList() ?? new List<StudentMonitoringEvent>();

            DialogTitle = $"Violation Summary - {studentName}";
            RiskLevelText = totalRiskScore < 20 ? "Safe" : totalRiskScore < 50 ? "Suspicious" : "Possible Dishonesty";

            var grouped = safeLogs
                .GroupBy(l => NormalizeEventType(l.EventType))
                .Select(g => new ViolationCategorySummary(
                    CategoryName: MapToFriendlyCategory(g.Key),
                    Count: g.Count(),
                    TotalScore: g.Sum(x => Math.Max(0, x.SeverityScore))))
                .OrderByDescending(x => x.TotalScore)
                .ThenByDescending(x => x.Count)
                .ThenBy(x => x.CategoryName)
                .ToList();

            TotalRiskScore = grouped.Sum(x => x.TotalScore);

            (string riskText, string riskColor) = TotalRiskScore switch
            {
                < 20 => ("Safe", "#1B5E20"),
                < 50 => ("Suspicious", "#E65100"),
                _    => ("Possible Dishonesty", "#D32F2F")
            };

            RiskLevelText      = riskText;
            RiskLevelScoreText = $"{TotalRiskScore} pts";
            RiskLevelColorHex  = riskColor;

            TotalViolationsCount = grouped.Sum(x => x.Count);
            ViolationSummaries = new ObservableCollection<ViolationCategorySummary>(grouped);

            InitializeComponent();
            DataContext = this;
        }

        private static string NormalizeEventType(string eventType)
        {
            return (eventType ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string MapToFriendlyCategory(string eventType)
        {
            // Canonical event-type vocabulary is wider than the original
            // switch covered — names like PROCESS_DETECTED, CLIPBOARD_COPY,
            // REMOTE_DESKTOP_DETECTED, FOCUS_LOST, etc. used to slip past
            // the exact-match cases and reach the fallback, which only
            // did Replace('_', ' ') and left the raw ALL CAPS in place.
            // That produced the inconsistent grid (Title Case rows next to
            // "PROCESS DETECTED"). Now every known family maps explicitly,
            // and the fallback runs ToTitleCase so ad-hoc / future event
            // types still come out in the same case style as the rest of
            // the column.
            return eventType switch
            {
                "ALT_TAB" or "RTFM" or "FOCUS" or "FOCUS_LOST"
                    or "WINDOW_SWITCH" or "CANVAS_FOCUS_LOST"
                    or "CANVAS_NOT_FOUND" or "CANVAS_CLOSED"
                    => "Window Focus Lost",

                "CSAD" or "CLIPBOARD" or "CLIPBOARD_COPY" or "CLIPBOARD_PASTE"
                    or "COPY" or "PASTE" or "PRINTSCREEN" or "SCREENSHOT"
                    or "SNIP_TOOL"
                    => "Clipboard Activity",

                "PBD" or "PROCESS" or "PROCESS_DETECTED" or "BLACKLIST"
                    => "Restricted App Opened",

                "VAC" or "VM" or "VAC_HAS_VIOLATION"
                    or "EMULATOR" or "VIRTUAL"
                    => "Virtualization Detected",

                "HAS" or "HARDWARE" or "ARTIFACT" or "SUSPICIOUS_SETUP"
                    or "HAS_DEBUGGER" or "HAS_TIME_TAMPER" or "HAS_CLOCK_DRIFT"
                    => "Hardware/Software Artifacts",

                "REMOTE" or "REMOTE_DESKTOP_DETECTED"
                    => "Remote Access Detected",

                "IDLE" or "INACTIVITY"
                    => "Inactivity Detected",

                _ => string.IsNullOrWhiteSpace(eventType)
                    ? "Uncategorized"
                    : ToTitleCase(eventType.Replace('_', ' '))
            };
        }

        /// <summary>
        /// Lower-cases the input, then upper-cases the first letter of
        /// each whitespace-separated word. Used for the fallback path so
        /// unmapped event types still appear in the same Title Case style
        /// as the explicit categories (e.g. "PROCESS DETECTED" → "Process
        /// Detected"), keeping the grid visually consistent.
        /// </summary>
        private static string ToTitleCase(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            var parts = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 1)
                    parts[i] = char.ToUpperInvariant(parts[i][0]).ToString();
                else
                    parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            }
            return string.Join(' ', parts);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
