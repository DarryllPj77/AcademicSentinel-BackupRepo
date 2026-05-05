using System;
using System.Collections.Generic;

namespace AcademicSentinel.Client.Models
{
    public class SessionArchiveDto
    {
        public int SessionId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string Duration { get; set; }
        public int AttendeeCount { get; set; }
        public int TotalViolations { get; set; }
    }

    public class SessionStudentDto
    {
        public int StudentId { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public int RiskScore { get; set; }
        public string RiskLevel { get; set; }
        public int ViolationCount { get; set; }
        public List<SessionLogDto> Logs { get; set; } = new();
    }

    public class SessionLogDto
    {
        public string EventType { get; set; }
        public string Description { get; set; }
        public int SeverityScore { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
