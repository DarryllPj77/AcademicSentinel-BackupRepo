using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Threading;
using AcademicSentinel.Client.Constants;
using AcademicSentinel.Client.Services;
using AcademicSentinel.Client.Services.SAC;
using Microsoft.AspNetCore.SignalR.Client;
using AcademicSentinel.Client.Views.SAC;
using System.Windows.Media;

namespace AcademicSentinel.Client.Views.SAC
{
    public partial class SecureAssessmentClientWindow : Window
    {
        public ObservableCollection<string> DetectionReports { get; set; }
        private bool _isMonitoringActive;
        private int _roomId;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _compactCountdownTimer;
        private readonly DispatcherTimer _detectorPollTimer;
        private HubConnection _hubConnection;
        private DateTime? _monitoringStartedAt;
        private readonly TimeSpan _defaultMonitoringDuration = TimeSpan.FromHours(1);
        private DateTime? _monitoringCountdownEndsAt;
        private TimeSpan _currentMonitoringDuration = TimeSpan.FromHours(1);
        private bool _sessionEnded;
        private bool _timerEnabled;
        private RoomDetectionSettingsDto _roomDetectionSettings;
        private bool _detectorsInitialized;
        private bool _detectorsRunning;
        private SacDetectorRuntime _detectorRuntime;
        private enum ExamPhase
        {
            PreSession,
            Countdown,
            Active
        }

        private enum LeaveRequestState
        {
            Locked,
            Pending,
            Unlocked
        }

        private ExamPhase _currentPhase = ExamPhase.PreSession;
        private LeaveRequestState _leaveRequestState = LeaveRequestState.Locked;
        private bool _allowClose;
        private bool _isPermanentlyDone;
        private bool _awaitingJoinApproval;
        private int _pendingParticipantId;
        private readonly Queue<MonitoringEventDto> _pendingViolationQueue = new Queue<MonitoringEventDto>();
        private readonly Dictionary<string, DateTime> _lastViolationSentByType = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ProcessBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windbg", "x64dbg", "ollydbg", "ida", "dnspy", "fiddler", "cheatengine",
            "processmonitor", "procexp", "teamviewer", "anydesk", "ultravnc", "gotomypc",
            "discord", "telegram", "slack", "whatsapp", "skype", "teams", "zoom",
            "obs", "ffmpeg", "camtasia", "snagit", "bandicam", "chrome", "firefox", "opera"
        };

        // Now accepts the Room ID from the Waiting Room!
        public SecureAssessmentClientWindow(int roomId, string roomTitle)
        {
            InitializeComponent();

            _roomId = roomId;
            TxtCourseRoom.Text = roomTitle;

            DetectionReports = new ObservableCollection<string>();
            DetectionReportsList.ItemsSource = DetectionReports;

            LoadSampleReports();

            // Students may leave while waiting; lock leave only once monitoring becomes active.
            SetMonitoringActive(false);

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _statusTimer.Tick += async (_, __) => await RefreshMonitoringStateAsync();
            _statusTimer.Start();

            _compactCountdownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _compactCountdownTimer.Tick += (_, __) => UpdateCompactCountdown();
            _compactCountdownTimer.Start();

            _detectorPollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _detectorPollTimer.Tick += (_, __) => PollDetectors();
            _detectorPollTimer.Start();

            UpdateRequestLeaveButtonState();

            _ = LoadDetectionSettingsAsync();
            _ = InitializeSignalRAsync();
            _ = RefreshMonitoringStateAsync();
        }

