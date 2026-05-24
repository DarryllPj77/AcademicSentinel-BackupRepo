namespace AcademicSentinel.Server.DTOs;

/// <summary>
/// DTO for enrolling a student using a room enrollment code
/// </summary>
public class EnrollByCodeDto
{
    public string EnrollmentCode { get; set; } = string.Empty;
}

public class RoomSetupDto
{
    public bool EnableClipboardMonitoring { get; set; }
    public bool EnableProcessDetection { get; set; }
    public bool EnableIdleDetection { get; set; }
    public int IdleThresholdSeconds { get; set; }
    public bool EnableFocusDetection { get; set; }
    public bool EnableVirtualizationCheck { get; set; }
    public bool StrictMode { get; set; }

    // REQUIRED — LMS exam URL anchored focus detection (see RoomDetectionSettings).
    // Server-side validation on save: must be a valid HTTPS absolute URL.
    public string LmsExamUrl { get; set; } = string.Empty;

    // OPTIONAL — comma-separated allowed-apps list (see
    // RoomDetectionSettings.AllowedAppsCsv for the supported token
    // formats and runtime semantics).
    public string? AllowedAppsCsv { get; set; }
}

public class StartSessionDto
{
    public string ExamType { get; set; } = "Summative";
}

/// <summary>
/// DTO for getting room status
/// </summary>
public class RoomStatusDto
{
    public int RoomId { get; set; }
    public string SubjectName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int TotalEnrolled { get; set; }
    public int TotalJoined { get; set; }
    public int TotalNotJoined { get; set; }
}

/// <summary>
/// DTO for participant information in a room
/// </summary>
public class ParticipantDto
{
    public int StudentId { get; set; }
    public string StudentEmail { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public string? ProfileImageUrl { get; set; }
    public string EnrollmentSource { get; set; } = string.Empty; // "Manual" or "Code"
    public DateTime EnrolledAt { get; set; }
    public DateTime? JoinedAt { get; set; }
    public string ParticipationStatus { get; set; } = string.Empty; // "Joined", "NotJoined", "Disconnected"
    public string ConnectionStatus { get; set; } = string.Empty; // "Connected", "Disconnected"
    public DateTime? DisconnectedAt { get; set; }
}

/// <summary>
/// DTO for monitoring event received from SAC
/// </summary>
public class MonitoringEventDto
{
    public int RoomId { get; set; }
    public string EventType { get; set; } = string.Empty; // ALT_TAB, PROCESS, CLIPBOARD, IDLE, VM, etc.
    public int SeverityScore { get; set; }
    public string Description { get; set; } = string.Empty;
    public int CurrentScore { get; set; }
    public string CurrentLevel { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// DTO for image upload response
/// </summary>
public class ImageUploadResponseDto
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Url { get; set; }
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
}

/// <summary>
/// DTO for user profile information including image
/// </summary>
public class UserProfileDto
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? ProfileImageUrl { get; set; }
    public DateTime? ProfileImageUploadedAt { get; set; }
}

/// <summary>
/// DTO for room information including image
/// </summary>
public class RoomWithImageDto
{
    public int Id { get; set; }
    public string SubjectName { get; set; } = string.Empty;
    public int InstructorId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? EnrollmentCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? RoomImageUrl { get; set; }
    public DateTime? RoomImageUploadedAt { get; set; }
}
