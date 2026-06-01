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
                < 20 => ($"Safe ({TotalRiskScore} pts)", "#1B5E20"),
                < 50 => ($"Suspicious ({TotalRiskScore} pts)", "#E65100"),
                _ => ($"Possible Dishonesty ({TotalRiskScore} pts)", "#D32F2F")
            };

            RiskLevelText = riskText;
            RiskLevelColorHex = riskColor;

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
            return eventType switch
            {
                "ALT_TAB" or "RTFM" or "FOCUS" or "WINDOW_SWITCH" => "Window Focus Lost",
                "CSAD" or "CLIPBOARD" or "COPY" or "PASTE" or "PRINTSCREEN" or "SCREENSHOT" => "Clipboard Copy",
                "PBD" or "PROCESS" or "BLACKLIST" => "Restricted App Opened",
                "VAC" or "VM" or "EMULATOR" or "VIRTUAL" => "Virtualization/Emulator Detected",
                "HAS" or "HARDWARE" or "ARTIFACT" or "SUSPICIOUS_SETUP" => "Hardware/Software Artifacts",
                "IDLE" or "INACTIVITY" => "Inactivity Detected",
                _ => string.IsNullOrWhiteSpace(eventType) ? "Uncategorized" : eventType.Replace('_', ' ')
            };
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
