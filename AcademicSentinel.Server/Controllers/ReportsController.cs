using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.DTOs;
using Microsoft.AspNetCore.Authorization;
using System.Linq;

namespace AcademicSentinel.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Instructor")] // Strictly locked to Instructors!
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _context;

    public ReportsController(AppDbContext context)
    {
        _context = context;
    }

    // GET: api/reports/room/{sessionId}
    [HttpGet("room/{sessionId}")]
    public async Task<ActionResult<RoomReportDto>> GetRoomReport(int sessionId)
    {
        // 1. Fetch the Room
        var room = await _context.Rooms.FindAsync(sessionId);
        if (room == null) return NotFound("Room not found.");

        // 2. Fetch the Students who actually joined this specific room
        var participants = await _context.SessionParticipants
                                         .Where(p => p.RoomId == sessionId)
                                         .ToListAsync();

        // 3. Fetch all cheating logs recorded for this room
        var allViolations = await _context.ViolationLogs
                                          .Where(v => v.RoomId == sessionId)
                                          .ToListAsync();

        // 4. Fetch the User accounts so we can attach their Emails to the report
        var participantIds = participants.Select(p => p.StudentId).ToList();
        var users = await _context.Users
                                  .Where(u => participantIds.Contains(u.Id))
                                  .ToDictionaryAsync(u => u.Id, u => u.Email);

        // 5. Build the Master Report
        var report = new RoomReportDto
        {
            RoomId = room.Id,
            SubjectName = room.SubjectName,
            Status = room.Status,
            CreatedAt = room.CreatedAt,
            TotalParticipants = participants.Count
        };

        // 6. Calculate the Risk for each student
        foreach (var participant in participants)
        {
            string studentEmail = users.ContainsKey(participant.StudentId) ? users[participant.StudentId] : "Unknown";

            // Find only the violations for this specific student
            var studentViolations = allViolations.Where(v => v.StudentEmail == studentEmail).ToList();

            // --- THESIS BEHAVIORAL RULE-BASED SCORING (BRBDE) ---
            // We assign a simple weighted score here: S1 = 10pts, S2 = 20pts, S3 = 50pts
            int totalScore = 0;
            foreach (var violation in studentViolations)
            {
                if (violation.SeverityLevel == "S1") totalScore += 10;
                else if (violation.SeverityLevel == "S2") totalScore += 20;
                else if (violation.SeverityLevel == "S3") totalScore += 50;
            }

            // Classify based on the total score
            string risk = "Safe";
            if (totalScore >= 50) risk = "Cheating";
            else if (totalScore >= 20) risk = "Suspicious";

            // Add them to the report
            report.StudentSummaries.Add(new StudentRiskSummaryDto
            {
                StudentId = participant.StudentId,
                Email = studentEmail,
                ConnectionStatus = participant.ConnectionStatus,
                TotalViolations = studentViolations.Count,
                TotalSeverityScore = totalScore,
                RiskLevel = risk,
                JoinedAt = participant.JoinedAt,
                DisconnectedAt = participant.DisconnectedAt
            });
        }

        return Ok(report);
    }

    // GET: api/reports/student/{sessionId}/{studentId}
    // Get individual student report with detailed violation timeline
    [HttpGet("student/{sessionId}/{studentId}")]
    public async Task<ActionResult<StudentReportDto>> GetStudentReport(int sessionId, int studentId)
    {
        // 1. Verify room exists
        var room = await _context.Rooms.FindAsync(sessionId);
        if (room == null) return NotFound("Room not found.");

        // 2. Get student info
        var student = await _context.Users.FindAsync(studentId);
        if (student == null || student.Role != "Student") return NotFound("Student not found.");

        // 3. Get enrollment info
        var enrollment = await _context.RoomEnrollments
            .FirstOrDefaultAsync(e => e.RoomId == sessionId && e.StudentId == studentId);

        if (enrollment == null) return NotFound("Student is not enrolled in this room.");

        // 4. Get participation info
        var participant = await _context.SessionParticipants
            .FirstOrDefaultAsync(p => p.RoomId == sessionId && p.StudentId == studentId);

        // 5. Get all violations for this student
        var violations = await _context.ViolationLogs
            .Where(v => v.RoomId == sessionId && v.StudentEmail == student.Email)
            .OrderByDescending(v => v.Timestamp)
            .ToListAsync();

        // 6. Calculate risk score
        int totalScore = 0;
        foreach (var violation in violations)
        {
            if (violation.SeverityLevel == "S1") totalScore += 10;
            else if (violation.SeverityLevel == "S2") totalScore += 20;
            else if (violation.SeverityLevel == "S3") totalScore += 50;
        }

        string riskLevel = "Safe";
        if (totalScore >= 50) riskLevel = "Cheating";
        else if (totalScore >= 20) riskLevel = "Suspicious";

        // 7. Build the report DTO
        var report = new StudentReportDto
        {
            StudentId = studentId,
            StudentEmail = student.Email,
            RoomId = room.Id,
            RoomSubjectName = room.SubjectName,
            JoinedAt = participant?.JoinedAt,
            DisconnectedAt = participant?.DisconnectedAt,
            ParticipationStatus = participant != null ? "Joined" : "NotJoined",
            ConnectionStatus = participant?.ConnectionStatus ?? "Disconnected",
            TotalViolations = violations.Count,
            TotalSeverityScore = totalScore,
            RiskLevel = riskLevel,
            ViolationTimeline = violations.Select(v => new ViolationTimelineDto
            {
                ViolationId = v.Id,
                EventType = v.Module,
                Description = v.Description,
                SeverityLevel = v.SeverityLevel,
                Timestamp = v.Timestamp
            }).ToList()
        };

        return Ok(report);
    }

    [HttpGet("rooms/{roomId}/sessions")]
    public async Task<ActionResult<IEnumerable<object>>> GetRoomSessions(int roomId)
    {
        var sessions = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Completed")
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();

        var result = new List<object>();
        foreach(var s in sessions)
        {
            var duration = s.EndTime.HasValue ? (s.EndTime.Value - s.StartTime).ToString(@"hh\:mm\:ss") : "Unknown";
            var attendees = await _context.SessionParticipants
                .Where(p => p.RoomId == roomId && p.JoinedAt >= s.StartTime && (s.EndTime == null || p.JoinedAt <= s.EndTime))
                .Select(p => p.StudentId)
                .Distinct()
                .CountAsync();

            var violations = await _context.MonitoringEvents
                .Where(e => e.RoomId == roomId && e.Timestamp >= s.StartTime && (s.EndTime == null || e.Timestamp <= s.EndTime) && e.SeverityScore > 0)
                .CountAsync();

            result.Add(new {
                SessionId = s.Id, StartTime = s.StartTime, EndTime = s.EndTime,
                Duration = duration, AttendeeCount = attendees, TotalViolations = violations
            });
        }
        return Ok(result);
    }

    [HttpGet("sessions/{sessionId}/students")]
    public async Task<ActionResult<IEnumerable<object>>> GetSessionStudents(int sessionId)
    {
        var session = await _context.ExamSessions.FindAsync(sessionId);
        if (session == null) return NotFound();

        var studentsInSession = await _context.SessionParticipants
            .Where(p => p.RoomId == session.RoomId && p.JoinedAt >= session.StartTime && (session.EndTime == null || p.JoinedAt <= session.EndTime))
            .Select(p => p.StudentId)
            .Distinct()
            .ToListAsync();

        var result = new List<object>();

        // Expand window to ensure we catch all events
        var sessionWindowStart = session.StartTime.AddMinutes(-1);
        var sessionWindowEnd = (session.EndTime ?? DateTime.UtcNow).AddMinutes(1);

        foreach (var studentId in studentsInSession)
        {
            var user = await _context.Users.FindAsync(studentId);
            if (user == null) continue;

            // 1. Fetch ALL logs for this student and pass them UNFILTERED to the UI
            var logs = await _context.MonitoringEvents
                .Where(e => e.RoomId == session.RoomId && e.StudentId == studentId
                            && e.Timestamp >= sessionWindowStart
                            && e.Timestamp <= sessionWindowEnd)
                .OrderByDescending(e => e.Timestamp)
                .Select(e => new {
                    EventType = e.EventType,
                    Description = e.Description,
                    SeverityScore = e.SeverityScore,
                    Timestamp = e.Timestamp
                })
                .ToListAsync();

            int totalRisk = logs.Where(l => l.SeverityScore > 0).Sum(l => l.SeverityScore);
            string riskLevel = totalRisk >= 50 ? "CHEATING" : (totalRisk >= 20 ? "SUSPICIOUS" : "SAFE");
            int violationCount = logs.Count(l => l.SeverityScore > 0);

            // =======================================================
            // 2. CONNECTION QUALITY — SessionParticipants as primary source
            // =======================================================
            // Root cause of the "Clean Connection" ghost bug:
            //   MonitoringEvents.STUDENT_DISCONNECTED is sometimes never
            //   written to PostgreSQL (ghost-write failure), so any logic
            //   that gates on that event produces a false "Clean Connection".
            //
            // Fix: use SessionParticipants.DisconnectedAt as the primary
            // signal.  That column is ALWAYS committed (the live UI proves
            // it).  MonitoringEvents is kept as a secondary cross-check only.

            // Fetch every participant row for this student in this session.
            // A reconnecting student will have more than one row, ordered by
            // JoinedAt so the last element is always the most recent.
            var allParticipantRows = await _context.SessionParticipants
                .AsNoTracking()
                .Where(p => p.RoomId == session.RoomId
                            && p.StudentId == studentId
                            && p.JoinedAt >= sessionWindowStart
                            && p.JoinedAt <= sessionWindowEnd)
                .OrderBy(p => p.JoinedAt)
                .ToListAsync();

            var mostRecentParticipantRow = allParticipantRows.LastOrDefault();
            string finalStatus = mostRecentParticipantRow?.ConnectionStatus ?? "Unknown";

            // =======================================================
            // SYNTHETIC LOG INJECTION
            // =======================================================
            // The UI renders whatever rows are in `logs` verbatim.  When
            // the STUDENT_DISCONNECTED MonitoringEvent is missing (the
            // ghost-bug write failure), we reconstruct it here from the
            // SessionParticipants row and inject it into the logs list so
            // the activity timeline still shows the disconnect.
            //
            // Matching window: 60 s.  If a real event landed within ±60 s
            // of the participant's DisconnectedAt we skip injection so the
            // archive never shows duplicate disconnect entries.
            foreach (var p in allParticipantRows)
            {
                if (!string.Equals(p.ConnectionStatus, "Disconnected",
                                   StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!p.DisconnectedAt.HasValue)
                    continue;

                var dropTime = p.DisconnectedAt.Value;
                bool alreadyLogged = logs.Any(l =>
                    l.EventType == "STUDENT_DISCONNECTED"
                    && Math.Abs((l.Timestamp - dropTime).TotalSeconds) < 60);

                if (alreadyLogged) continue;

                // Same anonymous-type shape as the EF Select above so it
                // appends cleanly into the strongly-typed list.
                logs.Add(new
                {
                    EventType = "STUDENT_DISCONNECTED",
                    Description = "Connection lost. Reconstructed from session participant record (heartbeat timeout or unexpected closure).",
                    SeverityScore = 0,
                    Timestamp = dropTime
                });
            }

            // Re-sort newest-first so the synthetic entries slot into the
            // correct chronological position in the UI grid.
            logs = logs.OrderByDescending(l => l.Timestamp).ToList();

            // --- PRIMARY: mid-session drops from SessionParticipants ---
            // A row counts as a mid-session drop when:
            //   • ConnectionStatus is "Disconnected", AND
            //   • DisconnectedAt is before session end (minus 30 s grace for
            //     the end-of-session broadcast lag), OR DisconnectedAt is null
            //     (we cannot prove it was post-session so we assume the worst).
            // If session is still running (EndTime == null) every Disconnected
            // row is a mid-session drop by definition.
            var sessionEndCutoff = session.EndTime?.AddSeconds(-30);

            var midSessionParticipantDrops = allParticipantRows
                .Where(p => string.Equals(p.ConnectionStatus, "Disconnected",
                                          StringComparison.OrdinalIgnoreCase)
                            && (sessionEndCutoff == null          // ongoing session
                                || !p.DisconnectedAt.HasValue     // unknown time → conservative
                                || p.DisconnectedAt.Value < sessionEndCutoff.Value))
                .ToList();

            // --- SECONDARY: mid-session drops from MonitoringEvents ---
            // Used as a cross-check only; an empty event log does NOT override
            // participant-table evidence.
            var midSessionEventDrops = logs
                .Where(l => l.EventType == "STUDENT_DISCONNECTED"
                            && (session.EndTime == null || l.Timestamp < session.EndTime.Value))
                .ToList();

            bool hadMidSessionDrop = midSessionParticipantDrops.Count > 0
                                  || midSessionEventDrops.Count > 0;

            string connectionQuality;

            if (!hadMidSessionDrop)
            {
                connectionQuality = "Clean Connection";
            }
            else
            {
                // Mid-session drop confirmed. Start at Disconnected, then
                // look for proof of recovery from either data source.
                connectionQuality = "Disconnected";

                // Proof A — graceful end-of-session exit (participant table):
                // Their most recent disconnect happened at or after session end,
                // so they were still live when the exam concluded and only
                // dropped because the session broadcast closed the connection.
                bool gracefulExitByTime = session.EndTime.HasValue
                    && mostRecentParticipantRow?.DisconnectedAt.HasValue == true
                    && mostRecentParticipantRow.DisconnectedAt.Value
                       >= session.EndTime.Value.AddSeconds(-30);

                // Proof B — graceful exit (events, if they exist):
                bool gracefulExitByEvent = session.EndTime.HasValue
                    && logs.Any(l => l.EventType == "STUDENT_DISCONNECTED"
                                     && l.Timestamp >= session.EndTime.Value);

                // Proof C — rejoined (participant table):
                // A later participant row exists whose JoinedAt is strictly
                // after the last mid-session drop row — the student came back.
                var lastParticipantDrop = midSessionParticipantDrops.LastOrDefault();
                bool rejoinedByRow = lastParticipantDrop != null
                    && allParticipantRows.Any(p => p.JoinedAt > lastParticipantDrop.JoinedAt);

                // Proof D — rejoined (events, if they exist):
                var lastEventDrop = midSessionEventDrops.Count > 0
                    ? midSessionEventDrops.MaxBy(l => l.Timestamp)
                    : null;
                bool rejoinedByEvent = lastEventDrop != null
                    && mostRecentParticipantRow?.JoinedAt > lastEventDrop.Timestamp;

                // Proof E — still live or cleanly finished:
                bool liveAtEnd =
                    string.Equals(finalStatus, "Connected",  StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(finalStatus, "Completed",  StringComparison.OrdinalIgnoreCase);

                if (gracefulExitByTime || gracefulExitByEvent ||
                    rejoinedByRow      || rejoinedByEvent      || liveAtEnd)
                {
                    connectionQuality = "Reconnected";
                }
            }

            result.Add(new
            {
                StudentId = studentId,
                Name = string.IsNullOrWhiteSpace(user.FullName) ? "Unknown" : user.FullName,
                Email = user.Email,
                RiskScore = totalRisk,
                RiskLevel = riskLevel,
                ViolationCount = violationCount,
                ConnectionQuality = connectionQuality,
                Logs = logs // Passing the pure, untouched logs to the frontend
            });
        }
        return Ok(result);
    }

    private static string FormatDuration(DateTime startTime, DateTime? endTime)
    {
        var effectiveEnd = endTime ?? DateTime.UtcNow;
        var duration = effectiveEnd - startTime;

        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var totalHours = (int)duration.TotalHours;
        var minutes = duration.Minutes;

        if (totalHours > 0)
            return $"{totalHours}h {minutes}m";

        return $"{minutes}m";
    }
}