using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Models;
using AcademicSentinel.Client.Services;
using System.ComponentModel;

namespace AcademicSentinel.Client.Views.IMC
{
    public partial class SessionArchiveDetailWindow : Window
    {
        private readonly int _sessionId;
        private readonly ObservableCollection<SessionStudentDto> _students = new();
        private ICollectionView _studentsView;

        public SessionArchiveDetailWindow(int sessionId)
        {
            InitializeComponent();
            _sessionId = sessionId;

            _studentsView = CollectionViewSource.GetDefaultView(_students);
            _studentsView.Filter = StudentFilter;
            ArchiveDataGrid.ItemsSource = _studentsView;

            Loaded += SessionArchiveDetailWindow_Loaded;
        }

        private async void SessionArchiveDetailWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadSessionStudentsAsync();
        }

        private async System.Threading.Tasks.Task LoadSessionStudentsAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var response = await client.GetAsync($"{ApiEndpoints.BaseUrl}/api/reports/sessions/{_sessionId}/students");
                if (!response.IsSuccessStatusCode)
                {
                    MessageBox.Show("Unable to load session archive details.", "Session Archive", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var data = await response.Content.ReadFromJsonAsync<List<SessionStudentDto>>() ?? new List<SessionStudentDto>();

                _students.Clear();
                foreach (var student in data)
                {
                    _students.Add(student);
                }

                _studentsView.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load session archive: {ex.Message}", "Session Archive", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool StudentFilter(object obj)
        {
            if (obj is not SessionStudentDto student)
            {
                return false;
            }

            var query = TxtSearch?.Text?.Trim() ?? string.Empty;
            var selectedRisk = (CmbRiskLevel?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";

            var matchesQuery = string.IsNullOrWhiteSpace(query)
                || (!string.IsNullOrWhiteSpace(student.Name) && student.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(student.Email) && student.Email.Contains(query, StringComparison.OrdinalIgnoreCase));

            // Legacy DB rows may still hold the raw "Cheating" / "CHEATING"
            // string. Run the row through RiskLevelDisplay.Normalize so the
            // ComboBox's "POSSIBLE DISHONESTY" entry still matches them.
            var displayRiskLevel = AcademicSentinel.Client.Services.SAC.Models.RiskLevelDisplay
                .Normalize(student.RiskLevel);
            var matchesRisk = string.Equals(selectedRisk, "All", StringComparison.OrdinalIgnoreCase)
                || string.Equals(displayRiskLevel, selectedRisk, StringComparison.OrdinalIgnoreCase);

            return matchesQuery && matchesRisk;
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _studentsView?.Refresh();
        }

        private void CmbRiskLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _studentsView?.Refresh();
        }

        // Lets the instructor preview a student's full activity timeline before
        // committing to a PDF export. Required by spec — see Bug #3 in QA list.
        private void BtnViewLogs_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            var student = button.DataContext as SessionStudentDto;
            if (student == null)
                return;

            var dialog = new Dialogs.StudentLogsPreviewDialog(student)
            {
                Owner = this
            };

            // Modal preview. If the user pressed "Export PDF" inside the
            // dialog, fall through to the existing export flow with the same
            // student bound — no need to duplicate file-save logic.
            dialog.ShowDialog();

            if (dialog.ExportRequested)
            {
                ExportStudentReport(student);
            }
        }

        private void BtnExportPdf_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            var student = button.DataContext as SessionStudentDto;
            if (student == null)
                return;

            ExportStudentReport(student);
        }

        // Shared export pipeline used by both the in-row Export PDF button
        // and the Export-from-preview button on the logs dialog.
        private void ExportStudentReport(SessionStudentDto student)
        {

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "PDF Files|*.pdf",
                DefaultExt = ".pdf",
                FileName = $"{student.Name.Replace(" ", "_")}_Report.pdf"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    PdfReportGenerator.GenerateStudentReport(student, DateTime.Now.ToString("d"), saveFileDialog.FileName);
                    MessageBox.Show("Report exported successfully.", "Session Archive", MessageBoxButton.OK, MessageBoxImage.Information);

                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(saveFileDialog.FileName)
                        {
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // Best-effort open: export already succeeded.
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export PDF: {ex.Message}", "Session Archive", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ============================================================
        // ARCHIVE ANALYTIC MODE
        // ============================================================
        // Toggles between the per-student DataGrid and a session-level
        // dashboard showing Risk Level distribution + Connection Quality
        // distribution + total violations. The two views share the same
        // Grid slot — visibility is flipped per click. The search box
        // and risk-level dropdown above stay visible either way; they
        // just become no-ops while analytics is on.
        private bool _isArchiveAnalyticMode = false;

        private void BtnArchiveAnalyticMode_Click(object sender, RoutedEventArgs e)
        {
            _isArchiveAnalyticMode = !_isArchiveAnalyticMode;

            if (_isArchiveAnalyticMode)
            {
                if (ArchiveDataGridCard != null)
                    ArchiveDataGridCard.Visibility = Visibility.Collapsed;
                if (ArchiveAnalyticsPanel != null)
                    ArchiveAnalyticsPanel.Visibility = Visibility.Visible;

                BtnArchiveAnalyticMode.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(56, 142, 60));   // #388E3C — active green
                if (TxtArchiveAnalyticMode != null)
                    TxtArchiveAnalyticMode.Text = "Standard View";

                LoadArchiveAnalytics();
            }
            else
            {
                if (ArchiveDataGridCard != null)
                    ArchiveDataGridCard.Visibility = Visibility.Visible;
                if (ArchiveAnalyticsPanel != null)
                    ArchiveAnalyticsPanel.Visibility = Visibility.Collapsed;

                BtnArchiveAnalyticMode.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(25, 118, 210));  // #1976D2 — default blue
                if (TxtArchiveAnalyticMode != null)
                    TxtArchiveAnalyticMode.Text = "Analytic Mode";
            }
        }

