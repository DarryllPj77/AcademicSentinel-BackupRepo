using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Services;
using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading; // NEW: Required for the Live Timer
using System.Collections.Generic;
using System.Collections.Concurrent;
using MaterialDesignThemes.Wpf;
using AcademicSentinel.Client.Models;
using AcademicSentinel.Client.Views.IMC.Dialogs;

namespace AcademicSentinel.Client.Views.IMC
{
    public partial class LiveSessionMonitoringWindow : Window
    {
        private enum MonitoringControlState
        {
            NotStarted,
            Active,
            Paused
        }

        private HubConnection _hubConnection;
        private int _roomId;
        private int _totalAlerts = 0;
        private int _currentSessionId;
        private bool _isMonitoringStarted;
        private int _enrolledCount;
        private readonly int _monitoringDurationSeconds;
        private readonly bool _endSessionWhenTimerEnds;
        private readonly int _startDelaySeconds;
        private bool _isEndingFromTimer;
        private DateTime? _monitoringEffectiveStartTime;
        private int _countdownSecondsRemaining;
        private MonitoringControlState _monitoringControlState = MonitoringControlState.NotStarted;

        // Timer Variables
        private DispatcherTimer _sessionTimer;
        private DispatcherTimer _participantsRefreshTimer;
        private DateTime _sessionStartTime;
        private bool _isSessionEnded;

        private ICollectionView _studentsView;
        private ICollectionView _logsView;
        private LiveStudentStatus _selectedStudent;
        private List<ParticipantDto> _allParticipants = new List<ParticipantDto>();
        private readonly Dictionary<int, bool> _leaveRequestedStateByStudentId = new();
        private readonly Dictionary<int, JoinApprovalRequestDto> _pendingJoinApprovals = new();
        private readonly HashSet<int> _safelyLeftStudentIds = new();
        private readonly HashSet<int> _permanentlyDismissedStudents = new HashSet<int>();
        // Tracks students for whom a STUDENT_DISCONNECTED line has already
        // been written to the Global Feed since their last join. Both the
        // SignalR push handler and the participant-poll diff guard against
        // it so the same disconnect can't be logged twice. Cleared on
        // rejoin via the StudentJoinedOrReconnected handler.
        private readonly HashSet<int> _disconnectLoggedStudentIds = new();
        private readonly HashSet<int> _studentsWithViolations = new HashSet<int>();
        private readonly ConcurrentDictionary<int, ObservableCollection<StudentMonitoringEvent>> _studentLogs = new();
        private readonly List<IDisposable> _hubSubscriptions = new();
        private int? _selectedStudentId;

        public ObservableCollection<LiveStudentStatus> ActiveStudents { get; set; }
        public ObservableCollection<LogEntry> LogFeed { get; set; }

        // True when the window opened via RoomDetailWindow's "Rejoin Session"
        // button (2-arg ctor with no sessionId). False when opened from the
        // Create Session setup wizard (sessionId passed). Used to discriminate
        // the Window_Loaded state-sync path — only rejoins should adopt the
        // server's current monitoring state; fresh creates start green.
        private readonly bool _openedAsRejoin;

        public LiveSessionMonitoringWindow(int roomId, string roomTitle, int sessionId = 0, int monitoringDurationSeconds = 3600, bool endSessionWhenTimerEnds = true, int startDelaySeconds = 10)
        {
            InitializeComponent();
            _roomId = roomId;
            _currentSessionId = sessionId;
            _openedAsRejoin = sessionId <= 0;
            _monitoringDurationSeconds = monitoringDurationSeconds;
            _endSessionWhenTimerEnds = endSessionWhenTimerEnds;
            _startDelaySeconds = Math.Max(0, startDelaySeconds);

            TxtRoomHeader.Text = $"Live Session Monitoring - {roomTitle}";
            if (FindName("TxtMonitoringState") is TextBlock monitoringState)
                monitoringState.Text = "Monitoring: Inactive";

            if (SessionManager.CurrentUser != null)
            {
                TxtProfName.Text = !string.IsNullOrWhiteSpace(SessionManager.CurrentUser.FullName)
                    ? SessionManager.CurrentUser.FullName
                    : SessionManager.CurrentUser.Email.Split('@')[0];
                SetTeacherAvatar(SessionManager.CurrentUser);
            }

            ActiveStudents = new ObservableCollection<LiveStudentStatus>();
            LogFeed = new ObservableCollection<LogEntry>();

            _studentsView = CollectionViewSource.GetDefaultView(ActiveStudents);
            _logsView = CollectionViewSource.GetDefaultView(LogFeed);

            // NEW: Auto-Sort Logic! 
            // 1st Priority: Most violations go to the top
            // 2nd Priority: Alphabetical by Email
            _studentsView.SortDescriptions.Add(new SortDescription("ViolationCount", ListSortDirection.Descending));
            _studentsView.SortDescriptions.Add(new SortDescription("Email", ListSortDirection.Ascending));

            StudentsItemsControl.ItemsSource = _studentsView;
            LogFeedItemsControl.ItemsSource = _logsView;

            InitializeTimerIndicatorState();
            SetMonitoringControlButtonState(MonitoringControlState.NotStarted);

            _ = LoadParticipantsFromServerAsync();

            _participantsRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(4)
            };
            _participantsRefreshTimer.Tick += async (_, __) => await LoadParticipantsFromServerAsync();
            _participantsRefreshTimer.Start();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeSignalR();
            await _hubConnection.InvokeAsync("JoinRoom", _roomId.ToString());

            // REJOIN STATE SYNC — only when the window was opened via the
            // RoomDetailWindow "Rejoin Session" button (no sessionId passed
            // to the ctor). Fresh Create Session flows pass a real sessionId
            // and must NOT adopt the server's state — those should start at
            // green "Start Session Monitoring" because the teacher hasn't
            // pressed Start yet.
            if (_openedAsRejoin)
            {
                await SyncMonitoringStateFromServerAsync();
                // Replay every event that happened while the teacher was
                // away (e.g. STUDENT_DISCONNECTED while the IMC was closed)
                // so the Global Log Feed shows the full picture instead of
                // "No log entries yet".
                if (_currentSessionId > 0)
                    await LoadHistoricalLogsAsync(_currentSessionId);
            }
        }

        /// <summary>
        /// Fetches every MonitoringEvent for the current session from the
        /// server and replays it into LogFeed in chronological order. Only
        /// invoked on the rejoin path so a fresh "Create Session" doesn't
        /// re-render any pre-existing events.
        /// </summary>
        private async Task LoadHistoricalLogsAsync(int sessionId)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var response = await client.GetAsync($"{ApiEndpoints.BaseUrl}/api/Reports/sessions/{sessionId}/students");
                if (!response.IsSuccessStatusCode) return;

                var students = await response.Content.ReadFromJsonAsync<List<HistoricalStudentDto>>();
                if (students == null || students.Count == 0) return;

                // Flatten the per-student log lists into a single timeline.
                var timeline = students
                    .SelectMany(s => (s.Logs ?? new List<HistoricalLogDto>())
                        .Select(l => new { s.Email, Log = l }))
                    .OrderBy(x => x.Log.Timestamp)
                    .ToList();

