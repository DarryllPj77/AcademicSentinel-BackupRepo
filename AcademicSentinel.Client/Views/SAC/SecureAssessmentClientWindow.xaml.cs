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
        private bool _isLeaveApproved;
        private bool _isLeaveRequested;
        private bool _isHandlingFailure = false;
        private bool _isTransitioningState = false;
        private System.Threading.CancellationTokenSource _stateCts;
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

            _isLeaveRequested = false;
            _isLeaveApproved = false;
            _detectorRuntime?.IsPaused = true;

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

            _detectorRuntime.IsPaused = true;

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

        private void SetMonitoringStateUI(bool isActive, string statusText, System.Windows.Media.Brush color)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_detectorRuntime != null)
                    _detectorRuntime.IsPaused = !isActive;

                // Always unlock the leave button so students are never trapped
                BtnRequestLeave.IsEnabled = true;

                // Update Main View
                if (TxtMonitoringStatus != null)
                {
                    TxtMonitoringStatus.Text = statusText;
                    TxtMonitoringStatus.Foreground = color;
                }

                // Update Compact View
                if (FindName("TxtCompactMonitoringStatus") is TextBlock compactStatus)
                {
                    compactStatus.Text = statusText;
                    compactStatus.Foreground = color;
                }
            });
        }

        private async Task RefreshMonitoringStateAsync()
        {
            try
            {
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

                _hubConnection.On<bool>("MonitoringStateChanged", (isActive) =>
                {
                    _stateCts?.Cancel();
                    if (isActive)
                    {
                        _isMonitoringActive = true;
                        _monitoringCountdownEndsAt = null;
                        _monitoringStartedAt ??= DateTime.Now;
                        _currentPhase = ExamPhase.Active;
                    }
                    else
                    {
                        _isMonitoringActive = false;
                        _monitoringCountdownEndsAt = null;
                        _monitoringStartedAt = null;
                        if (!_sessionEnded)
                            _currentPhase = ExamPhase.PreSession;
                        _leaveRequestState = LeaveRequestState.Locked;
                        _isLeaveRequested = false;
                    }

                    string text = isActive ? "ACTIVE" : "INACTIVE";
                    var color = isActive ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Gray;
                    SetMonitoringStateUI(isActive, text, color);
                    UpdateDetectorRuntimeState();
                    UpdateRequestLeaveButtonState();
                });

                _hubConnection.On("MonitoringPaused", () =>
                {
                    _stateCts?.Cancel();
                    _isMonitoringActive = false;
                    _monitoringCountdownEndsAt = null;
                    _monitoringStartedAt = null;
                    if (!_sessionEnded)
                        _currentPhase = ExamPhase.Active;
                    _leaveRequestState = LeaveRequestState.Locked;
                    _isLeaveRequested = false;

                    SetMonitoringStateUI(false, "PAUSED BY INSTRUCTOR", System.Windows.Media.Brushes.Goldenrod);
                    UpdateDetectorRuntimeState();
                    UpdateRequestLeaveButtonState();
                });

                _hubConnection.On("MonitoringResumed", () =>
                {
                    _stateCts?.Cancel();
                    _stateCts = new System.Threading.CancellationTokenSource();
                    var token = _stateCts.Token;

                    _isMonitoringActive = false;
                    _monitoringCountdownEndsAt = DateTime.Now.AddSeconds(10);
                    _currentPhase = ExamPhase.Countdown;
                    _leaveRequestState = LeaveRequestState.Locked;
                    UpdateDetectorRuntimeState();
                    UpdateRequestLeaveButtonState();

                    Task.Run(async () =>
                    {
                        try
                        {
                            var endTime = DateTime.UtcNow.AddSeconds(10);
                            while (true)
                            {
                                if (token.IsCancellationRequested)
                                    return;

                                var remaining = (int)Math.Ceiling((endTime - DateTime.UtcNow).TotalSeconds);
                                if (remaining <= 0)
                                    break;

                                SetMonitoringStateUI(false, $"RESUMING IN {remaining}s...", System.Windows.Media.Brushes.Goldenrod);
                                await Task.Delay(250, token);
                            }

                            if (!token.IsCancellationRequested)
                            {
                                _isMonitoringActive = true;
                                _monitoringCountdownEndsAt = null;
                                _monitoringStartedAt ??= DateTime.Now;
                                _currentPhase = ExamPhase.Active;
                                SetMonitoringStateUI(true, "ACTIVE", System.Windows.Media.Brushes.LimeGreen);
                                UpdateDetectorRuntimeState();
                                UpdateRequestLeaveButtonState();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }, token);
                });

                _hubConnection.On<int>("LeaveGranted", grantedStudentId =>
                {
                    Dispatcher.Invoke(async () =>
                    {
                        int currentStudentId = SessionManager.CurrentUser?.Id ?? 0;
                        if (grantedStudentId != currentStudentId)
                            return;

                        _detectorRuntime?.IsPaused = true;
                        _detectorRuntime?.Stop();

                        if (_detectorRuntime != null)
                        {
                            await _detectorRuntime.StopMonitoringAsync();
                        }

                        await ForceStopSignalRAsync();
                        StopMonitoringForApprovedLeave();
                        _leaveRequestState = LeaveRequestState.Unlocked;
                        _isLeaveRequested = false;
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

                _hubConnection.On<int, int>("MonitoringCountdownStarted", (delay, duration) =>
                {
                    _stateCts?.Cancel();
                    _stateCts = new System.Threading.CancellationTokenSource();
                    var token = _stateCts.Token;

                    _isMonitoringActive = false;
                    _monitoringCountdownEndsAt = DateTime.Now.AddSeconds(Math.Max(0, delay));
                    _currentPhase = ExamPhase.Countdown;
                    _leaveRequestState = LeaveRequestState.Locked;
                    UpdateDetectorRuntimeState();
                    UpdateRequestLeaveButtonState();

                    Task.Run(async () =>
                    {
                        try
                        {
                            var endTime = DateTime.UtcNow.AddSeconds(Math.Max(0, delay));
                            while (true)
                            {
                                if (token.IsCancellationRequested)
                                    return;

                                var remaining = (int)Math.Ceiling((endTime - DateTime.UtcNow).TotalSeconds);
                                if (remaining <= 0)
                                    break;

                                SetMonitoringStateUI(false, $"STARTING IN {remaining}s...", System.Windows.Media.Brushes.Goldenrod);
                                await Task.Delay(250, token);
                            }

                            if (!token.IsCancellationRequested)
                            {
                                _isMonitoringActive = true;
                                _monitoringCountdownEndsAt = null;
                                _monitoringStartedAt ??= DateTime.Now;
                                _currentPhase = ExamPhase.Active;
                                SetMonitoringStateUI(true, "ACTIVE", System.Windows.Media.Brushes.LimeGreen);
                                UpdateDetectorRuntimeState();
                                UpdateRequestLeaveButtonState();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }, token);
                });

                _hubConnection.On<string>("JoinFailed", message =>
                {
                    if (_isHandlingFailure)
                        return;

                    _isHandlingFailure = true;

                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Unable to join exam: {message}", "Join Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        _isLeaveApproved = true;
                        _ = ForceStopSignalRAsync();
                        new StudentDashboard().Show();
                        Close();
                    });
                });

                _hubConnection.On<int>("SessionInterrupted", interruptedRoomId =>
                {
                    if (interruptedRoomId != _roomId)
                        return;

                    Dispatcher.Invoke(() =>
                    {
                        _detectorRuntime?.IsPaused = true;
                        _detectorRuntime?.Stop();
                        MessageBox.Show("Session interrupted by instructor disconnect. You will be returned to the dashboard.", "Session Interrupted", MessageBoxButton.OK, MessageBoxImage.Warning);
                        _isLeaveApproved = true;
                        _ = ForceStopSignalRAsync();
                        new StudentDashboard().Show();
                        Close();
                    });
                });

                _hubConnection.On("SessionEnded", () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _detectorRuntime?.IsPaused = true;
                        _detectorRuntime?.Stop();
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
                await _hubConnection.InvokeAsync("JoinLiveExam", _roomId);

                try
                {
                    bool isMonitoringActive = await _hubConnection.InvokeAsync<bool>("GetMonitoringState", _roomId);
                    _isMonitoringActive = isMonitoringActive;
                    _monitoringCountdownEndsAt = null;
                    _monitoringStartedAt = isMonitoringActive ? DateTime.Now : null;
                    if (!_sessionEnded)
                        _currentPhase = isMonitoringActive ? ExamPhase.Active : ExamPhase.PreSession;

                    string text = isMonitoringActive ? "ACTIVE" : "INACTIVE";
                    var color = isMonitoringActive ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Gray;
                    SetMonitoringStateUI(isMonitoringActive, text, color);
                    UpdateDetectorRuntimeState();
                    UpdateRequestLeaveButtonState();
                }
                catch
                {
                }

                await FlushPendingViolationsAsync();
            }
            catch (Exception ex)
            {
                if (_isHandlingFailure)
                    return;

                _isHandlingFailure = true;

                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Unable to connect session: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _isLeaveApproved = true;
                    _ = ForceStopSignalRAsync();
                    new StudentDashboard().Show();
                    Close();
                });
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

            if (_isLeaveRequested)
                return;

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
                            _isLeaveRequested = true;
                            UpdateRequestLeaveButtonState();
                            await _hubConnection.InvokeAsync("RequestLeave", _roomId, studentId);
                        }
                        catch (Exception ex)
                        {
                            _leaveRequestState = LeaveRequestState.Locked;
                            _isLeaveRequested = false;
                            MessageBox.Show($"Failed to request leave: {ex.Message}", "Request Leave", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (!_allowClose && !_isPermanentlyDone && !_isLeaveApproved && _leaveRequestState != LeaveRequestState.Unlocked)
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
    }
}