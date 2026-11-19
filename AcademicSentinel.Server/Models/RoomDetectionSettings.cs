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

    // OPTIONAL — comma-separated allowlist of apps the student may
    // alt-tab to during the exam without producing WINDOW_SWITCH /
    // PROCESS_DETECTED violations.
    //
    // Token formats supported (mixed freely in one CSV):
    //   • Process tokens   — "teams", "zoom", "notepad", "calc",
    //                        "acrord32", etc. No ".exe" suffix.
    //                        Matched against Process.ProcessName of
    //                        the foreground window, case-insensitive.
    //   • Domain tokens    — anything containing a "." like
    //                        "meet.google.com", "teams.microsoft.com".
    //                        Matched as a case-insensitive substring
    //                        of the foreground BROWSER window's title,
    //                        so a Google Meet tab passes without
    //                        whitelisting the entire browser.
    //
    // Empty / null disables the feature entirely. Tokens are
    // canonicalised (trim, lowercase, strip ".exe") at parse time on
    // the SAC side; the DB stores whatever the instructor typed.
    [MaxLength(2000)]
    public string? AllowedAppsCsv { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}