        private async Task LoadDetectionSettingsAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);
                var response = await client.GetAsync($"{ApiEndpoints.Rooms}/{_roomId}/settings");
                if (response.IsSuccessStatusCode)
                {
                    _roomDetectionSettings = await response.Content.ReadFromJsonAsync<RoomDetectionSettingsDto>();
                }
                else
                {
                    _roomDetectionSettings = GetDefaultDetectionSettings();
                    DetectionReports.Add("Detector Setup: Room settings unavailable. Using safe defaults.");
                }

                InitializeDetectorsIfNeeded();
            }
            catch
            {
                _roomDetectionSettings = GetDefaultDetectionSettings();
                DetectionReports.Add("Detector Setup: Failed to load settings. Using safe defaults.");
                InitializeDetectorsIfNeeded();
            }
        }

        private static RoomDetectionSettingsDto GetDefaultDetectionSettings()
        {
            return new RoomDetectionSettingsDto
            {
                EnableFocusDetection = true,
                EnableClipboardMonitoring = true,
                EnableProcessDetection = true,
                EnableIdleDetection = true,
                IdleThresholdSeconds = 60,
                EnableVirtualizationCheck = true,
                StrictMode = false
            };
        }

        private void InitializeDetectorsIfNeeded()
        {
            if (_detectorsInitialized || _roomDetectionSettings == null)
                return;

            _detectorsInitialized = true;
            _detectorRuntime = new SacDetectorRuntime(new DetectorRuntimeOptions
            {
                EnableFocusDetection = _roomDetectionSettings.EnableFocusDetection,
                EnableClipboardMonitoring = _roomDetectionSettings.EnableClipboardMonitoring,
                EnableIdleDetection = _roomDetectionSettings.EnableIdleDetection,
                IdleThresholdSeconds = _roomDetectionSettings.IdleThresholdSeconds,
                EnableProcessDetection = _roomDetectionSettings.EnableProcessDetection,
                EnableVirtualizationCheck = _roomDetectionSettings.EnableVirtualizationCheck,
                BlacklistedProcessNames = new HashSet<string>(ProcessBlacklist, StringComparer.OrdinalIgnoreCase),
                OnHardwareStateDetected = async (isVm, isRemote) =>
                {
                    try
                    {
                        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
                            return;

                        int studentId = SessionManager.CurrentUser?.Id ?? 0;
                        if (studentId <= 0)
                            return;

                        await _hubConnection.InvokeAsync("UpdateHardwareState", _roomId, studentId, isVm, isRemote);
                    }
                    catch
                    {
                        // keep session stable
                    }
                },
                OnPreFlightViolationDetected = finding =>
                {
                    if (finding == null)
                        return;

                    Dispatcher.Invoke(() =>
                    {
                        _ = ReportViolationAsync(finding.EventType, finding.SeverityScore, finding.Description);
                    });
                }
            });

            var enabledModules = new List<string>();
            if (_roomDetectionSettings.EnableFocusDetection) enabledModules.Add("Focus");
            if (_roomDetectionSettings.EnableClipboardMonitoring) enabledModules.Add("Clipboard");
            if (_roomDetectionSettings.EnableProcessDetection) enabledModules.Add("Process");
            if (_roomDetectionSettings.EnableIdleDetection) enabledModules.Add("Idle");

            DetectionReports.Add($"Detector Setup: {(enabledModules.Count == 0 ? "No modules enabled" : string.Join(", ", enabledModules))}");
            ReportFindings(_detectorRuntime.RunStartupChecks());
            UpdateDetectorRuntimeState();
        }

        private void UpdateDetectorRuntimeState()
        {
            bool shouldRun = _detectorsInitialized
                             && _isMonitoringActive
                             && !_sessionEnded
                             && !_monitoringCountdownEndsAt.HasValue;

            _detectorRuntime?.SetMonitoringEnabled(shouldRun);

            if (shouldRun && !_detectorsRunning)
            {
                _detectorsRunning = true;
                DetectionReports.Add($"Detector Runtime: Active ({DateTime.Now:h:mm:ss tt})");
                DetectionReports.Add($"Idle baseline started at {DateTime.Now:h:mm:ss tt}");
            }
            else if (!shouldRun && _detectorsRunning)
            {
                _detectorsRunning = false;
                DetectionReports.Add($"Detector Runtime: Paused ({DateTime.Now:h:mm:ss tt})");
            }
        }

        private void LoadSampleReports()
        {
            // intentionally empty: no dummy detector data in production flow
        }

        private void SetMonitoringActive(bool isActive)
        {
            _isMonitoringActive = isActive;

            var compactStatus = FindName("TxtCompactMonitoringStatus") as System.Windows.Controls.TextBlock;
            var headerStatus = FindName("TxtHeaderMonitoringStatus") as System.Windows.Controls.TextBlock;
            var compactLeavePermission = FindName("TxtCompactLeavePermission") as System.Windows.Controls.TextBlock;

            if (isActive)
            {
                _currentPhase = ExamPhase.Active;
                MonitorDotBrush.Color = System.Windows.Media.Color.FromRgb(211, 47, 47);
                TxtMonitoringStatus.Text = "Monitoring Active - You cannot leave during the session";
                TxtMonitoringStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 40, 40));
                if (FindName("TxtFullCountdown") is System.Windows.Controls.TextBlock fullCountdown)
                    fullCountdown.Text = "Countdown: Running";
                if (compactStatus != null) { compactStatus.Text = "Monitoring: ACTIVE"; compactStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40)); }
                if (headerStatus != null) { headerStatus.Text = "Monitoring: ACTIVE"; headerStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40)); }
                if (compactLeavePermission != null) { compactLeavePermission.Text = "Leave Permission: Blocked"; compactLeavePermission.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40)); }

                _monitoringStartedAt ??= DateTime.Now;
                _monitoringCountdownEndsAt = null;
            }
            else
            {
                if (_sessionEnded)
                {
                    _currentPhase = ExamPhase.PreSession;
                    MonitorDotBrush.Color = System.Windows.Media.Color.FromRgb(97, 97, 97);
                    TxtMonitoringStatus.Text = "Session Ended - You may now leave the session";
                    TxtMonitoringStatus.Foreground = new SolidColorBrush(Color.FromRgb(97, 97, 97));
                    if (compactStatus != null) { compactStatus.Text = "Monitoring: Inactive"; compactStatus.Foreground = new SolidColorBrush(Color.FromRgb(97, 97, 97)); }
                    if (headerStatus != null) { headerStatus.Text = "SESSION ENDED"; headerStatus.Foreground = new SolidColorBrush(Color.FromRgb(97, 97, 97)); }
                    if (compactLeavePermission != null) { compactLeavePermission.Text = "Leave Permission: Allowed"; compactLeavePermission.Foreground = new SolidColorBrush(Color.FromRgb(97, 97, 97)); }
                }
                else
                {
                    _currentPhase = ExamPhase.PreSession;
                    MonitorDotBrush.Color = System.Windows.Media.Color.FromRgb(76, 175, 80);
                    TxtMonitoringStatus.Text = "Waiting for monitoring to start - You may leave for now";
                    TxtMonitoringStatus.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50));
                    if (compactStatus != null) { compactStatus.Text = "Monitoring: Waiting"; compactStatus.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50)); }
                    if (headerStatus != null) { headerStatus.Text = "Monitoring: Waiting"; headerStatus.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50)); }
                    if (compactLeavePermission != null) { compactLeavePermission.Text = "Leave Permission: Allowed"; compactLeavePermission.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50)); }
                }

                if (FindName("TxtFullCountdown") is System.Windows.Controls.TextBlock fullCountdown)
                    fullCountdown.Text = _sessionEnded ? "Countdown: 00:00" : "Countdown: --:--";

                _monitoringStartedAt = null;
            }

            UpdateCompactCountdown();
            UpdateHeaderSessionClock();
            UpdateDetectorRuntimeState();
            UpdateRequestLeaveButtonState();
        }

        private async Task RefreshMonitoringStateAsync()
        {
            try
            {
                if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
                {
                    var monitoringState = await _hubConnection.InvokeAsync<bool>("GetMonitoringState", _roomId);

                    if (_detectorRuntime?.IsPaused == true && monitoringState)
                    {
                        UpdateCompactCountdown();
                        return;
                    }

                    // Keep COUNTDOWN state stable until countdown ends.
                    if (_monitoringCountdownEndsAt.HasValue && DateTime.Now < _monitoringCountdownEndsAt.Value && !monitoringState)
                    {
                        UpdateCompactCountdown();
                        return;
                    }

                    SetMonitoringActive(monitoringState);
                    return;
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var response = await client.GetAsync($"{ApiEndpoints.Rooms}/{_roomId}");
                if (!response.IsSuccessStatusCode) return;

                var room = await response.Content.ReadFromJsonAsync<RoomStatusDto>();
                if (room == null) return;

                // Room status is session state, not monitoring state.
                // Do not force monitoring back to ACTIVE based on room.Status == Active.
                var roomIsActive = string.Equals(room.Status, "Active", StringComparison.OrdinalIgnoreCase);

                if (_monitoringCountdownEndsAt.HasValue && DateTime.Now < _monitoringCountdownEndsAt.Value)
                {
                    UpdateCompactCountdown();
                    return;
                }

                // If room is no longer active, treat as ended/off.
                if (!roomIsActive)
                {
                    _sessionEnded = true;
                    SetMonitoringActive(false);
                    return;
                }

                // Keep current monitoring state if room is active but hub isn't connected.
                UpdateCompactCountdown();
            }
            catch
            {
                // keep current UI state on transient errors
            }
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);

            if (FindName("CompactPanel") is not FrameworkElement compactPanel ||
                FindName("FullSessionPanel") is not FrameworkElement fullPanel)
                return;

            if (compactPanel.Visibility == Visibility.Visible)
                return;

            if (!_detectorsRunning)
            {
                SwitchToCompactMode();
                return;
            }

            ReportFindings(_detectorRuntime?.OnWindowDeactivated());
            SwitchToCompactMode();
        }

        private void PollDetectors()
        {
            if (!_detectorsRunning || _detectorRuntime == null)
                return;

            ReportFindings(_detectorRuntime.Poll(IsWindowForeground()));
        }

        private void ReportFindings(IReadOnlyList<DetectorFinding> findings)
        {
            if (findings == null || findings.Count == 0)
                return;

            foreach (var finding in findings)
                _ = ReportViolationAsync(finding.EventType, finding.SeverityScore, finding.Description);
        }

        private bool IsWindowForeground()
        {
            return IsActive;
        }

        private async Task ReportViolationAsync(string eventType, int severityScore, string description)
        {
            try
            {
                if (!_detectorRuntime?.IsLoggingEnabled ?? true)
                    return;

                if (!_detectorsRunning || !_isMonitoringActive || _sessionEnded || _monitoringCountdownEndsAt.HasValue)
                    return;

                var now = DateTime.UtcNow;
                if (_lastViolationSentByType.TryGetValue(eventType, out var lastSentAt) && (now - lastSentAt).TotalSeconds < 2)
                    return;

                _lastViolationSentByType[eventType] = now;

                var payload = new MonitoringEventDto
                {
                    RoomId = _roomId,
                    EventType = eventType,
                    SeverityScore = severityScore,
                    Description = description,
                    CurrentScore = ParseCurrentScore(description),
                    CurrentLevel = ParseCurrentLevel(description),
                    Timestamp = now
                };

                if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
                {
                    EnqueuePendingViolation(payload);
                    return;
                }

                int studentId = SessionManager.CurrentUser?.Id ?? 0;
                if (studentId <= 0)
                    return;

                await _hubConnection.InvokeAsync("SendMonitoringEvent", _roomId, studentId, payload);

                var logText = string.IsNullOrWhiteSpace(description)
                    ? $"Violation sent: {eventType} ({DateTime.Now:h:mm:ss tt})"
                    : $"Violation sent: {eventType} | {description} ({DateTime.Now:h:mm:ss tt})";

                DetectionReports.Insert(0, logText);
            }
            catch
            {
                // keep SAC session stable even if a send fails
            }
        }

        private void EnqueuePendingViolation(MonitoringEventDto payload)
        {
            while (_pendingViolationQueue.Count >= 50)
                _pendingViolationQueue.Dequeue();

            _pendingViolationQueue.Enqueue(payload);
        }

        private static int ParseCurrentScore(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return 0;

            const string marker = "CumulativeScore=";
            int start = description.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return 0;

            start += marker.Length;
            int end = description.IndexOf(';', start);
            var value = end >= 0 ? description.Substring(start, end - start) : description.Substring(start);
            return int.TryParse(value.Trim(), out var parsed) ? parsed : 0;
        }

        private static string ParseCurrentLevel(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return string.Empty;

            const string marker = "RiskLevel=";
            int start = description.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;

            start += marker.Length;
            int end = description.IndexOf(';', start);
            var value = end >= 0 ? description.Substring(start, end - start) : description.Substring(start);
            return value.Trim();
        }

        private async Task FlushPendingViolationsAsync()
        {
            if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
                return;

            int studentId = SessionManager.CurrentUser?.Id ?? 0;
            if (studentId <= 0)
                return;

            while (_pendingViolationQueue.Count > 0)
            {
                var payload = _pendingViolationQueue.Dequeue();
                await _hubConnection.InvokeAsync("SendMonitoringEvent", _roomId, studentId, payload);
            }
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (ReferenceEquals(current, parent))
                    return true;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private async Task<bool> RequestJoinGateAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SessionManager.JwtToken);

                var response = await client.PostAsync($"{ApiEndpoints.Rooms}/{_roomId}/request-join", null);
                if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
                {
                    var data = await response.Content.ReadFromJsonAsync<JoinResponseDto>();
                    if (data == null)
                        return false;

                    _awaitingJoinApproval = true;
                    _pendingParticipantId = data.ParticipantId;

                    if (WaitingScreenOverlay != null)
                        WaitingScreenOverlay.Visibility = Visibility.Visible;
                    if (WaitingScreenText != null)
                    {
                        WaitingScreenText.Text = data.IsLate
                            ? "Waiting for instructor to admit you to the session..."
                            : "Waiting for instructor to approve your rejoin request...";
                    }

                    var studentId = SessionManager.CurrentUser?.Id ?? 0;
                    if (studentId > 0 && _hubConnection?.State == HubConnectionState.Connected)
                    {
                        await _hubConnection.InvokeAsync("NotifyInstructorStudentPending", _roomId, studentId);
                    }

                    return false;
                }

                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadFromJsonAsync<JoinResponseDto>();
                    if (data != null)
                        _pendingParticipantId = data.ParticipantId;

                    _awaitingJoinApproval = false;
                    return true;
                }

                var errorMsg = await response.Content.ReadAsStringAsync();
                MessageBox.Show(errorMsg, "Join Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to request join: {ex.Message}", "Join Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _allowClose = true;
            ReturnToStudentDashboard();
            return false;
        }

        private async Task StartLiveExamAsync()
        {
            if (_hubConnection == null)
                return;

            await _hubConnection.InvokeAsync("JoinLiveExam", _roomId);
            await FlushPendingViolationsAsync();

            var monitoringState = await _hubConnection.InvokeAsync<bool>("GetMonitoringState", _roomId);
            SetMonitoringActive(monitoringState);
        }

        private async Task InitializeSignalRAsync()
        {
            try
            {
                _hubConnection = new HubConnectionBuilder()
                    .WithUrl($"{ApiEndpoints.BaseUrl}/monitoringHub", options =>
                    {
                        options.AccessTokenProvider = () => Task.FromResult(SessionManager.JwtToken);
                    })
                    .WithAutomaticReconnect()
                    .Build();

                _hubConnection.Reconnected += async _ =>
                {
                    try
                    {
                        if (_awaitingJoinApproval)
                            return;

                        await _hubConnection.InvokeAsync("JoinLiveExam", _roomId);
                        var reconnectedStudentId = SessionManager.CurrentUser?.Id ?? 0;
                        if (reconnectedStudentId > 0)
                            await _hubConnection.InvokeAsync("ReSyncState", _roomId, reconnectedStudentId);
                        await Dispatcher.InvokeAsync(async () => await FlushPendingViolationsAsync());
                    }
                    catch
                    {
                    }
                };

                _hubConnection.On("SessionStarted", () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_sessionEnded)
                            return;

                        _currentPhase = ExamPhase.Countdown;
                        _leaveRequestState = LeaveRequestState.Locked;
                        UpdateRequestLeaveButtonState();
                    });
                });

                _hubConnection.Closed += async _ =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    if (_hubConnection == null)
                        return;

                    try
                    {
                        await _hubConnection.StartAsync();
                        await _hubConnection.InvokeAsync("JoinLiveExam", _roomId);
                        var recoveredStudentId = SessionManager.CurrentUser?.Id ?? 0;
                        if (recoveredStudentId > 0)
                            await _hubConnection.InvokeAsync("ReSyncState", _roomId, recoveredStudentId);
                        await Dispatcher.InvokeAsync(async () => await FlushPendingViolationsAsync());
                    }
                    catch
                    {
                    }
                };

                _hubConnection.On<bool>("MonitoringStateChanged", isActive =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_sessionEnded && isActive)
                            return;
                        SetMonitoringActive(isActive);
                    });
                });

                _hubConnection.On("MonitoringPaused", () =>
                {
                    _ = Dispatcher.Invoke(async () =>
                    {
                        if (_sessionEnded)
                            return;

                        _detectorsRunning = false;
                        if (_detectorRuntime != null)
                        {
                            _detectorRuntime.IsPaused = true;
                        }

                        TxtMonitoringStatus.Text = "Paused (Awaiting Instructor)";
                        TxtMonitoringStatus.Foreground = new SolidColorBrush(Color.FromRgb(230, 126, 34));

                        if (FindName("TxtCompactMonitoringStatus") is TextBlock compactStatus)
                        {
                            compactStatus.Text = "Monitoring: PAUSED";
                            compactStatus.Foreground = new SolidColorBrush(Color.FromRgb(230, 126, 34));
                        }

                        if (FindName("TxtHeaderMonitoringStatus") is TextBlock headerStatus)
                        {
                            headerStatus.Text = "Monitoring: PAUSED";
                            headerStatus.Foreground = new SolidColorBrush(Color.FromRgb(230, 126, 34));
                        }

                        // Soft lock remains active: do not alter phase/leave-request state.
                        UpdateHeaderSessionClock();
                    });
                });

                _hubConnection.On("MonitoringResumed", () =>
                {
                    _ = Dispatcher.Invoke(async () =>
                    {
                        if (_sessionEnded)
                            return;

                        _detectorsRunning = true;
                        TxtMonitoringStatus.Text = "Monitoring resumes in 10 seconds...";
                        TxtMonitoringStatus.Foreground = new SolidColorBrush(Color.FromRgb(230, 126, 34));

                        if (FindName("TxtCompactMonitoringStatus") is TextBlock compactStatus)
                        {
                            compactStatus.Text = "Monitoring: RESUMING";
                            compactStatus.Foreground = new SolidColorBrush(Color.FromRgb(230, 126, 34));
                        }

                        if (FindName("TxtHeaderMonitoringStatus") is TextBlock headerStatus)
                        {
                            headerStatus.Text = "Monitoring: RESUMING";
                            headerStatus.Foreground = new SolidColorBrush(Color.FromRgb(230, 126, 34));
                        }

                        _ = Task.Run(async () =>
                        {
                            for (int i = 10; i > 0; i--)
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    TxtMonitoringStatus.Text = $"Monitoring resumes in {i} seconds...";
                                });

                                await Task.Delay(TimeSpan.FromSeconds(1));
                            }

                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (_detectorRuntime != null)
                                {
                                    _detectorRuntime.IsPaused = false;
                                }

                                TxtMonitoringStatus.Text = "Monitoring Active - You cannot leave during the session";
                                TxtMonitoringStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));

                                if (FindName("TxtCompactMonitoringStatus") is TextBlock compactResumeStatus)
                                {
                                    compactResumeStatus.Text = "Monitoring: ACTIVE";
                                    compactResumeStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                                }

                                if (FindName("TxtHeaderMonitoringStatus") is TextBlock headerResumeStatus)
                                {
                                    headerResumeStatus.Text = "Monitoring: ACTIVE";
                                    headerResumeStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                                }

                                // Soft lock remains active: do not alter phase/leave-request state.
                                UpdateHeaderSessionClock();
                            });
                        });
                    });
                });

                _hubConnection.On<int>("LeaveGranted", grantedStudentId =>
                {
                    Dispatcher.Invoke(async () =>
                    {
                        int currentStudentId = SessionManager.CurrentUser?.Id ?? 0;
                        if (grantedStudentId != currentStudentId)
                            return;

                        if (_detectorRuntime != null)
                        {
                            await _detectorRuntime.StopMonitoringAsync();
                        }

                        await ForceStopSignalRAsync();
                        StopMonitoringForApprovedLeave();
                        _leaveRequestState = LeaveRequestState.Unlocked;
                        TxtMonitoringStatus.Text = "Permission Granted - Leave Now";
                        TxtMonitoringStatus.Foreground = new SolidColorBrush(Color.FromRgb(27, 94, 32));
                        if (FindName("TxtCompactLeavePermission") is TextBlock leavePerm)
                        {
                            leavePerm.Text = "Permission Granted - Leave Now";
                            leavePerm.Foreground = new SolidColorBrush(Color.FromRgb(27, 94, 32));
                        }

                        if (FindName("BtnRequestLeave") is Button leaveButton)
                        {
                            leaveButton.Content = "Permission Granted - Leave Now";
                            leaveButton.IsEnabled = true;
                            leaveButton.Background = new SolidColorBrush(Color.FromRgb(27, 94, 32));
                            leaveButton.Foreground = Brushes.White;
                        }

                        UpdateRequestLeaveButtonState();
                    });
                });

                _hubConnection.On<int, int>("MonitoringCountdownStarted", (delaySeconds, monitoringDurationSeconds) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _currentPhase = ExamPhase.Countdown;
                        _leaveRequestState = LeaveRequestState.Locked;
                        _monitoringCountdownEndsAt = DateTime.Now.AddSeconds(Math.Max(0, delaySeconds));
                        _timerEnabled = monitoringDurationSeconds > 0;
                        _currentMonitoringDuration = _timerEnabled ? TimeSpan.FromSeconds(monitoringDurationSeconds) : TimeSpan.Zero;
                        _sessionEnded = false;

                        _isMonitoringActive = true; // lock leave during countdown
                        TxtMonitoringStatus.Text = $"Monitoring starts in {delaySeconds}s - Leave disabled";
                        TxtMonitoringStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));

                        if (FindName("TxtCompactMonitoringStatus") is System.Windows.Controls.TextBlock compactStatus)
                        {
                            compactStatus.Text = "Monitoring: COUNTDOWN";
                            compactStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                        }
                        if (FindName("TxtHeaderMonitoringStatus") is System.Windows.Controls.TextBlock headerStatus)
                        {
                            headerStatus.Text = "Monitoring: COUNTDOWN";
                            headerStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                        }

                        if (FindName("TxtCompactLeavePermission") is System.Windows.Controls.TextBlock leavePerm)
                        {
                            leavePerm.Text = "Leave Permission: Blocked";
                            leavePerm.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                        }
                        UpdateDetectorRuntimeState();
                        UpdateCompactCountdown();
                        UpdateRequestLeaveButtonState();
                    });
                });

                _hubConnection.On<string>("JoinFailed", message =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show(message, "Join Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        ReturnToStudentDashboard();
                    });
                });

                _hubConnection.On<JoinApprovedDto>("OnJoinApproved", response =>
                {
                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        _awaitingJoinApproval = false;
                        _pendingParticipantId = response.ParticipantId;

                        if (WaitingScreenOverlay != null)
                            WaitingScreenOverlay.Visibility = Visibility.Collapsed;

                        await StartLiveExamAsync();
                    });
                });

                _hubConnection.On<JoinDeniedDto>("OnJoinDenied", response =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _awaitingJoinApproval = false;

                        if (WaitingScreenOverlay != null)
                            WaitingScreenOverlay.Visibility = Visibility.Collapsed;

                        MessageBox.Show(response.Reason, "Access Denied", MessageBoxButton.OK, MessageBoxImage.Error);

                        _allowClose = true;
                        ReturnToStudentDashboard();
                    });
                });

                _hubConnection.On("SessionEnded", () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _sessionEnded = true;
                        _monitoringCountdownEndsAt = null;
                        _monitoringStartedAt = null;
                        _timerEnabled = false;
                        SetMonitoringActive(false);
                        TxtMonitoringStatus.Text = "Session Ended - You may now leave the session";
                        TxtMonitoringStatus.Foreground = new SolidColorBrush(Color.FromRgb(97, 97, 97));

                        if (FindName("TxtCompactMonitoringStatus") is System.Windows.Controls.TextBlock compactStatus)
                        {
                            compactStatus.Text = "Monitoring: Session Ended";
                            compactStatus.Foreground = new SolidColorBrush(Color.FromRgb(97, 97, 97));
                        }
                        if (FindName("TxtHeaderMonitoringStatus") is System.Windows.Controls.TextBlock headerStatus)
                        {
                            headerStatus.Text = "Monitoring: Session Ended";
                            headerStatus.Foreground = new SolidColorBrush(Color.FromRgb(97, 97, 97));
                        }
                        if (FindName("TxtCompactCountdown") is System.Windows.Controls.TextBlock countdown)
                            countdown.Text = "";
                        if (FindName("TxtCompactLeavePermission") is System.Windows.Controls.TextBlock leavePerm)
                        {
                            leavePerm.Text = "Leave Permission: Allowed";
                            leavePerm.Foreground = new SolidColorBrush(Color.FromRgb(97, 97, 97));
                        }
                        _ = _hubConnection?.StopAsync();
                        _hubConnection = null;
                        UpdateDetectorRuntimeState();
                        UpdateRequestLeaveButtonState();
                    });
                });

                await _hubConnection.StartAsync();
                if (await RequestJoinGateAsync())
                {
                    await StartLiveExamAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to connect to live session: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void StopMonitoringForApprovedLeave()
        {
            _monitoringCountdownEndsAt = null;
            _monitoringStartedAt = null;
            _isMonitoringActive = false;
            _detectorsRunning = false;
            _ = _detectorRuntime?.StopMonitoringAsync();
            _pendingViolationQueue.Clear();
            _lastViolationSentByType.Clear();

            if (FindName("TxtCompactMonitoringStatus") is TextBlock compactStatus)
            {
                compactStatus.Text = "Monitoring: Stopped";
                compactStatus.Foreground = new SolidColorBrush(Color.FromRgb(97, 97, 97));
            }

            TxtMonitoringStatus.Text = "Stopped";
            TxtMonitoringStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"));

            if (FindName("TxtHeaderSessionClock") is TextBlock headerClock)
                headerClock.Text = string.Empty;

            if (FindName("TxtFullCountdown") is TextBlock fullCountdown)
                fullCountdown.Text = string.Empty;
        }

        private async Task ForceStopSignalRAsync()
        {
            var connection = _hubConnection;
            if (connection == null)
                return;

            try
            {
                await connection.StopAsync();
            }
            catch
            {
            }
            finally
            {
                _hubConnection = null;
                _pendingViolationQueue.Clear();
            }
        }

        private async void BtnRequestLeave_Click(object sender, RoutedEventArgs e)
        {
            if (_sessionEnded)
            {
                int sessionEndedStudentId = SessionManager.CurrentUser?.Id ?? 0;
                await LeaveSessionSafelyAsync(sessionEndedStudentId);
                return;
            }

            int studentId = SessionManager.CurrentUser?.Id ?? 0;
            if (studentId <= 0)
            {
                UpdateRequestLeaveButtonState();
                return;
            }

            switch (_currentPhase)
            {
                case ExamPhase.PreSession:
                    await LeaveSessionSafelyAsync(studentId);
                    return;

                case ExamPhase.Countdown:
                    return;

                case ExamPhase.Active:
                {
                    if (_leaveRequestState == LeaveRequestState.Locked)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (FindName("BtnRequestLeave") is Button leaveButton)
                            {
                                leaveButton.IsEnabled = false;
                                leaveButton.Content = "Waiting for Instructor...";
                            }
                        });

                        try
                        {
                            if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
                            {
                                MessageBox.Show("Not connected to server.", "Request Leave", MessageBoxButton.OK, MessageBoxImage.Warning);
                                UpdateRequestLeaveButtonState();
                                return;
                            }

                            _leaveRequestState = LeaveRequestState.Pending;
                            UpdateRequestLeaveButtonState();
                            await _hubConnection.InvokeAsync("RequestLeave", _roomId, studentId);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to request leave: {ex.Message}", "Request Leave", MessageBoxButton.OK, MessageBoxImage.Warning);
                            if (_leaveRequestState != LeaveRequestState.Pending)
                                UpdateRequestLeaveButtonState();
                        }

                        return;
                    }

                    if (_leaveRequestState == LeaveRequestState.Unlocked)
                    {
                        await LeaveSessionSafelyAsync(studentId);
                    }

                    return;
                }

                default:
                    return;
            }
        }

        private async Task LeaveSessionSafelyAsync(int studentId)
        {
            _allowClose = true;
            _isPermanentlyDone = true;
            _detectorsRunning = false;
            if (_detectorRuntime != null)
            {
                await _detectorRuntime.StopMonitoringAsync();
            }
            _statusTimer?.Stop();
            _compactCountdownTimer?.Stop();
            _detectorPollTimer?.Stop();

            try
            {
                if (studentId > 0 && _hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
                {
                    await _hubConnection.InvokeAsync("NotifyStudentLeftSafely", _roomId, studentId);
                }
            }
            catch
            {
                // best-effort notify; do not block clean exit
            }

            await ForceStopSignalRAsync();

            ReturnToStudentDashboard();
        }

        private void UpdateRequestLeaveButtonState()
        {
            if (FindName("BtnRequestLeave") is not Button btn)
                return;

            Dispatcher.Invoke(() =>
            {
                if (_sessionEnded)
                {
                    btn.Content = "Leave Session";
                    btn.IsEnabled = true;
                    btn.Background = new SolidColorBrush(Color.FromRgb(27, 94, 32));
                    btn.Foreground = Brushes.White;
                    return;
                }

                switch (_currentPhase)
                {
                    case ExamPhase.PreSession:
                        btn.Content = "Leave Session";
                        btn.IsEnabled = true;
                        btn.Background = new SolidColorBrush(Color.FromRgb(27, 94, 32));
                        btn.Foreground = Brushes.White;
                        break;

                    case ExamPhase.Countdown:
                        btn.Content = "Cannot Leave";
                        btn.IsEnabled = false;
                        btn.Background = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                        btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#424242"));
                        break;

                    case ExamPhase.Active:
                        switch (_leaveRequestState)
                        {
                            case LeaveRequestState.Locked:
                                btn.Content = "Request to Leave";
                                btn.IsEnabled = true;
                                btn.Background = new SolidColorBrush(Color.FromRgb(211, 47, 47));
                                btn.Foreground = Brushes.White;
                                break;

                            case LeaveRequestState.Pending:
                                btn.Content = "Waiting for Instructor...";
                                btn.IsEnabled = false;
                                btn.Background = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                                btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#424242"));
                                break;

                            case LeaveRequestState.Unlocked:
                                btn.Content = "Permission Granted - Leave Now";
                                btn.IsEnabled = true;
                                btn.Background = new SolidColorBrush(Color.FromRgb(27, 94, 32));
                                btn.Foreground = Brushes.White;
                                break;
                        }
                        break;
                }
            });
        }

        private void ReturnToStudentDashboard()
        {
            try
            {
                var dashboard = new StudentDashboard();
                dashboard.Show();
            }
            catch
            {
                // no-op
            }

            this.Close();
        }

        private void UpdateCompactCountdown()
        {
            if (_detectorRuntime?.IsPaused == true && !_sessionEnded && _isMonitoringActive && !_monitoringCountdownEndsAt.HasValue)
            {
                TxtMonitoringStatus.Text = "Paused (Awaiting Instructor)";
                TxtMonitoringStatus.Foreground = Brushes.Orange;

                if (FindName("TxtCompactMonitoringStatus") is TextBlock compactStatus)
                {
                    compactStatus.Text = "Monitoring: PAUSED";
                    compactStatus.Foreground = Brushes.Orange;
                }
            }
            else if (!_sessionEnded && _isMonitoringActive && !_monitoringCountdownEndsAt.HasValue)
            {
                TxtMonitoringStatus.Text = "ACTIVE";
                TxtMonitoringStatus.Foreground = Brushes.LimeGreen;

                if (FindName("TxtCompactMonitoringStatus") is TextBlock compactStatus)
                {
                    compactStatus.Text = "Monitoring: ACTIVE";
                    compactStatus.Foreground = Brushes.LimeGreen;
                }
            }

            if (_monitoringCountdownEndsAt.HasValue)
            {
                var countdownLeft = _monitoringCountdownEndsAt.Value - DateTime.Now;
                if (countdownLeft <= TimeSpan.Zero)
                {
                    _monitoringCountdownEndsAt = null;
                    _monitoringStartedAt ??= DateTime.Now;
                    if (FindName("TxtFullCountdown") is System.Windows.Controls.TextBlock fullCountdown)
                        fullCountdown.Text = _timerEnabled ? $"Countdown: {_currentMonitoringDuration:mm\\:ss}" : string.Empty;
                }
                else
                {
                    if (FindName("TxtFullCountdown") is System.Windows.Controls.TextBlock fullCountdown)
                        fullCountdown.Text = $"Starts In: {countdownLeft:mm\\:ss}";
                    UpdateHeaderSessionClock();
                    return;
                }
            }

            if (_monitoringStartedAt == null || !_isMonitoringActive)
            {
                if (FindName("TxtFullCountdown") is System.Windows.Controls.TextBlock fullCountdown)
                    fullCountdown.Text = _sessionEnded ? "Countdown: 00:00" : "Countdown: --:--";
                UpdateHeaderSessionClock();
                return;
            }

            var elapsed = DateTime.Now - _monitoringStartedAt.Value;
            var remaining = _currentMonitoringDuration - elapsed;
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            // Keep soft lock enforced even when timer reaches zero.
            // Leave permissions must only change via Pre-Session, SessionEnded, or LeaveGranted flows.
            if (_timerEnabled && remaining == TimeSpan.Zero && _isMonitoringActive && !_sessionEnded)
            {
                if (FindName("TxtFullCountdown") is TextBlock fullCountdownAtZero)
                    fullCountdownAtZero.Text = "Countdown: 00:00";
                UpdateHeaderSessionClock();
                return;
            }

            if (FindName("TxtFullCountdown") is System.Windows.Controls.TextBlock fullRunningCountdown)
                fullRunningCountdown.Text = _timerEnabled ? $"Countdown: {remaining:mm\\:ss}" : string.Empty;
            UpdateHeaderSessionClock();
        }

        private void UpdateHeaderSessionClock()
        {
            if (FindName("TxtHeaderSessionClock") is not System.Windows.Controls.TextBlock headerClock)
                return;

            if (_sessionEnded)
            {
                headerClock.Text = "SESSION ENDED";
                return;
            }

            if (_monitoringCountdownEndsAt.HasValue)
            {
                var left = _monitoringCountdownEndsAt.Value - DateTime.Now;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                headerClock.Text = $"Starts In: {left:mm\\:ss}";
                return;
            }

            if (_monitoringStartedAt.HasValue && _isMonitoringActive)
            {
                if (!_timerEnabled)
                {
                    headerClock.Text = string.Empty;
                    return;
                }

                var elapsed = DateTime.Now - _monitoringStartedAt.Value;
                var remaining = _currentMonitoringDuration - elapsed;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                headerClock.Text = $"Timer: {remaining:mm\\:ss}";
                return;
            }

            headerClock.Text = string.Empty;
        }

        private void BtnExpandCompact_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
            Width = 900;
            Height = 720;
            Topmost = false;
            if (FindName("BtnHeaderExpand") is System.Windows.Controls.Button headerExpand)
                headerExpand.Visibility = Visibility.Collapsed;
            if (FindName("SessionContentGrid") is FrameworkElement contentGrid)
                contentGrid.Margin = new Thickness(30, 24, 30, 24);
            if (FindName("BtnRequestLeave") is System.Windows.Controls.Button leaveButton)
                leaveButton.Visibility = Visibility.Visible;
            Left = (SystemParameters.WorkArea.Width - Width) / 2 + SystemParameters.WorkArea.Left;
            Top = (SystemParameters.WorkArea.Height - Height) / 2 + SystemParameters.WorkArea.Top;

            if (FindName("FullSessionPanel") is FrameworkElement fullPanel)
                fullPanel.Visibility = Visibility.Visible;
            if (FindName("CompactPanel") is FrameworkElement compactPanel)
                compactPanel.Visibility = Visibility.Collapsed;

            Activate();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (WindowState == WindowState.Minimized)
            {
                SwitchToCompactMode();
            }
        }

        private void SwitchToCompactMode()
        {
            if (FindName("CompactPanel") is FrameworkElement compactPanel && compactPanel.Visibility == Visibility.Visible)
                return;

            WindowState = WindowState.Normal;
            Width = 420;
            Height = 220;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;

            if (FindName("BtnHeaderExpand") is Button headerExpand)
                headerExpand.Visibility = Visibility.Visible;
            if (FindName("BtnRequestLeave") is Button leaveButton)
                leaveButton.Visibility = Visibility.Visible;
            if (FindName("SessionContentGrid") is FrameworkElement contentGrid)
                contentGrid.Margin = new Thickness(8, 8, 8, 8);

            Left = SystemParameters.WorkArea.Right - Width - 16;
            Top = SystemParameters.WorkArea.Bottom - Height - 16;

            if (FindName("FullSessionPanel") is FrameworkElement fullPanel)
                fullPanel.Visibility = Visibility.Collapsed;
            if (FindName("CompactPanel") is FrameworkElement shownCompactPanel)
                shownCompactPanel.Visibility = Visibility.Visible;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowClose && !_isPermanentlyDone && _leaveRequestState != LeaveRequestState.Unlocked && !_awaitingJoinApproval)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                return;
            }

            _statusTimer?.Stop();
            _compactCountdownTimer?.Stop();
            _detectorPollTimer?.Stop();
            _detectorsRunning = false;
            if (_hubConnection != null)
            {
                _ = _hubConnection.StopAsync();
            }
            base.OnClosing(e);
        }

        private class RoomStatusDto
        {
            public string Status { get; set; } = string.Empty;
        }

        private class RoomDetectionSettingsDto
        {
            public bool EnableClipboardMonitoring { get; set; }
            public bool EnableProcessDetection { get; set; }
            public bool EnableIdleDetection { get; set; }
            public int IdleThresholdSeconds { get; set; }
            public bool EnableFocusDetection { get; set; }
            public bool EnableVirtualizationCheck { get; set; }
            public bool StrictMode { get; set; }
        }

        private class MonitoringEventDto
        {
            public int RoomId { get; set; }
            public string EventType { get; set; } = string.Empty;
            public int SeverityScore { get; set; }
            public string Description { get; set; } = string.Empty;
            public int CurrentScore { get; set; }
            public string CurrentLevel { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        }

        private class JoinResponseDto
        {
            public string Status { get; set; } = string.Empty;
            public int ParticipantId { get; set; }
            public bool IsRejoin { get; set; }
            public bool IsLate { get; set; }
        }

        private class JoinApprovedDto
        {
            public int RoomId { get; set; }
            public int StudentId { get; set; }
            public int ParticipantId { get; set; }
        }

        private class JoinDeniedDto
        {
            public int RoomId { get; set; }
            public int StudentId { get; set; }
            public string Reason { get; set; } = string.Empty;
        }
    }
}