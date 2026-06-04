using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.Models;
using AcademicSentinel.Server.DTOs;
using AcademicSentinel.Server.Services;
using System.Security.Claims;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace AcademicSentinel.Server.Hubs;

[Authorize] // 1. Secure the Hub so only logged-in apps can connect!
public class MonitoringHub : Hub
{
    private readonly AppDbContext _context;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MonitoringHub> _logger;
    private static readonly ConcurrentDictionary<int, bool> MonitoringStates = new();

    // Heartbeat-tracking map: ConnectionId → live student state.
    //
    // Why this exists: SignalR's OnDisconnectedAsync is unreliable for
    // abrupt drops — Task Manager kill, internet loss, or power loss
    // either delay the callback by ~30s (default ClientTimeoutInterval)
    // or never fire it at all if the transport hiccups. The
    // DisconnectSweeperService runs every 5s, scans this map, and any
    // entry whose LastBeat is older than the heartbeat-timeout threshold
    // is treated as a disconnect — guaranteed detection within ~15s.
    //
    // SAC clients call Heartbeat(roomId) every 5s; that call updates
    // LastBeat. JoinLiveExam populates the entry; OnDisconnectedAsync
    // and the sweeper both drain entries.
    //
    // The map is exposed `internal` so DisconnectSweeperService (same
    // assembly) can read it without a DI shuttle.
    internal sealed class ActiveStudentConnection
    {
        public int StudentId { get; init; }
        public int RoomId { get; init; }
        public DateTime LastBeat { get; set; }
    }
    internal static readonly ConcurrentDictionary<string, ActiveStudentConnection> _activeStudentConnections = new();

    // Per-room flag set when the instructor's IMC connection drops without
    // calling End Session. This is the single source of truth for the
    // dashboard's "Monitoring Session In Progress" banner and the
    // course-tile "IN PROGRESS" pill — completely independent of student
    // state, ExamSession.Status, or room.Status. The flag is cleared when:
    //   * The instructor calls JoinRoom (they came back).
    //   * EndExamSession (RoomsController) runs (clean close).
    internal static readonly ConcurrentDictionary<int, bool> _roomsWithDisconnectedInstructor = new();

    // ============================================================
    // INSTRUCTOR PRESENCE + HEARTBEAT (fast disconnect detection).
    // ============================================================
    // Mirrors the student heartbeat model so a teacher drop is detected just
    // as fast (~10s) instead of waiting on SignalR's transport timeout
    // (~20-30s). The IMC invokes TeacherHeartbeat(roomId) every 3s; the
    // DisconnectSweeperService scans this map every 2s and treats any entry
    // whose LastBeat is older than TeacherHeartbeatTimeout as a real drop,
    // flagging the room + broadcasting TeacherDisconnected. JoinRoom populates
    // and refreshes the entry; OnDisconnectedAsync drains it.
    internal sealed class ActiveInstructorConnection
    {
        public int RoomId { get; init; }
        public DateTime LastBeat { get; set; }
    }
    internal static readonly ConcurrentDictionary<string, ActiveInstructorConnection> _activeInstructorConnections = new();

    // Absence threshold. The 3s heartbeat + 8s timeout tolerates ~2 missed
    // beats (jitter) before flagging, and doubles as the debounce: a blip
    // shorter than this never surfaces, and a reconnect refreshes LastBeat.
    internal static readonly TimeSpan TeacherHeartbeatTimeout = TimeSpan.FromSeconds(8);

    // ============================================================
    // RAISED-HAND STATE (process-local; resets on server restart).
    // ============================================================
    // Key   = studentId
    // Value = roomId for which the raise-hand is currently approved.
    //
    // Presence means: the instructor has explicitly approved this
    // student's "raise hand" request, granting them a temporary
    // exception to alt-tab into approved meeting apps (Teams/Zoom/
    // Meet) without generating behavioural violations.
    //
    // The map is consulted by SendMonitoringEvent: events of types
    // listed in _handRaiseSuppressedEventTypes are dropped while a
    // student's entry is present. Hardware/screenshot/clipboard
    // detections remain enforced because they are not affected by
    // a Q&A context switch.
    //
    // Entries are added by ApproveRaiseHand, removed by LowerHand,
    // ForceLowerHand, and any session-ending path. Drained when the
    // student disconnects (OnDisconnectedAsync) so a re-join cannot
    // inherit a stale exception.
    internal static readonly ConcurrentDictionary<int, int> _raisedHandActive = new();