                foreach (var entry in timeline)
                {
                    var log = entry.Log;
                    string eventType = log.EventType ?? "SYSTEM";
                    string description = log.Description ?? string.Empty;

                    // Badge + color mapping — mirrors the live SignalR
                    // handlers' LogActivity calls so historical replays
                    // look identical to live entries.
                    string badge;
                    string color;
                    if (log.SeverityScore > 0)
                    {
                        badge = "VIOLATION";
                        color = "#D32F2F";
                    }
                    else if (eventType.Equals("STUDENT_DISCONNECTED", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "STUDENT_DISCONNECTED";
                        color = "#D32F2F";
                    }
                    else if (eventType.Equals("CANVAS_RETURNED", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "RETURN";
                        color = "#1B5E20";
                    }
                    else if (eventType.Equals("SESSION_COMPLETION_REQUESTED", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "DONE";
                        color = "#1B5E20";
                    }
                    else if (eventType.Equals("LEAVE_GRANTED", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "LEFT";
                        color = "#1B5E20";
                    }
                    else if (eventType.Equals("LEAVE_REQUEST_DENIED", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "DENIED";
                        color = "#D32F2F";
                    }
                    else if (eventType.Equals("REJOIN_REQUESTED", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "REJOIN_REQ";
                        color = "#FF9800";
                    }
                    else if (eventType.Equals("REJOIN_APPROVED", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "JOIN_OK";
                        color = "#4CAF50";
                    }
                    else if (eventType.Equals("JOIN_DENIED", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "JOIN_NO";
                        color = "#D32F2F";
                    }
                    else if (eventType.Equals("STUDENT_REMOVED", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "KICKED";
                        color = "#D32F2F";
                    }
                    else if (eventType.Equals("TEACHER_DISCONNECTED", StringComparison.OrdinalIgnoreCase)
                          || eventType.Equals("TEACHER_RECONNECTED", StringComparison.OrdinalIgnoreCase))
                    {
                        badge = "SYSTEM";
                        color = "#FF9800";
                    }
                    else
                    {
                        badge = "SYSTEM";
                        color = "#1B5E20";
                    }

                    LogActivity(entry.Email ?? "SYSTEM", badge, description, color);
                }
            }
            catch
            {
                // Best-effort replay — silently skip on transport failure.
            }
        }

        // Minimal DTOs scoped to the replay endpoint. Kept private to avoid
        // leaking a thin shape into the wider Models namespace.
        private sealed class HistoricalStudentDto
        {
            public int StudentId { get; set; }
            public string Name { get; set; }
            public string Email { get; set; }
            public List<HistoricalLogDto> Logs { get; set; }
        }
        private sealed class HistoricalLogDto
        {
            public string EventType { get; set; }
            public string Description { get; set; }
            public int SeverityScore { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private async Task SyncMonitoringStateFromServerAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var response = await client.GetAsync($"{ApiEndpoints.Rooms}/{_roomId}/status");
                if (!response.IsSuccessStatusCode) return;

                var status = await response.Content.ReadFromJsonAsync<RoomStatusSyncDto>();
                if (status == null) return;

                // Adopt the live session id only if we don't already have
                // one (i.e. this is a rejoin, not a fresh Create Session).
                if (_currentSessionId <= 0 && status.activeSessionId.HasValue && status.activeSessionId.Value > 0)
                    _currentSessionId = status.activeSessionId.Value;

                bool roomActive = string.Equals(status.status, "Active", StringComparison.OrdinalIgnoreCase);

                if (roomActive)
                {
                    _isMonitoringStarted = true;
                    _isSessionEnded = false;

                    // REJOIN POLICY: always default to the Active (red
                    // "Pause Monitoring") button state when the room is
                    // live. The student SAC keeps detecting through the
                    // teacher disconnect, so from the teacher's mental
                    // model monitoring IS running — the button should
                    // reflect "click to pause" (red), and a subsequent
                    // click can pause/resume as normal. This avoids the
                    // confusing "orange Resume Monitoring" state that
                    // appeared on rejoin when the server's IsMonitoringActive
                    // flag was momentarily false (e.g. mid-handoff).
                    //
                    // The live MonitoringStateChanged / MonitoringPaused
                    // hub events will correct the state if the teacher
                    // legitimately paused before disconnecting.
                    SetMonitoringControlButtonState(MonitoringControlState.Active);

                    if (FindName("TxtMonitoringState") is TextBlock label)
                        label.Text = "Monitoring: Active (Rejoined)";
                }
            }
            catch
            {
                // Best-effort sync — leave defaults intact on failure.
            }
        }

        private sealed class RoomStatusSyncDto
        {
            public int roomId { get; set; }
            public string status { get; set; }
            public bool isMonitoringActive { get; set; }
            public string subjectName { get; set; }
            public int? activeSessionId { get; set; }
        }

        // ======================== SEARCH & FILTER LOGIC ========================

        private void TxtStudentSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = TxtStudentSearch.Text.ToLower();
            _studentsView.Filter = obj => string.IsNullOrEmpty(filter) || (obj as LiveStudentStatus).Email.ToLower().Contains(filter);
            _studentsView.Refresh();
        }

        private void CmbLogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyAllFilters();
        }

        private void ApplyAllFilters()
        {
            if (_logsView == null) return;

            string category = (CmbLogFilter.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "All Entries";

            _logsView.Filter = obj =>
            {
                var entry = obj as LogEntry;
                bool matchesUser = _selectedStudent == null ||
                                   entry.StudentEmail == _selectedStudent.Email ||
                                   entry.StudentEmail == "SYSTEM";

                bool matchesCategory = true;
                if (category == "Violations Only")
                {
                    matchesCategory = entry.BadgeText == "VIOLATION";
                }
                else if (category == "Connections Only")
                {
                    // Anything that isn't a violation is a connection / lifecycle
                    // / approval event. Covers SYSTEM, KICKED, LEFT, UNLOCK,
                    // LEAVE_REQ, JOIN_REQ, JOIN_OK, JOIN_NO, COUNTDOWN, STARTED,
                    // PAUSED, RESUMED — and any future non-violation badge.
                    matchesCategory = entry.BadgeText != "VIOLATION";
                }

                return matchesUser && matchesCategory;
            };
            _logsView.Refresh();
        }

        private void InitializeTimerIndicatorState()
        {
            var isTimerDisabled = _monitoringDurationSeconds <= 0;

            if (FindName("CountdownDisabledIndicator") is TextBlock countdownDisabledIndicator)
                countdownDisabledIndicator.Visibility = isTimerDisabled ? Visibility.Visible : Visibility.Collapsed;

            if (FindName("TxtCountdownDisplay") is TextBlock countdownDisplay)
                countdownDisplay.Visibility = isTimerDisabled ? Visibility.Collapsed : Visibility.Visible;

            if (FindName("TxtMonitoringTimerDisplay") is TextBlock timerDisplay)
                timerDisplay.Text = isTimerDisabled ? "N/A" : "--:--:--";
        }

        private void SetMonitoringControlButtonState(MonitoringControlState state)
        {
            _monitoringControlState = state;

            if (FindName("TxtStartStopLabel") is not TextBlock label || FindName("StartStopIcon") is not PackIcon icon)
                return;

            switch (state)
            {
                case MonitoringControlState.Active:
                    label.Text = "Pause Monitoring";
                    icon.Kind = PackIconKind.Pause;
                    BtnStartMonitoring.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"));
                    break;

                case MonitoringControlState.Paused:
                    label.Text = "Resume Monitoring";
                    icon.Kind = PackIconKind.Play;
                    BtnStartMonitoring.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9800"));
                    break;

                default:
                    label.Text = "Start Session Monitoring";
                    icon.Kind = PackIconKind.Play;
                    BtnStartMonitoring.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B5E20"));
                    break;
            }
        }

        private async void BtnApproveJoin_Click(object sender, RoutedEventArgs e)
        {
            if (EnsureSessionNotEnded()) return;
            if (sender is not Button btn || btn.DataContext is not LiveStudentStatus student)
                return;

            if (_hubConnection == null)
                return;

            try
            {
                await _hubConnection.InvokeAsync("ApproveStudentJoin", _roomId, student.StudentId);

                student.IsJoinApprovalPending = false;
                student.Status = "Approved";
                student.StatusColor = "#4CAF50";
                _pendingJoinApprovals.Remove(student.StudentId);

                LogActivity(student.Email, "JOIN_OK", "Instructor approved join request.", "#4CAF50");
                _studentsView.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to approve join: {ex.Message}", "Join Approval", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnDenyJoin_Click(object sender, RoutedEventArgs e)
        {
            if (EnsureSessionNotEnded()) return;
            if (sender is not Button btn || btn.DataContext is not LiveStudentStatus student)
                return;

            if (_hubConnection == null)
                return;

            try
            {
                await _hubConnection.InvokeAsync("DenyStudentJoin", _roomId, student.StudentId, "Request denied by instructor.");

                student.IsJoinApprovalPending = false;
                student.Status = "Denied";
                student.StatusColor = "#D32F2F";
                _pendingJoinApprovals.Remove(student.StudentId);

                LogActivity(student.Email, "JOIN_NO", "Instructor denied join request.", "#D32F2F");
                _studentsView.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to deny join: {ex.Message}", "Join Denial", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ======================== SESSION & SIGNALR ========================

        private async void BtnStartMonitoring_Click(object sender, RoutedEventArgs e)
        {
            if (EnsureSessionNotEnded()) return;

            var monitoringState = FindName("TxtMonitoringState") as TextBlock;

            if (_monitoringControlState == MonitoringControlState.Active)
            {
                await PauseMonitoringAsync();
                return;
            }

            if (_monitoringControlState == MonitoringControlState.Paused)
            {
                await ResumeMonitoringAsync();
                return;
            }

            BtnStartMonitoring.IsEnabled = false;
            if (FindName("TxtStartStopLabel") is TextBlock startLabel)
                startLabel.Text = "Starting...";
            try
            {
                if (_currentSessionId <= 0)
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                    var response = await client.PostAsJsonAsync($"{ApiEndpoints.Rooms}/{_roomId}/start-session", new { ExamType = "Summative" });
                    if (!response.IsSuccessStatusCode)
                    {
                        BtnStartMonitoring.IsEnabled = true;
                        SetMonitoringControlButtonState(MonitoringControlState.NotStarted);
                        MessageBox.Show("Failed to start session.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var result = await response.Content.ReadFromJsonAsync<StartSessionResponse>();
                    _currentSessionId = result?.SessionId ?? 0;
                    if (monitoringState != null) monitoringState.Text = $"Monitoring: Ready (Session #{_currentSessionId})";
                }

                await InitializeSignalR();

                if (_startDelaySeconds > 0)
                {
                    await _hubConnection.InvokeAsync("BeginMonitoringCountdown", _roomId, _startDelaySeconds, _monitoringDurationSeconds > 0 ? _monitoringDurationSeconds : 0);
                    _monitoringEffectiveStartTime = DateTime.Now.AddSeconds(_startDelaySeconds);
                    _countdownSecondsRemaining = _startDelaySeconds;
                    if (monitoringState != null) monitoringState.Text = $"Monitoring: Starting in {_startDelaySeconds}s";
                    LogActivity("SYSTEM", "COUNTDOWN", $"Monitoring starts in {_startDelaySeconds} seconds.", "#FF9800");
                }
                else
                {
                    await _hubConnection.InvokeAsync("SetMonitoringState", _roomId, true);
                    _monitoringEffectiveStartTime = DateTime.Now;
                }

                _isMonitoringStarted = true;
                if (monitoringState != null)
                    monitoringState.Text = _startDelaySeconds > 0
                        ? $"Monitoring: Countdown (Session #{_currentSessionId})"
                        : $"Monitoring: Active (Session #{_currentSessionId})";
                SetMonitoringControlButtonState(MonitoringControlState.Active);
                BtnStartMonitoring.IsEnabled = true;
                TimerPanel.Visibility = Visibility.Visible; // Show the timer
                StartSessionTimer(); // Start the clock!

                LogActivity("SYSTEM", "STARTED", "Live monitoring active.", "#1B5E20");
            }
            catch (Exception ex)
            {
                SetMonitoringControlButtonState(MonitoringControlState.NotStarted);
                BtnStartMonitoring.IsEnabled = true;
                MessageBox.Show(ex.Message);
            }
        }

        private async Task PauseMonitoringAsync()
        {
            if (EnsureSessionNotEnded()) return;

            if (_currentSessionId > 0)
            {
                if (_hubConnection != null)
                {
                    await _hubConnection.InvokeAsync("PauseSessionMonitoring", _roomId);
                }
            }

            _isMonitoringStarted = false;
            if (FindName("TxtMonitoringState") is TextBlock monitoringState)
                monitoringState.Text = $"Monitoring: Paused (Session #{_currentSessionId})";
            SetMonitoringControlButtonState(MonitoringControlState.Paused);
            LogActivity("SYSTEM", "PAUSED", "Monitoring paused. Session remains active and soft lock is enforced.", "#FF9800");
        }

        private async Task ResumeMonitoringAsync()
        {
            if (EnsureSessionNotEnded()) return;

            if (_currentSessionId > 0 && _hubConnection != null)
            {
                await _hubConnection.InvokeAsync("ResumeSessionMonitoring", _roomId);
            }

            _isMonitoringStarted = true;
            if (FindName("TxtMonitoringState") is TextBlock monitoringState)
                monitoringState.Text = $"Monitoring: Active (Session #{_currentSessionId})";
            SetMonitoringControlButtonState(MonitoringControlState.Active);
            LogActivity("SYSTEM", "RESUMED", "Monitoring resumed.", "#1B5E20");
        }

        // NEW: Timer Method
        private void StartSessionTimer()
        {
            _sessionStartTime = DateTime.Now;
            _sessionTimer = new DispatcherTimer();
            _sessionTimer.Interval = TimeSpan.FromSeconds(1);
            _sessionTimer.Tick += async (s, e) =>
            {
                var elapsed = DateTime.Now - _sessionStartTime;
                TxtSessionTimer.Text = $"Duration: {elapsed:hh\\:mm\\:ss}";

                var elapsedForAutoStop = _monitoringEffectiveStartTime.HasValue
                    ? TimeSpan.Zero
                    : (_monitoringEffectiveStartTime == null && _startDelaySeconds > 0
                        ? DateTime.Now - (_sessionStartTime + TimeSpan.FromSeconds(_startDelaySeconds))
                        : elapsed);

                if (elapsedForAutoStop < TimeSpan.Zero)
                    elapsedForAutoStop = TimeSpan.Zero;

                if (_monitoringEffectiveStartTime.HasValue && DateTime.Now >= _monitoringEffectiveStartTime.Value)
                {
                    _monitoringEffectiveStartTime = null;
                    _countdownSecondsRemaining = 0;

                    if (FindName("TxtMonitoringState") is TextBlock monitoringState)
                        monitoringState.Text = $"Monitoring: Active (Session #{_currentSessionId})";
                    if (FindName("TxtCountdownDisplay") is TextBlock countdownDisplay)
                        countdownDisplay.Text = "00:00";

                    // ==============================================================
                    // THE FIX: Tell the Database that monitoring is officially ON!
                    // ==============================================================
                    try
                    {
                        if (_hubConnection != null)
                        {
                            await _hubConnection.InvokeAsync("SetMonitoringState", _roomId, true);
                        }
                    }
                    catch { }
                    // ==============================================================
                }
                else if (_monitoringEffectiveStartTime.HasValue)
                {
                    _countdownSecondsRemaining = Math.Max(0, (int)Math.Ceiling((_monitoringEffectiveStartTime.Value - DateTime.Now).TotalSeconds));
                    if (FindName("TxtMonitoringState") is TextBlock monitoringState)
                        monitoringState.Text = $"Monitoring: Countdown {_countdownSecondsRemaining}s";
                    if (FindName("TxtCountdownDisplay") is TextBlock countdownDisplay)
                        countdownDisplay.Text = TimeSpan.FromSeconds(_countdownSecondsRemaining).ToString(@"mm\:ss");
                }
                else if (FindName("TxtCountdownDisplay") is TextBlock noCountdownDisplay)
                {
                    noCountdownDisplay.Text = "00:00";
                }

                if (FindName("TxtMonitoringTimerDisplay") is TextBlock timerDisplay)
                {
                    var timerElapsed = _monitoringEffectiveStartTime.HasValue ? TimeSpan.Zero : elapsedForAutoStop;
                    var configured = TimeSpan.FromSeconds(_monitoringDurationSeconds);
                    var timerRemaining = configured - timerElapsed;
                    if (timerRemaining < TimeSpan.Zero) timerRemaining = TimeSpan.Zero;
                    timerDisplay.Text = _monitoringDurationSeconds > 0 ? timerRemaining.ToString(@"hh\:mm\:ss") : "N/A";
                }

                if (_monitoringDurationSeconds > 0 && !_isEndingFromTimer && elapsedForAutoStop >= TimeSpan.FromSeconds(_monitoringDurationSeconds))
                {
                    _sessionTimer.Stop();

                    if (_endSessionWhenTimerEnds)
                    {
                        _isEndingFromTimer = true;
                        await Dispatcher.InvokeAsync(() => BtnEndSession_Click(this, new RoutedEventArgs()));
                    }
                    else
                    {
                        await Dispatcher.InvokeAsync(async () => await PauseMonitoringOnlyAsync());
                    }
                }
            };
            _sessionTimer.Start();
        }

        private async Task PauseMonitoringOnlyAsync()
        {
            if (_hubConnection != null)
            {
                await _hubConnection.InvokeAsync("PauseSessionMonitoring", _roomId);
            }

            _isMonitoringStarted = false;
            if (FindName("TxtMonitoringState") is TextBlock monitoringState)
                monitoringState.Text = $"Monitoring: Paused (Session #{_currentSessionId})";
            SetMonitoringControlButtonState(MonitoringControlState.Paused);
            LogActivity("SYSTEM", "PAUSED", "Monitoring paused by timer. Session remains active and soft lock is enforced.", "#FF9800");
        }

        private bool EnsureSessionNotEnded()
        {
            if (_isSessionEnded)
            {
                MessageBox.Show("Session already ended.", "Session Ended", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            return false;
        }

        private void SetTeacherAvatar(UserResponseDto user)
        {
            if (string.IsNullOrWhiteSpace(user?.ProfileImageUrl))
                return;

            string url = user.ProfileImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? user.ProfileImageUrl
                : $"{ApiEndpoints.BaseUrl}{user.ProfileImageUrl}";

            try
            {
                if (FindName("TeacherAvatarBrush") is ImageBrush avatarBrush)
                    avatarBrush.ImageSource = new System.Windows.Media.Imaging.BitmapImage(new Uri(url, UriKind.Absolute));

                if (FindName("TeacherAvatarImage") is Ellipse avatarImage)
                    avatarImage.Visibility = Visibility.Visible;

                if (FindName("TeacherAvatarDefault") is Border avatarDefault)
                    avatarDefault.Visibility = Visibility.Collapsed;
            }
            catch
            {
            }
        }

        private async Task InitializeSignalR()
        {
            // Idempotent guard: if the hub connection is already alive, do NOT
            // tear it down. Stopping the instructor's connection mid-session
            // triggers the server's OnDisconnectedAsync path, which marks the
            // room as Ended and broadcasts SessionInterrupted to every student.
            // Just skip re-initialization — the existing handlers are already
            // attached exactly once (post-dedup), so a second InitializeSignalR
            // call is a no-op rather than a destructive rebuild.
            if (_hubConnection != null
                && _hubConnection.State != HubConnectionState.Disconnected)
            {
                return;
            }

            // Connection is null or fully disconnected: safe to clean up any
            // lingering subscriptions from a prior dead connection and rebuild.
            foreach (var subscription in _hubSubscriptions)
            {
                try { subscription?.Dispose(); } catch { }
            }
            _hubSubscriptions.Clear();

            if (_hubConnection != null)
            {
                try { await _hubConnection.DisposeAsync(); } catch { }
                _hubConnection = null;
            }

            _hubConnection = new HubConnectionBuilder()
                .WithUrl($"{ApiEndpoints.BaseUrl}/monitoringHub", o => o.AccessTokenProvider = () => Task.FromResult(SessionManager.JwtToken))
                .WithAutomaticReconnect().Build();

            _hubSubscriptions.Add(_hubConnection.On<int>("StudentJoined", (id) => Dispatcher.Invoke(() => {
                if (_permanentlyDismissedStudents.Contains(id))
                    return;

                // Bug fix: Bug1
                _permanentlyDismissedStudents.Remove(id);
                _safelyLeftStudentIds.Remove(id);
                _ = LoadParticipantsFromServerAsync();
            })));

            _hubSubscriptions.Add(_hubConnection.On<int>("StudentConnectionLost", studentId => Dispatcher.Invoke(() =>
            {
                if (_safelyLeftStudentIds.Contains(studentId) || _permanentlyDismissedStudents.Contains(studentId))
                    return;

                if (_selectedStudentId == studentId)
                {
                    ResetToMainMonitoringView();
                }

                // Resolve the student's display name from whichever source
                // is currently populated. The participant list refresh may
                // have wiped ActiveStudents momentarily; falling back to
                // the cached _allParticipants ensures the log line fires
                // with a real name instead of being silently dropped.
                var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                string displayName = targetStudent?.Name
                    ?? _allParticipants?.FirstOrDefault(p => p.StudentId == studentId)?.StudentName
                    ?? _allParticipants?.FirstOrDefault(p => p.StudentId == studentId)?.StudentEmail
                    ?? $"Student #{studentId}";
                string displayEmail = targetStudent?.Email
                    ?? _allParticipants?.FirstOrDefault(p => p.StudentId == studentId)?.StudentEmail
                    ?? "SYSTEM";

                if (targetStudent != null)
                {
                    targetStudent.IsOffline = true;
                    targetStudent.Status = "Disconnected";
                    targetStudent.StatusColor = "#D32F2F";
                    targetStudent.IsLeaveRequested = false;
                }

                _leaveRequestedStateByStudentId[studentId] = false;

                // ALWAYS log to the Global Feed — even if the student was
                // momentarily absent from ActiveStudents (race with the
                // periodic refresh). This is the entry the instructor
                // expects to see for "STUDENT_DISCONNECTED" scenarios.
                // Guard: the participant-poll diff in LoadParticipantsFromServerAsync
                // also catches Joined→Disconnected transitions as a safety net.
                // Mark the student here so the poller doesn't emit a second
                // identical entry for the same disconnect.
                if (_disconnectLoggedStudentIds.Add(studentId))
                {
                    LogActivity(displayEmail, "STUDENT_DISCONNECTED",
                        $"⚠ CONNECTION LOST. {displayName} disconnected unexpectedly.", "#D32F2F");
                }

                _studentsView.Refresh();
            })));

            // Single registration only — the previous duplicate registration here
            // caused every join/reconnect to be logged twice in the Global Log Feed.
            _hubSubscriptions.Add(_hubConnection.On<int, string>("StudentJoinedOrReconnected", (studentId, studentName) => Dispatcher.InvokeAsync(() =>
            {
                var student = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                if (student != null)
                {
                    student.IsOffline = false;
                    student.Status = "Connected";
                    student.StatusColor = "#4CAF50";
                }

                // Force them off the dismissed lists so a kicked-then-rejoined
                // student is treated as a fresh participant.
                _permanentlyDismissedStudents.Remove(studentId);
                _safelyLeftStudentIds.Remove(studentId);
                // Clear the disconnect-logged mark so a future disconnect for
                // this same student is allowed to log again.
                _disconnectLoggedStudentIds.Remove(studentId);

                LogActivity("SYSTEM", "SYSTEM", $"✅ SESSION JOINED / CONNECTION RESTORED. {studentName}", "#4CAF50");
                _ = LoadParticipantsFromServerAsync();
            })));

            _hubSubscriptions.Add(_hubConnection.On<ViolationAlertPayload>("ReceiveViolationAlert", payload => _ = Dispatcher.InvokeAsync(() =>
            {
                if (payload == null) return;

                if (_permanentlyDismissedStudents.Contains(payload.StudentId))
                    return;

                var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == payload.StudentId);
                var email = targetStudent?.Email
                            ?? _allParticipants.FirstOrDefault(p => p.StudentId == payload.StudentId)?.StudentEmail
                            ?? $"Student #{payload.StudentId}";

                // Informational events (e.g. CANVAS_RETURNED) carry zero
                // severity score and should NOT inflate the student's
                // violation count, mark them as "ALERT", or render the
                // red VIOLATION badge. Render a green RETURN entry instead.
                bool isInformational =
                    string.Equals(payload.EventType, "CANVAS_RETURNED",
                        StringComparison.OrdinalIgnoreCase);

                if (isInformational)
                {
                    var message = string.IsNullOrWhiteSpace(payload.Description)
                        ? "Student returned to the LMS exam."
                        : payload.Description;
                    LogActivity(email, "RETURN", message, "#1B5E20");

                    // Restore the connected status text so the row doesn't
                    // stay stuck on a previous "ALERT: WINDOW_SWITCH" caption.
                    if (targetStudent != null && !targetStudent.IsOffline)
                    {
                        targetStudent.Status = "Connected";
                        targetStudent.StatusColor = "#4CAF50";
                    }
                    _studentsView.Refresh();
                    return;
                }

                _studentsWithViolations.Add(payload.StudentId);
                AppendStudentMonitoringEvent(payload.StudentId, payload.EventType, payload.SeverityScore);

                if (targetStudent != null)
                {
                    targetStudent.ViolationCount += Math.Max(1, payload.SeverityScore);
                    targetStudent.HasViolation = true;
                    targetStudent.Status = $"ALERT: {payload.EventType}";
                    targetStudent.StatusColor = "#D32F2F";
                }

                var violationMessage = string.IsNullOrWhiteSpace(payload.Description)
                    ? payload.EventType
                    : $"{payload.EventType}: {payload.Description}";
                LogActivity(email, "VIOLATION", violationMessage, "#D32F2F");
                UpdateDetailPanelForIncomingViolation(payload.StudentId);
                _studentsView.Refresh();
            })));

            // Removed: a second On<int,string,int,DateTime>("ViolationDetected", ...)
            // listener used to live here. The server sends a single anonymous-object
            // payload; SignalR delivers it to every "ViolationDetected" handler, so
            // the second handler caused every violation to be logged 2x in the
            // Global Log Feed and double-counted on the student's score.

            _hubSubscriptions.Add(_hubConnection.On<int>("LeaveRequested", studentId => Dispatcher.Invoke(() =>
            {
                if (_permanentlyDismissedStudents.Contains(studentId))
                    return;

                _leaveRequestedStateByStudentId[studentId] = true;

                var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                if (targetStudent == null) return;

                targetStudent.IsLeaveRequested = true;
                targetStudent.Status = "Wants to Leave";
                targetStudent.StatusColor = "#FF9800";

                LogActivity(targetStudent.Email, "LEAVE_REQ", "Student requested leave approval.", "#FF9800");
                _studentsView.Refresh();
            })));

            // Soft Lock "Done" approval-request loop.
            // Student pressed Done → surface as a DONE entry in the live feed
            // AND flip IsLeaveRequested=true so the Approve/Deny buttons
            // appear next to the student's row in the Participants tab.
            _hubSubscriptions.Add(_hubConnection.On<int>("SessionCompletionRequested", studentId => Dispatcher.Invoke(() =>
            {
                if (_permanentlyDismissedStudents.Contains(studentId))
                    return;

                _leaveRequestedStateByStudentId[studentId] = true;

                var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                var email = targetStudent?.Email
                            ?? _allParticipants.FirstOrDefault(p => p.StudentId == studentId)?.StudentEmail
                            ?? $"Student #{studentId}";

                if (targetStudent != null)
                {
                    targetStudent.IsLeaveRequested = true;   // ← shows Approve/Deny buttons via XAML binding
                    targetStudent.Status = "Awaiting Approval";
                    targetStudent.StatusColor = "#1B5E20";
                }

                LogActivity(email, "DONE", "Student finished the assessment — awaiting instructor approval.", "#1B5E20");
                _studentsView.Refresh();
            })));

            _hubSubscriptions.Add(_hubConnection.On<JoinApprovalRequestDto>("StudentPendingApproval", payload => Dispatcher.Invoke(() =>
            {
                if (payload == null)
                    return;

                _pendingJoinApprovals[payload.StudentId] = payload;

                var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == payload.StudentId);
                if (targetStudent == null)
                {
                    targetStudent = new LiveStudentStatus
                    {
                        StudentId = payload.StudentId,
                        Name = string.IsNullOrWhiteSpace(payload.StudentName) ? payload.StudentEmail : payload.StudentName,
                        Email = payload.StudentEmail ?? string.Empty,
                        ProfileImageUrl = string.IsNullOrWhiteSpace(payload.ProfileImageUrl)
                            ? string.Empty
                            : (payload.ProfileImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                ? payload.ProfileImageUrl
                                : $"{ApiEndpoints.BaseUrl}{payload.ProfileImageUrl}"),
                        Status = payload.IsLate ? "Waiting to Join" : "Waiting to Rejoin",
                        StatusColor = "#FF9800",
                        IsJoinApprovalPending = true
                    };
                    ActiveStudents.Add(targetStudent);
                }
                else
                {
                    targetStudent.IsJoinApprovalPending = true;
                    targetStudent.Status = payload.IsLate ? "Waiting to Join" : "Waiting to Rejoin";
                    targetStudent.StatusColor = "#FF9800";
                }

                LogActivity(targetStudent.Email, "JOIN_REQ", "Student requested join approval.", "#FF9800");
                _studentsView.Refresh();
                UpdateParticipantCount();
            })));

            // RejoinRequest is the new event the hub fires when a previously
            // disconnected student tries to come back. Payload shape is
            // identical to StudentPendingApproval — surface it through the
            // exact same approval card UI so no new code path is needed.
            _hubSubscriptions.Add(_hubConnection.On<JoinApprovalRequestDto>("RejoinRequest", payload => Dispatcher.Invoke(() =>
            {
                if (payload == null) return;
                _pendingJoinApprovals[payload.StudentId] = payload;

                var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == payload.StudentId);
                if (targetStudent != null)
                {
                    targetStudent.IsOffline = false;
                    targetStudent.IsJoinApprovalPending = true;
                    targetStudent.Status = "Waiting to Rejoin";
                    targetStudent.StatusColor = "#FF9800";
                }

                LogActivity(payload.StudentEmail ?? "SYSTEM", "REJOIN_REQ",
                    "Disconnected student attempting to rejoin — awaiting approval.", "#FF9800");
                _studentsView.Refresh();
                UpdateParticipantCount();
            })));

            _hubSubscriptions.Add(_hubConnection.On<dynamic>("StudentJoinApprovalResolved", payload => Dispatcher.Invoke(() =>
            {
                try
                {
                    int studentId = payload.studentId;
                    string decision = payload.decision;

                    _pendingJoinApprovals.Remove(studentId);

                    var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                    if (targetStudent != null)
                    {
                        targetStudent.IsJoinApprovalPending = false;
                        if (string.Equals(decision, "Approved", StringComparison.OrdinalIgnoreCase))
                        {
                            targetStudent.Status = "Approved";
                            targetStudent.StatusColor = "#4CAF50";
                        }
                        else
                        {
                            targetStudent.Status = "Denied";
                            targetStudent.StatusColor = "#D32F2F";
                            // Bug fix: Bug6 - remove denied student from ActiveStudents
                            ActiveStudents.Remove(targetStudent);
                        }
                    }

                    // Bug fix: Bug2 - block periodic LoadParticipantsFromServerAsync from re-stitching the denied student
                    if (string.Equals(decision, "Denied", StringComparison.OrdinalIgnoreCase))
                    {
                        _permanentlyDismissedStudents.Add(studentId);
                    }

                    _studentsView.Refresh();
                    UpdateParticipantCount();
                }
                catch
                {
                }
            })));

            _hubSubscriptions.Add(_hubConnection.On<int>("StudentSafelyLeft", studentId => Dispatcher.Invoke(() =>
            {
                _permanentlyDismissedStudents.Add(studentId);
                _safelyLeftStudentIds.Add(studentId);

                if (_selectedStudentId == studentId)
                {
                    CollapseDetailPanel();
                }

                var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                if (targetStudent != null)
                {
                    LogActivity(targetStudent.Email, "LEFT", "Student left safely with instructor approval.", "#1B5E20");
                    ActiveStudents.Remove(targetStudent);
                    _leaveRequestedStateByStudentId[studentId] = false;
                    _studentsView.Refresh();
                    UpdateParticipantCount();
                }
            })));

            // Bug fix: when an instructor kicks a student via Remove from
            // Session, the server now broadcasts StudentRemoved. Drop the
            // participant from this console immediately rather than waiting
            // for the next periodic LoadParticipantsFromServerAsync refresh.
            _hubSubscriptions.Add(_hubConnection.On<int>("StudentRemoved", studentId => Dispatcher.InvokeAsync(() =>
            {
                _permanentlyDismissedStudents.Add(studentId);

                if (_selectedStudentId == studentId)
                {
                    ResetToMainMonitoringView();
                }

                var student = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                if (student != null)
                {
                    ActiveStudents.Remove(student);
                    _studentsView.Refresh();
                    UpdateParticipantCount();
                }
            })));

            _hubSubscriptions.Add(_hubConnection.On<int>("StudentLeftSession", studentId => Dispatcher.InvokeAsync(() =>
            {
                if (_selectedStudentId == studentId)
                {
                    ResetToMainMonitoringView();
                }

                var student = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                if (student != null)
                {
                    student.IsOffline = false;
                    student.Status = "Completed";
                    student.StatusColor = "#1B5E20";
                    LogActivity("SYSTEM", "SYSTEM", $"EXAM COMPLETED. Student exited properly. {student.Name}", "#1B5E20");
                    _permanentlyDismissedStudents.Add(studentId);
                    ActiveStudents.Remove(student);
                    _studentsView.Refresh();
                    UpdateParticipantCount();
                }
            })));

            _hubSubscriptions.Add(_hubConnection.On<int, bool, bool>("ReceiveHardwareStateUpdate", (studentId, isVm, isRemote) => Dispatcher.Invoke(() =>
            {
                var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                if (targetStudent == null)
                    return;

                targetStudent.IsUsingVM = isVm;
                targetStudent.IsRemoteDesktop = isRemote;

                var hasHardwareViolation = isVm || isRemote;
                targetStudent.HasHardwareViolation = hasHardwareViolation;
                if (hasHardwareViolation)
                {
                    targetStudent.HasViolation = true;
                    _studentsWithViolations.Add(studentId);

                    if (FindName("CriticalAlertBanner") is Border criticalAlertBanner)
                        criticalAlertBanner.Visibility = Visibility.Visible;

                    if (FindName("TxtCriticalAlertMessage") is TextBlock criticalAlertMessage)
                        criticalAlertMessage.Text = $"🚨 CRITICAL SECURITY ALERT: {targetStudent.Name} is using a restricted hardware environment!";
                }

                _studentsView.Refresh();
            })));

            try { await _hubConnection.StartAsync(); await _hubConnection.InvokeAsync("JoinRoom", _roomId.ToString()); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private async void BtnApproveLeave_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not LiveStudentStatus student)
                return;

            try
            {
                await _hubConnection.InvokeAsync("GrantLeave", _roomId, student.StudentId);

                _permanentlyDismissedStudents.Add(student.StudentId);
                _leaveRequestedStateByStudentId[student.StudentId] = false;
                student.IsLeaveRequested = false;
                student.Status = "Approved to Leave";
                student.StatusColor = "#1B5E20";

                LogActivity(student.Email, "UNLOCK", "Instructor granted leave.", "#1B5E20");
                _studentsView.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to grant leave: {ex.Message}", "Grant Leave", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Deny path — does NOT exit the student. Tells the SAC to restore
        // its Done button so the student can request again later.
        private async void BtnDenyLeave_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not LiveStudentStatus student)
                return;

            try
            {
                await _hubConnection.InvokeAsync("DenyLeaveRequest", _roomId, student.StudentId);

                _leaveRequestedStateByStudentId[student.StudentId] = false;
                student.IsLeaveRequested = false;
                student.Status = "Connected";
                student.StatusColor = "#4CAF50";

                LogActivity(student.Email, "DENY", "Instructor denied the Done request — student can resume work.", "#FF9800");
                _studentsView.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to deny leave: {ex.Message}", "Deny Leave", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LogActivity(string email, string badge, string msg, string color)
        {
            if (EmptyLogFeedState != null) EmptyLogFeedState.Visibility = Visibility.Collapsed;
            LogFeed.Insert(0, new LogEntry { Timestamp = DateTime.Now.ToString("T"), StudentEmail = email, BadgeText = badge, BadgeColor = color, Message = msg });
        }

        private void UpdateParticipantCount()
        {
            if (EmptyParticipantsState != null && ActiveStudents.Count > 0) EmptyParticipantsState.Visibility = Visibility.Collapsed;
            TxtParticipantCount.Text = $"{ActiveStudents.Count}/{_enrolledCount}";
            var missing = Math.Max(0, _enrolledCount - ActiveStudents.Count);
            if (FindName("TxtMissingCount") is TextBlock txtMissing) txtMissing.Text = $"Missing: {missing}";
        }

        // Tracks the last known ParticipationStatus per student between
        // poll cycles so the loader can detect Connected→Disconnected
        // transitions locally and write a STUDENT_DISCONNECTED log entry
        // even if the SignalR StudentConnectionLost broadcast doesn't
        // arrive (or arrives before the IMC subscribed to the group).
        private readonly Dictionary<int, string> _previousParticipantStatus = new();

        private async Task LoadParticipantsFromServerAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var response = await client.GetAsync($"{ApiEndpoints.Rooms}/{_roomId}/participants");
                if (!response.IsSuccessStatusCode) return;

                var participants = await response.Content.ReadFromJsonAsync<List<ParticipantDto>>() ?? new List<ParticipantDto>();
                _allParticipants = participants;
                _enrolledCount = participants.Count;

                // Detect Connected → Disconnected transitions by diffing
                // this snapshot against the prior one and log directly to
                // the Global Feed. This is the local safety net for cases
                // where the SignalR broadcast doesn't reach the IMC.
                foreach (var p in participants)
                {
                    if (string.IsNullOrEmpty(p.ParticipationStatus)) continue;
                    if (_previousParticipantStatus.TryGetValue(p.StudentId, out var prevStatus)
                        && string.Equals(prevStatus, "Joined", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.ParticipationStatus, "Disconnected", StringComparison.OrdinalIgnoreCase)
                        && !_safelyLeftStudentIds.Contains(p.StudentId)
                        && !_permanentlyDismissedStudents.Contains(p.StudentId))
                    {
                        // Safety-net path. Suppress if the SignalR push handler
                        // already wrote the disconnect line for this student
                        // — otherwise the Global Feed shows two identical
                        // entries with the same timestamp.
                        if (_disconnectLoggedStudentIds.Add(p.StudentId))
                        {
                            string displayName = string.IsNullOrWhiteSpace(p.StudentName) ? p.StudentEmail : p.StudentName;
                            LogActivity(p.StudentEmail ?? "SYSTEM", "STUDENT_DISCONNECTED",
                                $"⚠ CONNECTION LOST. {displayName} disconnected unexpectedly.", "#D32F2F");
                        }
                    }
                    _previousParticipantStatus[p.StudentId] = p.ParticipationStatus;
                }

                ActiveStudents.Clear();

                // 1. Add ONLY currently-Joined students to the live sidebar.
                //    UX rule per QA: a disconnected student is treated as if
                //    they had been removed from the session — they no longer
                //    appear in the participant list at all. The Session
                //    Archive still records them as Disconnected because the
                //    STUDENT_DISCONNECTED MonitoringEvent persists, so the
                //    ConnectionQuality classifier never reports "Clean
                //    Connection" for them. If they successfully rejoin
                //    through the approval flow, their participant row
                //    flips back to Connected and they reappear here.
                foreach (var p in participants.Where(p =>
                    string.Equals(p.ParticipationStatus, "Joined", StringComparison.OrdinalIgnoreCase)
                    && !_safelyLeftStudentIds.Contains(p.StudentId)))
                {
                    if (_permanentlyDismissedStudents.Contains(p.StudentId))
                        continue;

                    var isLeaveRequested = _leaveRequestedStateByStudentId.TryGetValue(p.StudentId, out var requested) && requested;
                    bool isDisconnected = false; // filtered above — only Joined rows reach here

                    // Status precedence: leave-request > connected.
                    string statusText;
                    string statusColor;
                    if (isLeaveRequested)
                    {
                        statusText = "Wants to Leave";
                        statusColor = "#FF9800";
                    }
                    else
                    {
                        statusText = "Connected";
                        statusColor = "#4CAF50";
                    }

                    ActiveStudents.Add(new LiveStudentStatus
                    {
                        StudentId = p.StudentId,
                        Name = string.IsNullOrWhiteSpace(p.StudentName) ? p.StudentEmail : p.StudentName,
                        Email = p.StudentEmail,
                        ProfileImageUrl = string.IsNullOrWhiteSpace(p.ProfileImageUrl)
                            ? string.Empty
                            : (p.ProfileImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                ? p.ProfileImageUrl
                                : $"{ApiEndpoints.BaseUrl}{p.ProfileImageUrl}"),
                        HasViolation = _studentsWithViolations.Contains(p.StudentId),
                        IsLeaveRequested = isLeaveRequested,
                        IsOffline = isDisconnected,
                        Status = statusText,
                        StatusColor = statusColor
                    });
                }

                // 2. FIX: Stitch the pending join approvals back into the UI!
                foreach (var pending in _pendingJoinApprovals.Values)
                {
                    var existingTarget = ActiveStudents.FirstOrDefault(s => s.StudentId == pending.StudentId);

                    if (existingTarget == null)
                    {
                        // If they were wiped out completely, re-add them to the list
                        ActiveStudents.Add(new LiveStudentStatus
                        {
                            StudentId = pending.StudentId,
                            Name = string.IsNullOrWhiteSpace(pending.StudentName) ? pending.StudentEmail : pending.StudentName,
                            Email = pending.StudentEmail ?? string.Empty,
                            ProfileImageUrl = string.IsNullOrWhiteSpace(pending.ProfileImageUrl)
                                ? string.Empty
                                : (pending.ProfileImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                    ? pending.ProfileImageUrl
                                    : $"{ApiEndpoints.BaseUrl}{pending.ProfileImageUrl}"),
                            Status = pending.IsLate ? "Waiting to Join" : "Waiting to Rejoin",
                            StatusColor = "#FF9800",
                            IsJoinApprovalPending = true
                        });
                    }
                    else
                    {
                        // If they exist but their status got reset by the DB, override it back to Pending
                        existingTarget.IsJoinApprovalPending = true;
                        existingTarget.Status = pending.IsLate ? "Waiting to Join" : "Waiting to Rejoin";
                        existingTarget.StatusColor = "#FF9800";
                    }
                }

                _studentsView.Refresh();
                UpdateParticipantCount();
            }
            catch
            {
            }
        }

        private void BtnViewParticipants_Click(object sender, RoutedEventArgs e)
        {
            if (EnsureSessionNotEnded()) return;
            ShowParticipantsOverviewWindow();
        }

        private void ShowParticipantsOverviewWindow()
        {
            var rows = _allParticipants
                .Select(p => new ParticipantOverviewRow
                {
                    Name = string.IsNullOrWhiteSpace(p.StudentName) ? p.StudentEmail : p.StudentName,
                    Email = p.StudentEmail,
                    Enrollment = p.EnrollmentSource,
                    Status = string.Equals(p.ConnectionStatus, "Connected", StringComparison.OrdinalIgnoreCase)
                        ? "In Session"
                        : "Not in Session"
                })
                .OrderBy(r => r.Status)
                .ThenBy(r => r.Name)
                .ToList();

            int joined = rows.Count(r => r.Status == "In Session");
            int missing = rows.Count - joined;

            var window = new Window
            {
                Title = "Participants Overview",
                Owner = this,
                Width = 780,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.White
            };

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new TextBlock
            {
                Text = $"Participants: {joined}/{rows.Count}  |  Missing: {missing}",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B5E20"))
            };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var hint = new TextBlock
            {
                Text = "In Session = currently connected. Not in Session = enrolled but not connected.",
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = Brushes.DimGray
            };
            Grid.SetRow(hint, 1);
            grid.Children.Add(hint);

            var table = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                ItemsSource = rows
            };
            table.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header = "Name", Binding = new Binding("Name"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            table.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header = "Email", Binding = new Binding("Email"), Width = new DataGridLength(2.2, DataGridLengthUnitType.Star) });
            table.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header = "Enrollment", Binding = new Binding("Enrollment"), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
            table.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header = "Status", Binding = new Binding("Status"), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
            Grid.SetRow(table, 2);
            grid.Children.Add(table);

            window.Content = grid;
            window.ShowDialog();
        }

        // ======================== UI ACTIONS ========================

        private void ParticipantRow_Click(object sender, MouseButtonEventArgs e)
        {
            _selectedStudent = (sender as Border)?.DataContext as LiveStudentStatus;
            if (_selectedStudent != null)
            {
                _selectedStudentId = _selectedStudent.StudentId;
                if (FindName("RightDetailPanel") is Border rightDetailPanel)
                    rightDetailPanel.Visibility = Visibility.Visible;

                // Bug fix — ResetToMainMonitoringView (called by the X close
                // button) collapses BOTH the outer RightDetailPanel AND this
                // inner StudentDetailPanel. Without explicitly re-showing
                // the inner panel here, the second click on a student would
                // open an empty right column.
                StudentDetailPanel.Visibility = Visibility.Visible;
                StudentDetailPanel.DataContext = _selectedStudent;

                TxtSelectedName.Text = _selectedStudent.Name;
                TxtSelectedStatus.Text = _selectedStudent.Status.ToUpper();
                SelectedStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedStudent.StatusColor));
                var specificLogs = _studentLogs.TryGetValue(_selectedStudent.StudentId, out var logs)
                    ? logs.ToList()
                    : new List<StudentMonitoringEvent>();

                TxtAlertCount.Text = specificLogs.Count.ToString();

                var totalRiskScore = specificLogs.Sum(x => Math.Max(0, x.SeverityScore));
                string riskText = "SAFE";
                if (totalRiskScore >= 50) riskText = "CHEATING";
                else if (totalRiskScore >= 20) riskText = "SUSPICIOUS";

                TxtRiskLevel.Text = riskText;
                TxtRiskLevel.Foreground = totalRiskScore >= 50 ? Brushes.Red : (totalRiskScore >= 20 ? Brushes.Goldenrod : Brushes.LimeGreen);

                TxtLogHeader.Text = $"Logs: {_selectedStudent.Name}";
                ApplyAllFilters();
            }
        }

        private void AppendStudentMonitoringEvent(int studentId, string eventType, int severityScore)
        {
            var logs = _studentLogs.GetOrAdd(studentId, _ => new ObservableCollection<StudentMonitoringEvent>());

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                logs.Insert(0, new StudentMonitoringEvent
                {
                    EventType = eventType,
                    SeverityScore = severityScore
                });
            });
        }

        private void BtnCloseDetailPanel_Click(object sender, RoutedEventArgs e)
        {
            // Bug fix — when the instructor closes the Student Details
            // panel, the right-hand log header was leaving "Logs: <name>"
            // stuck and the feed kept its per-student filter. Route through
            // the full ResetToMainMonitoringView() so:
            //   - TxtLogHeader → "Global Log Feed"
            //   - _selectedStudent / _selectedStudentId cleared
            //   - _logsView.Filter cleared (every entry visible again)
            //   - Participants list selection cleared
            ResetToMainMonitoringView();
            LogFeedItemsControl.ItemsSource = _logsView;
            ApplyAllFilters();
        }

        private void ResetToMainMonitoringView()
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (FindName("RightDetailPanel") is Border rightDetailPanel)
                    rightDetailPanel.Visibility = Visibility.Collapsed;

                StudentDetailPanel.Visibility = Visibility.Collapsed;
                LogFeedItemsControl.Visibility = Visibility.Visible;

                _selectedStudentId = 0;
                _selectedStudent = null;

                if (FindName("StudentsList") is System.Windows.Controls.Primitives.Selector studentsList)
                    studentsList.SelectedItem = null;

                if (_logsView != null)
                {
                    _logsView.Filter = null;
                    _logsView.Refresh();
                }

                TxtLogHeader.Text = "Global Log Feed";
            });
        }

        private void CollapseDetailPanel()
        {
            if (FindName("RightDetailPanel") is Border rightDetailPanel)
                rightDetailPanel.Visibility = Visibility.Collapsed;
            StudentDetailPanel.DataContext = null;
            _selectedStudent = null;
            _selectedStudentId = null;

            TxtSelectedName.Text = "Select a Student";
            TxtSelectedStatus.Text = "WAITING";
            SelectedStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"));
            TxtAlertCount.Text = "0";
            TxtRiskLevel.Text = "SAFE";
            TxtRiskLevel.Foreground = Brushes.LimeGreen;
        }

        private void UpdateDetailPanelForIncomingViolation(int studentId)
        {
            if (FindName("RightDetailPanel") is not Border rightDetailPanel
                || rightDetailPanel.Visibility != Visibility.Visible
                || _selectedStudentId != studentId)
                return;

            _ = Dispatcher.InvokeAsync(() =>
            {
                if (_selectedStudent == null)
                    return;

                var specificLogs = _studentLogs.TryGetValue(studentId, out var logs)
                    ? logs.ToList()
                    : new List<StudentMonitoringEvent>();

                TxtAlertCount.Text = specificLogs.Count.ToString();

                var totalRiskScore = specificLogs.Sum(x => Math.Max(0, x.SeverityScore));
                string riskText = "SAFE";
                if (totalRiskScore >= 50) riskText = "CHEATING";
                else if (totalRiskScore >= 20) riskText = "SUSPICIOUS";

                TxtRiskLevel.Text = riskText;
                TxtRiskLevel.Foreground = totalRiskScore >= 50 ? Brushes.Red : (totalRiskScore >= 20 ? Brushes.Goldenrod : Brushes.LimeGreen);
            });
        }

        private void BtnRemoveFromSession_Click(object sender, RoutedEventArgs e)
        {
            if (EnsureSessionNotEnded()) return;
            _ = RemoveSelectedStudentAsync();
        }

        private async Task RemoveSelectedStudentAsync()
        {
            if (_selectedStudent == null) return;

            // Capture every value we need from _selectedStudent BEFORE any
            // await, then never read _selectedStudent again. Reason: the
            // server broadcasts StudentRemoved to the room group on a
            // successful kick, and our own SignalR handler races back on
            // the dispatcher and calls ResetToMainMonitoringView(), which
            // nulls _selectedStudent. Without these locals, the next
            // reference after the PostAsync await would NullReference.
            // (Symptom on Render: "Error removing participant: Object
            // reference not set to an instance of an object" — the kick
            // itself succeeded, only the UI follow-up exploded.)
            var removedStudentId = _selectedStudent.StudentId;
            var removedStudentName = _selectedStudent.Name;
            var removedStudentEmail = _selectedStudent.Email;

            if (MessageBox.Show($"Remove {removedStudentName} from this room?", "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var response = await client.PostAsync($"{ApiEndpoints.Rooms}/{_roomId}/sessions/remove/{removedStudentId}", null);

                if (!response.IsSuccessStatusCode)
                {
                    MessageBox.Show("Failed to remove participant from room.", "Remove Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                LogActivity(removedStudentEmail, "KICKED", "Instructor removed student from room.", "#D32F2F");

                // Optimistic local removal. Mark the kicked student as
                // permanently dismissed BEFORE the participants refresh runs,
                // so even if the server's GET /participants response races
                // and still reports the row as "Disconnected", the
                // LoadParticipantsFromServerAsync filter drops it.
                _permanentlyDismissedStudents.Add(removedStudentId);
                var existing = ActiveStudents.FirstOrDefault(s => s.StudentId == removedStudentId);
                if (existing != null)
                {
                    ActiveStudents.Remove(existing);
                    _studentsView.Refresh();
                    UpdateParticipantCount();
                }

                // _selectedStudent may already be null at this point if the
                // SignalR StudentRemoved handler raced ahead — that's fine,
                // these UI writes are idempotent.
                _selectedStudent = null;
                if (TxtLogHeader != null) TxtLogHeader.Text = "Global Log Feed";
                if (TxtSelectedName != null) TxtSelectedName.Text = "Select a Student";
                if (LogFeedItemsControl != null) LogFeedItemsControl.ItemsSource = _logsView;
                ApplyAllFilters();
                await LoadParticipantsFromServerAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error removing participant: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnViolations_Click(object sender, RoutedEventArgs e)
        {
            if (EnsureSessionNotEnded()) return;
            if (_selectedStudent == null)
            {
                MessageBox.Show("Select a participant first.", "Violations", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var response = await client.GetAsync($"{ApiEndpoints.BaseUrl}/api/violations/room/{_roomId}");
                if (!response.IsSuccessStatusCode)
                {
                    MessageBox.Show("Unable to load violations.", "Violations", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var violations = await response.Content.ReadFromJsonAsync<List<ViolationLogDto>>() ?? new List<ViolationLogDto>();
                var studentLogs = violations.Where(v => string.Equals(v.StudentEmail, _selectedStudent.Email, StringComparison.OrdinalIgnoreCase)).ToList();

                var summary = new[]
                {
                    BuildRuleSummary("VAC - Virtualization/Emulator", studentLogs, new[] { "VM", "EMULATOR", "VIRTUAL" }),
                    BuildRuleSummary("HAS - Hardware/Software Artifacts", studentLogs, new[] { "HARDWARE", "ARTIFACT", "SUSPICIOUS_SETUP" }),
                    BuildRuleSummary("RTFM - Focus/Alt+Tab", studentLogs, new[] { "ALT_TAB", "FOCUS", "WINDOW_SWITCH" }),
                    BuildRuleSummary("PBD - Unauthorized Process", studentLogs, new[] { "PROCESS", "BLACKLIST" }),
                    BuildRuleSummary("CSAD - Clipboard/Screenshot", studentLogs, new[] { "CLIPBOARD", "COPY", "PASTE", "PRINTSCREEN", "SCREENSHOT" }),
                    BuildRuleSummary("IDLE - Inactivity", studentLogs, new[] { "IDLE", "INACTIVITY" })
                };

                MessageBox.Show(
                    $"Violations Summary for {_selectedStudent.Name}\n\n" + string.Join("\n", summary) + $"\n\nTotal Logged Violations: {studentLogs.Count}",
                    "Violations Summary",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load violations: {ex.Message}", "Violations", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnViewSummary_Click(object sender, RoutedEventArgs e)
        {
            if (EnsureSessionNotEnded()) return;
            if (_selectedStudent == null)
            {
                MessageBox.Show("Select a participant first.", "Violation Summary", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => BtnViewSummary_Click(sender, e));
                return;
            }

            var studentId = _selectedStudent.StudentId;
            var studentName = string.IsNullOrWhiteSpace(_selectedStudent.Name) ? _selectedStudent.Email : _selectedStudent.Name;

            var specificLogs = _studentLogs.TryGetValue(studentId, out var logs)
                ? logs.ToList()
                : new List<StudentMonitoringEvent>();

            var totalRiskScore = specificLogs.Sum(x => Math.Max(0, x.SeverityScore));

            var summaryDialog = new StudentViolationSummaryDialog(studentName, totalRiskScore, specificLogs)
            {
                Owner = this
            };

            summaryDialog.ShowDialog();
        }

        private static string BuildRuleSummary(string label, List<ViolationLogDto> logs, string[] tags)
        {
            int count = logs.Count(v => tags.Any(t =>
                (!string.IsNullOrWhiteSpace(v.Module) && v.Module.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrWhiteSpace(v.Description) && v.Description.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)));
            return $"• {label}: {count}";
        }

        private async void BtnEndSession_Click(object sender, RoutedEventArgs e)
        {
            if (_isSessionEnded)
            {
                this.Close();
                return;
            }

            var endedByTimer = _isEndingFromTimer;

            if (!_isEndingFromTimer && MessageBox.Show("End session?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;

            _isEndingFromTimer = true;

            // BUG FIX: if _currentSessionId is 0 (e.g. a rejoin path that
            // didn't successfully sync, or a transient state), the PUT
            // below would be silently skipped and the server room.Status
            // would never transition out of "Active". That left the
            // RoomDetail dashboard's "Monitoring Session In Progress"
            // banner showing forever even though the teacher clicked End
            // Session. Recover by probing the room-status endpoint for
            // the active session id before the PUT.
            if (_currentSessionId <= 0)
            {
                try
                {
                    using var probe = new HttpClient();
                    probe.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                    var probeResponse = await probe.GetAsync($"{ApiEndpoints.Rooms}/{_roomId}/status");
                    if (probeResponse.IsSuccessStatusCode)
                    {
                        var statusDto = await probeResponse.Content.ReadFromJsonAsync<RoomStatusSyncDto>();
                        if (statusDto != null
                            && statusDto.activeSessionId.HasValue
                            && statusDto.activeSessionId.Value > 0)
                        {
                            _currentSessionId = statusDto.activeSessionId.Value;
                        }
                    }
                }
                catch
                {
                    // Fall through — handled below by the success check.
                }
            }

            bool endRequestSucceeded = false;
            if (_currentSessionId > 0)
            {
                try
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                    var response = await client.PutAsync($"{ApiEndpoints.Rooms}/sessions/{_currentSessionId}/end", null);
                    endRequestSucceeded = response.IsSuccessStatusCode;
                }
                catch
                {
                    endRequestSucceeded = false;
                }
            }

            // Always follow up with /force-reset on the room. This is
            // idempotent on the server and guarantees room.Status flips
            // to Pending even when the sessionId-based call returned
            // 404 ("No active session record found to end"). Without
            // this fallback the room could stay stuck as Active and
            // every subsequent setup attempt would fail.
            try
            {
                using var resetClient = new HttpClient();
                resetClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var resetResponse = await resetClient.PostAsync(
                    $"{ApiEndpoints.Rooms}/{_roomId}/force-reset", null);
                if (resetResponse.IsSuccessStatusCode)
                    endRequestSucceeded = true;
            }
            catch { /* best-effort */ }

            // End Session always proceeds with local cleanup, even if
            // the server didn't confirm the PUT. Reasoning: blocking the
            // teacher's workflow when a student happens to be disconnected
            // (or on any transient server hiccup) is worse than the
            // alternative — if the room stays Active server-side, the
            // RoomDetail dashboard banner will pick it up on next visit
            // and the teacher can retry from there. Silent local cleanup
            // is the more forgiving default.
            if (!endRequestSucceeded)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"BtnEndSession: server didn't confirm end for sessionId={_currentSessionId}; proceeding with local cleanup.");
            }

            _isMonitoringStarted = false;
            _isSessionEnded = true;
            _sessionTimer?.Stop(); // Stop timer
            SetMonitoringControlButtonState(MonitoringControlState.NotStarted);
            if (FindName("TxtMonitoringState") is TextBlock monitoringState) monitoringState.Text = "Monitoring: Session Ended";
            if (FindName("TxtStartStopLabel") is TextBlock startStopLabel)
            {
                startStopLabel.Text = "Session Ended";
                BtnStartMonitoring.IsEnabled = false;
            }
            if (FindName("StartStopIcon") is PackIcon startStopIcon) startStopIcon.Kind = PackIconKind.CheckCircle;
            if (FindName("TxtCountdownDisplay") is TextBlock countdownDisplay) countdownDisplay.Text = "00:00";
            if (FindName("TxtMonitoringTimerDisplay") is TextBlock timerDisplay) timerDisplay.Text = "00:00:00";

            if (_hubConnection != null)
            {
                try { await _hubConnection.StopAsync(); } catch { }
            }

            if (!endedByTimer)
                this.Close();
        }

        protected override async void OnClosing(CancelEventArgs e)
        {
            var isSessionActive = _currentSessionId > 0 && !_isSessionEnded;
            if (isSessionActive)
            {
                e.Cancel = true;
                MessageBox.Show("You must end the active session before closing this window.", "Active Session", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_isEndingFromTimer && _currentSessionId > 0 && _isMonitoringStarted)
            {
                try
                {
                    if (_hubConnection != null)
                    {
                        await _hubConnection.InvokeAsync("SetMonitoringState", _roomId, false);
                    }
                }
                catch { }

                if (_hubConnection != null) await _hubConnection.StopAsync();
                _sessionTimer?.Stop(); // Stop timer
            }
            else if (_hubConnection != null)
            {
                await _hubConnection.StopAsync();
            }
            _participantsRefreshTimer?.Stop();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            foreach (var subscription in _hubSubscriptions)
            {
                subscription.Dispose();
            }
            _hubSubscriptions.Clear();

            if (_hubConnection != null)
            {
                _ = _hubConnection.DisposeAsync();
                _hubConnection = null;
            }

            base.OnClosed(e);
        }

        private void StudentDropdown_Click(object sender, RoutedEventArgs e)
        {
            if (EnsureSessionNotEnded()) return;
            if (sender is Button btn && btn.ContextMenu != null) { btn.ContextMenu.PlacementTarget = btn; btn.ContextMenu.IsOpen = true; }
        }

        private void BtnDismissCriticalAlert_Click(object sender, RoutedEventArgs e)
        {
            if (FindName("CriticalAlertBanner") is Border criticalAlertBanner)
                criticalAlertBanner.Visibility = Visibility.Collapsed;
        }

        public class StartSessionResponse { public int SessionId { get; set; } }
    }

    public class LiveStudentStatus : INotifyPropertyChanged
    {
        public int StudentId { get; set; }
        public string Name { get; set; }
        private string _status, _statusColor;
        private int _violations;
        private bool _isLeaveRequested;
        private bool _isJoinApprovalPending;
        private bool _hasViolation;
        private bool _hasHardwareViolation;
        private bool _isUsingVm;
        private bool _isRemoteDesktop;
        private bool _isOffline;
        public string Email { get; set; }
        public string ProfileImageUrl { get; set; } = string.Empty;
        public Visibility HasProfileImageVisibility => string.IsNullOrWhiteSpace(ProfileImageUrl) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility HasNoProfileImageVisibility => string.IsNullOrWhiteSpace(ProfileImageUrl) ? Visibility.Visible : Visibility.Collapsed;
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        public string StatusColor { get => _statusColor; set { _statusColor = value; OnPropertyChanged(); } }
        public bool IsOffline { get => _isOffline; set { _isOffline = value; OnPropertyChanged(); } }
        public int ViolationCount
        {
            get => _violations;
            set
            {
                _violations = value;
                OnPropertyChanged();
                if (_violations > 0 && !HasViolation)
                {
                    HasViolation = true;
                }
            }
        }
        public bool IsLeaveRequested { get => _isLeaveRequested; set { _isLeaveRequested = value; OnPropertyChanged(); } }
        public bool IsJoinApprovalPending { get => _isJoinApprovalPending; set { _isJoinApprovalPending = value; OnPropertyChanged(); } }
        public bool HasViolation { get => _hasViolation; set { _hasViolation = value; OnPropertyChanged(); } }
        public bool HasHardwareViolation { get => _hasHardwareViolation; set { _hasHardwareViolation = value; OnPropertyChanged(); } }
        public bool IsUsingVM { get => _isUsingVm; set { _isUsingVm = value; OnPropertyChanged(); } }
        public bool IsRemoteDesktop { get => _isRemoteDesktop; set { _isRemoteDesktop = value; OnPropertyChanged(); } }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class ParticipantDto
    {
        public int StudentId { get; set; }
        public string StudentEmail { get; set; } = string.Empty;
        public string StudentName { get; set; } = string.Empty;
        public string? ProfileImageUrl { get; set; }
        public string EnrollmentSource { get; set; } = string.Empty;
        public string ParticipationStatus { get; set; } = string.Empty;
        public string ConnectionStatus { get; set; } = string.Empty;
    }

    public class ParticipantOverviewRow
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Enrollment { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class JoinApprovalRequestDto
    {
        public int RoomId { get; set; }
        public int StudentId { get; set; }
        public int ParticipantId { get; set; }
        public string StudentName { get; set; } = string.Empty;
        public string StudentEmail { get; set; } = string.Empty;
        public string? ProfileImageUrl { get; set; }
        public bool IsRejoin { get; set; }
        public bool IsLate { get; set; }
        public DateTime RequestedAt { get; set; }
    }

    public class LogEntry { public string Timestamp { get; set; } public string StudentEmail { get; set; } public string BadgeText { get; set; } public string BadgeColor { get; set; } public string Message { get; set; } }

    public class StudentMonitoringEvent
    {
        public string EventType { get; set; } = string.Empty;
        public int SeverityScore { get; set; }
    }

    public class ViolationLogDto
    {
        public string StudentEmail { get; set; } = string.Empty;
        public string Module { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class ViolationAlertPayload
    {
        public int StudentId { get; set; }
        public string EventType { get; set; } = string.Empty;
        public int SeverityScore { get; set; }
        public string Description { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }
}