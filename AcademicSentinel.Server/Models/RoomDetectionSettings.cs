using System.ComponentModel.DataAnnotations;

namespace AcademicSentinel.Server.Models;

public class RoomDetectionSettings
{
    public int Id { get; set; }

    // This links these settings to a specific Room
    public int RoomId { get; set; }

    // The specific modules to toggle
    public bool EnableClipboardMonitoring { get; set; } = true;
    public bool EnableProcessDetection { get; set; } = true;
    public bool EnableIdleDetection { get; set; } = true;
    public int IdleThresholdSeconds { get; set; } = 300; // Default 5 minutes
    public bool EnableFocusDetection { get; set; } = true;
    public bool EnableVirtualizationCheck { get; set; } = true;
    public bool StrictMode { get; set; } = false;

    // REQUIRED — LMS exam URL anchored focus detection.
    // The SAC uses this to identify the only approved non-SAC focus target
    // (the browser window whose title contains this URL's domain). The
    // session cannot be started without a valid URL set here.
    [Required]
    [MaxLength(500)]
    public string LmsExamUrl { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}