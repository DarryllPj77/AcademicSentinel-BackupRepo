using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    }
}
