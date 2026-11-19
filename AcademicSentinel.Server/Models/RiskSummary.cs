namespace AcademicSentinel.Server.Models;

public class RiskSummary
{
    public int Id { get; set; }

    // Links to the Room
    public int RoomId { get; set; }

    // Links to the Student
    public int StudentId { get; set; }

    // The computed statistics when the exam ends
    public int TotalViolations { get; set; }
    public int TotalSeverityScore { get; set; }
    public string RiskLevel { get; set; } = "Safe"; // "Safe", "Suspicious", or "Possible Dishonesty"

    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}