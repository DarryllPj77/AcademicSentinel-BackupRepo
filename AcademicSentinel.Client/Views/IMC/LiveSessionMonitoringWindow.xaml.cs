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

        // Set once we've already routed the instructor back to the dashboard
        // because of a SignalR drop. Suppresses re-entry from the second
        // lifecycle event (Reconnecting → Closed fires both) and from the
        // hub StopAsync we issue during clean End-Session teardown.
        private bool _instructorDisconnectHandled;

        private ICollectionView _studentsView;
        private ICollectionView _logsView;
        private LiveStudentStatus _selectedStudent;
        private List<ParticipantDto> _allParticipants = new List<ParticipantDto>();
        private readonly Dictionary<int, bool> _leaveRequestedStateByStudentId = new();
        private readonly Dictionary<int, JoinApprovalRequestDto> _pendingJoinApprovals = new();

        // ============================================================
        // RAISED-HAND PERSISTENCE (decouples hub state from the periodic
        // /participants refresh in LoadParticipantsFromServerAsync).
        // ============================================================
        // Symptom this fixes: the participant refresh DispatcherTimer
        // fires every 4 s and rebuilds ActiveStudents from a fresh
        // HTTP snapshot. The new LiveStudentStatus rows default
        // IsHandRaisePending / IsHandRaiseActive to false, so the
        // Approve/Deny/Lower Hand controls were being wiped within
        // seconds of the request arriving — and almost always before
        // the instructor could even see them.
        //
        // Same shape as _pendingJoinApprovals / _leaveRequestedStateByStudentId:
        // mutated by the hub handlers, consulted by the rebuild loop
        // to restore the per-row flags after Clear().
        private readonly Dictionary<int, HandRaiseRequestDto> _pendingHandRaiseRequests = new();
        private readonly HashSet<int> _activeHandRaiseStudentIds = new();

        // Mirror of the Done bucket — survives LoadParticipantsFromServerAsync
        // refreshes. Set by the StudentLeftSession handler (and the
        // BtnApproveLeave_Click optimistic path) when a student
        // completes the exam via Done. The rebuild loop in
        // LoadParticipantsFromServerAsync consults this set so a
        // student returned by the server as "Disconnected" who is
        // actually a Done completion stays visible under the Done
        // filter instead of vanishing entirely.
        private readonly HashSet<int> _doneStudentIds = new();

        // Mirror of the pending-Done sub-state — survives the same
        // periodic refresh. Set by the SessionCompletionRequested
        // handler; cleared by Approve / Deny / StudentLeftSession.
        // The rebuild loop in LoadParticipantsFromServerAsync uses
        // it to restore IsDoneRequested on rebuilt LiveStudentStatus
        // rows so a pending-Done student doesn't lose their row
        // state every 4 seconds.
        private readonly HashSet<int> _doneRequestedStudentIds = new();

        // Participant-panel cohort filter. Three mutually-exclusive
        // tabs in XAML (RbFilterTaking / RbFilterDone / RbFilterFinished)
        // flip this and call _studentsView.Refresh(). Default is Taking
        // so the instructor's primary attention is on active students.
        //
        // Cohort semantics (mirrored in ParticipantFilterPredicate
        // and UpdateParticipantCount so every surface agrees):
        //   Disconnected → IsDisconnected                (offline mid-session)
        //   Taking       → !IsDisconnected && !IsDoneRequested && !IsDone
        //   Done         → !IsDisconnected && IsDoneRequested && !IsDone (pending)
        //   Finished     → !IsDisconnected && IsDone                     (approved)
        // The four predicates partition the participant set exactly
        // once — every row matches exactly one tab. Disconnected takes
        // precedence so a student whose SAC drops while in Done/Finished
        // is surfaced under the Disconnected tab (where the instructor
        // is looking for them) rather than staying in their previous
        // bucket.
        private enum ParticipantFilterMode { Taking, Done, Finished, Disconnected }
        private ParticipantFilterMode _participantFilter = ParticipantFilterMode.Taking;

        // The search box's lowercased current text. Kept as a field so
        // ParticipantFilterPredicate can read it without re-querying
        // the UI thread.
        private string _participantSearchTerm = string.Empty;

        private bool ParticipantFilterPredicate(object obj)
        {
            if (obj is not LiveStudentStatus s) return false;

            // Four disjoint cohorts. Disconnected wins outright so a
            // student whose SAC drops doesn't keep sitting in Taking
            // / Done / Finished and surprise the instructor — they
            // appear under the Disconnected tab the moment their
            // participant row flips to ConnectionStatus="Disconnected".
            // Done tab is ONLY the pending sub-state (where Approve/Deny
            // live); Finished tab is the teacher-approved completed
            // sub-state.
            bool isDisconnected = s.IsDisconnected;
            bool isPendingDone  = !isDisconnected && s.IsDoneRequested && !s.IsDone;
            bool isFinished     = !isDisconnected && s.IsDone;
            bool isTaking       = !isDisconnected && !isPendingDone && !isFinished;

            bool cohortMatch = _participantFilter switch
            {
                ParticipantFilterMode.Disconnected => isDisconnected,
                ParticipantFilterMode.Done         => isPendingDone,
                ParticipantFilterMode.Finished     => isFinished,
                // Default arm is Taking — covers ParticipantFilterMode.Taking
                // and any future addition that hasn't been wired yet,
                // erring on the safer "show active" side.
                _                                  => isTaking,
            };
            if (!cohortMatch) return false;

            if (string.IsNullOrEmpty(_participantSearchTerm)) return true;
            return !string.IsNullOrEmpty(s.Email)
                && s.Email.ToLower().Contains(_participantSearchTerm);
        }
        private readonly HashSet<int> _safelyLeftStudentIds = new();
        private readonly HashSet<int> _permanentlyDismissedStudents = new HashSet<int>();
        // Tracks students for whom a STUDENT_DISCONNECTED line has already
        // been written to the Global Feed since their last join. Both the
        // SignalR push handler and the participant-poll diff guard against
        // it so the same disconnect can't be logged twice. Cleared on
        // rejoin via the StudentJoinedOrReconnected handler.
        private readonly HashSet<int> _disconnectLoggedStudentIds = new();
        private readonly HashSet<int> _studentsWithViolations = new HashSet<int>();

        // Per-student historical violation totals seeded from the
        // /api/Reports/sessions/{sessionId}/students endpoint on rejoin.
        // Without this carry-over, a brand-new LiveSessionMonitoringWindow
        // built when the teacher comes back from a network drop starts
        // every student at ViolationCount=0 — the in-memory counters live
        // only on the destroyed previous window. The participant-row
        // builder (LoadParticipantsFromServerAsync) consults this map
        // when constructing fresh LiveStudentStatus rows so the displayed
        // counts include events the server kept persisting while the
        // teacher was offline. Live ReceiveViolationAlert increments are
        // mirrored back into this dict so a subsequent 4-second
        // participant refresh doesn't snap the row back to the seed.
        private readonly Dictionary<int, int> _initialViolationCountsByStudentId = new();
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
            // Initial pill state — NOT ACTIVE header + DisabledTimerPanel
            // ("No Active Countdown") visible, ActiveTimerPanel collapsed.
            SetMonitoringPanelState(isActive: false);

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

            // Sort:
            //   1. Section first (Taking ahead of Done) so the All view
            //      still renders the two cohorts together in order.
            //   2. Within a section, most violations go to the top.
            //   3. Alphabetical by Email as the stable tiebreaker.
            _studentsView.SortDescriptions.Add(new SortDescription(nameof(LiveStudentStatus.SectionSortOrder), ListSortDirection.Ascending));
            _studentsView.SortDescriptions.Add(new SortDescription("ViolationCount", ListSortDirection.Descending));
            _studentsView.SortDescriptions.Add(new SortDescription("Email", ListSortDirection.Ascending));

            // Filter (defaults to Taking on construction — see field
            // declaration). The predicate combines the participant
            // cohort filter with the existing text search so toggling
            // tabs and typing in the search box compose cleanly.
            _studentsView.Filter = ParticipantFilterPredicate;

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

                // ============================================================
                // REHYDRATE PER-STUDENT VIOLATION TOTALS
                // ============================================================
                // The server's response already carries an aggregated
                // ViolationCount per student (counted from MonitoringEvents
                // in ReportsController). Seed the in-memory maps so the
                // student tiles render the correct historical total
                // immediately, and so the periodic participant refresh
                // doesn't reset rows to 0 by overwriting freshly-built
                // LiveStudentStatus objects with no ViolationCount.
                foreach (var s in students)
                {
                    if (s.ViolationCount > 0)
                    {
                        _initialViolationCountsByStudentId[s.StudentId] = s.ViolationCount;
                        _studentsWithViolations.Add(s.StudentId);
                    }
                }

                // Apply the seed to any rows already constructed by the
                // constructor's first LoadParticipantsFromServerAsync tick
                // (which ran before this rehydration finished).
                foreach (var liveRow in ActiveStudents)
                {
                    if (_initialViolationCountsByStudentId.TryGetValue(liveRow.StudentId, out var historical)
                        && liveRow.ViolationCount < historical)
                    {
                        liveRow.ViolationCount = historical;
                        liveRow.HasViolation = true;
                    }
                }
                _studentsView.Refresh();

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
            catch (Exception ex)
            {
                // Surface the failure to the debug output so a broken
                // historical-fetch (401 token expiry, 500 server error,
                // network blip) doesn't silently leave the IMC showing
                // "0 alerts" and a blank Global Log Feed forever.
                System.Diagnostics.Debug.WriteLine(
                    $"[LoadHistoricalLogsAsync] failed for session {sessionId}: {ex.Message}");
            }
        }

        // Minimal DTOs scoped to the replay endpoint. Kept private to avoid
        // leaking a thin shape into the wider Models namespace.
        private sealed class HistoricalStudentDto
        {
            public int StudentId { get; set; }
            public string Name { get; set; }
            public string Email { get; set; }
            // Server already aggregates this in ReportsController.GetSessionStudents
            // (lines ~239, 376). We read it so the IMC can rebuild per-student
            // violation totals on rejoin instead of starting from zero.
            public int ViolationCount { get; set; }
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

                    // Rejoined an already-active session — countdown phase
                    // (if any) is long over, so disabled timer panel.
                    SetMonitoringPanelState(isActive: true);
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
            _participantSearchTerm = TxtStudentSearch.Text.ToLower();
            // ParticipantFilterPredicate consults both _participantFilter
            // and _participantSearchTerm — a single Refresh() applies
            // both. The Filter delegate itself was installed once in
            // the constructor; reassigning it here would clobber the
            // cohort filter, so we just trigger re-evaluation.
            _studentsView.Refresh();
        }

        // Cohort filter — radio buttons in the participant panel header.
        private void RbFilterTaking_Checked(object sender, RoutedEventArgs e)
            => SetParticipantFilter(ParticipantFilterMode.Taking);

        private void RbFilterDone_Checked(object sender, RoutedEventArgs e)
            => SetParticipantFilter(ParticipantFilterMode.Done);

        private void RbFilterFinished_Checked(object sender, RoutedEventArgs e)
            => SetParticipantFilter(ParticipantFilterMode.Finished);

        private void RbFilterDisconnected_Checked(object sender, RoutedEventArgs e)
            => SetParticipantFilter(ParticipantFilterMode.Disconnected);

        // ============================================================
        // SESSION ANALYTIC MODE
        // ============================================================
        // Toggles between the per-student participant list and a
        // session-level dashboard (Risk distribution + Connection
        // distribution + total violations). The participant ScrollViewer
        // and the analytics Border share the same Grid slot — only one
        // is Visible at a time. Real-time polling, filtering, and the
        // ObservableCollections behind the list keep updating in the
        // background; the analytics snapshot is recomputed every time
        // the user enters analytics mode so a long-open dashboard
        // doesn't get stale relative to the participant list under it.
        private bool _isSessionAnalyticMode = false;

        private void BtnSessionAnalyticMode_Click(object sender, RoutedEventArgs e)
        {
            _isSessionAnalyticMode = !_isSessionAnalyticMode;

            if (_isSessionAnalyticMode)
            {
                if (ParticipantsScrollViewer != null)
                    ParticipantsScrollViewer.Visibility = Visibility.Collapsed;
                if (ParticipantsAnalyticsPanel != null)
                    ParticipantsAnalyticsPanel.Visibility = Visibility.Visible;

                BtnSessionAnalyticMode.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(56, 142, 60));   // #388E3C — active green
                if (TxtSessionAnalyticMode != null)
                    TxtSessionAnalyticMode.Text = "List View";

                LoadSessionAnalytics();
            }
            else
            {
                if (ParticipantsScrollViewer != null)
                    ParticipantsScrollViewer.Visibility = Visibility.Visible;
                if (ParticipantsAnalyticsPanel != null)
                    ParticipantsAnalyticsPanel.Visibility = Visibility.Collapsed;

                BtnSessionAnalyticMode.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(25, 118, 210));  // #1976D2 — default blue
                if (TxtSessionAnalyticMode != null)
                    TxtSessionAnalyticMode.Text = "Analytics";
            }
        }

        /// <summary>
        /// Aggregates the current participant set (ActiveStudents) into
        /// Risk and Connection Status buckets, then binds the two
        /// MetricAnalyticItem lists to the dashboard ItemsControls. Risk
        /// is derived from the per-student summed SeverityScore in
        /// _studentLogs — this matches the same Safe / Suspicious /
        /// Possible Dishonesty band used by the right-hand Student
        /// Details pane. Connection bucket reads from
        /// LiveStudentStatus.IsDisconnected (true → "Disconnected") and
        /// falls back to the row's Status text for joined / pending
        /// rows. Total violations is just the sum of ViolationCount
        /// across every participant.
        /// </summary>
        private void LoadSessionAnalytics()
        {
            // Defensive guard. The button can technically be clicked
            // immediately on window open before LoadParticipantsFromServerAsync
            // populates ActiveStudents.
            if (ActiveStudents == null || ActiveStudents.Count == 0)
            {
                SessionRiskAnalyticsControl.ItemsSource = null;
                SessionConnectionAnalyticsControl.ItemsSource = null;
                TxtSessionTotalViolations.Text = "0 violations recorded.";
                if (TxtSessionAnalyticsEmpty != null)
                    TxtSessionAnalyticsEmpty.Visibility = Visibility.Visible;
                return;
            }

            int totalStudents = ActiveStudents.Count;

            // ----------------------------------------------------------
            // A. RISK LEVEL — derived from per-student total severity.
            //     Same thresholds the Student Details pane uses, so the
            //     dashboard's bucket counts agree with what an instructor
            //     sees when they click on an individual row.
            //         < 20  → "Safe"
            //         20-49 → "Suspicious"
            //         ≥ 50  → "Possible Dishonesty"
            // ----------------------------------------------------------
            var riskBuckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Safe"]                = 0,
                ["Suspicious"]          = 0,
                ["Possible Dishonesty"] = 0,
            };

            foreach (var s in ActiveStudents)
            {
                int score = _studentLogs.TryGetValue(s.StudentId, out var logs)
                    ? logs.Where(l => l.SeverityScore > 0).Sum(l => l.SeverityScore)
                    : 0;

                string bucket = score >= 50 ? "Possible Dishonesty"
                              : score >= 20 ? "Suspicious"
                              : "Safe";
                riskBuckets[bucket]++;
            }

            var riskData = riskBuckets
                .Where(kv => kv.Value > 0)
                .Select(kv => new MetricAnalyticItem
                {
                    Category = kv.Key.ToUpperInvariant(),
                    Count    = kv.Value,
                    Max      = totalStudents,
                    BarBrush = kv.Key.Equals("Possible Dishonesty", StringComparison.OrdinalIgnoreCase)
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47))
                        : kv.Key.Equals("Suspicious", StringComparison.OrdinalIgnoreCase)
                            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 81, 0))
                            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 94, 32))
                })
                .OrderByDescending(x => x.Count)
                .ToList();
            SessionRiskAnalyticsControl.ItemsSource = riskData;

            // ----------------------------------------------------------
            // B. CONNECTION STATUS — bucket from LiveStudentStatus
            //     flags. IsDisconnected wins (matches the cohort filter
            //     priority); the rest fall into the row's Status string
            //     (Connected, Wants to Leave, Waiting to Join, etc.).
            // ----------------------------------------------------------
            var connData = ActiveStudents
                .GroupBy(s => s.IsDisconnected
                    ? "Disconnected"
                    : (string.IsNullOrWhiteSpace(s.Status) ? "Unknown" : s.Status))
                .Select(g => new MetricAnalyticItem
                {
                    Category = g.Key.ToUpperInvariant(),
                    Count    = g.Count(),
                    Max      = totalStudents,
                    BarBrush = g.Key.Equals("Disconnected", StringComparison.OrdinalIgnoreCase)
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(211, 47, 47))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 118, 210))
                })
                .OrderByDescending(x => x.Count)
                .ToList();
            SessionConnectionAnalyticsControl.ItemsSource = connData;

            // ----------------------------------------------------------
            // C. TOTAL VIOLATIONS — sum of every row's ViolationCount.
            //     ActiveStudents tracks this as events stream in via the
            //     ReceiveViolationAlert handler, so the dashboard total
            //     matches what the bottom-right header pill shows.
            // ----------------------------------------------------------
            int totalViolations        = ActiveStudents.Sum(s => s.ViolationCount);
            int studentsWithViolations = ActiveStudents.Count(s => s.ViolationCount > 0);
            TxtSessionTotalViolations.Text =
                $"{totalViolations} violation(s) recorded across {studentsWithViolations} of {totalStudents} student(s).";

            if (TxtSessionAnalyticsEmpty != null)
                TxtSessionAnalyticsEmpty.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Row shape bound to the Risk / Connection analytics
        /// ItemsControls. Count drives the ProgressBar Value, Max drives
        /// Maximum (always total participants so bar widths are
        /// proportional to the whole session, not just to the largest
        /// bucket). BarBrush is denormalized so each row can colour-code
        /// itself without a converter.
        /// </summary>
        private sealed class MetricAnalyticItem
        {
            public string Category { get; set; }
            public int Count { get; set; }
            public int Max { get; set; }
            public System.Windows.Media.Brush BarBrush { get; set; }
        }

        private void SetParticipantFilter(ParticipantFilterMode mode)
        {
            if (_studentsView == null) return; // pre-init Checked callback
            if (_participantFilter == mode) return;
            _participantFilter = mode;
            _studentsView.Refresh();
            UpdateParticipantCount();
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

            // Note: CountdownDisabledIndicator / TxtCountdownDisplay visibility
            // is no longer toggled here — those two are children of the new
            // DisabledTimerPanel / ActiveTimerPanel pill, and their visibility
            // is owned by SetMonitoringPanelState.  Toggling them directly
            // here would fight the helper and re-introduce the
            // "Countdown 7s / Timer Disabled" contradiction.
            //
            // Only TxtMonitoringTimerDisplay (the separate auto-stop timer
            // display elsewhere on the window) is initialised here.
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
                    // Session created but monitoring not yet running — still NOT ACTIVE.
                    SetMonitoringPanelState(isActive: false);
                }

                await InitializeSignalR();

                if (_startDelaySeconds > 0)
                {
                    await _hubConnection.InvokeAsync("BeginMonitoringCountdown", _roomId, _startDelaySeconds, _monitoringDurationSeconds > 0 ? _monitoringDurationSeconds : 0);
                    _monitoringEffectiveStartTime = DateTime.Now.AddSeconds(_startDelaySeconds);
                    _countdownSecondsRemaining = _startDelaySeconds;
                    LogActivity("SYSTEM", "COUNTDOWN", $"Monitoring starts in {_startDelaySeconds} seconds.", "#FF9800");
                }
                else
                {
                    await _hubConnection.InvokeAsync("SetMonitoringState", _roomId, true);
                    _monitoringEffectiveStartTime = DateTime.Now;
                }

                _isMonitoringStarted = true;
                // Pill goes ACTIVE immediately.  If a start-delay countdown is
                // in progress, show it in the ActiveTimerPanel; otherwise the
                // disabled panel reads "No Active Countdown".
                if (_startDelaySeconds > 0)
                {
                    var countdownLabel = TimeSpan.FromSeconds(_startDelaySeconds).ToString(@"mm\:ss");
                    SetMonitoringPanelState(isActive: true, countdownText: countdownLabel);
                }
                else
                {
                    SetMonitoringPanelState(isActive: true);
                }
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
            // Paused → pill flips to NOT ACTIVE.
            SetMonitoringPanelState(isActive: false);
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
            // Resumed → MONITORING ACTIVE.  No countdown context here.
            SetMonitoringPanelState(isActive: true);
            SetMonitoringControlButtonState(MonitoringControlState.Active);
            LogActivity("SYSTEM", "RESUMED", "Monitoring resumed.", "#1B5E20");
        }

        /// <summary>
        /// Centralised toggle for the monitoring-state pill in the left
        /// sidebar.  Mutually exclusive visual states keep the header and
        /// the countdown badge in lockstep so the UI can never display
        /// the old "Monitoring: Countdown 7s" / "Timer Disabled"
        /// contradiction again.
        ///
        /// • <paramref name="isActive"/> = true  → header reads
        ///   "MONITORING ACTIVE" in FEU-green.
        /// • <paramref name="isActive"/> = false → header reads
        ///   "NOT ACTIVE" in neutral grey.
        ///
        /// The countdown pill below the header is independent of the
        /// header colour: pass <paramref name="countdownText"/> to show
        /// the orange ActiveTimerPanel (clock icon + mm:ss), or leave it
        /// null to show the grey DisabledTimerPanel ("No Active Countdown").
        ///
        /// All TxtMonitoringState / DisabledTimerPanel / ActiveTimerPanel
        /// mutations in the codebase go through this method so the
        /// invariant is enforced at a single point.
        /// </summary>
        private void SetMonitoringPanelState(bool isActive, string countdownText = null)
        {
            if (FindName("TxtMonitoringState") is TextBlock header)
            {
                header.Text = isActive ? "MONITORING ACTIVE" : "NOT ACTIVE";
                header.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                    isActive ? "#1B5E20"   // FEU green
                             : "#424242")); // neutral grey
            }

            bool showCountdown = countdownText != null;

            if (FindName("ActiveTimerPanel") is StackPanel active)
                active.Visibility = showCountdown ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("DisabledTimerPanel") is StackPanel disabled)
                disabled.Visibility = showCountdown ? Visibility.Collapsed : Visibility.Visible;

            if (showCountdown && FindName("TxtCountdownDisplay") is TextBlock countdown)
                countdown.Text = countdownText;
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

                    // Countdown finished → still MONITORING ACTIVE, but the
                    // countdown pill flips back to the grey "No Active
                    // Countdown" panel because there's nothing to count.
                    SetMonitoringPanelState(isActive: true);

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
                    // Countdown still ticking → keep the pill green-ACTIVE and
                    // update the orange ActiveTimerPanel with mm:ss.
                    _countdownSecondsRemaining = Math.Max(0, (int)Math.Ceiling((_monitoringEffectiveStartTime.Value - DateTime.Now).TotalSeconds));
                    var countdownLabel = TimeSpan.FromSeconds(_countdownSecondsRemaining).ToString(@"mm\:ss");
                    SetMonitoringPanelState(isActive: true, countdownText: countdownLabel);
                }
                // else: monitoring fully active without a countdown — pill state
                //       is already correct from the branch above; nothing to do.

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
            // Auto-paused by timer expiry → pill flips to NOT ACTIVE.
            SetMonitoringPanelState(isActive: false);
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

            // ============================================================
            // INSTRUCTOR CONNECTION-LIFECYCLE HOOKS
            // ============================================================
            // Without these the IMC freezes silently when the instructor's
            // internet drops — WithAutomaticReconnect retries in the
            // background but the UI has no idea the pipe is broken. Hook
            // both Reconnecting (transient drop) and Closed (terminal drop)
            // and route the instructor back to TeacherDashboard via the
            // UI thread so a background SignalR worker doesn't try to
            // construct WPF objects off-thread.
            _hubConnection.Reconnecting += error =>
            {
                Application.Current?.Dispatcher.InvokeAsync(HandleInstructorDisconnect);
                return Task.CompletedTask;
            };
            _hubConnection.Closed += error =>
            {
                Application.Current?.Dispatcher.InvokeAsync(HandleInstructorDisconnect);
                return Task.CompletedTask;
            };

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

                // Informational events carry zero severity score and
                // must NOT inflate the student's violation count, mark
                // them as "ALERT", or render the red VIOLATION badge.
                // Two known informational types right now:
                //   • CANVAS_RETURNED — student returned to the LMS.
                //   • ALLOWED_APP    — student switched to an
                //     instructor-allowed app (per-session allowlist).
                // Both render with a green badge and a non-alert
                // status; ALLOWED_APP just uses a different badge
                // label and message so the teacher can tell at a
                // glance which informational event fired.
                bool isCanvasReturn = string.Equals(payload.EventType, "CANVAS_RETURNED",
                                          StringComparison.OrdinalIgnoreCase);
                bool isAllowedApp  = string.Equals(payload.EventType, "ALLOWED_APP",
                                          StringComparison.OrdinalIgnoreCase);
                bool isInformational = isCanvasReturn || isAllowedApp;

                if (isInformational)
                {
                    string badge;
                    string message;
                    string color = "#1B5E20";
                    if (isCanvasReturn)
                    {
                        badge = "RETURN";
                        message = string.IsNullOrWhiteSpace(payload.Description)
                            ? "Student returned to the LMS exam."
                            : payload.Description;
                    }
                    else
                    {
                        badge = "ALLOWED";
                        // payload.Description carries just the friendly
                        // app name from the SAC (e.g., "Microsoft Teams").
                        // Wrap it in the canonical wording so the IMC
                        // log line matches what the student sees on
                        // their softlock log.
                        string appLabel = string.IsNullOrWhiteSpace(payload.Description)
                            ? "instructor-allowed app"
                            : payload.Description;
                        message = $"Allowed app switch detected: ALLOWED_APP | {appLabel}";
                    }

                    LogActivity(email, badge, message, color);

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

                int increment = Math.Max(1, payload.SeverityScore);
                if (targetStudent != null)
                {
                    targetStudent.ViolationCount += increment;
                    targetStudent.HasViolation = true;
                    targetStudent.Status = $"ALERT: {payload.EventType}";
                    targetStudent.StatusColor = "#D32F2F";
                }

                // Mirror the live increment into the historical-seed map
                // so the periodic participant refresh rebuilds the row
                // with the running total instead of snapping back to
                // whatever the server-side aggregate was at rejoin time.
                if (_initialViolationCountsByStudentId.TryGetValue(payload.StudentId, out var prior))
                    _initialViolationCountsByStudentId[payload.StudentId] = prior + increment;
                else
                    _initialViolationCountsByStudentId[payload.StudentId] = increment;

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
                    // IsDoneRequested=true moves the row into the Done
                    // tab via the cohort filter (Done = IsDone OR
                    // IsDoneRequested). IsLeaveRequested=true is also
                    // set because the XAML Approve / Deny button
                    // visibility is bound to it — keeping both flags
                    // up at the same time means the row appears in
                    // the Done tab WITH its action buttons rendered.
                    // IsDone stays false: the student is only pending
                    // approval, not finalized. Teacher Approve flips
                    // IsDone→true (and clears IsDoneRequested + IsLeaveRequested);
                    // teacher Deny clears IsDoneRequested + IsLeaveRequested
                    // so the row falls back to the Taking tab.
                    targetStudent.IsDoneRequested = true;
                    targetStudent.IsLeaveRequested = true;
                    targetStudent.Status = "Awaiting Done Approval";
                    targetStudent.StatusColor = "#E65100";
                }

                // Persist the pending-Done flag the same way
                // _leaveRequestedStateByStudentId persists the legacy
                // leave flag — the 4 s LoadParticipantsFromServerAsync
                // refresh rebuilds LiveStudentStatus rows and would
                // otherwise wipe IsDoneRequested back to false.
                _doneRequestedStudentIds.Add(studentId);

                LogActivity(email, "DONE", "Student finished the assessment — awaiting instructor approval.", "#1B5E20");
                _studentsView.Refresh();
                UpdateParticipantCount();
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

            // ============================================================
            // RAISED-HAND HUB EVENTS
            // ============================================================
            // Pending: student tapped Raise Hand on the softlock UI.
            // Surface the request through the participant tile (✋ WANTS
            // TO ASK label + Allow Q&A / Deny buttons) and log a
            // HAND_RAISED entry to the Global Log Feed. Strongly-typed
            // DTO — see HandRaiseRequestDto for the reason `dynamic`
            // fails to populate the payload at runtime.
            _hubSubscriptions.Add(_hubConnection.On<HandRaiseRequestDto>("HandRaiseRequested", payload => Dispatcher.Invoke(() =>
            {
                if (payload == null)
                {
                    Console.WriteLine("[HandRaise] HandRaiseRequested received but payload was null.");
                    return;
                }

                Console.WriteLine($"[HandRaise] HandRaiseRequested received: studentId={payload.StudentId}, name={payload.StudentName}, email={payload.StudentEmail}");

                // Persist BEFORE mutating ActiveStudents so a refresh
                // that races our Dispatcher.Invoke can still rebuild
                // the flag from this dictionary. This is the actual
                // anti-race guard — without it, the 4 s refresh wipes
                // IsHandRaisePending within seconds.
                _pendingHandRaiseRequests[payload.StudentId] = payload;
                _activeHandRaiseStudentIds.Remove(payload.StudentId); // not active yet

                var target = ActiveStudents.FirstOrDefault(s => s.StudentId == payload.StudentId);
                bool materialized = target == null;
                if (materialized)
                {
                    // The requesting student might not be present in
                    // ActiveStudents if the periodic participant refresh
                    // hasn't completed yet. Materialise a row from the
                    // payload — mirrors what StudentPendingApproval does
                    // for late-arriving join requests.
                    target = new LiveStudentStatus
                    {
                        StudentId = payload.StudentId,
                        Name = string.IsNullOrWhiteSpace(payload.StudentName) ? payload.StudentEmail : payload.StudentName,
                        Email = payload.StudentEmail ?? string.Empty,
                        Status = "Hand Raised",
                        StatusColor = "#1565C0"
                    };
                    ActiveStudents.Add(target);
                }

                target.IsHandRaisePending = true;
                target.IsHandRaiseActive  = false;

                Console.WriteLine($"[HandRaise] participant row {(materialized ? "materialised" : "found")}; IsHandRaisePending={target.IsHandRaisePending}; ActiveStudents.Count={ActiveStudents.Count}");

                LogActivity(target.Email, "HAND_RAISED",
                    $"{target.Name} raised hand — requesting Q&A access.", "#1565C0");
                _studentsView.Refresh();
                UpdateParticipantCount();
            })));

            // HandRaiseResolved: any IMC in the room (including the one
            // that clicked Approve) gets this so every dashboard stays
            // in sync. Approved → tile flips to Active and the Lower
            // Hand button takes over. Denied → both flags cleared.
            _hubSubscriptions.Add(_hubConnection.On<HandRaiseResolvedDto>("HandRaiseResolved", payload => Dispatcher.Invoke(() =>
            {
                if (payload == null) return;

                bool approved = string.Equals(payload.Decision, "Approved", StringComparison.OrdinalIgnoreCase);

                // Keep the persistence sets authoritative so the next
                // /participants refresh restores the correct flag.
                _pendingHandRaiseRequests.Remove(payload.StudentId);
                if (approved)
                    _activeHandRaiseStudentIds.Add(payload.StudentId);
                else
                    _activeHandRaiseStudentIds.Remove(payload.StudentId);

                Console.WriteLine($"[HandRaise] HandRaiseResolved received: studentId={payload.StudentId}, decision={payload.Decision}");

                var target = ActiveStudents.FirstOrDefault(s => s.StudentId == payload.StudentId);
                if (target == null) return;

                target.IsHandRaisePending = false;
                target.IsHandRaiseActive = approved;

                LogActivity(target.Email,
                    approved ? "HAND_APPROVED" : "HAND_DENIED",
                    approved
                        ? $"{target.Name} is now in Q&A mode — alt-tab / focus events suppressed."
                        : $"Raised-hand request from {target.Name} denied.",
                    approved ? "#E65100" : "#9E9E9E");
                _studentsView.Refresh();
            })));

            // HandLowered fires for both self-lower (student clicked
            // Lower Hand) and force-lower (instructor). Clear both
            // pending and active so the tile/buttons return to normal.
            _hubSubscriptions.Add(_hubConnection.On<int>("HandLowered", studentId => Dispatcher.Invoke(() =>
            {
                Console.WriteLine($"[HandRaise] HandLowered received: studentId={studentId}");

                // Drain the persistence sets first — even if the
                // student row isn't currently in ActiveStudents, the
                // next /participants refresh shouldn't re-mark them.
                bool wasPending = _pendingHandRaiseRequests.Remove(studentId);
                bool wasActive  = _activeHandRaiseStudentIds.Remove(studentId);

                var target = ActiveStudents.FirstOrDefault(s => s.StudentId == studentId);
                if (target == null) return;
                if (!target.IsHandRaisePending && !target.IsHandRaiseActive && !wasPending && !wasActive) return;

                bool tileWasActive = target.IsHandRaiseActive || wasActive;
                target.IsHandRaisePending = false;
                target.IsHandRaiseActive  = false;

                if (tileWasActive)
                {
                    LogActivity(target.Email, "HAND_LOWERED",
                        $"{target.Name} lowered hand — monitoring resumed.", "#1B5E20");
                }
                _studentsView.Refresh();
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
                    // Move the row into the Done bucket instead of
                    // deleting it. The CollectionViewSource grouping
                    // on Section flips this student under the "Done"
                    // header automatically after Refresh().
                    student.IsOffline = false;
                    student.IsDone = true;
                    student.Status = "Done";
                    student.StatusColor = "#1B5E20";

                    // Clear interaction flags — Done students do not
                    // get Approve/Deny or hand-raise affordances. The
                    // template's data triggers stop emitting those
                    // controls as soon as these flip to false.
                    student.IsLeaveRequested = false;
                    // Pending-Done is resolved by this approval; clear
                    // the flag so the row's Section computation now
                    // keys solely off IsDone.
                    student.IsDoneRequested = false;
                    student.IsJoinApprovalPending = false;
                    student.IsHandRaisePending = false;
                    student.IsHandRaiseActive = false;

                    // Persist the Done state so LoadParticipantsFromServerAsync
                    // can re-derive it on the next 4 s refresh — without
                    // this, the rebuild would skip the row (server reports
                    // Disconnected, the loader filters that out) and the
                    // student would disappear from the Done section after
                    // the very next poll. NOT adding to
                    // _permanentlyDismissedStudents on purpose: that set
                    // is reserved for kicks/denials that should fully
                    // hide the row, which is the opposite of what we
                    // want here.
                    _doneStudentIds.Add(studentId);
                    _doneRequestedStudentIds.Remove(studentId);
                    _leaveRequestedStateByStudentId.Remove(studentId);
                    _pendingJoinApprovals.Remove(studentId);
                    _pendingHandRaiseRequests.Remove(studentId);
                    _activeHandRaiseStudentIds.Remove(studentId);

                    LogActivity("SYSTEM", "SYSTEM", $"EXAM COMPLETED. Student exited properly. {student.Name}", "#1B5E20");
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

        // ============================================================
        // INSTRUCTOR DISCONNECT BAILOUT (UI-thread only)
        // ============================================================
        // Called from the Reconnecting / Closed hub lifecycle events.
        // Guards:
        //   • `_isSessionEnded` — the instructor clicked End Session and
        //     the StopAsync inside BtnEndSession_Click is what fired
        //     Closed. Not a real network drop; the window is already
        //     closing itself.
        //   • `_instructorDisconnectHandled` — Reconnecting fires first,
        //     then Closed; without this flag the instructor would see
        //     the dialog twice and we'd try to open two TeacherDashboard
        //     windows.
        // Routes back to TeacherDashboard so the instructor lands on a
        // live, clickable surface and can re-enter the room manually
        // through the normal Setup flow once their network returns.
        private void HandleInstructorDisconnect()
        {
            if (_isSessionEnded) return;
            if (_instructorDisconnectHandled) return;
            _instructorDisconnectHandled = true;

            MessageBox.Show(
                "Connection to the server was lost. Returning to the dashboard.",
                "Disconnected",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            try
            {
                var dashboard = new TeacherDashboard();
                dashboard.Show();
            }
            catch { /* best-effort surface — falling through to Close() either way */ }

            // Suppress the OnClosing "must end the active session" guard:
            // the connection is gone, the teacher can't End-Session on the
            // server, and trapping them in a dead window is the bug.
            _isSessionEnded = true;
            try { this.Close(); } catch { }
        }

        private async void BtnApproveLeave_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not LiveStudentStatus student)
                return;

            try
            {
                await _hubConnection.InvokeAsync("GrantLeave", _roomId, student.StudentId);

                // Optimistic UI: the StudentLeftSession broadcast that
                // GrantLeave triggers will officially flip the row
                // into the approved-Done sub-state, but we set it up
                // here so the instructor sees the transition (Approve
                // / Deny buttons disappear, status becomes "Done")
                // immediately without waiting for the round-trip.
                _doneStudentIds.Add(student.StudentId);
                _doneRequestedStudentIds.Remove(student.StudentId);
                _leaveRequestedStateByStudentId[student.StudentId] = false;
                student.IsLeaveRequested = false;
                student.IsDoneRequested = false;
                student.IsDone = true;
                student.Status = "Done";
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

                // Clear BOTH the pending Done flag and the legacy
                // leave flag. With IsDoneRequested=false and IsDone
                // still false, the cohort filter relocates this row
                // back into the Taking tab on the next Refresh().
                _leaveRequestedStateByStudentId[student.StudentId] = false;
                _doneRequestedStudentIds.Remove(student.StudentId);
                student.IsLeaveRequested = false;
                student.IsDoneRequested = false;
                student.Status = "Connected";
                student.StatusColor = "#4CAF50";

                LogActivity(student.Email, "DENY", "Instructor denied the Done request — student can resume work.", "#FF9800");
                _studentsView.Refresh();
                UpdateParticipantCount();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to deny leave: {ex.Message}", "Deny Leave", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ============================================================
        // RAISED-HAND APPROVAL HANDLERS (IMC → hub)
        // ============================================================
        // The tile state transitions are driven by the server-side
        // HandRaiseResolved / HandLowered broadcasts (see the hub
        // subscriptions above), not optimistically here, so every IMC
        // instance in the room stays in sync.

        private async void BtnApproveHandRaise_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not LiveStudentStatus student)
                return;
            try
            {
                await _hubConnection.InvokeAsync("ApproveRaiseHand", _roomId, student.StudentId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to approve raised hand: {ex.Message}",
                    "Allow Q&A", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnDenyHandRaise_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not LiveStudentStatus student)
                return;
            try
            {
                await _hubConnection.InvokeAsync("DenyRaiseHand", _roomId, student.StudentId,
                    "Please continue with the exam.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to deny raised hand: {ex.Message}",
                    "Deny Hand", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnLowerHand_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not LiveStudentStatus student)
                return;
            try
            {
                await _hubConnection.InvokeAsync("ForceLowerHand", _roomId, student.StudentId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to lower hand: {ex.Message}",
                    "Lower Hand", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LogActivity(string email, string badge, string msg, string color)
        {
            if (EmptyLogFeedState != null) EmptyLogFeedState.Visibility = Visibility.Collapsed;
            string label = ResolveLogDisplayLabel(email);
            LogFeed.Insert(0, new LogEntry
            {
                Timestamp    = DateTime.Now.ToString("T"),
                StudentLabel = label,
                StudentEmail = email,            // kept as hidden metadata
                BadgeText    = badge,
                BadgeColor   = color,
                Message      = msg
            });
        }

        /// <summary>
        /// Resolves the visible "who" label for a Global Log Feed entry.
        ///
        /// Most existing call sites pass an email address (student.Email,
        /// targetStudent.Email, ParticipantDto.StudentEmail, etc.). The
        /// feed UI must show the student's FullName instead — so look the
        /// email up against the in-memory student state and swap to the
        /// stored name. Resolution order:
        ///
        ///   1. ActiveStudents — freshest live state, refreshed every 4 s
        ///      by LoadParticipantsFromServerAsync and mutated on hub
        ///      events. Best source for "right now".
        ///   2. _allParticipants — the last /participants snapshot from
        ///      the server. Used when ActiveStudents is mid-rebuild or
        ///      the student isn't currently rendered.
        ///   3. The passed string verbatim — last-resort fallback so a
        ///      log entry is never blank.
        ///
        /// "SYSTEM" passes through unchanged. A passed value that
        /// doesn't look like an email (no "@") is assumed to already
        /// be a friendly label (e.g., a caller that pre-resolved the
        /// name) and is also passed through.
        /// </summary>
        private string ResolveLogDisplayLabel(string passed)
        {
            if (string.IsNullOrWhiteSpace(passed)) return "SYSTEM";
            if (string.Equals(passed, "SYSTEM", StringComparison.OrdinalIgnoreCase))
                return passed;
            if (!passed.Contains('@')) return passed;

            var liveMatch = ActiveStudents.FirstOrDefault(s =>
                string.Equals(s.Email, passed, StringComparison.OrdinalIgnoreCase));
            if (liveMatch != null && !string.IsNullOrWhiteSpace(liveMatch.Name))
                return liveMatch.Name;

            var snapMatch = _allParticipants?.FirstOrDefault(p =>
                string.Equals(p.StudentEmail, passed, StringComparison.OrdinalIgnoreCase));
            if (snapMatch != null && !string.IsNullOrWhiteSpace(snapMatch.StudentName))
                return snapMatch.StudentName;

            return passed;
        }

        private void UpdateParticipantCount()
        {
            // Four disjoint buckets — exactly mirrors
            // ParticipantFilterPredicate so the tab labels, the header
            // pill, the missing line, and the visible row counts never
            // disagree. Disconnected wins outright so a dropped row
            // is counted once under its own tab and never double-counted
            // in Taking / Done / Finished.
            int disconnectedCount = ActiveStudents.Count(s => s.IsDisconnected);
            int takingCount       = ActiveStudents.Count(s => !s.IsDisconnected && !s.IsDoneRequested && !s.IsDone);
            int doneCount         = ActiveStudents.Count(s => !s.IsDisconnected &&  s.IsDoneRequested && !s.IsDone);
            int finishedCount     = ActiveStudents.Count(s => !s.IsDisconnected &&  s.IsDone);

            if (EmptyParticipantsState != null && ActiveStudents.Count > 0)
                EmptyParticipantsState.Visibility = Visibility.Collapsed;

            // Header pill shows Taking out of total enrolled — the
            // "still being monitored" headline number the instructor
            // cares about most regardless of which tab is active.
            TxtParticipantCount.Text = $"{takingCount}/{_enrolledCount}";

            // Missing = enrolled minus everyone we currently have on
            // screen across all four buckets. Disconnected is shown
            // inline so a glance at the header tells the teacher
            // whether anyone has dropped offline mid-session.
            var missing = Math.Max(0, _enrolledCount - takingCount - doneCount - finishedCount - disconnectedCount);
            if (FindName("TxtMissingCount") is TextBlock txtMissing)
                txtMissing.Text = $"Finished: {finishedCount} · Done: {doneCount} · Offline: {disconnectedCount} · Missing: {missing}";

            // Refresh the filter-tab labels so each carries its own
            // running count without needing a binding converter.
            if (FindName("RbFilterTaking") is RadioButton rbTaking)
                rbTaking.Content = $"Taking ({takingCount})";
            if (FindName("RbFilterDone") is RadioButton rbDone)
                rbDone.Content = $"Done ({doneCount})";
            if (FindName("RbFilterFinished") is RadioButton rbFinished)
                rbFinished.Content = $"Finished ({finishedCount})";
            if (FindName("RbFilterDisconnected") is RadioButton rbDisconnected)
                rbDisconnected.Content = $"Disconnected ({disconnectedCount})";
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

                // Preserve Done rows across the rebuild — the server
                // reports them as "Disconnected" so the main loop below
                // (which only keeps "Joined" rows) would drop them
                // otherwise. Pull the existing instances out, wipe the
                // collection, then add the new Taking snapshot and
                // re-append the Done bucket at the end.
                var preservedDone = ActiveStudents.Where(s => s.IsDone).ToList();

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

                    // Done students live in the Done bucket — even if
                    // the server transiently reports them as Joined
                    // again (e.g., a race where the participant row
                    // hasn't flipped yet), skip the Taking add and
                    // let the preserved-Done loop below own the row.
                    if (_doneStudentIds.Contains(p.StudentId))
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

                    // Carry forward raised-hand state from the
                    // persistence sets so the 4 s refresh doesn't wipe
                    // the Approve/Deny/Lower Hand controls between hub
                    // events.
                    bool isHandRaisePending = _pendingHandRaiseRequests.ContainsKey(p.StudentId);
                    bool isHandRaiseActive  = _activeHandRaiseStudentIds.Contains(p.StudentId);

                    // Carry forward pending-Done state. Without this,
                    // a student who clicked Done would briefly bounce
                    // back into the Taking tab whenever the 4 s
                    // participant snapshot lands, until the next hub
                    // event re-asserted the flag.
                    bool isDoneRequested = _doneRequestedStudentIds.Contains(p.StudentId);

                    // Seed ViolationCount from the historical aggregate
                    // (populated by LoadHistoricalLogsAsync on rejoin).
                    // Without this the periodic refresh would build the
                    // row with ViolationCount=0 and clobber the historical
                    // total every 4 seconds.
                    int seededViolationCount = _initialViolationCountsByStudentId.TryGetValue(p.StudentId, out var hist)
                        ? hist
                        : 0;

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
                        ViolationCount = seededViolationCount,
                        HasViolation = seededViolationCount > 0 || _studentsWithViolations.Contains(p.StudentId),
                        IsLeaveRequested = isLeaveRequested,
                        IsDoneRequested = isDoneRequested,
                        IsHandRaisePending = isHandRaisePending,
                        IsHandRaiseActive  = isHandRaiseActive,
                        IsOffline = isDisconnected,
                        Status = statusText,
                        StatusColor = statusColor
                    });
                }

                // 1b. Surface DISCONNECTED participants in the dedicated
                //     Disconnected cohort tab. The previous QA rule
                //     ("disconnected students disappear from the list")
                //     left the instructor with no easy way to see who
                //     had dropped — now they appear under their own
                //     red-coloured tab with IsDisconnected=true so the
                //     filter predicate and SectionSortOrder route them
                //     correctly. They are NOT added to Taking/Done/
                //     Finished because IsDisconnected outranks those
                //     buckets in ParticipantFilterPredicate.
                foreach (var p in participants.Where(p =>
                    string.Equals(p.ParticipationStatus, "Disconnected", StringComparison.OrdinalIgnoreCase)
                    && !_safelyLeftStudentIds.Contains(p.StudentId)
                    && !_permanentlyDismissedStudents.Contains(p.StudentId)
                    && !_doneStudentIds.Contains(p.StudentId)))
                {
                    // Avoid duplicates if the row is already present
                    // (e.g., a brief race where it was added in the
                    // previous block before its status flipped).
                    if (ActiveStudents.Any(s => s.StudentId == p.StudentId))
                        continue;

                    // Same historical-seed treatment as the Connected
                    // cohort above — a disconnected student may still
                    // have prior violations the teacher needs to see.
                    int seededDisconnectedViolations = _initialViolationCountsByStudentId.TryGetValue(p.StudentId, out var histDisc)
                        ? histDisc
                        : 0;

                    ActiveStudents.Add(new LiveStudentStatus
                    {
                        StudentId = p.StudentId,
                        Name  = string.IsNullOrWhiteSpace(p.StudentName) ? p.StudentEmail : p.StudentName,
                        Email = p.StudentEmail,
                        ProfileImageUrl = string.IsNullOrWhiteSpace(p.ProfileImageUrl)
                            ? string.Empty
                            : (p.ProfileImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                ? p.ProfileImageUrl
                                : $"{ApiEndpoints.BaseUrl}{p.ProfileImageUrl}"),
                        ViolationCount = seededDisconnectedViolations,
                        HasViolation   = seededDisconnectedViolations > 0 || _studentsWithViolations.Contains(p.StudentId),
                        IsDisconnected = true,
                        IsOffline      = true,
                        Status         = "Disconnected",
                        StatusColor    = "#D32F2F"
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

                // 3. Stitch raised-hand state back in for any student
                //    whose request arrived before the next /participants
                //    snapshot landed (or whose tile flags were lost
                //    because the row was rebuilt). Same shape as the
                //    pending-join re-stitch above.
                foreach (var pending in _pendingHandRaiseRequests.Values)
                {
                    var existing = ActiveStudents.FirstOrDefault(s => s.StudentId == pending.StudentId);
                    if (existing == null)
                    {
                        ActiveStudents.Add(new LiveStudentStatus
                        {
                            StudentId = pending.StudentId,
                            Name = string.IsNullOrWhiteSpace(pending.StudentName) ? pending.StudentEmail : pending.StudentName,
                            Email = pending.StudentEmail ?? string.Empty,
                            Status = "Hand Raised",
                            StatusColor = "#1565C0",
                            IsHandRaisePending = true
                        });
                    }
                    else
                    {
                        existing.IsHandRaisePending = true;
                    }
                }
                foreach (var activeId in _activeHandRaiseStudentIds)
                {
                    var existing = ActiveStudents.FirstOrDefault(s => s.StudentId == activeId);
                    if (existing != null)
                        existing.IsHandRaiseActive = true;
                }

                // 4. Restore the Done bucket. Preserved instances are
                //    re-added intact so their accumulated state
                //    (ViolationCount, HasViolation, hardware flags,
                //    profile image) survives the refresh. If a Done id
                //    isn't in the preserved set (e.g., the IMC was just
                //    reopened mid-session and we've never seen them
                //    live), skip — we don't have a snapshot to render
                //    a row from and the next StudentLeftSession or the
                //    session-archive view will surface them properly.
                foreach (var done in preservedDone)
                {
                    if (!_doneStudentIds.Contains(done.StudentId))
                        continue;
                    if (ActiveStudents.Any(s => s.StudentId == done.StudentId))
                        continue;
                    done.IsDone = true;
                    ActiveStudents.Add(done);
                }

                _studentsView.Refresh();
                UpdateParticipantCount();
            }
            catch (Exception ex)
            {
                // Diagnostic — silent swallow was hiding the original
                // hand-raise issue; surface it to the VS Output window
                // without breaking the periodic poll.
                Console.WriteLine($"[Participants] LoadParticipantsFromServerAsync failed: {ex.Message}");
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
                if (totalRiskScore >= 50) riskText = "POSSIBLE DISHONESTY";
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
                if (totalRiskScore >= 50) riskText = "POSSIBLE DISHONESTY";
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

        // Opens the interactive tutorial window. Non-modal so the
        // instructor can keep glancing at the live console behind it;
        // Owner=this binds Z-order and ensures the tutorial closes
        // automatically when the console closes. The tutorial window
        // performs no monitoring actions — it's a static clickable
        // replica that only updates its own info panel.
        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            var help = new InstructorMonitoringHelpWindow { Owner = this };
            help.Show();
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
            // Session ended → pill reverts to NOT ACTIVE + DisabledTimerPanel.
            SetMonitoringPanelState(isActive: false);
            if (FindName("TxtStartStopLabel") is TextBlock startStopLabel)
            {
                startStopLabel.Text = "Session Ended";
                BtnStartMonitoring.IsEnabled = false;
            }
            if (FindName("StartStopIcon") is PackIcon startStopIcon) startStopIcon.Kind = PackIconKind.CheckCircle;
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

        private void GridSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {

        }
    }

    public class LiveStudentStatus : INotifyPropertyChanged
    {
        public int StudentId { get; set; }
        public string Name { get; set; }
        private string _status, _statusColor;
        private int _violations;
        private bool _isLeaveRequested;
        private bool _isJoinApprovalPending;
        // Drives the "✋ WANTS TO ASK" tile label and the Allow Q&A /
        // Deny buttons. Set true when the server broadcasts
        // HandRaiseRequested; cleared by HandRaiseResolved.
        private bool _isHandRaisePending;
        // Drives the "✋ HAND RAISED — Q&A ACTIVE" tile label and the
        // Lower Hand button. Set when HandRaiseResolved arrives with
        // decision="Approved"; cleared by HandLowered.
        private bool _isHandRaiseActive;
        // True after the student's Done request has been approved by
        // the instructor (StudentLeftSession broadcast). Drives the
        // Section grouping below so finished students show up under
        // the "Done" header instead of staying mixed with the active
        // Taking list. Once set, this row stops emitting violation
        // / hand-raise / leave-request UI affordances because all
        // those interaction flows assume an active monitored student.
        private bool _isDone;

        // True from the moment the student clicks Done in the SAC
        // (SessionCompletionRequested hub event) until the teacher
        // either Approves (IsDoneRequested→false, IsDone→true) or
        // Denies (IsDoneRequested→false, IsDone stays false). While
        // this is true the row lives in the Done tab as a PENDING
        // sub-state — the existing Approve/Deny buttons (bound to
        // IsLeaveRequested, set alongside) and the WANTS TO FINISH
        // label remain visible. Distinct from IsDone so that
        // approved-and-completed vs awaiting-approval can be styled
        // independently and so the filter predicate can include
        // both in the Done tab without confusing them at the row
        // level.
        private bool _isDoneRequested;
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

        // True when this row represents a participant whose server-side
        // ConnectionStatus is "Disconnected" — surfaced under the
        // dedicated "Disconnected" cohort tab in the IMC. Distinct from
        // IsOffline (which was the old "no longer in participant list"
        // flag) because we now keep disconnected students IN the list
        // so the instructor can track them.
        private bool _isDisconnected;
        public bool IsDisconnected
        {
            get => _isDisconnected;
            set
            {
                if (_isDisconnected == value) return;
                _isDisconnected = value;
                OnPropertyChanged();
                // Cohort buckets are computed from IsDisconnected so any
                // change must repaint Section + SectionSortOrder for the
                // CollectionView to re-sort on the next Refresh().
                OnPropertyChanged(nameof(Section));
                OnPropertyChanged(nameof(SectionSortOrder));
            }
        }
        public bool IsHandRaisePending { get => _isHandRaisePending; set { _isHandRaisePending = value; OnPropertyChanged(); } }
        public bool IsHandRaiseActive { get => _isHandRaiseActive; set { _isHandRaiseActive = value; OnPropertyChanged(); } }
        public bool IsDone
        {
            get => _isDone;
            set
            {
                if (_isDone == value) return;
                _isDone = value;
                OnPropertyChanged();
                // Section and SectionSortOrder are derived from BOTH
                // IsDone and IsDoneRequested; notify both so the
                // CollectionView re-sorts immediately on the next
                // Refresh().
                OnPropertyChanged(nameof(Section));
                OnPropertyChanged(nameof(SectionSortOrder));
            }
        }
        public bool IsDoneRequested
        {
            get => _isDoneRequested;
            set
            {
                if (_isDoneRequested == value) return;
                _isDoneRequested = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Section));
                OnPropertyChanged(nameof(SectionSortOrder));
            }
        }
        // Section / SectionSortOrder now split into four: Taking
        // (active), Done (pending approval), Finished (approved),
        // Disconnected (offline mid-session). Disconnected takes
        // precedence — same rule as ParticipantFilterPredicate.
        public string Section =>
            _isDisconnected   ? "Disconnected"
          : _isDone           ? "Finished"
          : _isDoneRequested  ? "Done"
          :                     "Taking";
        public int SectionSortOrder =>
            _isDisconnected   ? 3
          : _isDone           ? 2
          : _isDoneRequested  ? 1
          :                     0;
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

    // Strongly-typed payload for the HandRaiseRequested hub broadcast.
    // Mirrors the anonymous object the server emits in
    // MonitoringHub.RaiseHand. Using a concrete class (rather than
    // `dynamic`) is what makes SignalR's System.Text.Json deserializer
    // actually populate the fields — the dynamic path returns
    // JsonElement, on which `(int)payload.studentId` throws and gets
    // swallowed by the handler's try/catch, hiding the request from
    // the participant list entirely.
    public class HandRaiseRequestDto
    {
        public int RoomId { get; set; }
        public int StudentId { get; set; }
        public string StudentName { get; set; } = string.Empty;
        public string StudentEmail { get; set; } = string.Empty;
        public DateTime RequestedAt { get; set; }
    }

    public class HandRaiseResolvedDto
    {
        public int RoomId { get; set; }
        public int StudentId { get; set; }
        public string Decision { get; set; } = string.Empty;
    }

    public class LogEntry
    {
        public string Timestamp { get; set; }
        // The visible identity label rendered in the Global Log Feed.
        // For student-attributed entries this is the student's
        // FullName resolved from ActiveStudents / _allParticipants;
        // for SYSTEM entries it's the literal "SYSTEM". Email is no
        // longer exposed in the feed by design — see
        // LiveSessionMonitoringWindow.ResolveLogDisplayLabel.
        public string StudentLabel { get; set; }
        // Retained as a hidden field so future detail panels / hover
        // tooltips can still read the canonical email if needed.
        public string StudentEmail { get; set; }
        public string BadgeText { get; set; }
        public string BadgeColor { get; set; }
        public string Message { get; set; }
    }

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