    private static readonly HashSet<string> _handRaiseSuppressedEventTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ALT_TAB",
            "WINDOW_SWITCH",
            "FOCUS_LOST",
            "RTFM",
            "IDLE",
            "INACTIVITY",
            "PROCESS_DETECTED"
        };

    // ============================================================
    // MONITORING-EVENT COALESCING (server intake dedup).
    // ============================================================
    // Final cross-path safety net: any duplicate emission that
    // reaches the hub for the same (studentId, eventType,
    // description) inside a short window is dropped silently —
    // neither persisted nor broadcast. This catches:
    //   • Title-mutation storms inside a browser (e.g., Facebook
    //     updating its tab title several times per second while
    //     content loads) that the SAC's per-type 2-second client
    //     dedup can race past.
    //   • Multiple foreground-hook callbacks for what is logically
    //     a single user switch.
    //   • Any future channel (REST violations endpoint, a queued
    //     replay, a retried InvokeAsync) that emits the same payload.
    //
    // Window is intentionally short (1 s) so genuinely separate
    // user actions — e.g., the student switching to Facebook
    // three times across 10 seconds — each pass through and each
    // produce their own entry. The key includes Description so
    // switches to DIFFERENT targets (Facebook then Twitter then
    // Facebook again) are never collapsed together.
    private const int MonitoringEventCoalesceWindowSeconds = 1;
    private static readonly ConcurrentDictionary<string, DateTime> _recentMonitoringEvents = new();

    // Discrete per-action events that MUST bypass the coalesce gate.
    // Every occurrence is an independent, audit-worthy student action
    // (one Ctrl+V keystroke, one PrintScreen press, one right-click).
    // The SAC emits these via the low-level keyboard / mouse hook path
    // with deliberately zero client-side dedup, so applying the
    // server-side 1-second window collapses rapid taps into a single
    // logged event — the exact symptom the user reported: "Ctrl+V is
    // not working every time I do the keystroke". The original
    // coalesce filter was designed for browser title-mutation storms
    // on WINDOW_SWITCH / focus events; it must not swallow keystrokes.
    private static readonly HashSet<string> _discreteActionEventTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CLIPBOARD_PASTE",
            "CLIPBOARD_COPY",
            "PASTE",
            "COPY",
            "PRINTSCREEN",
            "SNIP_TOOL",
            "SCREENSHOT",
            "RIGHT_CLICK_CONTEXT"
        };

    private readonly DisconnectService _disconnectService;

    public MonitoringHub(
        AppDbContext context,
        IServiceScopeFactory scopeFactory,
        ILogger<MonitoringHub> logger,
        DisconnectService disconnectService)
    {
        _context = context;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _disconnectService = disconnectService;
    }

    // =======================================================
    // IMC (TEACHER) CALLS THIS TO LISTEN FOR ALERTS
    // =======================================================
    /// <summary>
    /// SAC clients invoke this every ~5 seconds to prove they're alive.
    /// The handler updates the per-connection LastBeat timestamp in
    /// <see cref="_activeStudentConnections"/>; the DisconnectSweeperService
    /// runs in the background and any entry whose LastBeat is stale is
    /// treated as a disconnect. This guarantees detection within ~15s for
    /// abrupt drops (force-close, no internet, power loss) regardless of
    /// SignalR's transport-level heartbeat behavior.
    /// </summary>
    public Task Heartbeat(int roomId)
    {
        if (_activeStudentConnections.TryGetValue(Context.ConnectionId, out var conn))
        {
            conn.LastBeat = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public async Task JoinRoom(string roomId)
    {
        // Adds the teacher to the SignalR group for this specific exam
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

        // Clear any "instructor dropped" flag for this room — they're back
        // in session. TryRemove returns TRUE only if the flag actually
        // existed, i.e. a REAL instructor disconnect had been recorded.
        // Capture that so we only announce a reconnect when one genuinely
        // happened.
        var roleNow = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        bool wasFlaggedDisconnected = false;
        if (string.Equals(roleNow, "Instructor", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(roomId, out int rId))
        {
            // Register / refresh this live instructor connection (seeds the
            // first heartbeat so the sweeper doesn't immediately flag it).
            _activeInstructorConnections[Context.ConnectionId] = new ActiveInstructorConnection
            {
                RoomId = rId,
                LastBeat = DateTime.UtcNow
            };

            wasFlaggedDisconnected = _roomsWithDisconnectedInstructor.TryRemove(rId, out _);
        }

        // Broadcast TeacherReconnected ONLY when the instructor had
        // genuinely been flagged disconnected. JoinRoom is called on EVERY
        // routine connect — IMC open (Window_Loaded), monitoring start, and
        // SignalR auto-reconnect re-join — so broadcasting unconditionally
        // told students "Instructor reconnected to the session" (and
        // flickered the banner) even though the instructor never dropped.
        // Gating on wasFlaggedDisconnected makes the notification truthful:
        // no prior TeacherDisconnected → no TeacherReconnected.
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (wasFlaggedDisconnected
            && string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(roomId, out int parsedRoomId))
        {
            var room = await _context.Rooms.FindAsync(parsedRoomId);
            if (room != null
                && string.Equals(room.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "JoinRoom: instructor genuinely reconnected to active room {RoomId} — broadcasting TeacherReconnected.",
                    parsedRoomId);

                _context.MonitoringEvents.Add(new MonitoringEvent
                {
                    RoomId = parsedRoomId,
                    StudentId = 0,
                    EventType = "TEACHER_RECONNECTED",
                    Description = "Instructor reconnected to the active session.",
                    SeverityScore = 0,
                    Timestamp = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();

                await Clients.Group(roomId).SendAsync("TeacherReconnected", parsedRoomId);
            }
        }
    }

    // IMC invokes this every ~3s while in the room. Refreshes LastBeat so the
    // DisconnectSweeperService can tell a live instructor from a dropped one
    // (mirrors the student Heartbeat). Frequent invocation also keeps SignalR's
    // server-side client timeout from firing on a healthy teacher.
    public Task TeacherHeartbeat(int roomId)
    {
        if (_activeInstructorConnections.TryGetValue(Context.ConnectionId, out var conn))
        {
            conn.LastBeat = DateTime.UtcNow;
        }
        else
        {
            // First beat before a JoinRoom registration (or after a reconnect
            // race) — register it so liveness is tracked immediately.
            var roleNow = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
            if (string.Equals(roleNow, "Instructor", StringComparison.OrdinalIgnoreCase))
            {
                _activeInstructorConnections[Context.ConnectionId] = new ActiveInstructorConnection
                {
                    RoomId = roomId,
                    LastBeat = DateTime.UtcNow
                };
            }
        }
        return Task.CompletedTask;
    }

    // Bug fix: Bug2
    public async Task<bool> GetMonitoringState(int roomId)
    {
        var room = await _context.Rooms.FindAsync(roomId);
        return room != null && room.IsMonitoringActive;
    }

    public async Task SetMonitoringState(int roomId, bool isActive)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        // 1. Update In-Memory Dictionary
        MonitoringStates[roomId] = isActive;

        // Bug fix: Bug3
        // 2. UPDATE THE DATABASE FOR THE GATEKEEPER!
        var room = await _context.Rooms.FindAsync(roomId);
        if (room != null)
        {
            room.IsMonitoringActive = isActive;
            await _context.SaveChangesAsync();
        }

        await Clients.Group(roomId.ToString()).SendAsync("MonitoringStateChanged", isActive);
        await Clients.Group(roomId.ToString()).SendAsync(
            "SessionStatusChanged",
            isActive ? "Active" : "Pending");
    }

    public async Task PauseSessionMonitoring(int roomId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        MonitoringStates[roomId] = false;

        // Bug fix: Bug3
        // OPEN THE GATE (Optional, but aligns with paused state)
        var room = await _context.Rooms.FindAsync(roomId);
        if (room != null)
        {
            room.IsMonitoringActive = false;
            await _context.SaveChangesAsync();
        }

        await Clients.Group(roomId.ToString()).SendAsync("MonitoringPaused");
        await Clients.Group(roomId.ToString()).SendAsync("SessionStatusChanged", "Paused");
    }

    public async Task ResumeSessionMonitoring(int roomId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        MonitoringStates[roomId] = true;

        // Bug fix: Bug3
        // LOCK THE GATE
        var room = await _context.Rooms.FindAsync(roomId);
        if (room != null)
        {
            room.IsMonitoringActive = true;
            await _context.SaveChangesAsync();
        }

        await Clients.Group(roomId.ToString()).SendAsync("MonitoringResumed");
        await Clients.Group(roomId.ToString()).SendAsync("SessionStatusChanged", "Active");
    }

    public async Task BeginMonitoringCountdown(int roomId, int delaySeconds, int monitoringDurationSeconds)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        MonitoringStates[roomId] = false;

        // Bug fix — keep room.IsMonitoringActive in lockstep with the in-memory
        // MonitoringStates dict so RequestJoinSession (REST) and GetMonitoringState
        // (hub) agree on whether monitoring is live. During countdown both must
        // be false; once the timer fires, both flip true.
        var room = await _context.Rooms.FindAsync(roomId);
        if (room != null)
        {
            room.IsMonitoringActive = false;
            await _context.SaveChangesAsync();
        }

        // Spec v3/v4/v5 — `SessionCountdownStarted` is the spec name for
        // the initial countdown signal. `SessionStatusChanged` carries the
        // current room status (Pending/Countdown/Active/Ended).
        await Clients.Group(roomId.ToString()).SendAsync("SessionCountdownStarted", delaySeconds, monitoringDurationSeconds);
        await Clients.Group(roomId.ToString()).SendAsync("SessionStatusChanged", "Countdown");

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, delaySeconds)));
            MonitoringStates[roomId] = true;

            // Persist the active flag in the DB so the join gate and the
            // SAC's GetMonitoringState see the same truth as the in-memory
            // dictionary.
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var liveRoom = await db.Rooms.FindAsync(roomId);
                if (liveRoom != null)
                {
                    liveRoom.IsMonitoringActive = true;
                    await db.SaveChangesAsync();
                }
            }

            // Discrete `SessionStarted` event (spec) + `SessionStatusChanged`
            // status broadcast + `MonitoringStateChanged(true)` for SAC's
            // pause/resume state machine (extra, beyond spec).
            await Clients.Group(roomId.ToString()).SendAsync("SessionStarted");
            await Clients.Group(roomId.ToString()).SendAsync("SessionStatusChanged", "Active");
            await Clients.Group(roomId.ToString()).SendAsync("MonitoringStateChanged", true);
        });
    }
    // 1
    public async Task EndSessionOnDisconnect(int roomId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        // Canonical rule: latest session by StartTime is the truth.
        // Room.Status is only a cache and must not be used as the gate.
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return;

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        if (activeSession == null
            || !string.Equals(activeSession.Status, "Active", StringComparison.OrdinalIgnoreCase))
            return;

        activeSession.Status = "Completed";
        activeSession.EndTime = DateTime.UtcNow;
        room.Status = "Pending";

        MonitoringStates[roomId] = false;
        await _context.SaveChangesAsync();

        await Clients.Group(roomId.ToString()).SendAsync("MonitoringStateChanged", false);
        await Clients.Group(roomId.ToString()).SendAsync("SessionEnded");
        await Clients.Group(roomId.ToString()).SendAsync("SessionStatusChanged", "Ended");
    }

    // SAC calls this when the student enters the active exam room
    public async Task JoinLiveExam(int roomId)
    {
        try
        {
            var userIdString = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (userIdString == null)
            {
                await Clients.Caller.SendAsync("JoinFailed", "User identity not found.");
                return;
            }

            int studentId = int.Parse(userIdString);

            var studentUser = await _context.Users.FindAsync(studentId);
            if (studentUser == null)
            {
                await Clients.Caller.SendAsync("JoinFailed", "Student record not found in database.");
                return;
            }

            // Canonical rule — latest session by StartTime is truth.
            // Room.Status is a stale cache; never gate on it alone.
            var room = await _context.Rooms.FindAsync(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("JoinFailed", "Cannot join room: room not found.");
                return;
            }

            var activeSession = await _context.ExamSessions
                .Where(s => s.RoomId == roomId)
                .OrderByDescending(s => s.StartTime)
                .FirstOrDefaultAsync();

            bool latestIsActive = activeSession != null
                && string.Equals(activeSession.Status, "Active", StringComparison.OrdinalIgnoreCase);

            if (!latestIsActive)
            {
                await Clients.Caller.SendAsync("JoinFailed", "Cannot join room: session is inactive.");
                return;
            }

            // From here on, activeSession is the latest Active session.
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString());

            // BUG A defense-in-depth — refuse hub re-entry after the student
            // has already had their Done request approved (LEAVE_GRANTED).
            // RequestJoinSession's HTTP gate is the primary block, but a
            // student that bypasses the REST call cannot also slip past the
            // hub. Limit to the current active session.
            if (activeSession != null)
            {
                bool examAlreadyCompleted = await _context.MonitoringEvents.AnyAsync(e =>
                    e.RoomId == roomId
                    && e.StudentId == studentId
                    && e.EventType == "LEAVE_GRANTED"
                    && e.Timestamp >= activeSession.StartTime);
                if (examAlreadyCompleted)
                {
                    await Clients.Caller.SendAsync("JoinFailed",
                        "You have already completed this exam. Rejoining is not allowed.");
                    return;
                }
            }

            var participant = await _context.SessionParticipants
                .Where(p => p.RoomId == roomId && p.StudentId == studentId && (activeSession == null || p.JoinedAt >= activeSession.StartTime))
                .OrderByDescending(p => p.JoinedAt)
                .FirstOrDefaultAsync();

            // ============================================================
            // STALE "Completed" NORMALIZATION (disconnect-rejoin fix).
            //
            // The canonical "exam finished" truth source is the
            // LEAVE_GRANTED audit event, already enforced by the gate
            // above. If we reach this point, no LEAVE_GRANTED exists for
            // this active session — so a participant row marked
            // ConnectionStatus="Completed" here is a stale write left
            // over from a prior teardown, NOT a legitimate completion.
            //
            // Previously this branch hard-rejected the rejoin with
            // "You have already completed and exited this active session.",
            // which falsely failed legitimate disconnect→manual-rejoin
            // flows. Heal the row to "Disconnected" instead so the
            // existing disconnect-rejoin pipeline (REJOIN_REQ →
            // instructor approval → REJOIN_APPROVED) handles it
            // cleanly.
            // ============================================================
            if (participant != null
                && string.Equals(participant.ConnectionStatus, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "JoinLiveExam: normalizing stale Completed participant for studentId={StudentId} in room {RoomId} — no LEAVE_GRANTED present, routing through disconnect-rejoin gate.",
                    studentId, roomId);

                participant.ConnectionStatus = "Disconnected";
                participant.DisconnectedAt = DateTime.UtcNow;
                // Force the manual-rejoin path: clear any prior Approved
                // flag so the ForceDashboardReturn gate below fires and
                // the student lands in /request-join's REJOIN_REQ flow.
                if (!string.Equals(participant.JoinApprovalStatus, "Approved", StringComparison.OrdinalIgnoreCase))
                {
                    participant.JoinApprovalStatus = "Pending";
                }
                participant.IsCurrentlyActive = false;
                await _context.SaveChangesAsync();
            }

            _logger.LogInformation("JoinLiveExam: roomId={RoomId}, studentId={StudentId}, existingParticipant={Existing}, connectionStatus={ConnStatus}, approvalStatus={ApprovalStatus}",
                roomId, studentId, participant != null,
                participant?.ConnectionStatus ?? "(none)",
                participant?.JoinApprovalStatus ?? "(none)");

            // ============================================================
            // STALE-RECONNECT DETECTION (timing-race fix)
            //
            // SignalR's default ClientTimeoutInterval is 30s. If a student's
            // SAC is force-killed / loses network and the SAC's
            // WithAutomaticReconnect path reconnects faster than that, the
            // server's OnDisconnectedAsync for the OLD ConnectionId has not
            // fired yet — so the participant row still says "Connected" and
            // the rejoin gate below would be bypassed.
            //
            // Detection: another ConnectionId for the same student is still
            // in the active-connections map. That means a previous WS is
            // either dead or has been replaced by this new call — either
            // way, the student dropped at some point and is now reconnecting.
            //
            // Resolution: synthesize the disconnect we missed:
            //   - flip the participant to Disconnected
            //   - clear approval flags so the gate below fires
            //   - write the STUDENT_DISCONNECTED audit event
            //   - drain the stale map entry
            //   - broadcast Student(Connection)Disconnected so the IMC
            //     updates the participant tile to red and writes the
            //     Global Log Feed entry BEFORE the rejoin-approval card
            //     pops up.
            // ============================================================
            bool hasStaleConnection = false;
            var staleConnectionIds = new List<string>();
            foreach (var kv in _activeStudentConnections)
            {
                if (kv.Value.StudentId == studentId && kv.Key != Context.ConnectionId)
                {
                    hasStaleConnection = true;
                    staleConnectionIds.Add(kv.Key);
                }
            }

            if (hasStaleConnection && participant != null
                && !string.Equals(participant.ConnectionStatus, "Disconnected", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("JoinLiveExam: stale reconnect detected for studentId={StudentId} (prior connection(s)={Count}). Synthesizing disconnect before rejoin gate.",
                    studentId, staleConnectionIds.Count);

                participant.ConnectionStatus = "Disconnected";
                participant.DisconnectedAt = DateTime.UtcNow;
                // "Pending" (not null) — JoinApprovalStatus is NOT NULL in
                // Postgres. "Pending" also satisfies the rejoin gate below
                // (!= "Approved") and matches DisconnectService's contract,
                // so the kicked-style /request-join approval pipeline can
                // pick this up consistently on the next attempt.
                participant.JoinApprovalStatus = "Pending";
                participant.IsCurrentlyActive = false;

                _context.MonitoringEvents.Add(new MonitoringEvent
                {
                    RoomId = roomId,
                    StudentId = studentId,
                    EventType = "STUDENT_DISCONNECTED",
                    Description = "Student disconnected from session (detected on reconnect attempt).",
                    SeverityScore = 0,
                    Timestamp = DateTime.UtcNow
                });

                await _context.SaveChangesAsync();

                // Drain every stale map entry for this student so the late
                // OnDisconnectedAsync (fires when the old TCP finally
                // closes) won't corrupt the now-live participant state.
                foreach (var staleId in staleConnectionIds)
                    _activeStudentConnections.TryRemove(staleId, out _);

                // Notify the IMC immediately. Existing handlers update the
                // participant tile to red "Disconnected" and append the
                // non-violation log entry to the Global Log Feed.
                await Clients.Group(roomId.ToString()).SendAsync("StudentDisconnected", studentId);
                await Clients.Group(roomId.ToString()).SendAsync("StudentConnectionLost", studentId);
            }

            // ============================================================
            // BLOCK SILENT AUTO-REJOINS (deployment fix).
            //
            // SignalR's WithAutomaticReconnect() in the SAC silently
            // re-invokes JoinLiveExam after a transient network drop.
            // Allowing that path here resurrects the participant with no
            // instructor approval AND glitches the SAC UI (the SAC was
            // already mid-teardown when the auto-reconnect fired).
            //
            // Hard policy: any participant whose DB state is "Disconnected"
            // must NOT be re-admitted through the hub. Instead, push them
            // back to the Student Dashboard so they explicitly re-click the
            // course tile, hit the REST /request-join endpoint, and land in
            // the instructor's pending-approval queue.
            //
            // The previous in-hub rejoin-approval flow
            // (REJOIN_REQUESTED + AwaitingRejoinApproval + RejoinRequest
            // broadcast) is intentionally removed — it competed with the
            // dashboard REST flow and produced duplicate / out-of-order
            // approval cards on the IMC.
            // ============================================================
            if (participant != null
                && string.Equals(participant.ConnectionStatus, "Disconnected", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(participant.JoinApprovalStatus, "Approved", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "JoinLiveExam: blocked silent auto-reconnect for disconnected student {StudentId} in room {RoomId}. Forcing dashboard navigation.",
                    studentId, roomId);

                await Clients.Caller.SendAsync(
                    "ForceDashboardReturn",
                    "Connection lost. Please rejoin manually from the Student Dashboard.");
                return;
            }

            if (participant == null)
            {
                participant = new SessionParticipant
                {
                    RoomId = roomId,
                    StudentId = studentId,
                    ConnectionStatus = "Connected",
                    JoinedAt = DateTime.UtcNow
                };
                _context.SessionParticipants.Add(participant);
            }
            else
            {
                // Update existing record for Reconnection
                participant.ConnectionStatus = "Connected";
                participant.JoinedAt = DateTime.UtcNow;
                participant.DisconnectedAt = null;
            }

            _context.MonitoringEvents.Add(new MonitoringEvent
            {
                RoomId = roomId,
                StudentId = studentId,
                EventType = "SYSTEM",
                Description = $"✅ SESSION JOINED / CONNECTION RESTORED. ({studentUser.Email})",
                SeverityScore = 0,
                Timestamp = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            // Register this connection. The DisconnectSweeperService will
            // scan this map every 5s; if LastBeat falls behind, the entry
            // is treated as a disconnect even if SignalR's transport
            // detection never fires. SAC seeds the very first LastBeat
            // by hitting Heartbeat(roomId) on a 5s timer.
            _activeStudentConnections[Context.ConnectionId] = new ActiveStudentConnection
            {
                StudentId = studentId,
                RoomId = roomId,
                LastBeat = DateTime.UtcNow
            };

            await Clients.Group(roomId.ToString()).SendAsync("StudentJoined", studentId);
            string studentDisplayName = string.IsNullOrWhiteSpace(studentUser.FullName) ? studentUser.Email : studentUser.FullName;
            await Clients.Group(roomId.ToString()).SendAsync("StudentJoinedOrReconnected", studentId, studentDisplayName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JoinLiveExam FAILED for Room {roomId} / Student {Context.User?.Identity?.Name}: {ex.ToString()}");
            await Clients.Caller.SendAsync("JoinFailed", "An unexpected internal server error occurred while finalizing your join.");
        }
    }

    // SignalR AUTOMATICALLY triggers this if a user's app closes or internet drops
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;

        // Render-side diagnostic — confirm the handler fired, who dropped,
        // and which branch we routed into. Read these in Render's log
        // stream to verify deployments are picking up the new code.
        _logger.LogInformation("OnDisconnectedAsync fired. userId={UserId}, role={Role}, connId={ConnId}, exception={ExceptionType}",
            userIdString ?? "(null)", role ?? "(null)", Context.ConnectionId,
            exception?.GetType().Name ?? "(none)");

        // ROBUST INSTRUCTOR DETECTION.
        //
        // On an ABRUPT drop (internet loss, power loss, force-close) SignalR
        // often delivers OnDisconnectedAsync with an EMPTY Context.User — the
        // JWT claims aren't available — so gating purely on the Role claim
        // silently skipped the entire teacher-disconnect path. That is why a
        // real teacher internet drop produced NO student notification, NO
        // rejoin banner, and NO "IN PROGRESS" pill: the room was never flagged.
        //
        // Fallback: the JoinRoom-populated _activeInstructorConnections map
        // (ConnectionId → roomId) reliably identifies an instructor connection
        // regardless of claim availability — mirroring the student branch's
        // _activeStudentConnections fallback.
        bool connIsInstructor = _activeInstructorConnections.TryGetValue(Context.ConnectionId, out var mappedInstructorRoomId);
        bool roleIsInstructor = string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase);

        if (roleIsInstructor || connIsInstructor)
        {
            // Drain this connection from the presence map up-front.
            _activeInstructorConnections.TryRemove(Context.ConnectionId, out _);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            try
            {
                // Resolve the room. Prefer the connection map (reliable on
                // abrupt drops); fall back to the instructor's active room via
                // claims when available.
                Room activeRoom = null;
                if (connIsInstructor && mappedInstructorRoomId != null)
                {
                    activeRoom = await db.Rooms.FindAsync(mappedInstructorRoomId.RoomId);
                }
                if (activeRoom == null
                    && userIdString != null
                    && int.TryParse(userIdString, out var instructorId))
                {
                    activeRoom = await db.Rooms
                        .Where(r => r.InstructorId == instructorId && r.Status == "Active")
                        .OrderByDescending(r => r.Id)
                        .FirstOrDefaultAsync();
                }

                if (activeRoom != null)
                {
                    // CANONICAL TRUTH CHECK — verify against the LATEST
                    // ExamSession before flagging the room as "instructor
                    // disconnected". Room.Status can be stale during the
                    // race between EndExamSession's save and this handler
                    // firing; without this verification a clean End Session
                    // could be immediately followed by the disconnect
                    // handler re-flagging the room, making the dashboard
                    // banner reappear after a successful end.
                    var latestForCheck = await db.ExamSessions
                        .Where(s => s.RoomId == activeRoom.Id)
                        .OrderByDescending(s => s.StartTime)
                        .FirstOrDefaultAsync();
                    bool latestIsActive = latestForCheck != null
                        && string.Equals(latestForCheck.Status, "Active", StringComparison.OrdinalIgnoreCase);

                    // A session row is Status="Active" from the moment it is
                    // CREATED — before the teacher presses Start. A teacher
                    // socket drop in that pre-start window is NOT a
                    // "disconnected from an active monitoring session" event,
                    // so require room.IsMonitoringActive too. Without this,
                    // creating a session then dropping (or a transient blip on
                    // the freshly-opened, not-yet-started IMC) flagged the room
                    // and produced a false "Rejoin Session" banner while the
                    // IMC still showed NOT ACTIVE.
                    bool monitoringActuallyLive = latestIsActive && activeRoom.IsMonitoringActive;
                    if (!monitoringActuallyLive)
                    {
                        // Session ended, or monitoring never started. Don't
                        // flag the room — drain any leftover flag and bail.
                        _roomsWithDisconnectedInstructor.TryRemove(activeRoom.Id, out _);
                        await db.SaveChangesAsync();
                        await base.OnDisconnectedAsync(exception);
                        return;
                    }

                    // Hand off to the single master. HandleInstructorDisconnect
                    // is idempotent (TryAdd on the flag) and re-checks for a
                    // fresh teacher heartbeat, so it's safe even though the
                    // heartbeat sweeper is the PRIMARY fast detector (~10s).
                    // This OnDisconnectedAsync path is a backstop for a clean
                    // transport close. The connection was already removed from
                    // the presence map above, so the heartbeat check won't see
                    // this dying connection.
                    var disconnectService = scope.ServiceProvider.GetRequiredService<DisconnectService>();
                    await disconnectService.HandleInstructorDisconnectAsync(activeRoom.Id);
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                // best-effort instructor disconnect handling under concurrent drops
            }
        }
        else
        {
            // Resolve the dropped student.  JWT claim is preferred, but on
            // abrupt disconnects (Task Manager kill, network/power loss)
            // Context.User can be empty — the per-connection map populated
            // in JoinLiveExam is the reliable fallback.  The map also
            // carries the room the student joined into, so we extract
            // roomId here too and avoid a downstream DB lookup in the
            // common case.  Always drain the map entry afterwards so the
            // dictionary doesn't grow forever.
            int studentId = 0;
            int? roomIdFromMap = null;
            if (userIdString != null && int.TryParse(userIdString, out var parsedFromClaim))
            {
                studentId = parsedFromClaim;
            }
            if (_activeStudentConnections.TryGetValue(Context.ConnectionId, out var mapped))
            {
                if (studentId == 0)
                {
                    studentId = mapped.StudentId;
                    _logger.LogInformation("Disconnect: resolved studentId={StudentId} from ConnectionId map (claim was null).", studentId);
                }
                roomIdFromMap = mapped.RoomId;
            }
            _activeStudentConnections.TryRemove(Context.ConnectionId, out _);

            if (studentId == 0)
            {
                _logger.LogWarning("Disconnect: could not resolve studentId from claim OR connection map. connId={ConnId}", Context.ConnectionId);
                await base.OnDisconnectedAsync(exception);
                return;
            }

            // STALE-TIMEOUT GUARD (timing-race fix, companion to JoinLiveExam).
            //
            // If another live ConnectionId still exists in the map for the
            // same student, the student already reconnected with a fresh
            // socket.  This callback is the LATE timeout of the previously
            // dropped connection.  Marking the participant as Disconnected
            // here would corrupt the live state — skip silently.
            bool studentHasNewerConnection = false;
            foreach (var kv in _activeStudentConnections)
            {
                if (kv.Value.StudentId == studentId)
                {
                    studentHasNewerConnection = true;
                    break;
                }
            }
            if (studentHasNewerConnection)
            {
                _logger.LogInformation("Disconnect: ignoring stale timeout for studentId={StudentId} (already reconnected via a newer ConnectionId).", studentId);
                await base.OnDisconnectedAsync(exception);
                return;
            }

            // Drain any active raised-hand exception. If the student
            // dropped while their hand was raised, a re-join must NOT
            // inherit the suppression — the new connection has to
            // request approval again.
            if (_raisedHandActive.TryRemove(studentId, out var drainedRoomId))
            {
                await Clients.Group(drainedRoomId.ToString())
                    .SendAsync("HandLowered", studentId);
            }

            // =================================================================
            // ROUTE THROUGH DisconnectService — the single master.
            // =================================================================
            // The hub's only responsibility is to identify (studentId, roomId)
            // and hand off.  DisconnectService owns:
            //   • the 10-second in-memory idempotency window
            //   • the Load-Modify-Save UPDATE (JoinApprovalStatus preserved)
            //   • the atomic MonitoringEvent insert
            //   • the single SignalR broadcast (after commit)
            // A concurrent call from the sweeper for the same student is
            // short-circuited by DisconnectService's dedup map — no
            // duplicate row, no duplicate event, no duplicate broadcast.
            const string reason = "SignalR connection dropped unexpectedly.";

            if (roomIdFromMap.HasValue)
            {
                // Fast path: the connection map carried the roomId.
                await _disconnectService.HandleDisconnectAsync(
                    studentId, roomIdFromMap.Value, reason);
            }
            else
            {
                // Slow path: no map entry (claim-only disconnect, or the
                // map was drained earlier).  Find every room where this
                // student still has a non-terminal participant row, then
                // route each through DisconnectService.
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var roomsToHandle = await db.SessionParticipants
                    .Where(p => p.StudentId == studentId
                                && p.ConnectionStatus != "Completed"
                                && p.ConnectionStatus != "Disconnected")
                    .Select(p => p.RoomId)
                    .Distinct()
                    .ToListAsync();

                _logger.LogInformation(
                    "OnDisconnectedAsync: no map entry for studentId={StudentId}; routing {Count} rooms through DisconnectService.",
                    studentId, roomsToHandle.Count);

                foreach (var roomId in roomsToHandle)
                {
                    await _disconnectService.HandleDisconnectAsync(
                        studentId, roomId, reason);
                }
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task ReSyncState(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;

        int authenticatedStudentId = int.Parse(userIdString);
        if (authenticatedStudentId != studentId) return;

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null)
            return;

        // CANONICAL TRUTH — never gate on room.Status (a derived cache that
        // lags / flips to "Pending" during disconnect bookkeeping). Use the
        // latest ExamSession by StartTime, the same rule JoinLiveExam uses.
        var latestSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        bool sessionIsActive = latestSession != null
            && string.Equals(latestSession.Status, "Active", StringComparison.OrdinalIgnoreCase);

        // A GENUINE prior completion for THIS student in the current active
        // session — the only thing that legitimately means "you already
        // exited properly". Scoped to the active session so a grant from an
        // earlier session can't resurrect a completion here.
        bool leaveAlreadyGranted = latestSession != null && await _context.MonitoringEvents
            .AnyAsync(e => e.RoomId == roomId
                        && e.StudentId == studentId
                        && e.EventType == "LEAVE_GRANTED"
                        && e.Timestamp >= latestSession.StartTime);

        // ============================================================
        // PRECEDENCE (explicit, state-based — NOT timeout-based):
        //   1. Real completion (LEAVE_GRANTED) → re-assert LeaveGranted.
        //   2. Session genuinely ended → SessionEnded (clean teardown).
        //   3. Otherwise (still active, no completion) → DO NOTHING.
        // ============================================================
        // The previous code sent "LeaveGranted" whenever room.Status wasn't
        // "Active". On a reconnect that fired the SAC's proper-exit flow
        // (NotifyStudentLeftSafely → ConnectionStatus="Completed" +
        // StudentLeftSession → IMC "EXAM COMPLETED. Student exited
        // properly."), turning an unexpected disconnect into a false
        // completion. A disconnect must NEVER imply a proper exit.
        if (leaveAlreadyGranted)
        {
            // Student truly completed before dropping — restore that state.
            await Clients.Client(Context.ConnectionId).SendAsync("LeaveGranted", studentId);
            return;
        }

        if (!sessionIsActive)
        {
            // The session ended while the student was away. This is a
            // session-end, NOT a "you exited properly" completion — route
            // the SAC through its SessionEnded teardown (which does not
            // mark the participant Completed via the leave path).
            await Clients.Client(Context.ConnectionId).SendAsync("SessionEnded");
            return;
        }

        // Session still active and no legitimate completion on record: the
        // student remains disconnected / reconnect-eligible and recovers via
        // the normal dashboard rejoin-approval flow. Nothing to assert here.
    }

    /// <summary>
    /// SAC calls this to send monitoring events in real-time
    /// This method receives detected violations from the Secure Assessment Client
    /// and stores them in the database, then relays alerts to the Instructor Monitoring Console
    /// </summary>
    public async Task SendMonitoringEvent(int roomId, int studentId, MonitoringEventDto eventData)
    {
        // Extract the Student's ID from their JWT to verify they're sending their own data
        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;

        int authenticatedStudentId = int.Parse(userIdString);

        // Security check: Students can only report their own events
        if (authenticatedStudentId != studentId) return;

        var latestParticipantState = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .Select(p => p.ConnectionStatus)
            .FirstOrDefaultAsync();

        // BUG-FIX (deployment): block ghost telemetry from a SAC that
        // regained network before the student manually re-joined.
        // Previously only "Completed" was filtered, so a Disconnected
        // student whose SAC's WithAutomaticReconnect succeeded silently
        // could keep flooding the server with focus/alt-tab/process
        // events while the student was actually on the dashboard's
        // re-join screen — unfairly raising their risk score.
        // ConnectionStatus only flips back to "Connected" once the
        // instructor approves the rejoin via the REST /request-join
        // path → ApproveStudentJoin, so this is the canonical gate.
        if (string.Equals(latestParticipantState, "Completed",    StringComparison.OrdinalIgnoreCase) ||
            string.Equals(latestParticipantState, "Disconnected", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Verify the room exists
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return;

        // ============================================================
        // RAISED-HAND VIOLATION SUPPRESSION (defence-in-depth).
        // ============================================================
        // The SAC also gates these locally, but a tampered/older client
        // could still emit them. If this student currently holds an
        // instructor-approved raised hand for this room AND the event
        // type is one we explicitly allow during Q&A (alt-tab / focus
        // changes / approved-meeting-app process / idle), drop the
        // event silently and write a zero-score audit row so the
        // suppression itself is auditable.
        if (_raisedHandActive.TryGetValue(studentId, out var approvedRoomId)
            && approvedRoomId == roomId
            && _handRaiseSuppressedEventTypes.Contains(eventData.EventType ?? string.Empty))
        {
            _context.MonitoringEvents.Add(new MonitoringEvent
            {
                RoomId        = roomId,
                StudentId     = studentId,
                EventType     = "HAND_RAISED_SUPPRESSED",
                Description   = $"Suppressed '{eventData.EventType}' while raised hand is active.",
                SeverityScore = 0,
                Timestamp     = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
            return;
        }

        // Server-intake coalescing — see _recentMonitoringEvents docs
        // above. Composite key includes studentId, event type and
        // description so identical re-emits within the window are
        // dropped while switches to genuinely different targets
        // remain distinct entries.
        //
        // BYPASS for discrete per-action events (paste / copy /
        // screenshot keys / right-click). Each occurrence is an
        // independent student action and must be persisted + broadcast
        // individually — see _discreteActionEventTypes above.
        var nowUtc = DateTime.UtcNow;
        var incomingEventType = eventData.EventType ?? string.Empty;
        if (!_discreteActionEventTypes.Contains(incomingEventType))
        {
            var coalesceKey = string.Concat(
                studentId.ToString(),
                "|",
                incomingEventType,
                "|",
                eventData.Description ?? string.Empty);
            if (_recentMonitoringEvents.TryGetValue(coalesceKey, out var lastSeen)
                && (nowUtc - lastSeen).TotalSeconds < MonitoringEventCoalesceWindowSeconds)
            {
                // Refresh the timestamp so a rapid burst keeps the gate
                // closed for the entire duration of the burst rather than
                // letting a stale entry expire mid-storm.
                _recentMonitoringEvents[coalesceKey] = nowUtc;
                return;
            }
            _recentMonitoringEvents[coalesceKey] = nowUtc;

            // Opportunistic cleanup so the dictionary doesn't grow without
            // bound across a long session. Removes any entry older than a
            // generous multiple of the window.
            if (_recentMonitoringEvents.Count > 256)
            {
                var staleCutoff = nowUtc.AddSeconds(-MonitoringEventCoalesceWindowSeconds * 30);
                foreach (var kv in _recentMonitoringEvents)
                {
                    if (kv.Value < staleCutoff)
                        _recentMonitoringEvents.TryRemove(kv.Key, out _);
                }
            }
        }

        // Create the monitoring event record
        var monitoringEvent = new MonitoringEvent
        {
            RoomId = roomId,
            StudentId = studentId,
            EventType = eventData.EventType,
            Description = eventData.Description,
            SeverityScore = eventData.SeverityScore,
            Timestamp = nowUtc
        };

        _context.MonitoringEvents.Add(monitoringEvent);
        await _context.SaveChangesAsync();

        // Broadcast violation alert to the Instructor Monitoring Console.
        // Spec v4/v5 names this hub method `ReceiveViolationAlert`.
        await Clients.Group(roomId.ToString()).SendAsync("ReceiveViolationAlert", new
        {
            studentId = studentId,
            eventType = eventData.EventType,
            severityScore = eventData.SeverityScore,
            description = eventData.Description,
            timestamp = nowUtc
        });
    }

    public async Task UpdateHardwareState(int roomId, int studentId, bool isVm, bool isRemote)
    {
        await Clients.Group(roomId.ToString()).SendAsync("ReceiveHardwareStateUpdate", studentId, isVm, isRemote);
    }

    public async Task RequestLeave(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;

        int authenticatedStudentId = int.Parse(userIdString);
        if (authenticatedStudentId != studentId) return;

        var isParticipantInRoom = await _context.SessionParticipants
            .AnyAsync(p => p.RoomId == roomId && p.StudentId == studentId);

        if (!isParticipantInRoom)
            return;

        await Clients.Group(roomId.ToString()).SendAsync("LeaveRequested", studentId);
    }

    // Spec v4/v5 — Soft Lock "Done" button.
    // Student presses Done when their assessment is finished. The instructor
    // sees a DONE entry in the IMC feed; monitoring stays active until they
    // approve via GrantLeave, at which point the SAC auto-exits.
    //
    // Renamed per QA spec to `StudentFinishedExam`. The legacy name
    // `RequestSessionCompletion` is preserved as a thin alias for any client
    // build still using it.
    public Task RequestSessionCompletion(int roomId, int studentId) =>
        StudentFinishedExam(roomId, studentId);

    public async Task StudentFinishedExam(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;

        int authenticatedStudentId = int.Parse(userIdString);
        if (authenticatedStudentId != studentId) return;

        var isParticipantInRoom = await _context.SessionParticipants
            .AnyAsync(p => p.RoomId == roomId && p.StudentId == studentId);

        if (!isParticipantInRoom)
            return;

        // Persist as a SYSTEM monitoring event so it shows up in the room
        // history audit trail next to violations and join/leave events.
        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId = roomId,
            StudentId = studentId,
            EventType = "SESSION_COMPLETION_REQUESTED",
            Description = "Student pressed Done — assessment finished, awaiting instructor approval.",
            SeverityScore = 0,
            Timestamp = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Notify the room (instructor sees it in the live feed).
        await Clients.Group(roomId.ToString()).SendAsync("SessionCompletionRequested", studentId);
    }

    public async Task GrantLeave(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var isParticipantInRoom = await db.SessionParticipants
                .AnyAsync(p => p.RoomId == roomId && p.StudentId == studentId);

            if (!isParticipantInRoom)
                return;

            var leaveGrantedEvent = new MonitoringEvent
            {
                RoomId = roomId,
                StudentId = studentId,
                EventType = "LEAVE_GRANTED",
                SeverityScore = 0,
                Timestamp = DateTime.UtcNow
            };

            db.MonitoringEvents.Add(leaveGrantedEvent);

            var participant = await db.SessionParticipants
                .Where(p => p.RoomId == roomId && p.StudentId == studentId)
                .OrderByDescending(p => p.JoinedAt)
                .FirstOrDefaultAsync();

            if (participant != null)
            {
                participant.ConnectionStatus = "Disconnected";
                participant.DisconnectedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
        }

        // Spec rename: `LeaveApproved` is the new name the SAC listens for
        // and triggers its auto-exit-to-dashboard flow. Legacy `LeaveGranted`
        // kept for any older build still subscribed.
        await Clients.User(studentId.ToString()).SendAsync("LeaveApproved", studentId);
        await Clients.User(studentId.ToString()).SendAsync("LeaveGranted", studentId);
        await Clients.Group(roomId.ToString()).SendAsync("StudentLeftSession", studentId);
        await Clients.Group(roomId.ToString()).SendAsync("LeaveApprovalUpdated", studentId, true);
    }

    // Instructor denies a Done / RequestLeaveApproval request. The student's
    // SAC restores its Done button so they can request again later. No state
    // change other than an audit-trail entry — the room stays Active.
    public async Task DenyLeaveRequest(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        var instructorIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (instructorIdString == null || !int.TryParse(instructorIdString, out var instructorId))
            return;

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.InstructorId != instructorId)
            return;

        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId = roomId,
            StudentId = studentId,
            EventType = "LEAVE_REQUEST_DENIED",
            Description = "Instructor denied the student's Done request.",
            SeverityScore = 0,
            Timestamp = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Direct-to-student so only the requesting student sees the denial.
        await Clients.User(studentId.ToString()).SendAsync("LeaveRequestDenied", studentId);
    }

    public async Task NotifyStudentLeftSafely(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null)
            return;

        int authenticatedStudentId = int.Parse(userIdString);
        if (authenticatedStudentId != studentId)
            return;

        // DEFENSE-IN-DEPTH: "left safely" must correspond to a REAL approved
        // exit. The instructor's GrantLeave writes a LEAVE_GRANTED event
        // before it broadcasts LeaveApproved/LeaveGranted, so a legitimate
        // call always has one on record. Without this guard, any spurious
        // LeaveGranted reaching the SAC (e.g. a reconnect/resync path) would
        // flip the participant to "Completed" and broadcast StudentLeftSession,
        // turning an unexpected disconnect into a false "exited properly".
        // If no LEAVE_GRANTED exists, this is NOT a completion — no-op so the
        // participant stays in its disconnected / reconnect-eligible state.
        var latestSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        bool hasApprovedExit = latestSession != null && await _context.MonitoringEvents
            .AnyAsync(e => e.RoomId == roomId
                        && e.StudentId == studentId
                        && e.EventType == "LEAVE_GRANTED"
                        && e.Timestamp >= latestSession.StartTime);

        if (!hasApprovedExit)
        {
            _logger.LogWarning(
                "NotifyStudentLeftSafely: ignoring spurious 'left safely' for student {StudentId} in room {RoomId} — no LEAVE_GRANTED on record (not a real completion).",
                studentId, roomId);
            return;
        }

        var participant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();

        if (participant != null)
        {
            participant.ConnectionStatus = "Completed";
            participant.DisconnectedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        await Clients.Group(roomId.ToString()).SendAsync("StudentLeftSession", studentId);
    }

    // =======================================================
    // JOIN APPROVAL STATE MACHINE
    // =======================================================

    // SAC calls this after the request-join HTTP gate returns 202 Pending.
    // The hub re-validates against the DB so a malicious client cannot fake
    // a pending state, then surfaces the request to the IMC in real time.
    public async Task NotifyInstructorStudentPending(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;
        int authenticatedStudentId = int.Parse(userIdString);
        if (authenticatedStudentId != studentId) return;

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (activeSession == null) return;

        var participant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();

        if (participant == null
            || participant.JoinedAt < activeSession.StartTime
            || !string.Equals(participant.JoinApprovalStatus, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var hadLeaveGranted = await _context.MonitoringEvents
            .AnyAsync(e => e.RoomId == roomId
                        && e.StudentId == studentId
                        && e.EventType == "LEAVE_GRANTED"
                        && e.Timestamp >= activeSession.StartTime);

        var student = await _context.Users.FindAsync(studentId);

        await Clients.Group(roomId.ToString()).SendAsync("StudentPendingApproval", new
        {
            roomId,
            studentId,
            participantId = participant.Id,
            studentName = student?.FullName ?? $"Student #{studentId}",
            studentEmail = student?.Email,
            profileImageUrl = student?.ProfileImageUrl,
            isRejoin = hadLeaveGranted,
            isLate = !hadLeaveGranted,
            requestedAt = DateTime.UtcNow
        });
    }

    public async Task ApproveStudentJoin(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        var instructorIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (instructorIdString == null || !int.TryParse(instructorIdString, out var instructorId))
            return;

        // Canonical rule — latest session is truth. Room.Status is cache only.
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.InstructorId != instructorId) return;
        var latestForApprove = await _context.ExamSessions
            .Where(s => s.RoomId == roomId)
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (latestForApprove == null
            || !string.Equals(latestForApprove.Status, "Active", StringComparison.OrdinalIgnoreCase))
            return;

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (activeSession == null) return;

        var participant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();
        if (participant == null || participant.JoinedAt < activeSession.StartTime) return;

        participant.JoinApprovalStatus = "Approved";
        participant.IsCurrentlyActive = true;

        // REJOIN_APPROVED supersedes any prior LEAVE_GRANTED for this student
        // in this session, so the HTTP gate will allow clean reconnects after
        // network drops without re-prompting the instructor.
        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId = roomId,
            StudentId = studentId,
            EventType = "REJOIN_APPROVED",
            SeverityScore = 0,
            Timestamp = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        await Clients.User(studentId.ToString()).SendAsync("OnJoinApproved", new
        {
            roomId,
            studentId,
            participantId = participant.Id
        });

        await Clients.Group(roomId.ToString()).SendAsync("StudentJoinApprovalResolved", new
        {
            roomId,
            studentId,
            decision = "Approved"
        });
    }

    public async Task DenyStudentJoin(int roomId, int studentId, string? reason)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        var instructorIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (instructorIdString == null || !int.TryParse(instructorIdString, out var instructorId))
            return;

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.InstructorId != instructorId) return;

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (activeSession == null) return;

        var participant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();
        if (participant == null || participant.JoinedAt < activeSession.StartTime) return;

        participant.JoinApprovalStatus = "Denied";
        participant.IsCurrentlyActive = false;

        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId = roomId,
            StudentId = studentId,
            EventType = "JOIN_DENIED",
            SeverityScore = 0,
            Timestamp = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        await Clients.User(studentId.ToString()).SendAsync("OnJoinDenied", new
        {
            roomId,
            studentId,
            reason = string.IsNullOrWhiteSpace(reason)
                ? "Your request to join was denied by the instructor."
                : reason
        });

        await Clients.Group(roomId.ToString()).SendAsync("StudentJoinApprovalResolved", new
        {
            roomId,
            studentId,
            decision = "Denied"
        });
    }

    // ============================================================
    // RAISED HAND FLOW
    // ============================================================
    // Student presses Raise Hand in the SAC softlock UI to ask the
    // instructor a question via the existing meeting app (Teams /
    // Zoom / Meet). The flow mirrors the existing approval patterns:
    //
    //   1. RaiseHand           — student → "I want to ask a question"
    //   2. ApproveRaiseHand    — instructor → "Yes, you may alt-tab"
    //      (or DenyRaiseHand)
    //   3. LowerHand           — student → "I'm done, resume monitoring"
    //      (or ForceLowerHand by instructor)
    //
    // While Approved, the in-memory map _raisedHandActive holds the
    // (studentId → roomId) pair. SendMonitoringEvent consults it to
    // drop alt-tab / focus / process / idle events for this student.
    // Hardware integrity, clipboard, and screenshot detections are
    // NOT suppressed — those represent academic-integrity risks that
    // a Q&A pause does not justify.

    public async Task RaiseHand(int roomId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;
        int studentId = int.Parse(userIdString);

        // Must be a real participant in an active session — same gate
        // as RequestLeave / StudentFinishedExam.
        var isParticipant = await _context.SessionParticipants
            .AnyAsync(p => p.RoomId == roomId && p.StudentId == studentId);
        if (!isParticipant) return;

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (activeSession == null) return;

        // Already raised — no-op (idempotent so a double-click is safe).
        if (_raisedHandActive.TryGetValue(studentId, out var existingRoom)
            && existingRoom == roomId)
        {
            await Clients.Caller.SendAsync("OnHandRaiseAlreadyActive", roomId);
            return;
        }

        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId        = roomId,
            StudentId     = studentId,
            EventType     = "HAND_RAISED_REQUESTED",
            Description   = "Student raised hand — requesting temporary Q&A access.",
            SeverityScore = 0,
            Timestamp     = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var studentUser = await _context.Users.FindAsync(studentId);
        var studentName = string.IsNullOrWhiteSpace(studentUser?.FullName)
            ? studentUser?.Email : studentUser.FullName;

        // Broadcast to the room group — the IMC's HandRaiseRequested
        // handler surfaces this on the participant tile (Approve /
        // Deny buttons) and logs JOIN_REQ-style entry to the Global
        // Log Feed.
        await Clients.Group(roomId.ToString()).SendAsync("HandRaiseRequested", new
        {
            roomId,
            studentId,
            studentName  = studentName ?? $"Student #{studentId}",
            studentEmail = studentUser?.Email,
            requestedAt  = DateTime.UtcNow
        });
    }

    public async Task ApproveRaiseHand(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        var instructorIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (instructorIdString == null || !int.TryParse(instructorIdString, out var instructorId))
            return;

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.InstructorId != instructorId) return;

        // Arm the in-memory exception BEFORE writing the audit row so
        // that any race between SendMonitoringEvent and the approval
        // resolves in favour of suppression.
        _raisedHandActive[studentId] = roomId;

        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId        = roomId,
            StudentId     = studentId,
            EventType     = "HAND_RAISED_APPROVED",
            Description   = "Instructor approved raised hand — Q&A access granted.",
            SeverityScore = 0,
            Timestamp     = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        await Clients.User(studentId.ToString()).SendAsync("OnHandRaiseApproved", roomId);
        await Clients.Group(roomId.ToString()).SendAsync("HandRaiseResolved", new
        {
            roomId,
            studentId,
            decision = "Approved"
        });
    }

    public async Task DenyRaiseHand(int roomId, int studentId, string? reason)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        var instructorIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (instructorIdString == null || !int.TryParse(instructorIdString, out var instructorId))
            return;

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.InstructorId != instructorId) return;

        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId        = roomId,
            StudentId     = studentId,
            EventType     = "HAND_RAISED_DENIED",
            Description   = string.IsNullOrWhiteSpace(reason)
                                ? "Instructor denied raised-hand request."
                                : $"Instructor denied raised-hand request: {reason}",
            SeverityScore = 0,
            Timestamp     = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        await Clients.User(studentId.ToString()).SendAsync("OnHandRaiseDenied", new
        {
            roomId,
            reason = string.IsNullOrWhiteSpace(reason)
                        ? "Your raised-hand request was denied by the instructor."
                        : reason
        });
        await Clients.Group(roomId.ToString()).SendAsync("HandRaiseResolved", new
        {
            roomId,
            studentId,
            decision = "Denied"
        });
    }

    public async Task LowerHand(int roomId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;
        int studentId = int.Parse(userIdString);

        // Drop the exception first — even if the audit write fails,
        // monitoring must resume so the student can't keep alt-tabbing.
        bool wasActive = _raisedHandActive.TryRemove(studentId, out _);
        if (!wasActive) return; // nothing to lower

        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId        = roomId,
            StudentId     = studentId,
            EventType     = "HAND_RAISED_LOWERED",
            Description   = "Student lowered hand — resuming normal monitoring.",
            SeverityScore = 0,
            Timestamp     = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        await Clients.Group(roomId.ToString()).SendAsync("HandLowered", studentId);
    }

    public async Task ForceLowerHand(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        var instructorIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (instructorIdString == null || !int.TryParse(instructorIdString, out var instructorId))
            return;

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.InstructorId != instructorId) return;

        bool wasActive = _raisedHandActive.TryRemove(studentId, out _);
        if (!wasActive) return;

        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId        = roomId,
            StudentId     = studentId,
            EventType     = "HAND_RAISED_LOWERED",
            Description   = "Instructor lowered student's raised hand — resuming monitoring.",
            SeverityScore = 0,
            Timestamp     = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        await Clients.User(studentId.ToString()).SendAsync("OnHandLoweredByInstructor", roomId);
        await Clients.Group(roomId.ToString()).SendAsync("HandLowered", studentId);
    }
}