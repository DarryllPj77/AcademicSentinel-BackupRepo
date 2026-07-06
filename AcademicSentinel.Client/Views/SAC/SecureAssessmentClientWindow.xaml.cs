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
        // Tracks whether the SAC window is currently in the compact (softlock
        // overlay) layout. Set explicitly by SwitchToCompactMode and
        // BtnExpandCompact_Click rather than read from XAML state, because
        // CompactPanel.Visibility lookups via FindName were returning stale
        // values during the MonitoringResumed dispatcher chain — that
        // race caused the Done button to disappear in compact view after a
        // pause/resume cycle until the user manually maximized.
        private bool _isInCompactMode;
        private int _roomId;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _compactCountdownTimer;
        // Heartbeat ping to the server every 3s. The server's
        // DisconnectSweeperService treats absence of heartbeats for
        // >= 10s as a disconnect — that's how force-close / no internet
        // / power loss is detected reliably, without depending on
        // SignalR's transport-level timeout. The 3s cadence (down from 5s)
        // lets the server flag a real disconnect in ~10s instead of
        // ~15-30s, so the instructor's IMC moves the student to the
        // Disconnected tab in near real time.
        private readonly DispatcherTimer _heartbeatTimer;
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

        // LeaveRequestState enum removed per QA overhaul — there is no
        // manual "Request to Leave" path anymore. The Done button is the
        // ONLY exit. _hasSentDone (declared next to BtnDone_Click) carries
        // the "awaiting instructor approval" sub-state.

        private ExamPhase _currentPhase = ExamPhase.PreSession;
        private bool _allowClose;

        // One-shot guard: set when a terminal SignalR disconnect has
        // already routed the student back to the dashboard, so the
        // Closed event (which can fire more than once during teardown)
        // can't pop a second dialog or open a second dashboard window.
        private bool _disconnectRouted;

        // SINGLE terminal-exit guard shared by EVERY path that ends the live
        // exam window (terminal disconnect, ForceDashboardReturn, SessionEnded,
        // SessionEndedForcedExit). The first path to claim it wins; all others
        // bail. This is what makes recovery single-path and deterministic:
        // it prevents (a) a second handler running inside a modal MessageBox's
        // nested message loop and painting a ghost UI / second dialog, and
        // (b) "Reconnect Required" and "SESSION ENDED" rendering at the same
        // time. Must be claimed SYNCHRONOUSLY before any modal/await.
        private bool _terminalHandled;
        private bool _isPermanentlyDone;
        private bool _isLeaveApproved;
        private bool _isHandlingFailure = false;
        private bool _isTransitioningState = false;
        private System.Threading.CancellationTokenSource _stateCts;
        private bool _awaitingJoinApproval;
        private int _pendingParticipantId;
        private bool _isDenied = false; // Bug fix: Bug1

        // Last-known instructor connectivity state for THIS student session.
        // True only after a real TeacherDisconnected was surfaced; the
        // TeacherReconnected banner is shown ONLY when this was true, so a
        // routine instructor re-join (which the server no longer broadcasts
        // for, but might in a duplicate/late case) can't flicker a false
        // "Instructor reconnected" notice.
        private bool _instructorDisconnectActive = false;
        private readonly Queue<MonitoringEventDto> _pendingViolationQueue = new Queue<MonitoringEventDto>();
        private readonly object _joinLiveExamLock = new object();
        private bool _hasJoinedLiveExam;

        /// <summary>
        /// Per-room custom blacklist sent to the BehavioralMonitoringService.
        /// Browsers are intentionally NOT here — students need them for the
        /// LMS, and the service-side <c>_protectedProcesses</c> guard would
        /// strip them anyway. Same goes for <c>snippingtool</c> (covered by
        /// the SNIP_TOOL keyboard hook) and <c>taskmgr</c> (disabled via the
        /// registry at session start). Keeping this list lean makes the
        /// PROCESS_DETECTED feed surface only genuinely unauthorized apps.
        /// </summary>
        private static readonly HashSet<string> ProcessBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Debuggers / reverse-engineering
            "windbg", "x64dbg", "ollydbg", "ida", "dnspy", "fiddler", "cheatengine",
            "processmonitor", "procexp",
            // Remote-access tools
            "teamviewer", "anydesk", "ultravnc", "gotomypc", "vncserver", "rustdesk", "splashtop",
            // Messaging / communication
            "discord", "telegram", "slack", "whatsapp", "skype", "teams", "zoom", "messenger",
            // Screen capture / streaming
            "obs", "obs64", "obs32", "ffmpeg", "camtasia", "snagit", "bandicam",
            "sharex", "screenrec",
            // AI desktop apps (standalone .exe only — chatgpt.ai / claude.ai
            // accessed via a browser tab is not a separate process and stays
            // out of this list).
            "claude",
            // Android emulators (covers BlueStacks even when VAC misses them)
            "bluestacks", "hd-player", "hd-agent", "bstksvc", "bluestacks_bgp",
            "nox", "noxvmhandle", "noxvmhandleagent",
            "memu", "memuheadless",
            "ldplayer", "dnplayer", "ldvbox",
            "genymotion", "genymotion-shell", "vboxheadless", "vboxmanage",
            "mumumvm", "mumuplayer",
            "andy", "droid4x"
        };

        // Now accepts the Room ID from the Waiting Room!
        public SecureAssessmentClientWindow(int roomId, string roomTitle)
        {
            InitializeComponent();

            _isLeaveApproved = false;
            if (_detectorRuntime != null) _detectorRuntime.IsPaused = true;

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

            // Heartbeat to the server. Best-effort: any failure (no hub
            // yet, transient transport error) is swallowed because the
            // sweeper will simply detect the absence on its next tick.
            _heartbeatTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _heartbeatTimer.Tick += async (_, __) =>
            {
                try
                {
                    if (_hubConnection != null
                        && _hubConnection.State == HubConnectionState.Connected
                        && _roomId > 0)
                    {
                        await _hubConnection.InvokeAsync("Heartbeat", _roomId);
                    }
                }
                catch
                {
                    // Swallow — silence IS the disconnect signal.
                }
            };
            _heartbeatTimer.Start();

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
            if (_roomDetectionSettings == null)
                return;

            // Strict singleton enforcement: if a previous _detectorRuntime exists
            // (race between LoadDetectionSettingsAsync success/catch paths, or a
            // re-init triggered by a settings refresh), tear it down completely
            // BEFORE creating a new one. Two live runtimes = two parallel poll
            // loops = every violation logged twice at the same millisecond.
            if (_detectorRuntime != null)
            {
                try { _detectorRuntime.Stop(); } catch { }
                try { _detectorRuntime.Dispose(); } catch { }
                _detectorRuntime = null;
                _detectorsRunning = false;
                _detectorsInitialized = false;
            }

            if (_detectorsInitialized)
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
                StrictMode = _roomDetectionSettings.StrictMode,
                // REQUIRED — pass the LMS exam URL through so BehavioralMonitoringService
                // can anchor focus detection to the matching browser window.
                LmsExamUrl = _roomDetectionSettings.LmsExamUrl ?? string.Empty,
                AllowedAppsCsv = _roomDetectionSettings.AllowedAppsCsv ?? string.Empty,
                BlacklistedProcessNames = new HashSet<string>(ProcessBlacklist, StringComparer.OrdinalIgnoreCase),
                OnHardwareStateDetected = async (isVm, isRemote, monitorCount) =>
                {
                    try
                    {
                        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
                            return;

                        int studentId = SessionManager.CurrentUser?.Id ?? 0;
                        if (studentId <= 0)
                            return;

                        await _hubConnection.InvokeAsync("UpdateHardwareState", _roomId, studentId, isVm, isRemote, monitorCount);
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

            // If SignalR has already locked us into an Active session before settings
            // finished loading, do NOT default-pause the runtime — that re-locks a
            // late joiner whose state machine is already Active and silences detectors.
            _detectorRuntime.IsPaused = !(_isMonitoringActive && !_sessionEnded && !_monitoringCountdownEndsAt.HasValue);

            var enabledModules = new List<string>();
            if (_roomDetectionSettings.EnableFocusDetection) enabledModules.Add("Focus");
            if (_roomDetectionSettings.EnableClipboardMonitoring) enabledModules.Add("Clipboard");
            if (_roomDetectionSettings.EnableProcessDetection) enabledModules.Add("Process");
            if (_roomDetectionSettings.EnableIdleDetection) enabledModules.Add("Idle");

            string modeSuffix = _roomDetectionSettings.StrictMode ? " | Strict Mode: ON" : "";
            DetectionReports.Add($"Detector Setup: {(enabledModules.Count == 0 ? "No modules enabled" : string.Join(", ", enabledModules))}{modeSuffix}");
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

        // DispatcherTimer used by the green "Instructor reconnected" banner
        // to auto-hide itself a few seconds after appearing — single
        // shared instance so a quick disconnect/reconnect storm doesn't
        // leak overlapping timers.
        private System.Windows.Threading.DispatcherTimer _bannerAutoHideTimer;

        /// <summary>
        /// Toggles the yellow connection-lost banner that sits above the
        /// main content area. The banner element lives in the XAML
        /// (TeacherDisconnectedBanner); this helper centralizes the
        /// visibility / message writes so the hub event handlers stay terse.
        /// </summary>
        private void ShowTeacherDisconnectedBanner(string message)
        {
            CancelBannerAutoHide();
            if (FindName("TeacherDisconnectedBanner") is System.Windows.Controls.Border banner)
            {
                banner.Visibility = Visibility.Visible;
                // Yellow warning palette.
                banner.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xE1));
                banner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));
            }
            if (FindName("TxtTeacherDisconnectedBanner") is System.Windows.Controls.TextBlock txt)
            {
                txt.Text = message;
                txt.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00));
            }
        }

        /// <summary>
        /// Positive reconnect banner — green palette, auto-hides after
        /// 4 seconds so the student gets a clear confirmation but the UI
        /// returns to its clean state shortly after.
        /// </summary>
        private void ShowTeacherReconnectedBanner(string message)
        {
            CancelBannerAutoHide();
            if (FindName("TeacherDisconnectedBanner") is System.Windows.Controls.Border banner)
            {
                banner.Visibility = Visibility.Visible;
                // Green success palette — same banner element, repainted.
                banner.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));
                banner.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
            }
            if (FindName("TxtTeacherDisconnectedBanner") is System.Windows.Controls.TextBlock txt)
            {
                txt.Text = message;
                txt.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20));
            }

            _bannerAutoHideTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(4)
            };
            _bannerAutoHideTimer.Tick += (_, __) =>
            {
                CancelBannerAutoHide();
                HideTeacherDisconnectedBanner();
            };
            _bannerAutoHideTimer.Start();
        }

        private void CancelBannerAutoHide()
        {
            if (_bannerAutoHideTimer != null)
            {
                _bannerAutoHideTimer.Stop();
                _bannerAutoHideTimer = null;
            }
        }

        private void HideTeacherDisconnectedBanner()
        {
            CancelBannerAutoHide();
            if (FindName("TeacherDisconnectedBanner") is FrameworkElement banner)
                banner.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Centralized "this client is offline" UI transition.
        ///
        /// Called from two places that previously diverged:
        ///   (1) Button guards (BtnDone_Click, BtnRaiseHand_Click) when the
        ///       student clicks an action while the hub is disconnected.
        ///       The old code showed an isolated MessageBox ("Not connected
        ///       to server.") and left the softlock UI claiming
        ///       "Monitoring: ACTIVE", trapping the student with no
        ///       indication of what to do next.
        ///   (2) The SignalR <c>Closed</c> handler — fires proactively the
        ///       moment the WS transport drops, so the UI flips even
        ///       before the student tries to click anything.
        ///
        /// Both entry points must produce the SAME safe-state UI:
        ///   • Yellow disconnect banner up with reconnect copy
        ///   • Main + compact monitoring status repainted to
        ///     "Monitoring: DISCONNECTED" in red so the student can see
        ///     the prior "ACTIVE" reading is no longer accurate
        ///   • Monitor dot recoloured red
        /// WithAutomaticReconnect() will continue to attempt reconnection
        /// in the background; on success, the <c>Reconnected</c> handler
        /// already calls <see cref="HideTeacherDisconnectedBanner"/> and
        /// SetMonitoringActive/SetMonitoringStateUI restores accurate
        /// status text. This helper deliberately does NOT teardown the
        /// detector — the exam softlock stays armed so a transient drop
        /// can't be used as a cheating vector.
        ///
        /// Safe to call repeatedly; banner / status writes are idempotent
        /// and the helper marshals to the dispatcher thread itself, so
        /// hub callbacks running on background threads can invoke it
        /// without explicit Dispatcher.Invoke at the call site.
        /// </summary>
        private void ShowOwnDisconnectOverlay()
        {
            void apply()
            {
                ShowTeacherDisconnectedBanner(
                    "You are offline. Attempting to reconnect — please check your internet.");

                var redBrush = new SolidColorBrush(Color.FromRgb(198, 40, 40));

                if (TxtMonitoringStatus != null)
                {
                    TxtMonitoringStatus.Text = "Monitoring: DISCONNECTED — please reconnect";
                    TxtMonitoringStatus.Foreground = redBrush;
                }

                if (FindName("TxtCompactMonitoringStatus") is TextBlock compactStatus)
                {
                    compactStatus.Text = "Monitoring: DISCONNECTED";
                    compactStatus.Foreground = redBrush;
                }

                if (FindName("TxtHeaderMonitoringStatus") is TextBlock headerStatus)
                {
                    headerStatus.Text = "DISCONNECTED";
                    headerStatus.Foreground = redBrush;
                }

                // Repaint the status dot red so the visual indicator
                // matches the textual state. MonitorDotBrush is an
                // x:Name'd SolidColorBrush in the XAML so we set Color
                // directly rather than swapping the brush reference.
                if (MonitorDotBrush != null)
                    MonitorDotBrush.Color = Color.FromRgb(211, 47, 47);

                // Hard-disable the two SAC action buttons. While the hub
                // is offline these clicks would just bounce off the
                // "not Connected" guards anyway, but leaving the buttons
                // visually enabled tricks the user into thinking the app
                // is responsive — which is exactly what made the bug
                // report describe the UI as "stuck on the last state".
                // The Reconnected handler restores IsEnabled.
                if (FindName("BtnDone") is System.Windows.Controls.Button btnDone)
                    btnDone.IsEnabled = false;
                if (FindName("BtnRaiseHand") is System.Windows.Controls.Button btnRaise)
                    btnRaise.IsEnabled = false;
            }

            if (Dispatcher.CheckAccess())
                apply();
            else
                Dispatcher.Invoke(apply);
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

                // Re-enforce softlock state machine — never leave the button
                // unconditionally enabled, since that defeats Pending/Countdown locks.
                UpdateRequestLeaveButtonState();
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
            {
                // Already compact — keep the tracked flag in sync so that
                // UpdateUIForPhase's Done-visibility branch never reads a
                // stale `false` and hides the button after resume.
                _isInCompactMode = true;
                return;
            }

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
                // Bug fix: Bug4 - block violation reports while awaiting instructor join approval
                if (_awaitingJoinApproval)
                    return;

                // Suppress violations while the student is waiting for the
                // instructor's Done-approval decision. The student has
                // declared themselves finished; any further focus / clipboard
                // / process activity until the instructor approves or denies
                // should NOT score against them. If the instructor denies,
                // _hasSentDone flips back to false (LeaveRequestDenied
                // handler) and emissions resume automatically.
                if (_hasSentDone)
                    return;

                if (!_detectorRuntime?.IsLoggingEnabled ?? true)
                    return;

                if (!_detectorsRunning || !_isMonitoringActive || _sessionEnded || _monitoringCountdownEndsAt.HasValue)
                    return;

                var now = DateTime.UtcNow;

                // 2-second per-event-type cooldown removed deliberately.
                // The runtime + BehavioralMonitoringService.AddEvent already
                // own dedup at the source; suppressing again here was
                // dropping legitimate rapid pastes (Ctrl+V pressed multiple
                // times within 2s).  Every event the detectors emit is now
                // forwarded to the server immediately.

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

                // CONNECTION-LOSS FAIRNESS GUARD.
                // If the hub is not Connected the student is effectively
                // offline. A behaviour detected in this window cannot be
                // fairly attributed (they may have lost internet through no
                // fault of their own) and — critically — must NEVER be
                // replayed to the server on reconnect. Buffer-and-replay
                // was the direct cause of the "queued violations bombard
                // the instructor's feed when the line comes back" bug.
                // Discard the event outright and tell the student why.
                if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
                {
                    DetectionReports.Insert(0,
                        $"Not recorded — you appear to be offline: {eventType} ({DateTime.Now:h:mm:ss tt})");
                    return;
                }

                int studentId = SessionManager.CurrentUser?.Id ?? 0;
                if (studentId <= 0)
                    return;

                await _hubConnection.InvokeAsync("SendMonitoringEvent", _roomId, studentId, payload);

                // Detection-report wording is driven by SeverityScore so
                // informational events (zero-severity) — most notably
                // CANVAS_RETURNED, which fires when the student tabs
                // back to the LMS — are not surfaced as "Violation
                // sent". The IMC already classifies these zero-severity
                // events as the green RETURN badge; this branch keeps
                // the student-facing log honest about the same data.
                string logText;
                bool isInformational = severityScore == 0;
                if (isInformational)
                {
                    if (string.Equals(eventType, "CANVAS_RETURNED", StringComparison.OrdinalIgnoreCase))
                    {
                        logText = string.IsNullOrWhiteSpace(description)
                            ? $"Return detected: focus returned to the LMS exam ({DateTime.Now:h:mm:ss tt})"
                            : $"Return detected: {description} ({DateTime.Now:h:mm:ss tt})";
                    }
                    else if (string.Equals(eventType, "ALLOWED_APP", StringComparison.OrdinalIgnoreCase))
                    {
                        // Description carries just the friendly app
                        // name (e.g. "Microsoft Teams"). The student
                        // softlock log uses an informational format
                        // — no "Violation sent:" prefix — because the
                        // instructor explicitly allowed this app for
                        // the session. The IMC global feed has its
                        // own formatter and is intentionally NOT
                        // changed by this branch.
                        string appLabel = string.IsNullOrWhiteSpace(description)
                            ? "instructor-allowed app"
                            : description;
                        logText = $"Switch detected : ALLOWED_APP | {appLabel} ({DateTime.Now:h:mm:ss tt})";
                    }
                    else
                    {
                        // Fallback for any other zero-severity event so
                        // a future informational type added on the
                        // server side does not regress to "Violation
                        // sent" prefix until this method is revisited.
                        logText = string.IsNullOrWhiteSpace(description)
                            ? $"Info: {eventType} ({DateTime.Now:h:mm:ss tt})"
                            : $"Info: {eventType} | {description} ({DateTime.Now:h:mm:ss tt})";
                    }
                }
                else
                {
                    logText = string.IsNullOrWhiteSpace(description)
                        ? $"Violation sent: {eventType} ({DateTime.Now:h:mm:ss tt})"
                        : $"Violation sent: {eventType} | {description} ({DateTime.Now:h:mm:ss tt})";
                }

                DetectionReports.Insert(0, logText);
            }
            catch
            {
                // keep SAC session stable even if a send fails
            }
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

        // Drop every violation buffered while the hub was disconnected.
        // Behaviour detected while the student was offline must never be
        // replayed to the server on reconnect — doing so unfairly scored
        // the student and flooded the instructor's live feed. Invoked on
        // reconnect, on initial join, and on terminal disconnect so the
        // queue can never leak an offline-window detection.
        private void DiscardPendingViolations()
        {
            _pendingViolationQueue.Clear();
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

                // Server returns 403 when the exam is already completed
                // (LEAVE_GRANTED earlier) — surface a clean "Exam already
                // completed" dialog instead of the generic Join Error box.
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    && !string.IsNullOrWhiteSpace(errorMsg)
                    && errorMsg.IndexOf("already completed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    MessageBox.Show(
                        "You have already completed this exam.\nYou cannot rejoin this session.",
                        "Exam Already Completed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(errorMsg, "Join Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
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

            // Bug 1 — fire JoinLiveExam at most once per connection lifecycle.
            // Without this gate, StartLiveExamAsync, the Reconnected handler, and the
            // Closed-then-restart handler each independently call JoinLiveExam, and
            // the server logs "✅ SESSION JOINED / CONNECTION RESTORED" once per call.
            bool shouldJoin;
            lock (_joinLiveExamLock)
            {
                shouldJoin = !_hasJoinedLiveExam;
                _hasJoinedLiveExam = true;
            }

            if (shouldJoin)
                await _hubConnection.InvokeAsync("JoinLiveExam", _roomId);

            // Never replay anything detected before this join handshake —
            // it would have been buffered during an offline window.
            DiscardPendingViolations();

            var monitoringState = await _hubConnection.InvokeAsync<bool>("GetMonitoringState", _roomId);

            await Dispatcher.InvokeAsync(() =>
            {
                if (monitoringState)
                {
                    // Late/rejoiner inherits an already-active session.
                    // Wake the hardware scanner BEFORE the state machine flips so the
                    // first UpdateDetectorRuntimeState pass sees an unpaused runtime.
                    if (_detectorRuntime != null)
                        _detectorRuntime.IsPaused = false;

                    SetMonitoringActive(true);
                    _currentPhase = ExamPhase.Active;

                    // Reset Done state so a rejoining student can press Done
                    // again on this fresh hub connection. Without this, a
                    // student who was kicked-then-rejoined or whose hub
                    // reconnected after a network blip would be stuck with a
                    // disabled "Done — awaiting instructor" button.
                    _hasSentDone = false;

                    _monitoringCountdownEndsAt = null;

                    UpdateRequestLeaveButtonState();

                    DetectionReports.Insert(0, $"System: Joined active session - monitoring active. ({DateTime.Now:h:mm:ss tt})");
                }
                else
                {
                    SetMonitoringActive(false);
                    UpdateRequestLeaveButtonState();
                }
            });
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

                // IMMEDIATE DASHBOARD RETURN ON ANY DROP (product decision).
                //
                // Reconnecting fires the instant WithAutomaticReconnect detects
                // the line is down. Rather than keep the student staring at the
                // softlock with an "Attempting to reconnect" overlay (and then
                // bouncing them through a separate "Reconnect Required" dialog
                // when the server's rejoin gate rejects the silent re-admit),
                // we tear the live window down NOW and return to the single
                // Student Dashboard — no popup. The dashboard's
                // "No internet" → "Connection restored" banner and the
                // "Reconnect to In-Progress Session" card own recovery, and the
                // student requests rejoin from there (instructor-approval gate
                // intact). This makes a 20s and a 50s outage produce the EXACT
                // same recovery experience — the only difference is how long
                // "No internet" stays up, which reflects real connectivity.
                _hubConnection.Reconnecting += _ =>
                {
                    if (!_sessionEnded && !_allowClose)
                        Dispatcher.InvokeAsync(HandleTerminalDisconnect);
                    return Task.CompletedTask;
                };

                _hubConnection.Reconnected += async _ =>
                {
                    // We return to the dashboard the moment a drop is detected,
                    // so a late "Reconnected" must NOT silently re-admit the
                    // student in place — recovery is dashboard-driven now.
                    if (_terminalHandled || _sessionEnded || _allowClose) return;
                    // Bug fix: Bug1 - block zombie reconnect after denial
                    if (_isDenied) return;
                    try
                    {
                        if (_awaitingJoinApproval)
                            return;

                        // A real reconnect: open the gate so JoinLiveExam fires once,
                        // and the server records a single "CONNECTION RESTORED" entry.
                        lock (_joinLiveExamLock) { _hasJoinedLiveExam = false; }

                        bool shouldRejoin;
                        lock (_joinLiveExamLock)
                        {
                            shouldRejoin = !_hasJoinedLiveExam;
                            _hasJoinedLiveExam = true;
                        }

                        if (shouldRejoin)
                            await _hubConnection.InvokeAsync("JoinLiveExam", _roomId);

                        var reconnectedStudentId = SessionManager.CurrentUser?.Id ?? 0;
                        if (reconnectedStudentId > 0)
                            await _hubConnection.InvokeAsync("ReSyncState", _roomId, reconnectedStudentId);
                        await Dispatcher.InvokeAsync(DiscardPendingViolations);

                        // Restore the action buttons + drop the
                        // disconnect banner now that we're back online.
                        // UpdateUIForPhase will repaint the monitoring
                        // status text from the canonical phase state, so
                        // we don't have to manually overwrite the red
                        // "DISCONNECTED" label here.
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (FindName("BtnDone") is System.Windows.Controls.Button btnDone)
                                btnDone.IsEnabled = true;
                            if (FindName("BtnRaiseHand") is System.Windows.Controls.Button btnRaise)
                                btnRaise.IsEnabled = true;
                            HideTeacherDisconnectedBanner();
                        });
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
                        UpdateRequestLeaveButtonState();
                    });
                });

                _hubConnection.Closed += _ =>
                {
                    // WithAutomaticReconnect() already handles transient drops and surfaces a
                    // single Reconnected event. Manually calling StartAsync() + JoinLiveExam
                    // here races the auto-reconnect path and produced duplicate
                    // "SESSION JOINED / CONNECTION RESTORED" log entries. Just clear the
                    // join flag so the next genuine reconnect can rejoin once.
                    lock (_joinLiveExamLock) { _hasJoinedLiveExam = false; }

                    // Defensive: never replay anything detected while the
                    // connection was down.
                    Dispatcher.InvokeAsync(DiscardPendingViolations);

                    // Closed is the other drop signal (fires if Reconnecting
                    // didn't already claim the teardown, or on an immediate
                    // non-recoverable close). Route to the dashboard via the
                    // same no-popup path. HandleTerminalDisconnect is one-shot
                    // (guarded by _terminalHandled), so if Reconnecting already
                    // routed, this is a no-op. Skipped when the close was
                    // intentional (session ended, Done/leave set _allowClose).
                    if (!_sessionEnded && !_allowClose)
                        Dispatcher.InvokeAsync(HandleTerminalDisconnect);

                    return Task.CompletedTask;
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

                    Dispatcher.Invoke(() =>
                    {
                        _isMonitoringActive = false;
                        _monitoringCountdownEndsAt = null;
                        _monitoringStartedAt = null;
                        if (!_sessionEnded)
                            _currentPhase = ExamPhase.Active;

                        if (_detectorRuntime != null)
                            _detectorRuntime.IsPaused = true;

                        DetectionReports.Insert(0, $"System: Monitoring paused by instructor ({DateTime.Now:h:mm:ss tt})");

                        SetMonitoringStateUI(false, "PAUSED BY INSTRUCTOR", System.Windows.Media.Brushes.Goldenrod);
                        UpdateDetectorRuntimeState();
                        UpdateRequestLeaveButtonState();
                    });
                });

                // Resume mirrors the proven SessionCountdownStarted shape
                // EXACTLY — same threading, same finalizer calls, same order.
                // The previous implementation wrapped the finalizer in
                // Dispatcher.InvokeAsync(async () => ...) which interacted
                // badly with the compact-mode visibility branch and left the
                // Done button hidden after resume. Using the start-monitoring
                // pattern guarantees every student in the session gets Done
                // back, just like they all get it at session start.
                _hubConnection.On("MonitoringResumed", () =>
                {
                    _stateCts?.Cancel();
                    _stateCts = new System.Threading.CancellationTokenSource();
                    var token = _stateCts.Token;

                    // Initial state set — assigned directly (matches
                    // SessionCountdownStarted; field writes are atomic).
                    _isMonitoringActive = false;
                    _monitoringCountdownEndsAt = DateTime.Now.AddSeconds(10);
                    _currentPhase = ExamPhase.Countdown;
                    _hasSentDone = false; // student can press Done again next cycle

                    if (_detectorRuntime != null)
                        _detectorRuntime.IsPaused = true;

                    UpdateDetectorRuntimeState();
                    UpdateRequestLeaveButtonState();

                    // Inform the student the resume countdown started.
                    Application.Current.Dispatcher.Invoke(() =>
                        DetectionReports.Insert(0, $"System: Monitoring resuming... ({DateTime.Now:h:mm:ss tt})"));

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
                                // Finalizer mirrors SessionCountdownStarted
                                // line-for-line. No Dispatcher.InvokeAsync
                                // wrapper — SetMonitoringStateUI already
                                // marshals to the UI thread internally.
                                _isMonitoringActive = true;
                                _monitoringCountdownEndsAt = null;
                                _monitoringStartedAt ??= DateTime.Now;
                                _currentPhase = ExamPhase.Active;

                                if (_detectorRuntime != null)
                                    _detectorRuntime.IsPaused = false;

                                SetMonitoringStateUI(true, "ACTIVE", System.Windows.Media.Brushes.LimeGreen);
                                UpdateDetectorRuntimeState();
                                UpdateRequestLeaveButtonState();

                                Application.Current.Dispatcher.Invoke(() =>
                                    DetectionReports.Insert(0, $"System: Monitoring resumed. ({DateTime.Now:h:mm:ss tt})"));
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }, token);
                });

                // Per QA decision: instructor approval AUTO-EXITS the SAC.
                // The student no longer needs to click a second button — the
                // window closes itself and returns to the dashboard.
                //
                // Listen for both `LeaveApproved` (new spec name, user's R2)
                // and `LeaveGranted` (legacy) so this works against either
                // server build. Both go through the same handler; a re-entry
                // guard makes it idempotent.
                bool _leaveExitInFlight = false;
                Action<int> handleLeaveApproved = grantedStudentId =>
                {
                    Dispatcher.Invoke(async () =>
                    {
                        int currentStudentId = SessionManager.CurrentUser?.Id ?? 0;
                        if (grantedStudentId != currentStudentId)
                            return;

                        if (_leaveExitInFlight)
                            return;
                        _leaveExitInFlight = true;

                        // Brief acknowledgement so the student sees what happened.
                        TxtMonitoringStatus.Text = "Approved — returning to dashboard...";
                        TxtMonitoringStatus.Foreground = new SolidColorBrush(Color.FromRgb(27, 94, 32));
                        if (FindName("TxtCompactMonitoringStatus") is TextBlock compactStatus)
                        {
                            compactStatus.Text = "Approved — returning to dashboard...";
                            compactStatus.Foreground = new SolidColorBrush(Color.FromRgb(27, 94, 32));
                        }

                        // Tear everything down and exit.
                        if (_detectorRuntime != null) _detectorRuntime.IsPaused = true;
                        _detectorRuntime?.Stop();
                        if (_detectorRuntime != null)
                            await _detectorRuntime.StopMonitoringAsync();

                        // LeaveSessionSafelyAsync handles allowClose, timer
                        // shutdown, hub teardown, and dashboard navigation.
                        await LeaveSessionSafelyAsync(currentStudentId);
                    });
                };

                _hubConnection.On<int>("LeaveApproved", id => handleLeaveApproved(id));
                _hubConnection.On<int>("LeaveGranted",  id => handleLeaveApproved(id));

                // Instructor denied the Done request — restore the button so
                // the student can request again later. Session stays active.
                _hubConnection.On<int>("LeaveRequestDenied", deniedStudentId =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        int currentStudentId = SessionManager.CurrentUser?.Id ?? 0;
                        if (deniedStudentId != currentStudentId)
                            return;

                        _hasSentDone = false;
                        UpdateUIForPhase();

                        DetectionReports.Insert(0,
                            $"System: Instructor denied your Done request — you can request again. ({DateTime.Now:h:mm:ss tt})");
                    });
                });

                _hubConnection.On<int, int>("SessionCountdownStarted", (delay, duration) =>
                {
                    _stateCts?.Cancel();
                    _stateCts = new System.Threading.CancellationTokenSource();
                    var token = _stateCts.Token;

                    _isMonitoringActive = false;
                    _monitoringCountdownEndsAt = DateTime.Now.AddSeconds(Math.Max(0, delay));
                    _currentPhase = ExamPhase.Countdown;
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
                        StudentDashboard.ShowSingleInstance();
                        Close();
                    });
                });

                // BEHAVIOR CHANGE: a teacher-disconnect must NOT force the
                // student back to the dashboard. Monitoring continues, the
                // detector stays armed, and the SAC just keeps the yellow
                // banner up until the instructor reconnects. The server has
                // stopped emitting SessionInterrupted on teacher drop; this
                // handler is kept as a no-op safety net in case any legacy
                // code path or older build still sends it.
                _hubConnection.On<int>("SessionInterrupted", interruptedRoomId =>
                {
                    if (interruptedRoomId != _roomId) return;
                    Dispatcher.Invoke(() => ShowTeacherDisconnectedBanner(
                        "Connection to Instructor Lost. Reconnecting..."));
                });

                // Hub fires TeacherDisconnected the instant the instructor's
                // SignalR connection drops, BEFORE the slower SessionInterrupted
                // teardown. Pop the yellow banner immediately so the student
                // has visible feedback that the network is the problem.
                _hubConnection.On<int>("TeacherDisconnected", droppedRoomId =>
                {
                    if (droppedRoomId != _roomId) return;
                    Dispatcher.Invoke(() =>
                    {
                        // Dedup: ignore a repeated disconnect broadcast while
                        // the banner is already up (state unchanged).
                        if (_instructorDisconnectActive) return;
                        _instructorDisconnectActive = true;
                        System.Diagnostics.Debug.WriteLine("[SAC] TeacherDisconnected — showing instructor-lost banner.");
                        ShowTeacherDisconnectedBanner("Connection to Instructor Lost. Reconnecting...");
                    });
                });

                // Hub fires TeacherReconnected when the instructor's
                // SignalR connection returns and they call JoinRoom on a
                // still-Active room. Flip the banner to a green positive
                // notice that auto-hides after a few seconds.
                _hubConnection.On<int>("TeacherReconnected", reconnectedRoomId =>
                {
                    if (reconnectedRoomId != _roomId) return;
                    Dispatcher.Invoke(() =>
                    {
                        // Defense-in-depth dedup: only announce a reconnect
                        // if THIS student actually saw the instructor drop.
                        // The server now only broadcasts on a real
                        // disconnect, but this guard also absorbs any
                        // duplicate/late broadcast so the banner can't
                        // flicker on routine instructor re-joins.
                        if (!_instructorDisconnectActive)
                        {
                            System.Diagnostics.Debug.WriteLine("[SAC] TeacherReconnected ignored — no prior instructor disconnect.");
                            return;
                        }
                        _instructorDisconnectActive = false;
                        System.Diagnostics.Debug.WriteLine("[SAC] TeacherReconnected — clearing instructor-lost banner.");
                        ShowTeacherReconnectedBanner("Instructor reconnected to the session. Monitoring continues normally.");
                    });
                });

                // Reconnect attempt got rerouted through the approval gate.
                // Reuse the banner element with different copy.
                _hubConnection.On<int>("AwaitingRejoinApproval", pendingRoomId =>
                {
                    if (pendingRoomId != _roomId) return;
                    Dispatcher.Invoke(() => ShowTeacherDisconnectedBanner(
                        "Reconnection request sent. Waiting for instructor approval..."));
                });

                // ============================================================
                // RAISED-HAND APPROVAL EVENTS (server → SAC)
                // ============================================================
                _hubConnection.On<int>("OnHandRaiseApproved", approvedRoomId => Dispatcher.Invoke(() =>
                {
                    if (approvedRoomId != _roomId) return;
                    _handRaiseState = HandRaiseState.Active;
                    if (_detectorRuntime != null)
                        _detectorRuntime.IsHandRaised = true;
                    DetectionReports.Insert(0,
                        $"System: Instructor approved raised hand. Temporary Q&A access ACTIVE — alt-tab / focus events suppressed. ({DateTime.Now:h:mm:ss tt})");
                    UpdateUIForPhase();
                }));

                _hubConnection.On<HandRaiseDeniedPayload>("OnHandRaiseDenied", payload => Dispatcher.Invoke(() =>
                {
                    string reason = string.IsNullOrWhiteSpace(payload?.Reason)
                        ? "Your raised-hand request was denied."
                        : payload!.Reason;

                    _handRaiseState = HandRaiseState.Inactive;
                    if (_detectorRuntime != null)
                        _detectorRuntime.IsHandRaised = false;
                    DetectionReports.Insert(0,
                        $"System: Raised-hand request denied. {reason} ({DateTime.Now:h:mm:ss tt})");
                    UpdateUIForPhase();
                }));

                // Server already-active reply — set state to Active without
                // logging a new approval line (handles double-click idempotency).
                _hubConnection.On<int>("OnHandRaiseAlreadyActive", activeRoomId => Dispatcher.Invoke(() =>
                {
                    if (activeRoomId != _roomId) return;
                    _handRaiseState = HandRaiseState.Active;
                    if (_detectorRuntime != null)
                        _detectorRuntime.IsHandRaised = true;
                    UpdateUIForPhase();
                }));

                // Instructor forced the hand down (or the student's own
                // LowerHand round-trip echoed back via HandLowered).
                _hubConnection.On<int>("OnHandLoweredByInstructor", loweredRoomId => Dispatcher.Invoke(() =>
                {
                    if (loweredRoomId != _roomId) return;
                    _handRaiseState = HandRaiseState.Inactive;
                    if (_detectorRuntime != null)
                        _detectorRuntime.IsHandRaised = false;
                    DetectionReports.Insert(0,
                        $"System: Instructor lowered your raised hand — monitoring resumed. ({DateTime.Now:h:mm:ss tt})");
                    UpdateUIForPhase();
                }));

                // Room-group broadcast for any lowered hand. Use it as
                // a backstop in case OnHandLoweredByInstructor doesn't fire
                // (e.g. self-lower path) so the SAC UI never gets stuck in
                // Active after the suppression has actually been dropped.
                _hubConnection.On<int>("HandLowered", loweredStudentId => Dispatcher.Invoke(() =>
                {
                    var selfId = SessionManager.CurrentUser?.Id ?? 0;
                    if (loweredStudentId != selfId) return;
                    if (_handRaiseState == HandRaiseState.Inactive) return;

                    _handRaiseState = HandRaiseState.Inactive;
                    if (_detectorRuntime != null)
                        _detectorRuntime.IsHandRaised = false;
                    UpdateUIForPhase();
                }));

                // Server side: RoomsController.RemoveStudentFromCurrentSession sends
                //     Clients.User(studentId).SendAsync("RemovedFromSession", roomId)
                // The integer payload is the ROOM id, not the student id. The previous
                // handler compared it against currentStudentId and short-circuited every
                // single time — that's why kicked students kept polling the server.
                _hubConnection.On<int>("RemovedFromSession", removedRoomId =>
                {
                    if (removedRoomId != _roomId)
                        return;

                    // 1. Forcefully kill the hardware detectors on whichever thread
                    //    SignalR delivered this callback on, BEFORE any UI work.
                    //    Stop → Dispose → null out so no further polls reach the server.
                    try { _detectorRuntime?.Stop(); } catch { }
                    try { _detectorRuntime?.Dispose(); } catch { }
                    _detectorRuntime = null;
                    _detectorsRunning = false;
                    _detectorsInitialized = false;

                    // Cut the SignalR connection up-front so Reconnected/Closed
                    // handlers cannot re-resurrect the session during the countdown.
                    _ = ForceStopSignalRAsync();

                    // Run the UI work and the countdown loop on the dispatcher.
                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        // Stop dispatcher timers BEFORE Close() so a queued tick can't
                        // touch a disposed window mid-teardown.
                        _statusTimer?.Stop();
                        _compactCountdownTimer?.Stop();
                        _detectorPollTimer?.Stop();
            _heartbeatTimer?.Stop();
                    _heartbeatTimer?.Stop();
                        _heartbeatTimer?.Stop();

                        // 2. Update the softlock UI to the removal banner.
                        MonitorDotBrush.Color = System.Windows.Media.Color.FromRgb(211, 47, 47);
                        TxtMonitoringStatus.Text = "REMOVED BY THE INSTRUCTOR";
                        TxtMonitoringStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));

                        if (FindName("TxtCompactMonitoringStatus") is TextBlock compactStatus)
                        {
                            compactStatus.Text = "REMOVED BY THE INSTRUCTOR";
                            compactStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                        }
                        if (FindName("TxtHeaderMonitoringStatus") is TextBlock headerStatus)
                        {
                            headerStatus.Text = "REMOVED BY THE INSTRUCTOR";
                            headerStatus.Foreground = new SolidColorBrush(Color.FromRgb(198, 40, 40));
                        }

                        // Hide Done so the student can't fight the countdown.
                        if (BtnDone != null)
                            BtnDone.Visibility = Visibility.Collapsed;

                        DetectionReports.Insert(0, $"System: Removed by instructor. ({DateTime.Now:h:mm:ss tt})");

                        // 3. 5-second countdown. Task.Delay(1000) yields back to the
                        //    dispatcher so the UI text re-renders every tick.
                        for (int remaining = 5; remaining > 0; remaining--)
                        {
                            string banner = $"Going back to dashboard in {remaining}...";
                            TxtMonitoringStatus.Text = banner;

                            if (FindName("TxtCompactMonitoringStatus") is TextBlock compact)
                                compact.Text = banner;
                            if (FindName("TxtHeaderMonitoringStatus") is TextBlock header)
                                header.Text = banner;

                            await Task.Delay(1000);
                        }

                        // 4. Bypass OnClosing's softlock guard and destroy the window.
                        _allowClose = true;
                        _isPermanentlyDone = true;
                        _isLeaveApproved = true;

                        try { StudentDashboard.ShowSingleInstance(); } catch { }
                        Close();
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

                        // Hide the connection-lost banner — the rejoin was
                        // approved, so the student is back in.
                        HideTeacherDisconnectedBanner();

                        await StartLiveExamAsync();
                    });
                });

                // R2 — Join-Denied lockdown with 5-second countdown.
                // Hub event name `OnJoinDenied` is the existing server contract;
                // we also listen for the spec-friendly alias `JoinDenied` so
                // either server build broadcasts correctly.
                Func<JoinDeniedDto, Task> handleJoinDenied = async response =>
                {
                    // Mark denied BEFORE StopAsync so reconnect/closed handlers
                    // short-circuit and don't fight the countdown.
                    _isDenied = true;

                    // Cut the hub up-front so there's no path back into the
                    // session while the dialog ticks.
                    if (_hubConnection != null)
                    {
                        try { await _hubConnection.StopAsync(); } catch { }
                    }

                    // Stop background tasks (detector + dispatcher timers).
                    _detectorRuntime?.Stop();
                    _detectorsRunning = false;
                    _statusTimer?.Stop();
                    _compactCountdownTimer?.Stop();
                    _detectorPollTimer?.Stop();
            _heartbeatTimer?.Stop();
                    _heartbeatTimer?.Stop();

                    await Dispatcher.InvokeAsync(async () =>
                    {
                        _awaitingJoinApproval = false;

                        if (WaitingScreenOverlay != null)
                            WaitingScreenOverlay.Visibility = Visibility.Collapsed;

                        // No exit button — the countdown auto-closes the window.
                        if (BtnDone != null) BtnDone.Visibility = Visibility.Collapsed;

                        var redBrush = new SolidColorBrush(Color.FromRgb(211, 47, 47));

                        // 5-second countdown — re-render every second.
                        for (int remaining = 5; remaining > 0; remaining--)
                        {
                            string banner = $"Instructor denied your request to join. Going back to dashboard in {remaining}...";
                            TxtMonitoringStatus.Text = banner;
                            TxtMonitoringStatus.Foreground = redBrush;
                            TxtMonitoringStatus.Visibility = Visibility.Visible;

                            if (FindName("TxtCompactMonitoringStatus") is TextBlock compact)
                            {
                                compact.Text = banner;
                                compact.Foreground = redBrush;
                                compact.Visibility = Visibility.Visible;
                            }
                            if (FindName("TxtHeaderMonitoringStatus") is TextBlock header)
                            {
                                header.Text = banner;
                                header.Foreground = redBrush;
                            }
                            await Task.Delay(1000);
                        }

                        // Bypass OnClosing's softlock guard and exit cleanly.
                        _allowClose = true;
                        _isPermanentlyDone = true;
                        _isLeaveApproved = true;
                        try { StudentDashboard.ShowSingleInstance(); } catch { }
                        Close();
                    });
                };

                _hubConnection.On<JoinDeniedDto>("OnJoinDenied", async r => await handleJoinDenied(r));
                _hubConnection.On<JoinDeniedDto>("JoinDenied",   async r => await handleJoinDenied(r));

                // R1 — End-Session lockdown with 3-second countdown.
                // Server fires SessionEnded; SAC stops the detector, freezes
                // the UI to the END banner, ticks down 3 → 2 → 1, then
                // closes the window so the dashboard regains focus.
                // Per-user fallback for any case where the SAC is no longer
                // in the room SignalR group (transient drop, mid-rejoin, etc.).
                // The server fires this AFTER the room-group SessionEnded
                // broadcast so the SAC is guaranteed to receive at least one.
                _hubConnection.On<int>("SessionEndedForcedExit", endedRoomId =>
                {
                    if (endedRoomId != _roomId) return;
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (!BeginTerminalReturn("SessionEndedForcedExit", hideWindow: true))
                            return;
                        _isLeaveApproved = true;
                        CompleteTerminalReturn();
                    });
                });

                // Server-driven abort of a silent SignalR auto-reconnect.
                // Fired when JoinLiveExam detects this connection belongs to
                // a participant in the Disconnected state — the server
                // refuses to silently re-admit them, so we tear down the SAC
                // window and surface the dashboard so the student must
                // manually click the course tile and go through the REST
                // /request-join approval flow.
                _hubConnection.On<string>("ForceDashboardReturn", reason =>
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        // Single terminal path. If a SessionEnded (or any other
                        // terminal handler) already claimed, bail — this prevents
                        // the "Reconnect Required" dialog from rendering on top of
                        // a "SESSION ENDED" teardown (Case B). Hide first so no
                        // softlock UI shows behind the dialog.
                        if (!BeginTerminalReturn("ForceDashboardReturn", hideWindow: true))
                            return;

                        _isDenied = true;
                        _awaitingJoinApproval = false;

                        // No popup — consistent with the disconnect→dashboard
                        // flow. The dashboard banner + "Reconnect to In-Progress
                        // Session" card explain the state and drive rejoin.
                        System.Diagnostics.Debug.WriteLine("[SAC] ForceDashboardReturn — returning to dashboard (no popup).");
                        CompleteTerminalReturn();
                    });
                });

                _hubConnection.On("SessionEnded", () =>
                {
                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        // Graceful end. Claim the single terminal guard FIRST
                        // (window stays visible for the 3-2-1 countdown). If a
                        // disconnect/force-return path already claimed, bail so
                        // we don't paint "SESSION ENDED" under its dialog.
                        if (!BeginTerminalReturn("SessionEnded", hideWindow: false))
                            return;

                        // Stop the hardware detector immediately — no further
                        // polls or violations after the session ends.
                        if (_detectorRuntime != null) _detectorRuntime.IsPaused = true;
                        _detectorRuntime?.Stop();
                        _detectorsRunning = false;

                        // Drop the "Connection to Instructor Lost" banner if
                        // it was still up — the session is over either way,
                        // so leaving the reconnecting message visible
                        // alongside the session-ended countdown was confusing.
                        HideTeacherDisconnectedBanner();

                        _sessionEnded = true;
                        _monitoringCountdownEndsAt = null;
                        _monitoringStartedAt = null;
                        _timerEnabled = false;
                        SetMonitoringActive(false);

                        // Hide Done immediately — the countdown is the only
                        // exit path now, no manual action.
                        if (BtnDone != null) BtnDone.Visibility = Visibility.Collapsed;

                        // Stop the hub up-front so reconnect/closed handlers
                        // can't fight the countdown.
                        _ = ForceStopSignalRAsync();

                        // Stop the WPF dispatcher timers before Close()
                        // so a queued tick can't touch a disposed visual tree.
                        _statusTimer?.Stop();
                        _compactCountdownTimer?.Stop();
                        _detectorPollTimer?.Stop();
            _heartbeatTimer?.Stop();
                    _heartbeatTimer?.Stop();
                        _heartbeatTimer?.Stop();

                        var greyBrush = new SolidColorBrush(Color.FromRgb(97, 97, 97));

                        // 3-second countdown — re-render every second.
                        for (int remaining = 3; remaining > 0; remaining--)
                        {
                            string banner = $"Session Ended. Returning to dashboard in {remaining}...";
                            TxtMonitoringStatus.Text = banner;
                            TxtMonitoringStatus.Foreground = greyBrush;

                            if (FindName("TxtCompactMonitoringStatus") is TextBlock compact)
                            {
                                compact.Text = banner;
                                compact.Foreground = greyBrush;
                            }
                            if (FindName("TxtHeaderMonitoringStatus") is TextBlock header)
                            {
                                header.Text = banner;
                                header.Foreground = greyBrush;
                            }
                            await Task.Delay(1000);
                        }

                        // Bypass OnClosing's softlock guard and exit cleanly.
                        _isPermanentlyDone = true;
                        _isLeaveApproved = true;
                        CompleteTerminalReturn();
                    });
                });

                await _hubConnection.StartAsync();

                if (await RequestJoinGateAsync())
                {
                    await StartLiveExamAsync();
                    UpdateDetectorRuntimeState();
                    UpdateRequestLeaveButtonState();
                }
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
                    StudentDashboard.ShowSingleInstance();
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

        // Spec v4/v5 (final QA decision) — Soft Lock "Done" workflow.
        //
        //   1. Student clicks Done.
        //   2. SAC sends RequestSessionCompletion to the hub; instructor sees
        //      the student in "Completed Assessment" state via DONE feed entry.
        //   3. SAC locks Done to "awaiting instructor" and waits.
        //   4. Instructor reviews and approves (existing GrantLeave flow).
        //   5. Server fires LeaveGranted; SAC AUTO-EXITS to StudentDashboard.
        //      The student does NOT need to click anything else.
        //
        // There is no "Request to Leave" button anymore — Done is the only
        // way out of an active session.
        private bool _hasSentDone;

        // Raised-hand state machine — drives BtnRaiseHand label / colour
        // and SacDetectorRuntime.IsHandRaised. Inactive → Raise Hand
        // button enabled. Pending → button reads "Waiting…" + disabled.
        // Active → button label flips to "Lower Hand" + green/orange.
        private enum HandRaiseState { Inactive, Pending, Active }
        private HandRaiseState _handRaiseState = HandRaiseState.Inactive;

        private async void BtnDone_Click(object sender, RoutedEventArgs e)
        {
            int studentId = SessionManager.CurrentUser?.Id ?? 0;
            if (studentId <= 0)
                return;

            if (_hasSentDone)
                return;

            // Mutual-exclusion guard: refuse Done while a Raise Hand
            // request is in any non-Inactive state — Pending
            // (awaiting instructor decision) OR Active (Q&A in
            // progress, hand still up). UpdateUIForPhase greys the
            // button out for both, but this is defence in depth
            // against a fast double-click that fires before
            // IsEnabled propagates through the WPF dispatcher.
            if (_handRaiseState != HandRaiseState.Inactive)
                return;

            if (_sessionEnded || _currentPhase != ExamPhase.Active)
            {
                MessageBox.Show(
                    "The Done button is only available once the session is active.",
                    "Session Not Active", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
                {
                    // Replaced the legacy MessageBox "Not connected to
                    // server." popup with the centralized disconnect
                    // overlay so the softlock UI no longer keeps claiming
                    // "Monitoring: ACTIVE" while the student is actually
                    // offline. See ShowOwnDisconnectOverlay() for the full
                    // rationale and the list of UI elements it repaints.
                    ShowOwnDisconnectOverlay();
                    return;
                }

                _hasSentDone = true;
                UpdateRequestLeaveButtonState();

                await _hubConnection.InvokeAsync("StudentFinishedExam", _roomId, studentId);

                DetectionReports.Insert(0,
                    $"System: You marked the assessment as Done. Waiting for instructor approval... ({DateTime.Now:h:mm:ss tt})");
            }
            catch (Exception ex)
            {
                _hasSentDone = false;
                UpdateRequestLeaveButtonState();

                MessageBox.Show($"Could not send Done signal: {ex.Message}", "Done",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Raise Hand / Lower Hand toggle. Behaviour depends on
        // _handRaiseState:
        //   Inactive → ask the server to raise our hand.
        //   Pending  → no-op (still awaiting instructor decision).
        //   Active   → ask the server to lower our hand (resume monitoring).
        // The actual state transition is driven by the server's
        // OnHandRaiseApproved / OnHandRaiseDenied / HandLowered events,
        // never optimistically here — so a denied raise doesn't briefly
        // grant suppression locally.
        private async void BtnRaiseHand_Click(object sender, RoutedEventArgs e)
        {
            int studentId = SessionManager.CurrentUser?.Id ?? 0;
            if (studentId <= 0) return;
            if (_sessionEnded || _currentPhase != ExamPhase.Active) return;
            if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
            {
                // Match BtnDone_Click — surface the centralized
                // disconnect overlay instead of silently swallowing the
                // click. Previously a tap on Raise Hand while offline
                // did absolutely nothing, leaving the student with no
                // feedback that the network was the reason.
                ShowOwnDisconnectOverlay();
                return;
            }

            // Mutual-exclusion guard: refuse Raise Hand while a Done
            // request is already pending. Lower Hand (the Active
            // branch below) is still allowed because the student
            // needs a way OUT of Q&A mode even mid-Done; only the
            // Inactive→Pending raise transition is gated.
            if (_hasSentDone && _handRaiseState == HandRaiseState.Inactive)
                return;

            try
            {
                if (_handRaiseState == HandRaiseState.Active)
                {
                    // Lower hand — drop suppression immediately on the
                    // SAC side too so monitoring resumes the instant the
                    // user clicks, before the server round-trip.
                    if (_detectorRuntime != null)
                        _detectorRuntime.IsHandRaised = false;
                    _handRaiseState = HandRaiseState.Inactive;
                    UpdateUIForPhase();

                    await _hubConnection.InvokeAsync("LowerHand", _roomId);
                    DetectionReports.Insert(0,
                        $"System: Lowered hand — monitoring resumed. ({DateTime.Now:h:mm:ss tt})");
                    return;
                }

                if (_handRaiseState == HandRaiseState.Pending)
                    return;

                _handRaiseState = HandRaiseState.Pending;
                UpdateUIForPhase();

                await _hubConnection.InvokeAsync("RaiseHand", _roomId);
                DetectionReports.Insert(0,
                    $"System: Raised hand — waiting for instructor approval... ({DateTime.Now:h:mm:ss tt})");
            }
            catch (Exception ex)
            {
                // Rollback to Inactive so the student can retry.
                _handRaiseState = HandRaiseState.Inactive;
                UpdateUIForPhase();
                MessageBox.Show($"Could not send raise-hand request: {ex.Message}", "Raise Hand",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // BtnRequestLeave_Click removed entirely per QA overhaul. The Leave
        // button itself is gone from the XAML; Done is the only exit.

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
            _heartbeatTimer?.Stop();

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

        // STRICT 3-state machine per QA overhaul. Driven SOLELY by ExamPhase
        // and _isMonitoringActive (which together produce Pre-Exam / Active /
        // Paused). The Done button is the ONLY UI control governed here.
        //
        //   STATE 1  Pre-Exam  : "Monitoring: Waiting"   | "Leave Permission: Blocked" | Done HIDDEN
        //   STATE 2  Active    : "Monitoring: ACTIVE"    | "Leave Permission: Blocked" | Done VISIBLE
        //   STATE 3  Paused    : "PAUSED BY INSTRUCTOR"  | "Leave Permission: Blocked" | Done HIDDEN
        //
        // The "Allowed" permission text was removed entirely — permission is
        // ALWAYS "Blocked" now, even in the waiting room. Window resize
        // (maximize / minimize / compact) NEVER touches button visibility;
        // those events are handled separately and must call this method
        // afterward to re-assert the state-driven view.
        private void UpdateUIForPhase()
        {
            Dispatcher.Invoke(() =>
            {
                if (BtnDone == null)
                    return;

                // Default — hide Done. Visible below ONLY in Active state.
                BtnDone.Visibility = Visibility.Collapsed;
                // Same default for Raise Hand — it's a softlock-overlay
                // affordance that only makes sense while the session is
                // Active and the window is in compact mode.
                if (BtnRaiseHand != null)
                    BtnRaiseHand.Visibility = Visibility.Collapsed;

                // Permission text is always "Blocked" — no more Allowed state.
                string permissionText = "Leave Permission: Blocked";
                var permissionColor = new SolidColorBrush(Color.FromRgb(198, 40, 40));

                if (_sessionEnded)
                {
                    SetStatusUI("SESSION ENDED",
                        new SolidColorBrush(Color.FromRgb(97, 97, 97)),
                        permissionText, permissionColor);
                    return;
                }

                bool isPausedByInstructor =
                    _currentPhase == ExamPhase.Active && !_isMonitoringActive;

                if (isPausedByInstructor)
                {
                    // STATE 3 — Paused. All buttons hidden, status text shows pause.
                    SetStatusUI("PAUSED BY INSTRUCTOR",
                        new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                        permissionText, permissionColor);
                    return;
                }

                if (_currentPhase == ExamPhase.PreSession)
                {
                    // STATE 1 — Pre-Exam. Done HIDDEN, status "Monitoring: Waiting".
                    SetStatusUI("Monitoring: Waiting",
                        new SolidColorBrush(Color.FromRgb(46, 125, 50)),
                        permissionText, permissionColor);
                    return;
                }

                if (_currentPhase == ExamPhase.Countdown)
                {
                    // Countdown is a transient phase between Pre-Exam and Active.
                    // Keep Done hidden until monitoring is fully Active.
                    SetStatusUI("Starting in a moment...",
                        new SolidColorBrush(Color.FromRgb(255, 152, 0)),
                        permissionText, permissionColor);
                    return;
                }

                if (_currentPhase == ExamPhase.Active)
                {
                    // STATE 2 — Active.
                    // Per QA decision: Done is the softlock-overlay's exit
                    // affordance and lives ONLY in the compact (minimized)
                    // view. The maximized full window is for context (status,
                    // detection reports) and intentionally has NO exit
                    // button — students must shrink the window down to the
                    // softlock overlay before clicking Done.
                    SetStatusUI("Monitoring: ACTIVE",
                        new SolidColorBrush(Color.FromRgb(198, 40, 40)),
                        permissionText, permissionColor);

                    // Compact-mode detection: trust whichever signal says
                    // "we are compact", because both can drift —
                    //   * _isInCompactMode is missed by code paths that
                    //     bypass SwitchToCompactMode (e.g. OnDeactivated's
                    //     early-return when CompactPanel was already shown).
                    //   * CompactPanel.Visibility lookups via FindName have
                    //     historically been stale during the MonitoringResumed
                    //     dispatcher chain on Render-hosted sessions.
                    // OR-ing both eliminates the regression in either path
                    // and resyncs the tracked flag for the next call.
                    bool compactPanelVisible =
                        FindName("CompactPanel") is FrameworkElement cp
                        && cp.Visibility == Visibility.Visible;
                    bool isCompactMode = _isInCompactMode || compactPanelVisible;
                    _isInCompactMode = isCompactMode;

                    if (!isCompactMode)
                    {
                        // Full / maximized view — Done strictly HIDDEN.
                        BtnDone.Visibility = Visibility.Collapsed;
                        return;
                    }

                    // Compact view — Done visible.
                    BtnDone.Visibility = Visibility.Visible;
                    if (_hasSentDone)
                    {
                        BtnDone.Content = "Waiting for instructor approval...";
                        BtnDone.IsEnabled = false;
                        BtnDone.Background = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                        BtnDone.Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString("#424242"));
                    }
                    else
                    {
                        BtnDone.Content = "Done";
                        BtnDone.IsEnabled = true;
                        BtnDone.Background = new SolidColorBrush(Color.FromRgb(27, 94, 32));
                        BtnDone.Foreground = Brushes.White;
                    }

                    // Mutual-exclusion overlay: while a Raise Hand is
                    // in flight — either Pending (instructor hasn't
                    // decided yet) OR Active (Q&A approved, hand is
                    // currently up) — force-disable Done. Done
                    // re-enables automatically once _handRaiseState
                    // drops back to Inactive (denied / lowered). This
                    // makes _hasSentDone and _handRaiseState behave
                    // as a single combined state machine for IsEnabled
                    // without changing either flag's transition rules.
                    if (!_hasSentDone && _handRaiseState != HandRaiseState.Inactive)
                    {
                        BtnDone.IsEnabled  = false;
                        BtnDone.Background = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                        BtnDone.Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString("#424242"));
                    }

                    // Raise / Lower Hand button — only meaningful in the
                    // softlock-overlay (compact) view, alongside Done.
                    if (BtnRaiseHand != null)
                    {
                        BtnRaiseHand.Visibility = Visibility.Visible;
                        switch (_handRaiseState)
                        {
                            case HandRaiseState.Pending:
                                BtnRaiseHand.Content   = "Waiting for approval...";
                                BtnRaiseHand.IsEnabled = false;
                                BtnRaiseHand.Background = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                                BtnRaiseHand.Foreground = new SolidColorBrush(
                                    (Color)ColorConverter.ConvertFromString("#424242"));
                                break;
                            case HandRaiseState.Active:
                                BtnRaiseHand.Content   = "Lower Hand (Q&A Active)";
                                BtnRaiseHand.IsEnabled = true;
                                // Orange — same colour family the IMC uses
                                // for "waiting" / attention states, so the
                                // student is reminded the exception is on.
                                BtnRaiseHand.Background = new SolidColorBrush(Color.FromRgb(230, 81, 0));
                                BtnRaiseHand.Foreground = Brushes.White;
                                break;
                            default:
                                BtnRaiseHand.Content   = "Raise Hand";
                                BtnRaiseHand.IsEnabled = true;
                                BtnRaiseHand.Background = new SolidColorBrush(Color.FromRgb(21, 101, 192));
                                BtnRaiseHand.Foreground = Brushes.White;
                                break;
                        }

                        // Mutual-exclusion overlay: if Done is in flight,
                        // force-disable Raise Hand too (only meaningful
                        // when raise-hand itself is Inactive — Pending
                        // and Active already disable / specially style
                        // the button via the switch above). Raise Hand
                        // re-enables automatically when _hasSentDone is
                        // cleared (Done denied / reset).
                        if (_hasSentDone && _handRaiseState == HandRaiseState.Inactive)
                        {
                            BtnRaiseHand.IsEnabled  = false;
                            BtnRaiseHand.Background = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                            BtnRaiseHand.Foreground = new SolidColorBrush(
                                (Color)ColorConverter.ConvertFromString("#424242"));
                        }
                    }
                }
            });
        }

        // Centralised status-text writer used only by UpdateUIForPhase. Keeps
        // every textblock the state machine touches in one place so a window
        // resize cannot accidentally desync them.
        private void SetStatusUI(string monitoringText, Brush monitoringBrush,
                                 string permissionText, Brush permissionBrush)
        {
            if (TxtMonitoringStatus != null)
            {
                TxtMonitoringStatus.Text = monitoringText;
                TxtMonitoringStatus.Foreground = monitoringBrush;
            }

            if (FindName("TxtCompactMonitoringStatus") is TextBlock compactStatus)
            {
                compactStatus.Text = monitoringText;
                compactStatus.Foreground = monitoringBrush;
            }
            if (FindName("TxtHeaderMonitoringStatus") is TextBlock headerStatus)
            {
                headerStatus.Text = monitoringText;
                headerStatus.Foreground = monitoringBrush;
            }

            if (FindName("TxtCompactLeavePermission") is TextBlock leavePerm)
            {
                leavePerm.Text = permissionText;
                leavePerm.Foreground = permissionBrush;
            }
        }

        // Backward-compat shim — old call sites still reference the previous
        // method name. Forward to the new state-driven update.
        private void UpdateRequestLeaveButtonState() => UpdateUIForPhase();

        private void ReturnToStudentDashboard()
        {
            // Single-window recovery: reuse the open StudentDashboard if any,
            // otherwise create one. Prevents duplicate dashboards when
            // multiple disconnect paths (Closed event, JoinFailed,
            // ForceDashboardReturn, SessionEnded) race against each other.
            try
            {
                System.Diagnostics.Debug.WriteLine("[SAC recovery] Navigating to single StudentDashboard.");
                StudentDashboard.ShowSingleInstance();
            }
            catch
            {
                // no-op
            }

            this.Close();
        }

        // ============================================================
        // SINGLE-PATH TERMINAL TEARDOWN
        // ============================================================
        // Every terminal exit (terminal disconnect, ForceDashboardReturn,
        // SessionEnded, SessionEndedForcedExit) calls BeginTerminalReturn
        // FIRST. It claims the shared guard synchronously — before any modal
        // dialog or await — so a second terminal handler queued on the
        // dispatcher (which can run inside a MessageBox's nested message
        // loop) bails instead of rendering a contradictory state.
        //
        // hideWindow=true tears the live window out of view immediately so no
        // ghost softlock UI is visible underneath a dialog (Case A). The
        // graceful SessionEnded countdown passes hideWindow=false because it
        // intentionally keeps the window visible to show its 3-2-1 banner.
        //
        // Returns false if a terminal path already ran — caller must bail.
        private bool BeginTerminalReturn(string source, bool hideWindow)
        {
            if (_terminalHandled)
            {
                System.Diagnostics.Debug.WriteLine($"[SAC terminal] '{source}' ignored — terminal state already handled.");
                return false;
            }
            _terminalHandled = true;
            // Commit ALL terminal guards up-front so no other handler (which
            // historically keyed off _sessionEnded / _allowClose / _disconnectRouted
            // individually) can proceed concurrently.
            _sessionEnded = true;
            _allowClose = true;
            _disconnectRouted = true;
            System.Diagnostics.Debug.WriteLine($"[SAC terminal] '{source}' claimed terminal state (hideWindow={hideWindow}).");

            // Halt detector + timers so nothing is evaluated, queued, or
            // rendered while we tear down.
            try { if (_detectorRuntime != null) _detectorRuntime.IsPaused = true; } catch { }
            try { _detectorRuntime?.Stop(); } catch { }
            _detectorsRunning = false;
            _statusTimer?.Stop();
            _compactCountdownTimer?.Stop();
            _detectorPollTimer?.Stop();
            _heartbeatTimer?.Stop();

            // Offline-window detections must never reach the server.
            try { DiscardPendingViolations(); } catch { }

            if (hideWindow)
            {
                // Remove the live window from view BEFORE any dialog so the
                // dashboard is the only visible surface and no softlock UI
                // shows underneath.
                try { this.Hide(); } catch { }
            }

            return true;
        }

        // Completes a terminal return: stop SignalR, navigate to the single
        // dashboard, and close this window. Safe to call once per teardown.
        private void CompleteTerminalReturn()
        {
            try { _ = ForceStopSignalRAsync(); } catch { }
            ReturnToStudentDashboard();
        }

        // Student disconnect → dashboard. Invoked (UI thread only) the moment
        // the hub connection drops (Reconnecting or Closed). Per product
        // decision: ANY disconnect immediately and silently returns the
        // student to the single Student Dashboard — NO popup. The dashboard's
        // "No internet" → "Connection restored" banner and the
        // "Reconnect to In-Progress Session" card own recovery, and the
        // student requests rejoin from there (the participant row is flipped
        // to Disconnected server-side, so the rejoin runs through the normal
        // REST /request-join instructor-approval gate). One-shot via
        // BeginTerminalReturn's _terminalHandled guard, so Reconnecting and a
        // later Closed can't both fire it.
        private void HandleTerminalDisconnect()
        {
            // Claim the single terminal guard and hide the window. hideWindow
            // means no softlock UI lingers underneath while we navigate.
            if (!BeginTerminalReturn("Disconnect→dashboard", hideWindow: true))
                return;

            System.Diagnostics.Debug.WriteLine("[SAC] Disconnect detected — returning to dashboard (no popup).");
            CompleteTerminalReturn();
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
            // Per QA: countdown lives at the BOTTOM-LEFT of the SAC softlock,
            // not in the header. We compute the same text and write it into
            // TxtBottomCountdown. Header clock is cleared (kept in XAML for
            // backward compat — empty string just hides it visually).
            string text = ComputeCountdownText();

            if (FindName("TxtHeaderSessionClock") is System.Windows.Controls.TextBlock headerClock)
                headerClock.Text = string.Empty;

            if (FindName("TxtBottomCountdown") is System.Windows.Controls.TextBlock bottomClock)
                bottomClock.Text = text;
        }

        private string ComputeCountdownText()
        {
            if (_sessionEnded)
                return "SESSION ENDED";

            if (_monitoringCountdownEndsAt.HasValue)
            {
                var left = _monitoringCountdownEndsAt.Value - DateTime.Now;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                return $"Starts In: {left:mm\\:ss}";
            }

            if (_monitoringStartedAt.HasValue && _isMonitoringActive)
            {
                if (!_timerEnabled)
                    return string.Empty;

                var elapsed = DateTime.Now - _monitoringStartedAt.Value;
                var remaining = _currentMonitoringDuration - elapsed;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                return $"Timer: {remaining:mm\\:ss}";
            }

            return string.Empty;
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
            // Vertical margin kept at 0 even in full mode so the
            // FullSessionPanel flows directly into the header above
            // and the bottom countdown bar below — the previous
            // (30, 24, 30, 24) re-introduced the two transparent
            // bands every time the user expanded back from compact.
            if (FindName("SessionContentGrid") is FrameworkElement contentGrid)
                contentGrid.Margin = new Thickness(30, 0, 30, 0);

            // R3 fix: never set BtnDone.Visibility from a resize handler.
            // Visibility is the EXCLUSIVE responsibility of UpdateUIForPhase.
            Left = (SystemParameters.WorkArea.Width - Width) / 2 + SystemParameters.WorkArea.Left;
            Top = (SystemParameters.WorkArea.Height - Height) / 2 + SystemParameters.WorkArea.Top;

            if (FindName("FullSessionPanel") is FrameworkElement fullPanel)
                fullPanel.Visibility = Visibility.Visible;
            if (FindName("CompactPanel") is FrameworkElement compactPanel)
                compactPanel.Visibility = Visibility.Collapsed;

            // Header button-pair swap: in full mode we show Minimize
            // (collapse back to softlock overlay) and hide Expand
            // (which only makes sense from the compact overlay).
            if (FindName("BtnMinimize") is FrameworkElement btnMinimize)
                btnMinimize.Visibility = Visibility.Visible;

            // Flip the tracked-compact flag BEFORE the state machine runs so
            // UpdateUIForPhase observes the new layout, not the prior one.
            _isInCompactMode = false;

            // After panel switch, re-assert the state-driven view.
            UpdateUIForPhase();

            Activate();
        }

        // Minimize button (header top-right, visible only in full mode).
        // Routes to SwitchToCompactMode rather than setting WindowState
        // to Minimized so the transition is instant and avoids the
        // double-flip the OnStateChanged minimize-trap would produce.
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            SwitchToCompactMode();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            // Spec v5 — Soft Lock: "Window cannot be manually closed, minimized,
            // or terminated by the student." If the OS or a stray hotkey
            // (Win+D / Win+M / taskbar click) drops us into a Minimized state,
            // immediately re-surface as the compact always-on-top overlay.
            if (WindowState == WindowState.Minimized)
            {
                // Restore first so the dispatcher pumps a non-Minimized state
                // before SwitchToCompactMode mutates Width/Height.
                WindowState = WindowState.Normal;
                SwitchToCompactMode();
                Activate();
            }
        }

        private void SwitchToCompactMode()
        {
            if (FindName("CompactPanel") is FrameworkElement compactPanel && compactPanel.Visibility == Visibility.Visible)
            {
                // Defensive: keep the tracked flag in sync with the actual
                // panel state in case the early return path is hit before
                // the flag was ever set.
                _isInCompactMode = true;
                return;
            }

            WindowState = WindowState.Normal;
            // Compact mode is wide enough to render: shield icon + (gap) +
            // BtnHeaderExpand + BtnDone(MinWidth=120) + padding without
            // clipping. The previous 420 px caused Done to render at the
            // edge in compact view, so students reported needing to expand
            // first before Done appeared.
            Width = 480;
            // Compact softlock overlay height. 150 was clipping the
            // "Leave Permission: Blocked" line on common display scales
            // (the screenshot showed only "Block" peeking out above the
            // window's bottom edge). 180 gives enough room for header
            // strip + two status lines + countdown bar to all render
            // without truncation, while still keeping the overlay
            // minimally intrusive in the corner.
            Height = 180;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;

            if (FindName("BtnHeaderExpand") is Button headerExpand)
                headerExpand.Visibility = Visibility.Visible;

            // Inverse pair: minimize-to-compact only makes sense
            // when we're in full mode. Hide it whenever we collapse
            // back to the softlock overlay.
            if (FindName("BtnMinimize") is FrameworkElement btnMinimize)
                btnMinimize.Visibility = Visibility.Collapsed;

            // R3 fix: do NOT touch BtnDone.Visibility here. The state machine
            // is the single source of truth. The expand button is purely a
            // resize affordance and can stay visible in either layout.
            //
            // Margin zeroed in compact mode so the three frosted strips
            // (header, status band, bottom countdown) sit flush — the
            // previous 8 px on top and bottom rendered as a visible
            // transparent gap between the panels. The status band's
            // border + corner radius were also stripped in XAML for
            // the same reason; together those two edits eliminate the
            // visual "stacked cards" effect without changing content.
            if (FindName("SessionContentGrid") is FrameworkElement contentGrid)
                contentGrid.Margin = new Thickness(0);

            Left = SystemParameters.WorkArea.Right - Width - 16;
            Top = SystemParameters.WorkArea.Bottom - Height - 16;

            if (FindName("FullSessionPanel") is FrameworkElement fullPanel)
                fullPanel.Visibility = Visibility.Collapsed;
            if (FindName("CompactPanel") is FrameworkElement shownCompactPanel)
                shownCompactPanel.Visibility = Visibility.Visible;

            // Mark compact mode active BEFORE UpdateUIForPhase so the state
            // machine's Done-visibility branch reads the fresh value.
            _isInCompactMode = true;

            // After the panel swap, re-run the state machine so the Done
            // button's visibility reflects the current ExamPhase, not the
            // resize event we just processed.
            UpdateUIForPhase();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowClose
                && !_isPermanentlyDone
                && !_isLeaveApproved
                && !_awaitingJoinApproval)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                return;
            }

            _statusTimer?.Stop();
            _compactCountdownTimer?.Stop();
            _detectorPollTimer?.Stop();
            _heartbeatTimer?.Stop();
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
            public string LmsExamUrl { get; set; } = string.Empty;
            // OPTIONAL per-session allowlist (Allowed Apps During Exam).
            // Forwarded verbatim to SacDetectorRuntime, which splits it
            // into process-name and title-keyword sets used by
            // BehavioralMonitoringService.IsAllowedExceptionApp.
            public string? AllowedAppsCsv { get; set; }
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
            public bool HasMultipleMonitors { get; set; }
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

        // Same `dynamic`-vs-strongly-typed lesson as JoinDeniedDto:
        // SignalR's System.Text.Json delivers anonymous objects as
        // JsonElement; explicit casts off `dynamic` throw at runtime
        // and the handler swallows the failure. Concrete DTO fixes it.
        private class HandRaiseDeniedPayload
        {
            public int RoomId { get; set; }
            public string Reason { get; set; } = string.Empty;
        }
    }
}
