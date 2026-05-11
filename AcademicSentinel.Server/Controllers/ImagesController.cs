using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.DTOs;
using AcademicSentinel.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace AcademicSentinel.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class ImagesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IImageStorageService _imageStorageService;
    private readonly ILogger<ImagesController> _logger;

    public ImagesController(AppDbContext context, IImageStorageService imageStorageService, ILogger<ImagesController> logger)
    {
        _context = context;
        _imageStorageService = imageStorageService;
        _logger = logger;
    }

    /// <summary>
    /// Upload a user profile image
    /// </summary>
    [HttpPost("profile")]
    [Authorize]
    public async Task<IActionResult> UploadProfileImage(IFormFile image)
    {
        if (image == null || image.Length == 0)
            return BadRequest("No image file provided.");

        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();

        int userId = int.Parse(userIdString);

        // Validate image
        if (!_imageStorageService.IsValidImageFile(image, out var errorMessage))
            return BadRequest(errorMessage);

        // Get user
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound("User not found.");

        // Save image
        var uploadResult = await _imageStorageService.SaveUserProfileImageAsync(userId, image);
        if (!uploadResult.Success)
            return BadRequest(uploadResult.ErrorMessage);

        // Update user with image metadata
        user.ProfileImageUrl = uploadResult.Url;
        user.ProfileImagePath = uploadResult.FilePath;
        user.ProfileImageContentType = uploadResult.ContentType;
        user.ProfileImageSize = uploadResult.SizeBytes;
        user.ProfileImageUploadedAt = uploadResult.UploadedAt;

        await _context.SaveChangesAsync();

        return Ok(new ImageUploadResponseDto
        {
            Success = true,
            Message = "Profile image uploaded successfully.",
            Url = uploadResult.Url,
            ContentType = uploadResult.ContentType,
            SizeBytes = uploadResult.SizeBytes,
            UploadedAt = uploadResult.UploadedAt
        });
    }

    /// <summary>
    /// Get a specific user's profile image
    /// </summary>
    [HttpGet("profile/{userId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProfileImage(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound("User not found.");

        if (string.IsNullOrEmpty(user.ProfileImagePath))
            return NotFound("User has no profile image.");

        var imageResult = await _imageStorageService.GetUserProfileImageAsync(userId);
        if (imageResult == null)
            return NotFound("Profile image not found.");

        return File(imageResult.FileBytes, imageResult.ContentType, imageResult.FileName);
    }

    /// <summary>
    /// Delete user's profile image
    /// </summary>
    [HttpDelete("profile")]
    [Authorize]
    public async Task<IActionResult> DeleteProfileImage()
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();

        int userId = int.Parse(userIdString);

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound("User not found.");

        // Delete from storage
        var deleteResult = await _imageStorageService.DeleteUserProfileImageAsync(userId);
        if (!deleteResult)
            return BadRequest("Failed to delete profile image.");

        // Clear metadata from database
        user.ProfileImageUrl = null;
        user.ProfileImagePath = null;
        user.ProfileImageContentType = null;
        user.ProfileImageSize = null;
        user.ProfileImageUploadedAt = null;

        await _context.SaveChangesAsync();

        return Ok(new { message = "Profile image deleted successfully." });
    }

    /// <summary>
    /// Upload a room image
    /// </summary>
    [HttpPost("room/{roomId}")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> UploadRoomImage(int roomId, IFormFile image)
    {
        _logger.LogInformation("UploadRoomImage start: roomId={RoomId}, fileName={FileName}, size={Size}, contentType={ContentType}, backend={Backend}",
            roomId, image?.FileName, image?.Length, image?.ContentType, _imageStorageService.GetType().Name);

        if (image == null || image.Length == 0)
        {
            _logger.LogWarning("UploadRoomImage: empty file payload for roomId={RoomId}", roomId);
            return BadRequest("No image file provided.");
        }

        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();

        int instructorId = int.Parse(userIdString);

        if (!_imageStorageService.IsValidImageFile(image, out var errorMessage))
        {
            _logger.LogWarning("UploadRoomImage validation failed for roomId={RoomId}: {Error}", roomId, errorMessage);
            return BadRequest(errorMessage);
        }

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return NotFound("Room not found.");

        if (room.InstructorId != instructorId)
            return Forbid();

        ImageUploadResult uploadResult;
        try
        {
            uploadResult = await _imageStorageService.SaveRoomImageAsync(roomId, image);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UploadRoomImage threw for roomId={RoomId}", roomId);
            return StatusCode(500, $"Upload backend threw: {ex.Message}");
        }

        if (!uploadResult.Success)
        {
            _logger.LogWarning("UploadRoomImage backend rejected roomId={RoomId}: {Error}", roomId, uploadResult.ErrorMessage);
            return BadRequest(uploadResult.ErrorMessage);
        }

        _logger.LogInformation("UploadRoomImage ok: roomId={RoomId}, url={Url}", roomId, uploadResult.Url);

        // Update room with image metadata
        room.RoomImageUrl = uploadResult.Url;
        room.RoomImagePath = uploadResult.FilePath;
        room.RoomImageContentType = uploadResult.ContentType;
        room.RoomImageSize = uploadResult.SizeBytes;
        room.RoomImageUploadedAt = uploadResult.UploadedAt;

        await _context.SaveChangesAsync();

        return Ok(new ImageUploadResponseDto
        {
            Success = true,
            Message = "Room image uploaded successfully.",
            Url = uploadResult.Url,
            ContentType = uploadResult.ContentType,
            SizeBytes = uploadResult.SizeBytes,
            UploadedAt = uploadResult.UploadedAt
        });
    }

    /// <summary>
    /// Get a specific room's image
    /// </summary>
    [HttpGet("room/{roomId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetRoomImage(int roomId)
    {
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return NotFound("Room not found.");

        if (string.IsNullOrEmpty(room.RoomImagePath))
            return NotFound("Room has no image.");

        var imageResult = await _imageStorageService.GetRoomImageAsync(roomId);
        if (imageResult == null)
            return NotFound("Room image not found.");

        return File(imageResult.FileBytes, imageResult.ContentType, imageResult.FileName);
    }

    /// <summary>
    /// Delete a room's image (Instructor only)
    /// </summary>
    [HttpDelete("room/{roomId}")]
    [Authorize(Roles = "Instructor")]
    public async Task<IActionResult> DeleteRoomImage(int roomId)
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();

        int instructorId = int.Parse(userIdString);

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return NotFound("Room not found.");

        // Verify instructor owns the room
        if (room.InstructorId != instructorId)
            return Forbid();

        // Delete from storage
        var deleteResult = await _imageStorageService.DeleteRoomImageAsync(roomId);
        if (!deleteResult)
            return BadRequest("Failed to delete room image.");

        // Clear metadata from database
        room.RoomImageUrl = null;
        room.RoomImagePath = null;
        room.RoomImageContentType = null;
        room.RoomImageSize = null;
        room.RoomImageUploadedAt = null;

        await _context.SaveChangesAsync();

        return Ok(new { message = "Room image deleted successfully." });
    }

    /// <summary>
    /// Get current user's profile with image URL
    /// </summary>
    [HttpGet("profile")]
    [Authorize]
    public async Task<IActionResult> GetCurrentUserProfile()
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return Unauthorized();

        int userId = int.Parse(userIdString);
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound("User not found.");

        return Ok(new UserProfileDto
        {
            Id = user.Id,
            Email = user.Email,
            Role = user.Role,
            CreatedAt = user.CreatedAt,
            ProfileImageUrl = user.ProfileImageUrl,
            ProfileImageUploadedAt = user.ProfileImageUploadedAt
        });
    }

    /// <summary>
    /// Get room details with image URL
    /// </summary>
    [HttpGet("room/{roomId}/details")]
    [Authorize]
    public async Task<IActionResult> GetRoomWithImage(int roomId)
    {
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return NotFound("Room not found.");

        return Ok(new RoomWithImageDto
        {
            Id = room.Id,
            SubjectName = room.SubjectName,
            InstructorId = room.InstructorId,
            Status = room.Status,
            EnrollmentCode = room.EnrollmentCode,
            CreatedAt = room.CreatedAt,
            RoomImageUrl = room.RoomImageUrl,
            RoomImageUploadedAt = room.RoomImageUploadedAt
        });
    }
}
