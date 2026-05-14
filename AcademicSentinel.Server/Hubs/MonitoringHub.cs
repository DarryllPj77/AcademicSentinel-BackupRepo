using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.Models;
using AcademicSentinel.Server.DTOs;
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

    public MonitoringHub(AppDbContext context, IServiceScopeFactory scopeFactory, ILogger<MonitoringHub> logger)
    {
        _context = context;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // =======================================================
    // IMC (TEACHER) CALLS THIS TO LISTEN FOR ALERTS
    // =======================================================
    public async Task JoinRoom(string roomId)
    {
        // Adds the teacher to the SignalR group for this specific exam
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

        // If an Instructor is rejoining a room whose session is still
        // Active (typical scenario: their previous connection dropped and
        // students kept being monitored), broadcast TeacherReconnected so
        // every SAC in the room can clear / replace the yellow
        // "Connection to Instructor Lost" banner with a positive
        // reconnect notice.
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(roomId, out int parsedRoomId))
        {
            var room = await _context.Rooms.FindAsync(parsedRoomId);
            if (room != null
                && string.Equals(room.Status, "Active", StringComparison.OrdinalIgnoreCase))
            {
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

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.Status != "Active")
            return;

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        if (activeSession == null)
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

            var room = await _context.Rooms.FindAsync(roomId);
            if (room == null || room.Status != "Active")
            {
                await Clients.Caller.SendAsync("JoinFailed", "Cannot join room: session is inactive.");
                return;
            }

            var activeSession = await _context.ExamSessions
                .Where(s => s.RoomId == roomId && s.Status == "Active")
                .OrderByDescending(s => s.StartTime)
                .FirstOrDefaultAsync();

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

            if (participant != null && participant.ConnectionStatus == "Completed")
            {
                await Clients.Caller.SendAsync("JoinFailed", "You have already completed and exited this active session.");
                return;
            }

            _logger.LogInformation("JoinLiveExam: roomId={RoomId}, studentId={StudentId}, existingParticipant={Existing}, connectionStatus={ConnStatus}, approvalStatus={ApprovalStatus}",
                roomId, studentId, participant != null,
                participant?.ConnectionStatus ?? "(none)",
                participant?.JoinApprovalStatus ?? "(none)");

            // SECURITY FIX: Rejoin must require teacher approval.
            // A participant whose previous state is "Disconnected" is NOT
            // allowed to silently reconnect — this was the auto-rejoin hole.
            // Mark them Pending, log the request, and notify the instructor
            // so they can Approve/Deny via the existing approval UI.
            if (participant != null
                && string.Equals(participant.ConnectionStatus, "Disconnected", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(participant.JoinApprovalStatus, "Approved", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("JoinLiveExam: routing student {StudentId} through rejoin approval gate", studentId);
                participant.JoinApprovalStatus = "Pending";
                participant.IsCurrentlyActive = false;

                _context.MonitoringEvents.Add(new MonitoringEvent
                {
                    RoomId = roomId,
                    StudentId = studentId,
                    EventType = "REJOIN_REQUESTED",
                    Description = "Student attempted to rejoin after a disconnect — awaiting instructor approval.",
                    SeverityScore = 0,
                    Timestamp = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();

                string studentLabel = string.IsNullOrWhiteSpace(studentUser.FullName)
                    ? studentUser.Email : studentUser.FullName;

                // Broadcast the rejoin request to the instructor. Payload
                // shape matches StudentPendingApproval so the IMC can reuse
                // its existing approval card UI without a new code path.
                await Clients.Group(roomId.ToString()).SendAsync("RejoinRequest", new
                {
                    roomId,
                    studentId,
                    participantId = participant.Id,
                    studentName = studentLabel,
                    studentEmail = studentUser.Email,
                    profileImageUrl = studentUser.ProfileImageUrl,
                    isRejoin = true,
                    isLate = false,
                    requestedAt = DateTime.UtcNow
                });

                // Tell the student to display a pending-approval overlay
                // instead of pretending they reconnected successfully.
                await Clients.Caller.SendAsync("AwaitingRejoinApproval", roomId);
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

        if (string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase)
            && userIdString != null
            && int.TryParse(userIdString, out var instructorId))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            try
            {
                var activeRoom = await db.Rooms
                    .Where(r => r.InstructorId == instructorId && r.Status == "Active")
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefaultAsync();

                if (activeRoom != null)
                {
                    // BEHAVIOR CHANGE: Per requirement, an instructor drop
                    // must NOT end the session. Monitoring continues on every
                    // connected student, detection stays armed (no implicit
                    // pause), and the room stays Active so the instructor
                    // can reconnect and resume control. Only the audit-trail
                    // event + TeacherDisconnected broadcast remain; the
                    // session.Status / room.Status mutations and the
                    // SessionInterrupted broadcast were removed because they
                    // were the path that force-closed the student SAC.
                    db.MonitoringEvents.Add(new MonitoringEvent
                    {
                        RoomId = activeRoom.Id,
                        StudentId = 0,
                        EventType = "TEACHER_DISCONNECTED",
                        Description = "Instructor lost connection mid-session — session stays Active, monitoring continues.",
                        SeverityScore = 0,
                        Timestamp = DateTime.UtcNow
                    });

                    await db.SaveChangesAsync();

                    // SAC renders the yellow "Connection to Instructor Lost"
                    // banner on this event and keeps detecting. IMC instances
                    // (if any other instructor consoles share the room) can
                    // also surface a warning.
                    await Clients.Group(activeRoom.Id.ToString()).SendAsync("TeacherDisconnected", activeRoom.Id);
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                // best-effort instructor disconnect handling under concurrent drops
            }
        }
        else if (userIdString != null && int.TryParse(userIdString, out var studentId))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            try
            {
                // BUG FIX: The previous "hasCompletedSessionParticipant"
                // short-circuit was a GLOBAL check — if this student had
                // ever completed ANY session in their history, every future
                // disconnect was silently swallowed. That left participants
                // stuck at ConnectionStatus="Connected" in the IMC, no
                // STUDENT_DISCONNECTED log was written, and the JoinLiveExam
                // rejoin-approval gate could not fire because it keys off
                // ConnectionStatus="Disconnected". Per-participant filtering
                // below already excludes cleanly-completed sessions (their
                // row sits at "Completed", not "Connected"), so the global
                // short-circuit was both wrong and redundant.
                //
                // Find every participant row for this student whose status
                // is neither cleanly Completed nor already Disconnected.
                // Anything that's Connected, Pending (rejoin in flight),
                // or in any transitional state gets cleaned up here.
                var activeParticipants = await db.SessionParticipants
                    .Where(p => p.StudentId == studentId
                                && p.ConnectionStatus != "Completed"
                                && p.ConnectionStatus != "Disconnected")
                    .ToListAsync();

                _logger.LogInformation("Student disconnect path: studentId={StudentId}, foundActiveParticipants={Count}",
                    studentId, activeParticipants.Count);

                foreach (var participant in activeParticipants)
                {
                    participant.ConnectionStatus = "Disconnected";
                    participant.DisconnectedAt = DateTime.UtcNow;
                    // Clear any prior "Approved" rejoin so the next
                    // reconnect attempt re-enters the approval gate
                    // (closes the auto-rejoin hole described in the bug
                    // report).
                    participant.JoinApprovalStatus = null;
                    participant.IsCurrentlyActive = false;

                    // Structured event type so the StudentLogsPreviewDialog's
                    // violations breakdown groups student drops as their own
                    // category rather than burying them under "SYSTEM".
                    db.MonitoringEvents.Add(new MonitoringEvent
                    {
                        EventType = "STUDENT_DISCONNECTED",
                        Description = "Student lost connection to the session.",
                        SeverityScore = 0,
                        RoomId = participant.RoomId,
                        StudentId = studentId,
                        Timestamp = DateTime.UtcNow
                    });
                }

                await db.SaveChangesAsync();

                foreach (var participant in activeParticipants)
                {
                    var roomGroup = participant.RoomId.ToString();
                    await Clients.Group(roomGroup).SendAsync("StudentDisconnected", studentId);
                    await Clients.Group(roomGroup).SendAsync("StudentConnectionLost", studentId);
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                // best-effort disconnect update under concurrent drops
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

        var isSessionEnded = !string.Equals(room.Status, "Active", StringComparison.OrdinalIgnoreCase);

        var leaveAlreadyGranted = await _context.MonitoringEvents
            .AnyAsync(e => e.RoomId == roomId
                        && e.StudentId == studentId
                        && e.EventType == "LEAVE_GRANTED");

        if (isSessionEnded || leaveAlreadyGranted)
        {
            await Clients.Client(Context.ConnectionId).SendAsync("LeaveGranted", studentId);
        }
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

        if (string.Equals(latestParticipantState, "Completed", StringComparison.OrdinalIgnoreCase))
            return;

        // Verify the room exists
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return;

        // Create the monitoring event record
        var monitoringEvent = new MonitoringEvent
        {
            RoomId = roomId,
            StudentId = studentId,
            EventType = eventData.EventType,
            Description = eventData.Description,
            SeverityScore = eventData.SeverityScore,
            Timestamp = DateTime.UtcNow
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
            timestamp = DateTime.UtcNow
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

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.Status != "Active" || room.InstructorId != instructorId) return;

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
}