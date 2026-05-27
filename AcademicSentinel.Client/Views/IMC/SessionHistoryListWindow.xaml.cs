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

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
