namespace AcademicSentinel.Server.DTOs;

// This holds the summary for a single student
public class StudentRiskSummaryDto
{
    public int StudentId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string ConnectionStatus { get; set; } = string.Empty;
    public int TotalViolations { get; set; }
    public int TotalSeverityScore { get; set; }
    public string RiskLevel { get; set; } = "Safe";
    public DateTime? JoinedAt { get; set; }
    public DateTime? DisconnectedAt { get; set; }
}

// This holds the entire room's data, containing a list of the students above
public class RoomReportDto
{
    public int RoomId { get; set; }
    public string SubjectName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int TotalParticipants { get; set; }
    public DateTime CreatedAt { get; set; }

    // The list of all students and their scores
    public List<StudentRiskSummaryDto> StudentSummaries { get; set; } = new();
}

/// <summary>
/// DTO for individual student detailed report with violation timeline
/// </summary>
public class StudentReportDto
{
    public int StudentId { get; set; }
    public string StudentEmail { get; set; } = string.Empty;
    public int RoomId { get; set; }
    public string RoomSubjectName { get; set; } = string.Empty;
    public DateTime? JoinedAt { get; set; }
    public DateTime? DisconnectedAt { get; set; }
    public string ParticipationStatus { get; set; } = string.Empty;
    public string ConnectionStatus { get; set; } = string.Empty;
    public int TotalViolations { get; set; }
    public int TotalSeverityScore { get; set; }
    public string RiskLevel { get; set; } = "Safe";
    public List<ViolationTimelineDto> ViolationTimeline { get; set; } = new();
}

/// <summary>
/// DTO for violation timeline entry
/// </summary>
public class ViolationTimelineDto
{
    public int ViolationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SeverityLevel { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

public class SessionArchiveDto
{
    public int SessionId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Duration { get; set; } = string.Empty;
    public int AttendeeCount { get; set; }
    public int TotalViolations { get; set; }
}

public class SessionStudentDto
{
    public int StudentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int RiskScore { get; set; }
    public string RiskLevel { get; set; } = "SAFE";
    public int ViolationCount { get; set; }
    public List<SessionLogDto> Logs { get; set; } = new();
}

public class SessionLogDto
{
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int SeverityScore { get; set; }
    public DateTime Timestamp { get; set; }
}