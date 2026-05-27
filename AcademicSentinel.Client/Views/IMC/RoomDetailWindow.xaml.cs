using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AcademicSentinel.Client.Models;

namespace AcademicSentinel.Client.Views.IMC
{
    public partial class RoomDetailWindow : Window
    {
        public int CurrentRoomId { get; private set; }
        public ObservableCollection<SessionItem> Sessions { get; set; }
        private ICollectionView _sessionsView;

        public RoomDetailWindow(int roomId, string roomTitle)
        {
            InitializeComponent();

            CurrentRoomId = roomId;
            TxtRoomTitle.Text = roomTitle;

            Sessions = new ObservableCollection<SessionItem>();
            _sessionsView = CollectionViewSource.GetDefaultView(Sessions);
            _sessionsView.Filter = SessionMatchesFilters;
            PastSessionsGrid.ItemsSource = _sessionsView; // Post-merge fix: SessionsList renamed to PastSessionsGrid in XAML

            // Load Sidebar Branding
            LoadTeacherSidebarInfo();

            // Load Database Content
            FetchRoomStatus();
            _ = LoadPastSessionsAsync();
        }

        private void LoadTeacherSidebarInfo()
        {
            if (SessionManager.CurrentUser != null)
            {
                TxtSidebarProfName.Text = !string.IsNullOrWhiteSpace(SessionManager.CurrentUser.FullName)
                    ? SessionManager.CurrentUser.FullName
                    : SessionManager.CurrentUser.Email.Split('@')[0];

                if (!string.IsNullOrEmpty(SessionManager.CurrentUser.ProfileImageUrl))
                {
                    try
                    {
                        // Safety: Trim slashes to prevent "http://localhost:5000//path"
                        string baseUrl = ApiEndpoints.BaseUrl.TrimEnd('/');
                        string imgPath = SessionManager.CurrentUser.ProfileImageUrl.TrimStart('/');
                        string fullUrl = $"{baseUrl}/{imgPath}";

                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(fullUrl);
                        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // Force fresh download
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();

                        SidebarProfileBrush.ImageSource = bitmap;
                        SidebarProfileImage.Visibility = Visibility.Visible;
                        SidebarDefaultIcon.Visibility = Visibility.Collapsed;
                    }
                    catch (Exception ex)
                    {
                        // If this hits, your URL is likely malformed. 
                        System.Diagnostics.Debug.WriteLine($"PFP Load Failed: {ex.Message}");
                    }
                }
            }
        }

        // ======================== UPDATED SIDEBAR NAVIGATION ========================

        // Profile button lands the user on the Account Profile panel — bug fix:
        // previously this routed to the Courses panel just like NavCourses_Click.
        private void NavProfile_Click(object sender, RoutedEventArgs e) => NavigateBackToDashboard(landOnProfile: true);
        private void NavCourses_Click(object sender, RoutedEventArgs e) => NavigateBackToDashboard(landOnProfile: false);

        // Back button also returns to the Dashboard (Courses panel by default).
        private void BtnBack_Click(object sender, RoutedEventArgs e) => NavigateBackToDashboard(landOnProfile: false);

        // Helper method to handle the transition.
        private void NavigateBackToDashboard(bool landOnProfile)
        {
            // Persist the current window state so the dashboard reopens
            // in the same Maximized/Normal mode the teacher had here.
            TeacherDashboard.LastWindowState = this.WindowState;

            var dashboard = new TeacherDashboard(landOnProfile)
            {
                WindowState = this.WindowState
            };
            dashboard.Show();

            // Close this Room Detail window to prevent window piling
            this.Close();
        }

        // Help button goes to the Shared Help Guide
        private void NavHelp_Click(object sender, RoutedEventArgs e)
        {
            var helpWindow = new AcademicSentinel.Client.Views.Shared.HelpGuideWindow();
            helpWindow.Owner = this; // Centers it over the room window

            // ShowDialog opens it as a popup. The user must close it to click the room again.
            helpWindow.ShowDialog();

            // REMOVED: this.Close(); 
        }

        // ======================== DATA LOADING ========================

        private async Task LoadPastSessionsAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                    var response = await client.GetAsync($"{ApiEndpoints.Rooms}/{CurrentRoomId}/history");

