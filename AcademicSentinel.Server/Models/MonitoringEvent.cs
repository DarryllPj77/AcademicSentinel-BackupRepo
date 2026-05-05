namespace AcademicSentinel.Server.Models;

/// <summary>
/// Stores all detected monitoring events from SAC
/// Used for timeline and risk computation
/// </summary>
public class MonitoringEvent
{
    public int Id { get; set; }

    // Links to the Room
    public int RoomId { get; set; }

    // Links to the Student
    public int StudentId { get; set; }

    // Type of event: ALT_TAB, PROCESS, CLIPBOARD, IDLE, VM, EMULATOR, etc.
    public string EventType { get; set; } = string.Empty;

    // Human-readable details for timeline reconstruction
    public string Description { get; set; } = string.Empty;

    // Severity score assigned by the monitoring module
    public int SeverityScore { get; set; }

    // Timestamp when event was detected
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
