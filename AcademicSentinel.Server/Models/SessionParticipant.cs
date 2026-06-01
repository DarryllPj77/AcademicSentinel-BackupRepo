namespace AcademicSentinel.Server.Models;

public class SessionParticipant
{
    public int Id { get; set; }

    // Links to the Room
    public int RoomId { get; set; }

    // Links to the User (Student)
    public int StudentId { get; set; }

    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DisconnectedAt { get; set; } // Nullable because they are currently connected!

    public string ConnectionStatus { get; set; } = "Connected";
    public string? FinalRiskLevel { get; set; } // "Safe", "Suspicious", or "Possible Dishonesty"

    // Join/Rejoin approval state machine. "Approved" by default so existing
    // join paths and backfilled rows behave as before; flipped to "Pending"
    // by the request-join gate when the student has a pending LEAVE_GRANTED.
    public string JoinApprovalStatus { get; set; } = "Approved";
    public bool IsCurrentlyActive { get; set; } = true;
}