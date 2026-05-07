using System.Linq;
using System.Windows;
using AcademicSentinel.Client.Models;

namespace AcademicSentinel.Client.Views.IMC.Dialogs
{
    public partial class StudentLogsPreviewDialog : Window
    {
        public bool ExportRequested { get; private set; }

        public StudentLogsPreviewDialog(SessionStudentDto student)
        {
            InitializeComponent();

            if (student == null)
            {
                Close();
                return;
            }

            TxtStudentHeader.Text = $"{student.Name} — {student.Email}";

            var logs = student.Logs ?? new System.Collections.Generic.List<SessionLogDto>();
            // Newest first.
            var sortedLogs = logs.OrderByDescending(l => l.Timestamp).ToList();
            LogsDataGrid.ItemsSource = sortedLogs;

            TxtTotalEvents.Text = sortedLogs.Count.ToString();
            TxtTotalSeverity.Text = sortedLogs.Sum(l => l.SeverityScore).ToString();
            TxtRiskLevel.Text = string.IsNullOrWhiteSpace(student.RiskLevel)
                ? "—"
                : student.RiskLevel;

            // Color the risk level chip based on classification.
            TxtRiskLevel.Foreground = student.RiskLevel?.ToLowerInvariant() switch
            {
                "cheating"  => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47)),
                "suspicious" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0)),
                _            => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 94, 32))
            };
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            ExportRequested = false;
            DialogResult = false;
            Close();
        }

        private void BtnExportFromPreview_Click(object sender, RoutedEventArgs e)
        {
            ExportRequested = true;
            DialogResult = true;
            Close();
        }
    }
}