        /// <summary>
        /// Groups the loaded participant list by RiskLevel and
        /// ConnectionQuality, computes total violations, and binds the
        /// results to the dashboard ItemsControls. Works against the
        /// FULL participant set regardless of the search box or
        /// risk-level dropdown — the dashboard is meant to be a
        /// session-wide overview, not a filtered subset.
        ///
        /// Legacy "CHEATING" rows are normalized to "POSSIBLE
        /// DISHONESTY" via RiskLevelDisplay.Normalize so the bucket
        /// label matches the panel-approved wording used elsewhere.
        /// </summary>
        private void LoadArchiveAnalytics()
        {
            if (_students == null || _students.Count == 0)
            {
                ArchiveRiskAnalyticsControl.ItemsSource = null;
                ArchiveConnectionAnalyticsControl.ItemsSource = null;
                if (TxtArchiveTotalViolations != null)
                    TxtArchiveTotalViolations.Text = "0 violation(s) recorded.";
                if (TxtArchiveAnalyticsEmpty != null)
                    TxtArchiveAnalyticsEmpty.Visibility = Visibility.Visible;
                return;
            }

            int totalStudents = _students.Count;

            // ----------------------------------------------------------
            // A. Risk Level distribution. Normalize the raw RiskLevel
            //    string so legacy "Cheating" rows fold into the
            //    "POSSIBLE DISHONESTY" bucket and don't show up as a
            //    separate slice in the chart.
            // ----------------------------------------------------------
            var riskData = _students
                .GroupBy(p =>
                {
                    var normalized = AcademicSentinel.Client.Services.SAC.Models.RiskLevelDisplay
                        .Normalize(p.RiskLevel);
                    return string.IsNullOrWhiteSpace(normalized) ? "UNKNOWN" : normalized.ToUpperInvariant();
                }, StringComparer.OrdinalIgnoreCase)
                .Select(g => new MetricAnalyticItem
                {
                    Category = g.Key,
                    Count    = g.Count(),
                    Max      = totalStudents,
                    BarBrush = BrushForRisk(g.Key)
                })
                .OrderByDescending(x => x.Count)
                .ToList();
            ArchiveRiskAnalyticsControl.ItemsSource = riskData;

            // ----------------------------------------------------------
            // B. Connection Quality distribution. Buckets are whatever
            //    the server-side classifier emits: "Clean Connection",
            //    "Reconnected", "Disconnected", or empty for rows that
            //    never produced an event. Empty falls into "UNKNOWN" to
            //    keep the bar chart readable.
            // ----------------------------------------------------------
            var connData = _students
                .GroupBy(p => string.IsNullOrWhiteSpace(p.ConnectionQuality)
                    ? "UNKNOWN"
                    : p.ConnectionQuality.ToUpperInvariant(),
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => new MetricAnalyticItem
                {
                    Category = g.Key,
                    Count    = g.Count(),
                    Max      = totalStudents,
                    BarBrush = BrushForConnection(g.Key)
                })
                .OrderByDescending(x => x.Count)
                .ToList();
            ArchiveConnectionAnalyticsControl.ItemsSource = connData;

            // ----------------------------------------------------------
            // C. Total Violations headline.
            // ----------------------------------------------------------
            int totalViolations        = _students.Sum(p => p.ViolationCount);
            int studentsWithViolations = _students.Count(p => p.ViolationCount > 0);
            if (TxtArchiveTotalViolations != null)
                TxtArchiveTotalViolations.Text =
                    $"{totalViolations} violation(s) recorded across {studentsWithViolations} of {totalStudents} student(s).";

            if (TxtArchiveAnalyticsEmpty != null)
                TxtArchiveAnalyticsEmpty.Visibility = Visibility.Collapsed;
        }

        private static System.Windows.Media.Brush BrushForRisk(string category)
        {
            // Critical red / warning orange / safe green — matches the
            // colour vocabulary the Student Details pane and the live
            // monitoring dashboard already use, so cross-window glances
            // read consistently.
            if (category.Equals("POSSIBLE DISHONESTY", StringComparison.OrdinalIgnoreCase)
                || category.Equals("CHEATING",          StringComparison.OrdinalIgnoreCase))
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47));
            if (category.Equals("SUSPICIOUS", StringComparison.OrdinalIgnoreCase))
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 81, 0));
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 94, 32));
        }

        private static System.Windows.Media.Brush BrushForConnection(string category)
        {
            if (category.Equals("DISCONNECTED", StringComparison.OrdinalIgnoreCase))
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 40, 40));
            if (category.Equals("RECONNECTED", StringComparison.OrdinalIgnoreCase))
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 81, 0));
            // Clean Connection (or any positive category) → blue.
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 118, 210));
        }

        /// <summary>
        /// Row shape bound to the analytics ItemsControls. Count drives
        /// the ProgressBar Value, Max drives Maximum, BarBrush colours
        /// the bar per row.
        /// </summary>
        private sealed class MetricAnalyticItem
        {
            public string Category { get; set; }
            public int Count { get; set; }
            public int Max { get; set; }
            public System.Windows.Media.Brush BarBrush { get; set; }
        }
    }
}
