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
        // Populated server-side from MonitoringEvents + final participant row.
        //   "Clean Connection" — no STUDENT_DISCONNECTED events, ended cleanly.
        //   "Reconnected"       — disconnected mid-session then rejoined.
        //   "Disconnected"      — disconnected and never recovered.
        // No C# default — the server always returns one of the three strings.
        // A blank cell in the grid means the HTTP response was malformed,
        // which is preferable to silently showing "Clean Connection".
        public string ConnectionQuality { get; set; }
        public List<SessionLogDto> Logs { get; set; } = new();
    }

    public class SessionLogDto
    {
        public string EventType { get; set; }
        public string Description { get; set; }
        public int SeverityScore { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Row shape returned by GET /api/rooms/{roomId}/trash. Mirrors
    // the past-sessions history payload but adds DeletedAt so the
    // Trash window can show when each session was trashed and how
    // many days it's been there.
    public class TrashedSessionDto
    {
        public int Id { get; set; }
        public int SessionNumber { get; set; }
        public int RoomId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string Status { get; set; } = string.Empty;
        public string ExamType { get; set; } = string.Empty;
        public DateTime DeletedAt { get; set; }
        public int ParticipantCount { get; set; }
        public int EnrolledCount { get; set; }
    }

    // Body for POST /api/rooms/sessions/bulk-delete.
    public class BulkSessionIdsDto
    {
        public List<int> Ids { get; set; } = new();
    }

    // Response shape of POST /api/rooms/sessions/bulk-delete.
    public class BulkSessionsDeleteResponse
    {
        public List<int> SoftDeleted { get; set; } = new();
        public List<BulkSessionsSkippedItem> Skipped { get; set; } = new();
    }

    public class BulkSessionsSkippedItem
    {
        public int Id { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string? Status { get; set; }
    }

    // Response shape for /bulk-restore and /bulk-purge.
    // Same skipped-list contract as BulkSessionsDeleteResponse but
    // the success field is named generically since the action varies.
    public class BulkSessionsActionResponse
    {
        public List<int> Processed { get; set; } = new();
        public List<BulkSessionsSkippedItem> Skipped { get; set; } = new();
    }
}
