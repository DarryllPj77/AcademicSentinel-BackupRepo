namespace AcademicSentinel.Server.Models;

public class Room
{
    public int Id { get; set; }

    public string SubjectName { get; set; } = string.Empty;

    // Links the room to the Instructor who created it
    public int InstructorId { get; set; }

    public string Status { get; set; } = "Pending";

    // The '?' makes it nullable. Null means it's a manual-assignment room!
    public string? EnrollmentCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Room image/icon storage
    public string? RoomImageUrl { get; set; } // URL to stored image
    public string? RoomImagePath { get; set; } // Local file path
    public string? RoomImageContentType { get; set; } // MIME type (image/png, image/jpeg, etc.)
    public long? RoomImageSize { get; set; } // File size in bytes
    public DateTime? RoomImageUploadedAt { get; set; } // When the image was uploaded

    // NEW: Tracks if the live monitoring feed is actively running
    public bool IsMonitoringActive { get; set; } = false;
}