                    if (response.IsSuccessStatusCode)
                    {
                        var pastSessions = await response.Content.ReadFromJsonAsync<List<PastSessionDto>>();

                        if (pastSessions != null)
                        {
                            Sessions.Clear();
                            foreach (var s in pastSessions)
                            {
                                DateTime startTime = s.StartTime.ToLocalTime();
                                string status = s.Status;

                                // Date column = clean local date/time only.
                                string dateText = startTime.ToString("MMM dd, yyyy - hh:mm tt");

                                // Duration column = how long the session actually ran,
                                // in minutes. Empty string if never ended.
                                string durationOnly = "—";
                                string endedAtText = "—";
                                if (s.EndTime.HasValue)
                                {
                                    DateTime endTime = s.EndTime.Value.ToLocalTime();
                                    int minutes = (int)Math.Round((endTime - startTime).TotalMinutes);
                                    if (minutes < 1) minutes = 1;
                                    durationOnly = $"{minutes} min{(minutes == 1 ? "" : "s")}";
                                    endedAtText = endTime.ToString("MMM dd, yyyy - hh:mm tt");
                                }

                                Sessions.Add(new SessionItem
                                {
                                    SessionId = $"Session {Math.Max(1, s.SessionNumber)}",
                                    RealSessionId = s.Id,
                                    DateDuration = dateText,
                                    Duration = durationOnly,
                                    EndedAtDisplay = endedAtText,
                                    StatusText = status,
                                    Status = status,
                                    ExamType = string.IsNullOrWhiteSpace(s.ExamType) ? "Summative" : s.ExamType,
                                    StudentCount = s.ParticipantCount,
                                    EnrolledCount = s.EnrolledCount
                                });
                            }
                            UpdatePaginationUI();
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); } // Post-merge fix
        }

        private async void FetchRoomStatus()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                    var response = await client.GetAsync($"{ApiEndpoints.Rooms}/{CurrentRoomId}/status");
                    if (!response.IsSuccessStatusCode)
                    {
                        ToggleSessionPanels(activeSessionLive: false);
                        return;
                    }

