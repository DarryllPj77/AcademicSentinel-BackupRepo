using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
    /// is set (i.e. soft-deleted via the bulk Delete Selected action
    /// on the Past Session grid) and allows bulk Restore or bulk
    /// Permanent Delete. Opened as a dialog from
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

                ResetSelectAllLabel();
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

            int selected = _rows.Count(r => r.IsSelected);
            TxtSummary.Text = isEmpty
                ? string.Empty
                : (selected == 0
                    ? $"{_rows.Count} session(s) in Trash"
                    : $"{selected} of {_rows.Count} selected");
        }

        private void ResetSelectAllLabel()
        {
            if (TxtSelectAllTrashLabel != null)
                TxtSelectAllTrashLabel.Text = "Select All";
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // Select-all toggle for the entire visible Trash list.
        // No filter exists on this view, so "visible" == _rows.
        private void BtnSelectAllTrash_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0) return;

            bool allSelected = _rows.All(r => r.IsSelected);
            bool newValue    = !allSelected;
            foreach (var row in _rows)
            {
                row.IsSelected = newValue;
            }

            if (TxtSelectAllTrashLabel != null)
                TxtSelectAllTrashLabel.Text = newValue ? "Clear Selection" : "Select All";

            RefreshEmptyAndSummary();
        }

        // Bulk restore. POST {ids} to /sessions/bulk-restore; server
        // replies with {processed, skipped}. We remove only the
        // confirmed-restored IDs so the grid mirrors server truth
        // even if a row was already restored from another tab.
        private async void BtnRestoreSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(
                    "Tick the rows you want to restore, then click Restore Selected again.",
                    "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Restore {selected.Count} session(s) from Trash?\n\nThey will reappear in the Past Session list.",
                "Restore Sessions",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (confirm != MessageBoxResult.Yes) return;

            BtnRestoreSelected.IsEnabled = false;
            BtnPurgeSelected.IsEnabled   = false;
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var body = new BulkSessionIdsDto { Ids = selected.Select(r => r.RealSessionId).ToList() };
                var response = await client.PostAsJsonAsync(ApiEndpoints.RoomsSessionsBulkRestore, body);
                if (!response.IsSuccessStatusCode)
                {
                    var errText = await response.Content.ReadAsStringAsync();
                    MessageBox.Show(
                        $"Bulk restore failed.\n\nServer responded: {(int)response.StatusCode} {response.ReasonPhrase}\n{errText}",
                        "Restore Sessions", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = await response.Content.ReadFromJsonAsync<BulkSessionsActionResponse>() ?? new BulkSessionsActionResponse();
                ApplyBulkResult(result, selected, action: "Restored");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Bulk restore failed: {ex.Message}", "Restore Sessions", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRestoreSelected.IsEnabled = true;
                BtnPurgeSelected.IsEnabled   = true;
            }
        }

        // Bulk permanent delete. Two-step confirmation because this
        // bypasses the retention window — once the server commits,
        // there is no undo and ArchiveCleanupService can't recover
        // anything.
        private async void BtnPurgeSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(
                    "Tick the rows you want to delete permanently, then click Delete Permanently again.",
                    "Nothing Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Permanently delete {selected.Count} session(s)?\n\n" +
                "This cannot be undone. The sessions will be removed from the database immediately, " +
                "bypassing the normal Trash retention window.",
                "Delete Permanently",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            BtnRestoreSelected.IsEnabled = false;
            BtnPurgeSelected.IsEnabled   = false;
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var body = new BulkSessionIdsDto { Ids = selected.Select(r => r.RealSessionId).ToList() };
                var response = await client.PostAsJsonAsync(ApiEndpoints.RoomsSessionsBulkPurge, body);
                if (!response.IsSuccessStatusCode)
                {
                    var errText = await response.Content.ReadAsStringAsync();
                    MessageBox.Show(
                        $"Bulk permanent delete failed.\n\nServer responded: {(int)response.StatusCode} {response.ReasonPhrase}\n{errText}",
                        "Delete Permanently", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = await response.Content.ReadFromJsonAsync<BulkSessionsActionResponse>() ?? new BulkSessionsActionResponse();
                ApplyBulkResult(result, selected, action: "Permanently deleted");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Bulk permanent delete failed: {ex.Message}", "Delete Permanently", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRestoreSelected.IsEnabled = true;
                BtnPurgeSelected.IsEnabled   = true;
            }
        }

        // Shared result-application path for both bulk endpoints
        // (same response shape). Removes confirmed rows from the
        // grid, surfaces a skipped summary if any, and resets the
        // Select-All label so the next click reads correctly.
        private void ApplyBulkResult(BulkSessionsActionResponse result, List<TrashedSessionRow> attempted, string action)
        {
            var processedSet = result.Processed.ToHashSet();
            foreach (var row in attempted.Where(r => processedSet.Contains(r.RealSessionId)).ToList())
            {
                _rows.Remove(row);
            }

            ResetSelectAllLabel();
            RefreshEmptyAndSummary();

            if (result.Skipped.Count > 0)
            {
                var summary = string.Join("\n", result.Skipped.Select(s =>
                    $"  • Session {s.Id}: {s.Reason}{(string.IsNullOrEmpty(s.Status) ? "" : $" ({s.Status})")}"));
                MessageBox.Show(
                    $"{action} {result.Processed.Count} session(s).\n\n{result.Skipped.Count} skipped:\n{summary}",
                    "Trash", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    /// <summary>
    /// Row view-model for the Trash DataGrid. Pre-formats display
    /// strings so the XAML stays free of converters. Implements
    /// INotifyPropertyChanged so the Select All button can flip
    /// IsSelected programmatically and the row checkboxes refresh.
    /// </summary>
    public class TrashedSessionRow : INotifyPropertyChanged
    {
        public string SessionId { get; set; } = string.Empty;
        public int    RealSessionId { get; set; }
        public string DateDisplay { get; set; } = string.Empty;
        public string Duration { get; set; } = string.Empty;
        public string AttendeesDisplay { get; set; } = string.Empty;
        public string TrashedAtDisplay { get; set; } = string.Empty;
        public string InTrashDisplay { get; set; } = string.Empty;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

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
