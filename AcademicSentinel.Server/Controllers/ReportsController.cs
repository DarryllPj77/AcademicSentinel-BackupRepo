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

            // Classify based on the total score. Defense panel requires
            // non-accusatory wording — the high band is reported as
            // "Possible Dishonesty" rather than the legacy "Cheating".
            string risk = "Safe";
            if (totalScore >= 50) risk = "Possible Dishonesty";
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
        if (totalScore >= 50) riskLevel = "Possible Dishonesty";
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
            .Where(s => s.RoomId == roomId
                        && s.Status == "Completed"
                        // Past Sessions Trash: see RoomsController.GetRoomHistory.
                        && s.DeletedAt == null)
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
            string riskLevel = totalRisk >= 50 ? "POSSIBLE DISHONESTY" : (totalRisk >= 20 ? "SUSPICIOUS" : "SAFE");
            int violationCount = logs.Count(l => l.SeverityScore > 0);

            // =======================================================
            // 2. CONNECTION QUALITY — KICK vs NETWORK-DROP, ORDERED
            // =======================================================
            // The DB is the source of truth. Three distinct event
            // sources drive the classifier:
            //
            //   STUDENT_DISCONNECTED  — written by DisconnectService
            //                           and the stale-reconnect path.
            //                           Represents a TRUE network
            //                           failure (transport drop or
            //                           heartbeat timeout).
            //
            //   STUDENT_REMOVED       — written by
            //                           RemoveStudentFromCurrentSession
            //                           when the instructor kicks. NOT
            //                           a network event; the student's
            //                           connection was fine.
            //
            //   REJOIN_APPROVED       — written by ApproveStudentJoin
            //                           when the instructor admits the
            //                           student back (after either a
            //                           kick or a real disconnect).
            //
            // The "Connection" column reports the student's NETWORK
            // experience. Reserve "Reconnected" for genuine recovery
            // from STUDENT_DISCONNECTED only. A kick is an instructor
            // policy action and must not pollute the network label.
            //
            // Rule set:
            //   (1) Latest STUDENT_REMOVED NOT followed by a later
            //       REJOIN_APPROVED          → "Disconnected"
            //       (student was forcibly removed and never readmitted
            //        before session end — the final state is "removed",
            //        which is rendered as Disconnected in this column.)
            //   (2) No STUDENT_DISCONNECTED ever recorded
            //                                → "Clean Connection"
            //       (covers fresh runs AND kick → rejoin: the kick was
            //        not a network event, so without an actual
            //        STUDENT_DISCONNECTED the network experience was
            //        clean.)
            //   (3) STUDENT_DISCONNECTED recorded:
            //       • REJOIN_APPROVED after the latest STUDENT_DISCONNECTED
            //         OR current row "Connected"      → "Reconnected"
            //       • otherwise                       → "Disconnected"

            // Current participant status (most recent row in this session).
            var mostRecentParticipantRow = await _context.SessionParticipants
                .AsNoTracking()
                .Where(p => p.RoomId    == session.RoomId
                            && p.StudentId == studentId
                            && p.JoinedAt  >= sessionWindowStart
                            && p.JoinedAt  <= sessionWindowEnd)
                .OrderByDescending(p => p.JoinedAt)
                .FirstOrDefaultAsync();

            string finalStatus = mostRecentParticipantRow?.ConnectionStatus ?? "Unknown";

            // Latest TRUE network-disconnect event. STUDENT_REMOVED is
            // tracked separately because it represents an instructor
            // action, not a network event.
            DateTime? lastNetworkDisconnectTs = logs
                .Where(l => l.EventType == "STUDENT_DISCONNECTED")
                .Select(l => (DateTime?)l.Timestamp)
                .OrderByDescending(t => t)
                .FirstOrDefault();

            // Latest instructor kick.
            DateTime? lastKickTs = logs
                .Where(l => l.EventType == "STUDENT_REMOVED")
                .Select(l => (DateTime?)l.Timestamp)
                .OrderByDescending(t => t)
                .FirstOrDefault();

            // Latest explicit recovery event (covers both kick-return
            // and disconnect-return — same REJOIN_APPROVED path).
            DateTime? lastRejoinApprovedTs = logs
                .Where(l => l.EventType == "REJOIN_APPROVED")
                .Select(l => (DateTime?)l.Timestamp)
                .OrderByDescending(t => t)
                .FirstOrDefault();

            string connectionQuality;

            bool kickedAndNotReadmitted =
                lastKickTs.HasValue
                && (!lastRejoinApprovedTs.HasValue || lastRejoinApprovedTs < lastKickTs);

            if (kickedAndNotReadmitted)
            {
                // Rule (1): student was kicked and never came back. The
                // final state is "removed" — render as Disconnected.
                connectionQuality = "Disconnected";
            }
            else if (!lastNetworkDisconnectTs.HasValue)
            {
                // Rule (2): no real network disconnect ever occurred.
                // This branch covers BOTH:
                //   • students who ran cleanly from start to finish,
                //   • kicked-then-readmitted students (Rule 1 already
                //     filtered out the kicked-and-stayed-out case;
                //     anything reaching here had a superseding rejoin
                //     after the kick, and their network experience was
                //     fine because no STUDENT_DISCONNECTED was ever
                //     written).
                connectionQuality = "Clean Connection";
            }
            else
            {
                // Rule (3): A real network disconnect occurred at some
                // point. The label depends on whether the LATEST
                // STUDENT_DISCONNECTED was followed by a recovery.
                bool hasRejoinAfterNetworkDisconnect =
                    lastRejoinApprovedTs.HasValue
                    && lastRejoinApprovedTs > lastNetworkDisconnectTs;

                // Live-row fallback: a fast WithAutomaticReconnect can
                // restore ConnectionStatus="Connected" without writing
                // REJOIN_APPROVED. The kicked-and-not-readmitted branch
                // above already shielded us from accidentally rescuing
                // a kicked-out student via this path.
                bool isCurrentlyConnected = string.Equals(
                    finalStatus, "Connected", StringComparison.OrdinalIgnoreCase);

                connectionQuality = (hasRejoinAfterNetworkDisconnect || isCurrentlyConnected)
                    ? "Reconnected"
                    : "Disconnected";
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