                    // Banner is now driven PURELY by the new server-side
                    // flag `instructorDisconnected` — set when the
                    // instructor's IMC connection drops without an End
                    // Session click, cleared when they call JoinRoom or
                    // when End Session runs. This decouples the banner
                    // from ANY student / session / room.Status state, so
                    // student disconnects can no longer keep the banner
                    // alive after the teacher has cleanly ended.
                    var dto = await response.Content.ReadFromJsonAsync<RoomStatusDto>();
                    bool isLive = dto != null && dto.instructorDisconnected;
                    ToggleSessionPanels(activeSessionLive: isLive);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                ToggleSessionPanels(activeSessionLive: false);
            }
        }

        /// <summary>
        /// Mutually exclusive visibility — only one of the two top panels
        /// (rejoin vs. create-new) is shown at a time.
        /// </summary>
        private void ToggleSessionPanels(bool activeSessionLive)
        {
            if (ActiveSessionPanel != null)
                ActiveSessionPanel.Visibility = activeSessionLive ? Visibility.Visible : Visibility.Collapsed;
            if (NewSessionPanel != null)
                NewSessionPanel.Visibility = activeSessionLive ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// Opens the LiveSessionMonitoringWindow against the room's in-progress
        /// session. The 2-arg constructor (used by the past-session row click)
        /// already locates the active session server-side, so we reuse it here.
        /// </summary>
        private void BtnRejoinSession_Click(object sender, RoutedEventArgs e)
        {
            var liveWindow = new LiveSessionMonitoringWindow(CurrentRoomId, TxtRoomTitle.Text);
            this.Hide();
            liveWindow.Closed += async (_, __) =>
            {
                this.Show();
                await RefreshAfterLiveSessionClosedAsync();
            };
            liveWindow.Show();
        }

        // After the LiveSessionMonitoringWindow closes (end / cancel / timer
        // expiry), the just-completed ExamSession must show up in the Past
        // Sessions table immediately — otherwise the teacher sees an empty
        // table for a beat and assumes End Session failed. Two concrete
        // problems we're fixing here:
        //   1) The previous handler used `_ = LoadPastSessionsAsync()` —
        //      fire-and-forget. The dashboard re-rendered with the OLD
        //      Sessions list visible before the new GET returned. Now we
        //      await the load so the table is populated before the user
        //      sees the window.
        //   2) Database visibility lag — on slower machines the PUT-end /
        //      force-reset commits return before the next read sees the
        //      Status="Completed" row (read-your-write across connection
        //      pools, especially with EF's tracking layer). We retry up to
        //      3 times with short back-off if the list comes back empty so
        //      the table is never visibly blank when a session was just
        //      ended.
        private async System.Threading.Tasks.Task RefreshAfterLiveSessionClosedAsync()
        {
            FetchRoomStatus();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                await LoadPastSessionsAsync();
                if (Sessions.Count > 0)
                    break;
                await System.Threading.Tasks.Task.Delay(350);
            }
        }

        /// <summary>
        /// Thin DTO mirror of GET /api/rooms/{id}/status. Local-only — kept
        /// nested to avoid polluting the wider Models namespace.
        /// </summary>
        private sealed class RoomStatusDto
        {
            public int roomId { get; set; }
            public string status { get; set; }
            public bool isMonitoringActive { get; set; }
            public string subjectName { get; set; }
            public int? activeSessionId { get; set; }
            // True only when the instructor's IMC connection dropped
            // without End Session being clicked. This is the canonical
            // condition for the dashboard's Rejoin banner.
            public bool instructorDisconnected { get; set; }
        }

        private void BtnCreateSession_Click(object sender, RoutedEventArgs e)
        {
            var setupWindow = new CreateSessionSetupWindow(CurrentRoomId, TxtRoomTitle.Text);
            setupWindow.Owner = this;
            if (setupWindow.ShowDialog() == true)
            {
                var liveWindow = new LiveSessionMonitoringWindow(
                    CurrentRoomId,
                    TxtRoomTitle.Text,
                    setupWindow.CreatedSessionId,
                    setupWindow.MonitoringDurationSeconds,
                    setupWindow.EndSessionWhenTimerEnds,
                    setupWindow.StartDelaySeconds);
                this.Hide();
                liveWindow.Closed += async (_, __) =>
                {
                    this.Show();
                    await RefreshAfterLiveSessionClosedAsync();
                };
                liveWindow.Show();
            }
        }

        private void BtnStudentList_Click(object sender, RoutedEventArgs e)
        {
            // 1. Open the actual Student List Window
            var studentListWindow = new StudentListWindow(CurrentRoomId, TxtRoomTitle.Text);
            studentListWindow.Owner = this;
            studentListWindow.ShowDialog();

            // 2. Refresh the room status when the list window closes 
            // (in case the teacher added/removed students while in that window)
            FetchRoomStatus();
        }

        private void SessionAction_Click(object sender, RoutedEventArgs e) => new LiveSessionMonitoringWindow(CurrentRoomId, TxtRoomTitle.Text).Show();
        private async void BtnGenerateCode_Click(object sender, RoutedEventArgs e) { /* Code Generation Logic */ }
        private async void BtnForceEndStuckSession_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var roomResponse = await client.GetAsync($"{ApiEndpoints.Rooms}/{CurrentRoomId}");
                if (!roomResponse.IsSuccessStatusCode)
                {
                    MessageBox.Show("Unable to check room status.", "Force End", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var room = await roomResponse.Content.ReadFromJsonAsync<RoomStatusSnapshot>();
                if (room == null)
                {
                    MessageBox.Show("Room data unavailable.", "Force End", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!string.Equals(room.Status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Room is not active.", "Force End", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var historyResponse = await client.GetAsync($"{ApiEndpoints.Rooms}/{CurrentRoomId}/history");
                if (!historyResponse.IsSuccessStatusCode)
                {
                    MessageBox.Show("Unable to load room session history.", "Force End", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var history = await historyResponse.Content.ReadFromJsonAsync<List<PastSessionDto>>() ?? new List<PastSessionDto>();
                var activeSession = history
                    .Where(s => string.Equals(s.Status, "Active", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.StartTime)
                    .FirstOrDefault();

                if (activeSession == null)
                {
                    MessageBox.Show("No active session record found to end.", "Force End", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var endResponse = await client.PutAsync($"{ApiEndpoints.Rooms}/sessions/{activeSession.Id}/end", null);
                if (!endResponse.IsSuccessStatusCode)
                {
                    MessageBox.Show("Failed to force end stuck session.", "Force End", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                FetchRoomStatus();
                await LoadPastSessionsAsync();
                MessageBox.Show("Room forcefully reset.", "Force End", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to reset room: {ex.Message}", "Force End", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _sessionsView?.Refresh();
            UpdatePaginationUI();
        }

        // Post-merge fix: UpdatePaginationUI was referenced from the merged code but never defined; minimal stub keeps call sites valid.
        private void UpdatePaginationUI()
        {
            int total = Sessions?.Count ?? 0;
            int visible = _sessionsView != null ? _sessionsView.Cast<object>().Count() : total;
            if (TxtPaginationInfo != null)
            {
                TxtPaginationInfo.Text = $"Showing {visible} of {total} sessions";
            }
        }

        private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _sessionsView?.Refresh();
        }

        private bool SessionMatchesFilters(object obj)
        {
            if (obj is not SessionItem item) return false;

            // Status filter from ComboBox
            string selectedStatus = (CmbFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Sessions";
            if (!string.Equals(selectedStatus, "All Sessions", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(item.Status, selectedStatus, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.StatusText, selectedStatus, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Search filter (matches session id or date/duration text)
            string searchTerm = TxtSearch?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(searchTerm)) return true;

            return (item.SessionId?.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                || (item.DateDuration?.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                || (item.EndedAtDisplay?.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                || (item.ExamType?.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        private void BtnViewArchive_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.DataContext is SessionItem selectedSession) // Bug fix: A1 - corrected DataContext cast from SessionArchiveDto to SessionItem
            {
                var detailWindow = new SessionArchiveDetailWindow(selectedSession.RealSessionId); // Bug fix: Bug2 - pass real DB PK instead of parsed ordinal
                detailWindow.Owner = this;
                detailWindow.ShowDialog();
            }
        }
        // Post-merge fix: removed duplicate empty stubs of TxtSearch_TextChanged and CmbFilter_SelectionChanged (real handlers above)
        private void ViewSession_Click(object sender, RoutedEventArgs e) { }

        // Soft-delete (Trash) a Past Session archive. Confirms, then
        // calls DELETE /api/rooms/sessions/{realSessionId}. On success
        // the row is removed from the Sessions ObservableCollection so
        // the DataGrid updates instantly without a refetch. The server
        // sets DeletedAt and keeps the row until ArchiveCleanupService
        // hard-deletes it after the configured retention window.
        // Note: uses SessionItem.RealSessionId (the DB primary key),
        // NOT the display SessionId — same fix as Bug2 on
        // BtnViewArchive_Click above.
        private async void DeleteSession_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn || btn.DataContext is not SessionItem selectedSession) return;

            var confirm = System.Windows.MessageBox.Show(
                $"Move {selectedSession.SessionId} to Trash?\n\nIt will be permanently deleted after the retention period.",
                "Delete Session Archive",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            btn.IsEnabled = false;
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var response = await client.DeleteAsync($"{ApiEndpoints.RoomsSessionDeletePrefix}/{selectedSession.RealSessionId}");
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    System.Windows.MessageBox.Show(
                        $"Could not delete {selectedSession.SessionId}.\n\nServer responded: {(int)response.StatusCode} {response.ReasonPhrase}\n{body}",
                        "Delete Session Archive",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    btn.IsEnabled = true;
                    return;
                }

                Sessions.Remove(selectedSession);
                UpdatePaginationUI();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Failed to delete session: {ex.Message}",
                    "Delete Session Archive",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                btn.IsEnabled = true;
            }
        }
    }

    public class SessionItem
    {
        public string SessionId { get; set; } = string.Empty;
        public int RealSessionId { get; set; } // DB primary key (ExamSessions.Id) for SessionArchiveDetailWindow lookups
        public string DateDuration { get; set; } = string.Empty; // formatted "MMM dd, yyyy - hh:mm tt"
        public string Duration { get; set; } = string.Empty;     // formatted "N mins" or "—" if not ended
        // Session end timestamp, formatted in local time. Falls back to
        // "—" when the underlying ExamSession.EndTime is null (the row
        // was force-closed by a self-heal pass that never stamped a
        // wall-clock end). The Duration column already handles the same
        // fallback so the two columns stay visually consistent.
        public string EndedAtDisplay { get; set; } = "—";
        public string StatusText { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ExamType { get; set; } = string.Empty;
        public int StudentCount { get; set; }
        public int EnrolledCount { get; set; }

        // Display ratio: "attended / enrolled in course", e.g. "2/5".
        public string AttendeesDisplay => $"{StudentCount}/{EnrolledCount}";
    }

    public class PastSessionDto
    {
        public int Id { get; set; }
        public int SessionNumber { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string Status { get; set; } = string.Empty;
        public string ExamType { get; set; } = string.Empty;
        public int ParticipantCount { get; set; }
        public int EnrolledCount { get; set; }
    }

    public class GenerateCodeResponse { public string EnrollmentCode { get; set; } = string.Empty; }

    public class RoomStatusSnapshot
    {
        public string Status { get; set; } = string.Empty;
    }
}