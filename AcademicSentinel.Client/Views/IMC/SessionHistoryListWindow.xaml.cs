using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Models;
using AcademicSentinel.Client.Services;

namespace AcademicSentinel.Client.Views.IMC
{
    public partial class SessionHistoryListWindow : Window
    {
        private readonly int _roomId;
        private readonly ObservableCollection<SessionArchiveDto> _sessions = new();

        public SessionHistoryListWindow(int roomId)
        {
            InitializeComponent();
            _roomId = roomId;
            HistoryDataGrid.ItemsSource = _sessions;
            Loaded += SessionHistoryListWindow_Loaded;
        }

        private async void SessionHistoryListWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadSessionsAsync();
        }

        private async System.Threading.Tasks.Task LoadSessionsAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var response = await client.GetAsync($"{ApiEndpoints.Rooms}/{_roomId}/sessions");
                if (!response.IsSuccessStatusCode)
                {
                    MessageBox.Show("Unable to load archived sessions.", "Session History", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var items = await response.Content.ReadFromJsonAsync<List<SessionArchiveDto>>() ?? new List<SessionArchiveDto>();

                _sessions.Clear();
                foreach (var item in items)
                {
                    _sessions.Add(item);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load sessions: {ex.Message}", "Session History", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenSessionDetail(SessionArchiveDto session)
        {
            if (session == null)
                return;

            var detailWindow = new SessionArchiveDetailWindow(session.SessionId)
            {
                Owner = this
            };

            detailWindow.ShowDialog();
        }

        private void HistoryDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (HistoryDataGrid.SelectedItem is SessionArchiveDto selectedSession)
            {
                OpenSessionDetail(selectedSession);
            }
        }

        private void BtnView_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is SessionArchiveDto selectedSession)
            {
                OpenSessionDetail(selectedSession);
            }
        }

        // Soft-delete (Trash) a Past Session archive. The row goes
        // to Trash on the server (DeletedAt timestamp set) and is
        // hard-deleted later by ArchiveCleanupService after the
        // configured retention window. Removed from the in-memory
        // ObservableCollection on success so the grid updates
        // immediately without a full reload.
        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not SessionArchiveDto session) return;

            var confirm = MessageBox.Show(
                $"Move session {session.SessionId} to Trash?\n\nIt will be permanently deleted after the retention period.",
                "Delete Session Archive",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            btn.IsEnabled = false;
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var response = await client.DeleteAsync($"{ApiEndpoints.RoomsSessionDeletePrefix}/{session.SessionId}");
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    MessageBox.Show(
                        $"Could not delete session {session.SessionId}.\n\nServer responded: {(int)response.StatusCode} {response.ReasonPhrase}\n{body}",
                        "Delete Session Archive",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    btn.IsEnabled = true;
                    return;
                }

                _sessions.Remove(session);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to delete session: {ex.Message}",
                    "Delete Session Archive",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                btn.IsEnabled = true;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
