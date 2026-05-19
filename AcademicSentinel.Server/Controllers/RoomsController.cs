using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.DTOs;
using AcademicSentinel.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using AcademicSentinel.Server.Hubs;
using System.Linq;

namespace AcademicSentinel.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class RoomsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IHubContext<MonitoringHub> _hubContext;

    public RoomsController(AppDbContext context, IHubContext<MonitoringHub> hubContext)
    {
        _context = context;
        _hubContext = hubContext;
    }

    // ==========================================
    // BASIC ROOM MANAGEMENT
    // ==========================================

    [HttpPost]
    [Authorize(Roles = "Instructor")]
    public async Task<ActionResult<Room>> CreateRoom([FromBody] Room roomRequest)
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();

        roomRequest.InstructorId = int.Parse(userIdString);
        roomRequest.Status = "Pending";
        // REMOVED the roomRequest.EnrollmentCode = null; line so it actually saves!
        roomRequest.CreatedAt = DateTime.UtcNow;

        _context.Rooms.Add(roomRequest);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetRoom), new { id = roomRequest.Id }, roomRequest);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Room>> GetRoom(int id)
    {
        var room = await _context.Rooms.FindAsync(id);
        if (room == null) return NotFound("Room not found.");
        return room;
    }

    // PUT: api/rooms/{id}
    // Lets an instructor rename / re-tag a room they own. Used by the Edit Course
    // dialog. Image edits go through ImagesController.
    [HttpPut("{id}")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> UpdateRoom(int id, [FromBody] RoomUpdateDto update)
    {
        var room = await _context.Rooms.FindAsync(id);
        if (room == null) return NotFound("Room not found.");

        var instructorIdString = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (instructorIdString == null || !int.TryParse(instructorIdString, out var instructorId))
            return Unauthorized();

        if (room.InstructorId != instructorId)
            return StatusCode(403, "You can only edit rooms you created.");

        if (!string.IsNullOrWhiteSpace(update.SubjectName))
            room.SubjectName = update.SubjectName.Trim();

        if (!string.IsNullOrWhiteSpace(update.EnrollmentCode))
            room.EnrollmentCode = update.EnrollmentCode.Trim();

        await _context.SaveChangesAsync();
        return Ok(new { room.Id, room.SubjectName, room.EnrollmentCode });
    }

    public class RoomUpdateDto
    {
        public string? SubjectName { get; set; }
        public string? EnrollmentCode { get; set; }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> DeleteRoom(int id)
    {
        var room = await _context.Rooms.FindAsync(id);
        if (room == null) return NotFound("Room not found.");

        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();
        int instructorId = int.Parse(userIdString);

        if (room.InstructorId != instructorId)
        {
            return StatusCode(403, "You do not have permission to delete this room.");
        }

        var settings = await _context.RoomDetectionSettings.Where(s => s.RoomId == id).ToListAsync();
        var enrollments = await _context.RoomEnrollments.Where(e => e.RoomId == id).ToListAsync();
        var examSessions = await _context.ExamSessions.Where(s => s.RoomId == id).ToListAsync();
        var sessionParticipants = await _context.SessionParticipants.Where(p => p.RoomId == id).ToListAsync();
        var monitoringEvents = await _context.MonitoringEvents.Where(m => m.RoomId == id).ToListAsync();
        var violations = await _context.ViolationLogs.Where(v => v.RoomId == id).ToListAsync();
        var assignments = await _context.SessionAssignments.Where(a => a.RoomId == id).ToListAsync();
        var riskSummaries = await _context.RiskSummaries.Where(r => r.RoomId == id).ToListAsync();

        if (settings.Count > 0) _context.RoomDetectionSettings.RemoveRange(settings);
        if (enrollments.Count > 0) _context.RoomEnrollments.RemoveRange(enrollments);
        if (examSessions.Count > 0) _context.ExamSessions.RemoveRange(examSessions);
        if (sessionParticipants.Count > 0) _context.SessionParticipants.RemoveRange(sessionParticipants);
        if (monitoringEvents.Count > 0) _context.MonitoringEvents.RemoveRange(monitoringEvents);
        if (violations.Count > 0) _context.ViolationLogs.RemoveRange(violations);
        if (assignments.Count > 0) _context.SessionAssignments.RemoveRange(assignments);
        if (riskSummaries.Count > 0) _context.RiskSummaries.RemoveRange(riskSummaries);

        _context.Rooms.Remove(room);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Room deleted successfully." });
    }

    // ==========================================
    // EXAM SESSION MANAGEMENT (STEP 4)
    // ==========================================

    // POST: api/rooms/{roomId}/start-session
    // This creates a NEW session record every time an exam starts.
    // Spec v3/v4 alias: POST /api/rooms/{roomId}/start
    [HttpPost("{roomId}/start-session")]
    [HttpPost("{roomId}/start")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> StartExamSession(int roomId, [FromBody] StartSessionDto? request)
    {
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return NotFound("Room not found.");

        // REQUIRED — block start if the LMS Exam URL has not been configured.
        // The SAC's anchored focus detection cannot function without it.
        var roomSettings = await _context.RoomDetectionSettings
            .FirstOrDefaultAsync(s => s.RoomId == roomId);
        if (roomSettings == null || string.IsNullOrWhiteSpace(roomSettings.LmsExamUrl))
        {
            return BadRequest("Session cannot be started without a valid LMS Exam URL.");
        }

        // Check if there is already an active session for this room
        var activeSession = await _context.ExamSessions
            .FirstOrDefaultAsync(s => s.RoomId == roomId && s.Status == "Active");

        if (activeSession != null)
        {
            return Ok(new { message = "Session already running", sessionId = activeSession.Id });
        }

        var examType = string.IsNullOrWhiteSpace(request?.ExamType) ? "Summative" : request.ExamType;

        var nextSessionNumber = (await _context.ExamSessions
            .Where(s => s.RoomId == roomId)
            .Select(s => (int?)s.SessionNumber)
            .MaxAsync() ?? 0) + 1;

        var newSession = new ExamSession
        {
            RoomId = roomId,
            SessionNumber = nextSessionNumber,
            StartTime = DateTime.UtcNow,
            Status = "Active",
            ExamType = examType
        };

        _context.ExamSessions.Add(newSession);

        // Update Room Status to Active.
        room.Status = "Active";

        // Bug fix — explicitly reset IsMonitoringActive to false at session
        // creation. A previous session that ended ungracefully (instructor
        // force-quit, network drop before EndExamSession ran) could leave
        // this flag stuck on `true`, which made:
        //   1. RequestJoinSession think monitoring was already running →
        //      forced first-time joiners through approval needlessly.
        //   2. GetMonitoringState return true to the SAC the moment a
        //      student joined → detectors fired violations on a session
        //      that hadn't actually been started yet.
        // Monitoring only goes live when the instructor presses "Start
        // Session Monitoring" → BeginMonitoringCountdown sets the flag.
        room.IsMonitoringActive = false;

        await _context.SaveChangesAsync();

        // Broadcast to SignalR so students know it started
        await _hubContext.Clients.Group(roomId.ToString()).SendAsync("SessionStarted");

        return Ok(new { message = "Session started successfully", sessionId = newSession.Id });
    }

    // PUT: api/rooms/sessions/{sessionId}/end
    // This marks a specific session as finished.
    // Spec v3/v4 aliases: POST /api/rooms/{sessionId}/end (POST verb to match spec).
    [HttpPut("sessions/{sessionId}/end")]
    [HttpPost("{sessionId}/end")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> EndExamSession(int sessionId)
    {
        var session = await _context.ExamSessions.FindAsync(sessionId);
        if (session == null) return NotFound("Session not found.");

        // -------------------------------------------------------------
        // FINAL DISCONNECT FLUSH.
        //
        // Before we mark the session Completed, sweep any participant
        // that is still on paper "Connected" but whose ConnectionId has
        // already vanished from the hub's heartbeat map (or whose last
        // heartbeat is stale). The instructor often clicks End Session
        // within seconds of a student dropping — faster than the 15 s
        // heartbeat-timeout window — so without this flush the
        // STUDENT_DISCONNECTED event never lands in the audit trail and
        // Session Archive falls back to "Clean Connection".
        //
        // This is the same DB transition + broadcast as the lazy commit
        // in GetRoomParticipants, just triggered at end-of-session time.
        // -------------------------------------------------------------
        // Best-effort flush. Any exception here MUST NOT prevent the
        // session from being marked Completed below — the End Session
        // request should always succeed for the teacher.
        try
        {
            var liveCutoff = DateTime.UtcNow.AddSeconds(-15);
            var aliveStudentIdsAtEnd = new HashSet<int>();
            foreach (var kv in AcademicSentinel.Server.Hubs.MonitoringHub._activeStudentConnections)
            {
                if (kv.Value.RoomId == session.RoomId && kv.Value.LastBeat >= liveCutoff)
                    aliveStudentIdsAtEnd.Add(kv.Value.StudentId);
            }

            var participantsToFlush = await _context.SessionParticipants
                .Where(p => p.RoomId == session.RoomId
                            && p.JoinedAt >= session.StartTime
                            && p.ConnectionStatus != "Completed"
                            && p.ConnectionStatus != "Disconnected")
                .ToListAsync();

            foreach (var p in participantsToFlush)
            {
                if (aliveStudentIdsAtEnd.Contains(p.StudentId))
                    continue;

                p.ConnectionStatus = "Disconnected";
                p.DisconnectedAt = DateTime.UtcNow;
                p.JoinApprovalStatus = null;
                p.IsCurrentlyActive = false;

                _context.MonitoringEvents.Add(new MonitoringEvent
                {
                    EventType = "STUDENT_DISCONNECTED",
                    Description = "Student was offline when the instructor ended the session.",
                    SeverityScore = 0,
                    RoomId = p.RoomId,
                    StudentId = p.StudentId,
                    Timestamp = DateTime.UtcNow
                });

                foreach (var kv in AcademicSentinel.Server.Hubs.MonitoringHub._activeStudentConnections)
                {
                    if (kv.Value.StudentId == p.StudentId && kv.Value.RoomId == session.RoomId)
                        AcademicSentinel.Server.Hubs.MonitoringHub._activeStudentConnections.TryRemove(kv.Key, out _);
                }

                try
                {
                    await _hubContext.Clients.Group(session.RoomId.ToString()).SendAsync("StudentDisconnected", p.StudentId);
                    await _hubContext.Clients.Group(session.RoomId.ToString()).SendAsync("StudentConnectionLost", p.StudentId);
                }
                catch { /* broadcast best-effort */ }
            }
        }
        catch
        {
            // Swallow — the only critical work is below (session.Status =
            // "Completed" + room.Status = "Pending"). Disconnect bookkeeping
            // can be cleaned up later by the IMC's poll-driven lazy commit.
        }

        session.EndTime = DateTime.UtcNow;
        session.Status = "Completed";

        // CLOSE ALL ACTIVE SESSIONS for this room, not just the one the
        // client passed in. Reason: if any previous session was orphaned
        // (Status still "Active" because of a crashed window, force-quit,
        // or a half-failed End Session), leaving it Active would cause
        // GetRoomStatus to keep reporting activeSessionId on its next call
        // and the dashboard banner would reappear. This sweep makes End
        // Session an idempotent, definitive "the room is closed" operation.
        var otherActive = await _context.ExamSessions
            .Where(s => s.RoomId == session.RoomId
                        && s.Id != session.Id
                        && s.Status == "Active")
            .ToListAsync();
        foreach (var orphan in otherActive)
        {
            orphan.Status = "Completed";
            orphan.EndTime ??= DateTime.UtcNow;
        }

        // Always flip the Room back to Pending after End Session.
        var room = await _context.Rooms.FindAsync(session.RoomId);
        if (room != null)
        {
            room.Status = "Pending";
            room.IsMonitoringActive = false;
        }

        // Clear the instructor-disconnect flag so the dashboard banner
        // / IN PROGRESS pill disappears immediately on the next status
        // refresh, regardless of whether the teacher's previous IMC
        // window closed cleanly or by drop.
        AcademicSentinel.Server.Hubs.MonitoringHub._roomsWithDisconnectedInstructor.TryRemove(session.RoomId, out _);

        await _context.SaveChangesAsync();

        // Broadcast to SignalR that the session is over...
        await _hubContext.Clients.Group(session.RoomId.ToString()).SendAsync("MonitoringStateChanged", false);
        await _hubContext.Clients.Group(session.RoomId.ToString()).SendAsync("SessionEnded");

        return Ok(new { message = "Session officially ended and logged in history." });
    }

    // Spec v3/v4: GET /api/rooms/{sessionId}/status — returns the room's
    // current lifecycle state (Pending / Countdown / Active / Ended) and
    // whether monitoring is currently engaged. Useful for SAC clients that
    // want to query state without holding a SignalR connection.
    [HttpGet("{roomId}/status")]
    public async Task<IActionResult> GetRoomStatus(int roomId)
    {
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return NotFound("Room not found.");

        // Load all sessions for this room so we can self-heal orphans in
        // either direction. The status response only needs the freshest
        // Active session id, but the heal needs the full list.
        var activeSessions = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();

        int? activeSessionId = activeSessions.FirstOrDefault()?.Id;

        // SELF-HEAL — both directions:
        //   (A) Room says Active but no underlying Active ExamSession →
        //       reconcile room to Pending. Used to be the only branch;
        //       caught half-failed End Session bookkeeping artifacts.
        //   (B) Room says Pending but there ARE Active ExamSessions →
        //       those are orphans (the teacher never successfully ended
        //       them, or a crash skipped EndExamSession). Force them
        //       Completed and reset activeSessionId to null so the
        //       Rejoin banner can't keep resurrecting itself.
        bool healed = false;
        if (string.Equals(room.Status, "Active", StringComparison.OrdinalIgnoreCase)
            && activeSessions.Count == 0)
        {
            room.Status = "Pending";
            room.IsMonitoringActive = false;
            healed = true;
        }
        else if (!string.Equals(room.Status, "Active", StringComparison.OrdinalIgnoreCase)
                 && activeSessions.Count > 0)
        {
            foreach (var orphan in activeSessions)
            {
                orphan.Status = "Completed";
                orphan.EndTime ??= DateTime.UtcNow;
            }
            activeSessionId = null;
            room.IsMonitoringActive = false;
            healed = true;
        }
        if (healed)
        {
            try { await _context.SaveChangesAsync(); }
            catch { /* best-effort; next poll will retry */ }
        }

        // The dashboard banner is driven by this flag alone — set when
        // the instructor's IMC connection drops without End Session, and
        // cleared on JoinRoom or EndExamSession.
        bool instructorDisconnected = AcademicSentinel.Server.Hubs.MonitoringHub
            ._roomsWithDisconnectedInstructor.ContainsKey(roomId);

        return Ok(new
        {
            roomId = room.Id,
            status = room.Status,
            isMonitoringActive = room.IsMonitoringActive,
            subjectName = room.SubjectName,
            activeSessionId,
            instructorDisconnected
        });
    }

    // GET: api/rooms/{roomId}/history
    // Fetches all past sessions for this room — both cleanly Completed
    // sessions AND sessions Interrupted by a teacher disconnect. The
    // Interrupted status is what OnDisconnectedAsync writes when the
    // instructor drops mid-session; without including it here, those
    // sessions would vanish from the Past Sessions table (Ghost Sessions).
    [HttpGet("{roomId}/history")]
    public async Task<IActionResult> GetRoomHistory(int roomId)
    {
        var history = await _context.ExamSessions
            .Where(s => s.RoomId == roomId
                        && (s.Status == "Completed" || s.Status == "Interrupted"))
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();

        // Total students enrolled in the room — used to render the
        // "attended/enrolled" ratio (e.g. 2/5) on the past-sessions table.
        var enrolledCount = await _context.RoomEnrollments
            .Where(e => e.RoomId == roomId)
            .CountAsync();

        var result = history.Select(session =>
        {
            var endTime = session.EndTime ?? DateTime.UtcNow;

            var participantCount = _context.SessionParticipants
                .Where(p => p.RoomId == roomId && p.JoinedAt >= session.StartTime && p.JoinedAt <= endTime)
                .Select(p => p.StudentId)
                .Distinct()
                .Count();

            return new
            {
                session.Id,
                session.SessionNumber,
                session.RoomId,
                session.StartTime,
                session.EndTime,
                session.Status,
                session.ExamType,
                ParticipantCount = participantCount,
                EnrolledCount = enrolledCount
            };
        }).ToList();

        return Ok(result);
    }

    // ==========================================
    // SETTINGS, ENROLLMENT, & STUDENT LISTS
    // ==========================================

    [HttpGet("{roomId}/settings")]
    public async Task<ActionResult<RoomDetectionSettings>> GetRoomSettings(int roomId)
    {
        var settings = await _context.RoomDetectionSettings.FirstOrDefaultAsync(s => s.RoomId == roomId);
        if (settings == null) return NotFound("Settings not found.");
        return Ok(settings);
    }

    // Spec v3/v4 calls this `PUT /api/rooms/{roomId}/settings`. Accept both
    // POST (existing client) and PUT (spec) to keep backward compatibility.
    [HttpPost("{roomId}/settings")]
    [HttpPut("{roomId}/settings")]
    public async Task<ActionResult<RoomDetectionSettings>> SaveRoomSettings(int roomId, [FromBody] RoomSetupDto setupRequest)
    {
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return NotFound("Room not found.");

        if (room.Status == "Active") return BadRequest("Cannot modify settings while an exam is running.");

        // REQUIRED — LMS Exam URL must be a valid HTTPS absolute URL with
        // a real host. Reject empty / invalid / non-HTTPS / hostless inputs.
        var urlError = ValidateLmsExamUrl(setupRequest.LmsExamUrl);
        if (urlError != null)
            return BadRequest(urlError);

        var existingSettings = await _context.RoomDetectionSettings.FirstOrDefaultAsync(s => s.RoomId == roomId);
        if (existingSettings != null)
        {
            existingSettings.EnableClipboardMonitoring = setupRequest.EnableClipboardMonitoring;
            existingSettings.EnableProcessDetection = setupRequest.EnableProcessDetection;
            existingSettings.EnableIdleDetection = setupRequest.EnableIdleDetection;
            existingSettings.IdleThresholdSeconds = setupRequest.IdleThresholdSeconds;
            existingSettings.EnableFocusDetection = setupRequest.EnableFocusDetection;
            existingSettings.EnableVirtualizationCheck = setupRequest.EnableVirtualizationCheck;
            existingSettings.StrictMode = setupRequest.StrictMode;
            existingSettings.LmsExamUrl = setupRequest.LmsExamUrl.Trim();
        }
        else
        {
            var settings = new RoomDetectionSettings
            {
                RoomId = roomId,
                EnableClipboardMonitoring = setupRequest.EnableClipboardMonitoring,
                EnableProcessDetection = setupRequest.EnableProcessDetection,
                EnableIdleDetection = setupRequest.EnableIdleDetection,
                IdleThresholdSeconds = setupRequest.IdleThresholdSeconds,
                EnableFocusDetection = setupRequest.EnableFocusDetection,
                EnableVirtualizationCheck = setupRequest.EnableVirtualizationCheck,
                StrictMode = setupRequest.StrictMode,
                LmsExamUrl = setupRequest.LmsExamUrl.Trim(),
                CreatedAt = DateTime.UtcNow
            };

            _context.RoomDetectionSettings.Add(settings);
        }

        await _context.SaveChangesAsync();
        return Ok("Settings saved successfully.");
    }

    /// <summary>
    /// Validates an LMS exam URL for the anchored focus-detection feature.
    /// Returns null if valid, or an error message describing what's wrong.
    /// Rules:
    ///   - non-empty
    ///   - parses as Uri.UriKind.Absolute
    ///   - scheme is "https"
    ///   - host contains a "." (rejects bare strings like "canvas" / "localhost")
    /// </summary>
    private static string? ValidateLmsExamUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "LMS Exam URL is required.";

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return "LMS Exam URL must be a valid absolute URL.";

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return "LMS Exam URL must use HTTPS.";

        if (string.IsNullOrWhiteSpace(parsed.Host) || !parsed.Host.Contains('.'))
            return "LMS Exam URL must have a valid host (e.g. 'feu.instructure.com').";

        return null;
    }

    [HttpGet("{roomId}/participants")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> GetRoomParticipants(int roomId)
    {
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return NotFound("Room not found.");

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        var enrollments = await _context.RoomEnrollments.Where(e => e.RoomId == roomId).ToListAsync();
        var studentIds = enrollments.Select(e => e.StudentId).ToList();

        // Fetch users including the new FullName field
        var users = await _context.Users
            .Where(u => studentIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.Email, u.FullName, u.ProfileImageUrl });

        var participantsQuery = _context.SessionParticipants.Where(p => p.RoomId == roomId);
        if (activeSession != null)
        {
            participantsQuery = participantsQuery.Where(p => p.JoinedAt >= activeSession.StartTime);
        }
        else
        {
            // No active session means nobody should appear as currently in-session.
            participantsQuery = participantsQuery.Where(p => false);
        }

        var participants = await participantsQuery.ToListAsync();
        var participantDictionary = participants
            .GroupBy(p => p.StudentId)
            .Select(g => g.OrderByDescending(p => p.JoinedAt).First())
            .ToDictionary(p => p.StudentId);

        var leaveGrantedStudentIds = await _context.MonitoringEvents
            .Where(e => e.RoomId == roomId && e.EventType == "LEAVE_GRANTED")
            .Select(e => e.StudentId)
            .Distinct()
            .ToHashSetAsync();

        // HEARTBEAT-LIVENESS SNAPSHOT + lazy commit.
        //
        // Build a per-student "is currently alive" map by scanning the
        // hub's active-connection dictionary. A student is alive only if
        // they have at least one ConnectionId entry whose LastBeat is
        // within the last 15 seconds.
        //
        // Beyond just overriding the response, this block now PROMOTES
        // the heartbeat-derived state into the DB and broadcasts the
        // standard StudentDisconnected / StudentConnectionLost events for
        // any student whose row is still "Connected" but whose heartbeat
        // has gone stale. That way:
        //   - The Global Log Feed gets the "⚠ CONNECTION LOST" entry
        //     (IMC's StudentConnectionLost handler).
        //   - The Session Archive's ConnectionQuality calculation sees a
        //     real STUDENT_DISCONNECTED event and reports "Disconnected"
        //     (or "Reconnected" on later rejoin) instead of "Clean
        //     Connection".
        // The IMC's 4 s participant poll drives this lazily, with
        // TryRemove on the hub dictionary acting as the idempotency gate
        // — only one IMC's poll wins per stale entry, no double-fires.
        var liveCutoff = DateTime.UtcNow.AddSeconds(-15);
        var aliveStudentIds = new HashSet<int>();
        var staleConnectionsToFlush = new List<KeyValuePair<string, AcademicSentinel.Server.Hubs.MonitoringHub.ActiveStudentConnection>>();
        foreach (var kv in AcademicSentinel.Server.Hubs.MonitoringHub._activeStudentConnections)
        {
            if (kv.Value.RoomId != roomId) continue;
            if (kv.Value.LastBeat >= liveCutoff)
                aliveStudentIds.Add(kv.Value.StudentId);
            else
                staleConnectionsToFlush.Add(kv);
        }

        // Promote stale heartbeat entries: claim them via TryRemove (only
        // the first IMC poll that observes a stale entry wins), then write
        // the DB transition and broadcast the standard SignalR events.
        bool flushedAny = false;
        foreach (var stale in staleConnectionsToFlush)
        {
            if (!AcademicSentinel.Server.Hubs.MonitoringHub._activeStudentConnections.TryRemove(stale.Key, out _))
                continue;

            var staleStudentId = stale.Value.StudentId;
            var staleRoomId = stale.Value.RoomId;

            var staleParticipants = await _context.SessionParticipants
                .Where(p => p.RoomId == staleRoomId
                            && p.StudentId == staleStudentId
                            && p.ConnectionStatus != "Completed"
                            && p.ConnectionStatus != "Disconnected")
                .ToListAsync();

            if (staleParticipants.Count == 0) continue;

            foreach (var sp in staleParticipants)
            {
                sp.ConnectionStatus = "Disconnected";
                sp.DisconnectedAt = DateTime.UtcNow;
                sp.JoinApprovalStatus = null;
                sp.IsCurrentlyActive = false;
            }
            _context.MonitoringEvents.Add(new MonitoringEvent
            {
                EventType = "STUDENT_DISCONNECTED",
                Description = "Student lost connection to the session (heartbeat timeout).",
                SeverityScore = 0,
                RoomId = staleRoomId,
                StudentId = staleStudentId,
                Timestamp = DateTime.UtcNow
            });
            flushedAny = true;

            // Broadcast in the same iteration so the IMC's Global Log Feed
            // gets the entry immediately. Sister event StudentConnectionLost
            // is what the IMC's existing handler keys off for the log line.
            var roomGroup = staleRoomId.ToString();
            await _hubContext.Clients.Group(roomGroup).SendAsync("StudentDisconnected", staleStudentId);
            await _hubContext.Clients.Group(roomGroup).SendAsync("StudentConnectionLost", staleStudentId);
        }
        if (flushedAny)
        {
            await _context.SaveChangesAsync();
            // Refresh the local participant snapshot so the response we're
            // about to assemble reflects the rows we just updated.
            participants = await participantsQuery.ToListAsync();
            participantDictionary = participants
                .GroupBy(p => p.StudentId)
                .Select(g => g.OrderByDescending(p => p.JoinedAt).First())
                .ToDictionary(p => p.StudentId);
        }

        var result = enrollments.Select(enrollment => {
            string participationStatus = "NotJoined";
            if (participantDictionary.TryGetValue(enrollment.StudentId, out var p))
            {
                participationStatus = string.Equals(p.ConnectionStatus, "Disconnected", StringComparison.OrdinalIgnoreCase)
                    ? "Disconnected"
                    : "Joined";

                // Heartbeat override: if the DB still says "Joined" but
                // the hub map has no fresh heartbeat for this student,
                // the SAC is gone — surface as Disconnected so the IMC
                // tile turns red on the next poll even before the sweeper
                // / OnDisconnectedAsync writes to the DB.
                if (string.Equals(participationStatus, "Joined", StringComparison.OrdinalIgnoreCase)
                    && !aliveStudentIds.Contains(enrollment.StudentId))
                {
                    participationStatus = "Disconnected";
                }
            }

            users.TryGetValue(enrollment.StudentId, out var user);

            var isCompletedParticipant = participantDictionary.TryGetValue(enrollment.StudentId, out var latestParticipantStatus)
                && (string.Equals(latestParticipantStatus.ConnectionStatus, "Completed", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(latestParticipantStatus.ConnectionStatus, "Disconnected", StringComparison.OrdinalIgnoreCase)
                        && leaveGrantedStudentIds.Contains(enrollment.StudentId)));

            // Bug fix: a student kicked via RemoveStudentFromCurrentSession has
            // JoinApprovalStatus = "Removed" on their latest participant row.
            // Surface that as a distinct ParticipationStatus so the IMC's
            // participant list stops rendering them as a passive "Disconnected"
            // member after removal.
            var isRemovedParticipant = participantDictionary.TryGetValue(enrollment.StudentId, out var latestForRemovalCheck)
                && string.Equals(latestForRemovalCheck.JoinApprovalStatus, "Removed", StringComparison.OrdinalIgnoreCase);

            return new ParticipantDto
            {
                StudentId = enrollment.StudentId,
                StudentEmail = user?.Email ?? "Unknown",
                StudentName = user?.FullName ?? "No Name Set", // Sending the real name
                ProfileImageUrl = user?.ProfileImageUrl,
                EnrollmentSource = enrollment.EnrollmentSource,
                ParticipationStatus = isRemovedParticipant
                    ? "Removed"
                    : isCompletedParticipant
                        ? "Completed"
                        : participationStatus,
                ConnectionStatus = participantDictionary.TryGetValue(enrollment.StudentId, out var latestParticipant)
                    ? latestParticipant.ConnectionStatus
                    : "Disconnected"
            };
        }).ToList();

        return Ok(result);
    }

    // NEW: Actual Delete logic for unenrollment 
    [HttpDelete("{roomId}/unenroll/{studentId}")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> UnenrollStudent(int roomId, int studentId)
    {
        var enrollments = await _context.RoomEnrollments
            .Where(e => e.RoomId == roomId && e.StudentId == studentId)
            .ToListAsync();

        if (enrollments.Count == 0) return NotFound("Enrollment record not found.");

        _context.RoomEnrollments.RemoveRange(enrollments);

        var participantRecords = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .ToListAsync();
        if (participantRecords.Count > 0)
            _context.SessionParticipants.RemoveRange(participantRecords);

        await _context.SaveChangesAsync();

        return Ok(new { message = "Student unenrolled successfully." });
    }

    [HttpPost("{roomId}/sessions/remove/{studentId}")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> RemoveStudentFromCurrentSession(int roomId, int studentId)
    {
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return NotFound("Room not found.");

        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();
        int instructorId = int.Parse(userIdString);

        if (room.InstructorId != instructorId)
            return StatusCode(403, "You do not have permission to modify this room session.");

        var latestParticipant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();

        if (latestParticipant == null)
            return NotFound("Student is not part of this session.");

        latestParticipant.ConnectionStatus = "Disconnected";
        latestParticipant.DisconnectedAt = DateTime.UtcNow;
        // Force the next rejoin to go through instructor approval — clearing
        // IsCurrentlyActive and resetting JoinApprovalStatus stops the rejoin
        // gate from treating this participant as a still-approved member.
        latestParticipant.IsCurrentlyActive = false;
        latestParticipant.JoinApprovalStatus = "Removed";

        // Record a STUDENT_REMOVED event so RequestJoinSession can detect that
        // a kick happened after the last REJOIN_APPROVED and require a fresh
        // approval — same pattern as LEAVE_GRANTED.
        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId = roomId,
            StudentId = studentId,
            EventType = "STUDENT_REMOVED",
            Description = "Student removed from session by instructor.",
            SeverityScore = 0,
            Timestamp = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        // Distinct StudentRemoved broadcast so the IMC can drop the row
        // immediately. StudentDisconnected alone is ambiguous — a student who
        // simply lost network would also fire that, but the IMC keeps them in
        // the list as "Disconnected". Removal is permanent until rejoin
        // approval, so we need a separate signal.
        await _hubContext.Clients.Group(roomId.ToString()).SendAsync("StudentRemoved", studentId);
        await _hubContext.Clients.Group(roomId.ToString()).SendAsync("StudentDisconnected", studentId);
        await _hubContext.Clients.User(studentId.ToString()).SendAsync("RemovedFromSession", roomId);

        return Ok(new { message = "Student removed from current session." });
    }

    // ==========================================
    // JOIN APPROVAL GATE (late join + rejoin)
    // Spec v3/v4 alias: POST /api/rooms/{roomId}/join
    // ==========================================
    [HttpPost("{roomId}/request-join")]
    [HttpPost("{roomId}/join")]
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> RequestJoinSession(int roomId)
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();
        int studentId = int.Parse(userIdString);

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return NotFound("Room not found.");
        if (room.Status != "Active")
            return BadRequest("This room does not have an active session.");

        var isEnrolled = await _context.RoomEnrollments
            .AnyAsync(e => e.RoomId == roomId && e.StudentId == studentId);
        if (!isEnrolled)
            return StatusCode(403, "You are not enrolled in this room.");

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (activeSession == null)
            return BadRequest("No active session for this room.");

        var latestParticipant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();

        bool isRejoin = latestParticipant != null
                     && latestParticipant.JoinedAt >= activeSession.StartTime;
        bool isLate = !isRejoin;

        var lastLeaveGranted = await _context.MonitoringEvents
            .Where(e => e.RoomId == roomId
                     && e.StudentId == studentId
                     && e.EventType == "LEAVE_GRANTED"
                     && e.Timestamp >= activeSession.StartTime)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefaultAsync();

        // BUG A FIX — Exam Completed = hard block.
        // Once an instructor has approved a student's "Done" request the exam
        // is finished for that student permanently. They cannot rejoin (and
        // therefore cannot trigger another approval loop in the IMC), even
        // if monitoring is still running for other students.
        if (lastLeaveGranted.HasValue)
        {
            return StatusCode(403, "You have already completed this exam. Rejoining is not allowed.");
        }

        var lastRejoinApproved = await _context.MonitoringEvents
            .Where(e => e.RoomId == roomId
                     && e.StudentId == studentId
                     && e.EventType == "REJOIN_APPROVED"
                     && e.Timestamp >= activeSession.StartTime)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefaultAsync();

        // Track the most recent kick so a removed-then-rejoining student must
        // pass through instructor approval, just like a student who left.
        var lastStudentRemoved = await _context.MonitoringEvents
            .Where(e => e.RoomId == roomId
                     && e.StudentId == studentId
                     && e.EventType == "STUDENT_REMOVED"
                     && e.Timestamp >= activeSession.StartTime)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefaultAsync();

        // BUG B FIX — track the most recent join denial. Without this, a
        // student whose first attempt was denied could just send another
        // /request-join and the gate would treat them as a fresh joiner
        // (no LEAVE_GRANTED, no STUDENT_REMOVED) → auto-approved. Treating
        // JOIN_DENIED as a blocking event forces re-approval.
        var lastJoinDenied = await _context.MonitoringEvents
            .Where(e => e.RoomId == roomId
                     && e.StudentId == studentId
                     && e.EventType == "JOIN_DENIED"
                     && e.Timestamp >= activeSession.StartTime)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefaultAsync();

        // Compose the blocking-event high-water mark across all three sources.
        // Any of {LEAVE_GRANTED, STUDENT_REMOVED, JOIN_DENIED} requires a
        // fresh REJOIN_APPROVED with a newer timestamp before the gate opens.
        DateTime? lastBlockingEvent = null;
        foreach (var ts in new[] { lastStudentRemoved, lastJoinDenied })
        {
            if (ts.HasValue && (!lastBlockingEvent.HasValue || ts > lastBlockingEvent))
                lastBlockingEvent = ts;
        }

        // Late joiners are also rate-limited by JOIN_DENIED — so if their
        // first attempt was denied, the second attempt also goes through
        // approval (not auto-approved as a fresh late joiner).
        bool rejoinNeedsApproval =
            (isRejoin || lastJoinDenied.HasValue)
            && lastBlockingEvent.HasValue
            && (!lastRejoinApproved.HasValue || lastRejoinApproved < lastBlockingEvent);

        // =========================================================================
        // UPDATED LOGIC: Auto-accept late joiners if monitoring hasn't started yet!
        // =========================================================================
        // IMPORTANT: Make sure `IsMonitoringActive` exists in your Room.cs model 
        // and is toggled to true/false in your MonitoringHub when the instructor
        // starts/pauses/stops the feed.
        bool isMonitoringRunning = room.IsMonitoringActive;

        // Late joiners only wait if monitoring is actively running. 
        // Rejoiners wait if they have an unresolved leave-grant.
        bool requiresApproval = (isLate && isMonitoringRunning) || rejoinNeedsApproval;
        // =========================================================================

        SessionParticipant participant;
        if (isRejoin)
        {
            participant = latestParticipant!;
            participant.JoinApprovalStatus = requiresApproval ? "Pending" : "Approved";
            participant.IsCurrentlyActive = !requiresApproval;
        }
        else
        {
            participant = new SessionParticipant
            {
                RoomId = roomId,
                StudentId = studentId,
                ConnectionStatus = "Disconnected",
                JoinApprovalStatus = "Pending",
                IsCurrentlyActive = false,
                JoinedAt = DateTime.UtcNow
            };
            _context.SessionParticipants.Add(participant);
        }

        await _context.SaveChangesAsync();

        if (requiresApproval)
        {
            return StatusCode(StatusCodes.Status202Accepted, new
            {
                status = "Pending",
                participantId = participant.Id,
                isRejoin,
                isLate
            });
        }

        // If they bypass approval (because monitoring isn't running yet, or it's a clean reconnect)
        // Make sure to set them as instantly approved!
        participant.JoinApprovalStatus = "Approved";
        participant.IsCurrentlyActive = true;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            status = "Approved",
            participantId = participant.Id,
            isRejoin,
            isLate
        });
    }

    [HttpGet("instructor")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> GetInstructorRooms()
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();
        int instructorId = int.Parse(userIdString);

        var rooms = await _context.Rooms.Where(r => r.InstructorId == instructorId).ToListAsync();

        // Project each room so the dashboard course tile can pick up the
        // "instructor disconnected" flag — that's what drives the
        // IN PROGRESS pill, not room.Status.
        var result = rooms.Select(r => new
        {
            r.Id,
            r.SubjectName,
            r.EnrollmentCode,
            r.Status,
            r.RoomImageUrl,
            r.InstructorId,
            instructorDisconnected = AcademicSentinel.Server.Hubs.MonitoringHub
                ._roomsWithDisconnectedInstructor.ContainsKey(r.Id)
        });
        return Ok(result);
    }

    // ==========================================
    // STUDENT DASHBOARD ENDPOINTS
    // ==========================================

    // Spec v3/v4 alias: GET /api/rooms/my
    [HttpGet("student")]
    [HttpGet("my")]
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> GetStudentRooms()
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();
        int studentId = int.Parse(userIdString);

        // Find all room IDs this student is enrolled in
        var enrolledRoomIds = await _context.RoomEnrollments
            .Where(e => e.StudentId == studentId)
            .Select(e => e.RoomId)
            .ToListAsync();

        // Fetch the actual room details for those IDs
        var rooms = await _context.Rooms
            .Where(r => enrolledRoomIds.Contains(r.Id))
            .ToListAsync();

        var instructorIds = rooms.Select(r => r.InstructorId).Distinct().ToList();
        var instructors = await _context.Users
            .Where(u => instructorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => !string.IsNullOrWhiteSpace(u.FullName) ? u.FullName : u.Email);

        // Per-room derivations the student dashboard needs to render the
        // correct joinability state:
        //   * HasActiveSession  — there's a live ExamSession.Status=Active
        //                          in this room RIGHT NOW. This is what
        //                          gates "Joinable Now" — room.Status
        //                          alone was unreliable because orphan
        //                          values could linger.
        //   * StudentWasDisconnected — this student has a Disconnected
        //                          participant row for the current Active
        //                          session. If true, the dashboard shows
        //                          "In Progress, Reconnect NOW!" instead
        //                          of "Joinable Now".
        var roomIds = rooms.Select(r => r.Id).ToList();
        var activeSessionsByRoom = await _context.ExamSessions
            .Where(s => roomIds.Contains(s.RoomId) && s.Status == "Active")
            .GroupBy(s => s.RoomId)
            .Select(g => new { RoomId = g.Key, Session = g.OrderByDescending(x => x.StartTime).First() })
            .ToDictionaryAsync(x => x.RoomId, x => x.Session);

        var disconnectedByRoom = new Dictionary<int, bool>();
        foreach (var room in rooms)
        {
            if (!activeSessionsByRoom.TryGetValue(room.Id, out var activeSession))
            {
                disconnectedByRoom[room.Id] = false;
                continue;
            }
            var participant = await _context.SessionParticipants
                .Where(p => p.RoomId == room.Id
                            && p.StudentId == studentId
                            && p.JoinedAt >= activeSession.StartTime)
                .OrderByDescending(p => p.JoinedAt)
                .FirstOrDefaultAsync();
            disconnectedByRoom[room.Id] = participant != null
                && string.Equals(participant.ConnectionStatus, "Disconnected", StringComparison.OrdinalIgnoreCase);
        }

        var result = rooms.Select(r =>
        {
            string subjectName = r.SubjectName;
            string section = string.Empty;

            if (!string.IsNullOrWhiteSpace(r.SubjectName))
            {
                var split = r.SubjectName.Split(new[] { " - " }, 2, StringSplitOptions.None);
                subjectName = split[0];
                if (split.Length > 1)
                {
                    section = split[1];
                }
            }

            bool hasActiveSession = activeSessionsByRoom.ContainsKey(r.Id);
            bool studentWasDisconnected = disconnectedByRoom.TryGetValue(r.Id, out var d) && d;

            return new
            {
                Id = r.Id,
                SubjectName = subjectName,
                Section = section,
                EnrollmentCode = r.EnrollmentCode,
                Status = r.Status,
                CourseImagePath = r.RoomImageUrl,
                RoomDescription = r.SubjectName,
                CreatedBy = instructors.TryGetValue(r.InstructorId, out var creator) ? creator : "Unknown Instructor",
                HasActiveSession = hasActiveSession,
                StudentWasDisconnected = studentWasDisconnected
            };
        })
            .ToList();

        return Ok(result);
    }

    // Spec v3/v4 alias: POST /api/rooms/enroll
    [HttpPost("enroll-code")]
    [HttpPost("enroll")]
    [Authorize(Roles = "Student")]
    public async Task<IActionResult> EnrollStudentByCode([FromBody] EnrollByCodeDto request)
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();
        int studentId = int.Parse(userIdString);

        // 1. Find the room matching the code exactly
        var room = await _context.Rooms.FirstOrDefaultAsync(r => r.EnrollmentCode == request.EnrollmentCode);
        if (room == null) return BadRequest("Invalid course code. Please check with your instructor.");

        if (room.Status != "Pending")
            return BadRequest("Enrollment is only allowed while the room is in Pending status.");

        // 2. Check if already enrolled
        var existingEnrollment = await _context.RoomEnrollments
            .AnyAsync(e => e.RoomId == room.Id && e.StudentId == studentId);

        if (existingEnrollment) return BadRequest("You are already enrolled in this course.");

        // 3. Save the new enrollment
        var enrollment = new RoomEnrollment
        {
            RoomId = room.Id,
            StudentId = studentId,
            EnrollmentSource = "Code",
            EnrolledAt = DateTime.UtcNow
        };

        _context.RoomEnrollments.Add(enrollment);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Successfully enrolled." });
    }

    [HttpPost("{sessionId}/generate-code")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> GenerateRoomCode(int sessionId)
    {
        var room = await _context.Rooms.FindAsync(sessionId);
        if (room == null) return NotFound();

        string newCode = GenerateRoomCode();
        room.EnrollmentCode = newCode;
        await _context.SaveChangesAsync();

        return Ok(new { enrollmentCode = newCode });
    }

    [HttpPost("{roomId}/enroll-email")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> EnrollStudentByEmail(int roomId, [FromBody] string studentEmail)
    {
        // 1. Find the student by their unique email 
        var student = await _context.Users.FirstOrDefaultAsync(u => u.Email == studentEmail && u.Role == "Student");
        if (student == null) return NotFound("Student not found. Ask them to register first.");

        // 2. Prevent duplicate enrollments in the same room 
        var existing = await _context.RoomEnrollments
            .AnyAsync(e => e.RoomId == roomId && e.StudentId == student.Id);

        if (existing) return BadRequest("Student is already in this list.");

        // 3. Save the enrollment with source "Manual" 
        var enrollment = new RoomEnrollment
        {
            RoomId = roomId,
            StudentId = student.Id,
            EnrollmentSource = "Manual",
            EnrolledAt = DateTime.UtcNow
        };

        _context.RoomEnrollments.Add(enrollment);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Student added successfully." });
    }

    // ==========================================
    // HELPERS
    // ==========================================
    private string GenerateRoomCode()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, 6).Select(s => s[random.Next(s.Length)]).ToArray());
    }
}