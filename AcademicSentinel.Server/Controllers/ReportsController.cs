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
        foreach (var studentId in studentsInSession)
        {
            var user = await _context.Users.FindAsync(studentId);
            if (user == null) continue;

            var logs = await _context.MonitoringEvents
                .Where(e => e.RoomId == session.RoomId && e.StudentId == studentId && e.Timestamp >= session.StartTime && (session.EndTime == null || e.Timestamp <= session.EndTime))
                .OrderByDescending(e => e.Timestamp)
                .Select(e => new {
                    EventType = e.EventType, Description = e.Description,
                    SeverityScore = e.SeverityScore, Timestamp = e.Timestamp
                })
                .ToListAsync();

            int totalRisk = logs.Where(l => l.SeverityScore > 0).Sum(l => l.SeverityScore);
            string riskLevel = totalRisk >= 50 ? "CHEATING" : (totalRisk >= 20 ? "SUSPICIOUS" : "SAFE");
            int violationCount = logs.Count(l => l.SeverityScore > 0);

            result.Add(new {
                StudentId = studentId, Name = string.IsNullOrWhiteSpace(user.FullName) ? "Unknown" : user.FullName,
                Email = user.Email, RiskScore = totalRisk, RiskLevel = riskLevel,
                ViolationCount = violationCount, Logs = logs
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