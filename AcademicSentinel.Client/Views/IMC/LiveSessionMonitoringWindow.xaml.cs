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
        private readonly HashSet<int> _studentsWithViolations = new HashSet<int>();
        private readonly ConcurrentDictionary<int, ObservableCollection<StudentMonitoringEvent>> _studentLogs = new();
        private readonly List<IDisposable> _hubSubscriptions = new();
        private int? _selectedStudentId;

        public ObservableCollection<LiveStudentStatus> ActiveStudents { get; set; }
        public ObservableCollection<LogEntry> LogFeed { get; set; }

        public LiveSessionMonitoringWindow(int roomId, string roomTitle, int sessionId = 0, int monitoringDurationSeconds = 3600, bool endSessionWhenTimerEnds = true, int startDelaySeconds = 10)
        {
            InitializeComponent();
            _roomId = roomId;
            _currentSessionId = sessionId;
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

                var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                if (targetStudent == null)
                    return;

                targetStudent.IsOffline = true;
                targetStudent.Status = "Offline/Disconnected";
                targetStudent.StatusColor = "#D32F2F";
                targetStudent.IsLeaveRequested = false;
                _leaveRequestedStateByStudentId[studentId] = false;
                LogActivity("SYSTEM", "SYSTEM", $"⚠ CONNECTION LOST. {targetStudent.Name} disconnected unexpectedly.", "#D32F2F");
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

                LogActivity("SYSTEM", "SYSTEM", $"✅ SESSION JOINED / CONNECTION RESTORED. {studentName}", "#4CAF50");
                _ = LoadParticipantsFromServerAsync();
            })));

            _hubSubscriptions.Add(_hubConnection.On<ViolationAlertPayload>("ReceiveViolationAlert", payload => _ = Dispatcher.InvokeAsync(() =>
            {
                if (payload == null) return;

                if (_permanentlyDismissedStudents.Contains(payload.StudentId))
                    return;

                _studentsWithViolations.Add(payload.StudentId);
                AppendStudentMonitoringEvent(payload.StudentId, payload.EventType, payload.SeverityScore);

                var targetStudent = ActiveStudents.FirstOrDefault(s => s.StudentId == payload.StudentId);
                if (targetStudent != null)
                {
                    targetStudent.ViolationCount += Math.Max(1, payload.SeverityScore);
                    targetStudent.HasViolation = true;
                    targetStudent.Status = $"ALERT: {payload.EventType}";
                    targetStudent.StatusColor = "#D32F2F";
                }

                var email = targetStudent?.Email ?? _allParticipants.FirstOrDefault(p => p.StudentId == payload.StudentId)?.StudentEmail ?? $"Student #{payload.StudentId}";
                var message = string.IsNullOrWhiteSpace(payload.Description)
                    ? payload.EventType
                    : $"{payload.EventType}: {payload.Description}";
                LogActivity(email, "VIOLATION", message, "#D32F2F");
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

                ActiveStudents.Clear();

                // 1. Add normal connected/disconnected students from the DB
                foreach (var p in participants.Where(p =>
                    (string.Equals(p.ParticipationStatus, "Joined", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(p.ParticipationStatus, "Disconnected", StringComparison.OrdinalIgnoreCase))
                    && !string.Equals(p.ParticipationStatus, "Completed", StringComparison.OrdinalIgnoreCase)
                    && !_safelyLeftStudentIds.Contains(p.StudentId)))
                {
                    if (_permanentlyDismissedStudents.Contains(p.StudentId))
                        continue;

                    var isLeaveRequested = _leaveRequestedStateByStudentId.TryGetValue(p.StudentId, out var requested) && requested;
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
                        Status = isLeaveRequested ? "Wants to Leave" : "Connected",
                        StatusColor = isLeaveRequested ? "#FF9800" : "#4CAF50"
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
            CollapseDetailPanel();
            LogFeedItemsControl.ItemsSource = _logsView;
            _selectedStudentId = null;
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

            if (MessageBox.Show($"Remove {_selectedStudent.Name} from this room?", "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var response = await client.PostAsync($"{ApiEndpoints.Rooms}/{_roomId}/sessions/remove/{_selectedStudent.StudentId}", null);

                if (!response.IsSuccessStatusCode)
                {
                    MessageBox.Show("Failed to remove participant from room.", "Remove Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                LogActivity(_selectedStudent.Email, "KICKED", "Instructor removed student from room.", "#D32F2F");
                _selectedStudent = null;
                TxtLogHeader.Text = "Global Log Feed";
                TxtSelectedName.Text = "Select a Student";
                LogFeedItemsControl.ItemsSource = _logsView;
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

            if (_currentSessionId > 0)
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                await client.PutAsync($"{ApiEndpoints.Rooms}/sessions/{_currentSessionId}/end", null);
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