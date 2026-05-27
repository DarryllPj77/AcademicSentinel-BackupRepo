using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Models;
using AcademicSentinel.Client.Services;

namespace AcademicSentinel.Client.Views.IMC
{
    /// <summary>
    /// Per-room Trash view. Lists ExamSession rows whose DeletedAt
    /// is set (i.e. soft-deleted via the bin/delete buttons on the
    /// Past Session grid) and allows restoring them before
    /// ArchiveCleanupService permanently purges them after the
    /// configured retention window. Opened as a dialog from
    /// RoomDetailWindow.BtnViewTrash_Click.
    /// </summary>
    public partial class SessionTrashWindow : Window
    {
        private readonly int _roomId;
        private readonly ObservableCollection<TrashedSessionRow> _rows = new();

        public SessionTrashWindow(int roomId)
        {
            InitializeComponent();
            _roomId = roomId;
            TrashGrid.ItemsSource = _rows;
            Loaded += async (_, _) => await LoadAsync();
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var response = await client.GetAsync($"{ApiEndpoints.RoomsTrashPrefix}/{_roomId}/trash");
                if (!response.IsSuccessStatusCode)
                {
                    MessageBox.Show(
                        $"Unable to load trash.\n\nServer responded: {(int)response.StatusCode} {response.ReasonPhrase}",
                        "Trash",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var items = await response.Content.ReadFromJsonAsync<List<TrashedSessionDto>>() ?? new List<TrashedSessionDto>();

                _rows.Clear();
                foreach (var dto in items)
                {
                    _rows.Add(TrashedSessionRow.From(dto));
                }

                RefreshEmptyAndSummary();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load trash: {ex.Message}", "Trash", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshEmptyAndSummary()
        {
            bool isEmpty = _rows.Count == 0;
            TxtEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            TrashGrid.Visibility     = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            TxtSummary.Text          = isEmpty ? string.Empty : $"{_rows.Count} session(s) in Trash";
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private async void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not TrashedSessionRow row) return;

            var confirm = MessageBox.Show(
                $"Restore {row.SessionId} from Trash?\n\nIt will reappear in the Past Session list.",
                "Restore Session",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            if (confirm != MessageBoxResult.Yes) return;

            btn.IsEnabled = false;
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                // No body — sessionId is in the path.
                var response = await client.PostAsync($"{ApiEndpoints.RoomsSessionRestorePrefix}/{row.RealSessionId}/restore", content: null);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    MessageBox.Show(
                        $"Could not restore session.\n\nServer responded: {(int)response.StatusCode} {response.ReasonPhrase}\n{body}",
                        "Restore Session",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    btn.IsEnabled = true;
                    return;
                }

                _rows.Remove(row);
                RefreshEmptyAndSummary();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to restore session: {ex.Message}", "Restore Session", MessageBoxButton.OK, MessageBoxImage.Error);
                btn.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// Row view-model for the Trash DataGrid. Pre-formats display
    /// strings so the XAML stays free of converters.
    /// </summary>
    public class TrashedSessionRow
    {
        public string SessionId { get; set; } = string.Empty;
        public int    RealSessionId { get; set; }
        public string DateDisplay { get; set; } = string.Empty;
        public string Duration { get; set; } = string.Empty;
        public string AttendeesDisplay { get; set; } = string.Empty;
        public string TrashedAtDisplay { get; set; } = string.Empty;
        public string InTrashDisplay { get; set; } = string.Empty;

        public static TrashedSessionRow From(TrashedSessionDto dto)
        {
            string duration;
            if (dto.EndTime.HasValue)
            {
                var span = dto.EndTime.Value - dto.StartTime;
                int mins = (int)Math.Round(span.TotalMinutes);
                duration = mins <= 1 ? "1 min" : $"{mins} mins";
            }
            else
            {
                duration = "—";
            }

            var deletedLocal = dto.DeletedAt.ToLocalTime();
            var daysInTrash  = Math.Max(0, (int)Math.Floor((DateTime.UtcNow - dto.DeletedAt).TotalDays));

            return new TrashedSessionRow
            {
                SessionId        = $"Session {dto.SessionNumber}",
                RealSessionId    = dto.Id,
                DateDisplay      = dto.StartTime.ToLocalTime().ToString("MMM dd, yyyy - hh:mm tt"),
                Duration         = duration,
                AttendeesDisplay = $"{dto.ParticipantCount}/{dto.EnrolledCount}",
                TrashedAtDisplay = deletedLocal.ToString("MMM dd, yyyy - hh:mm tt"),
                InTrashDisplay   = daysInTrash == 0 ? "Today" : (daysInTrash == 1 ? "1 day" : $"{daysInTrash} days"),
            };
        }
    }
}
