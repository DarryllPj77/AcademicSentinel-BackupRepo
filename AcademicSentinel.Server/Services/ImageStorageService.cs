using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace AcademicSentinel.Server.Services;

/// <summary>
/// Handles image storage for user profiles and room images.
/// Stores images in a secure directory and tracks metadata in the database.
/// </summary>
public interface IImageStorageService
{
    /// <summary>
    /// Saves a user profile image and returns metadata
    /// </summary>
    Task<ImageUploadResult> SaveUserProfileImageAsync(int userId, IFormFile imageFile);

    /// <summary>
    /// Saves a room image and returns metadata
    /// </summary>
    Task<ImageUploadResult> SaveRoomImageAsync(int roomId, IFormFile imageFile);

    /// <summary>
    /// Retrieves a user profile image
    /// </summary>
    Task<ImageRetrievalResult?> GetUserProfileImageAsync(int userId);

    /// <summary>
    /// Retrieves a room image
    /// </summary>
    Task<ImageRetrievalResult?> GetRoomImageAsync(int roomId);

    /// <summary>
    /// Deletes a user profile image
    /// </summary>
    Task<bool> DeleteUserProfileImageAsync(int userId);

    /// <summary>
    /// Deletes a room image
    /// </summary>
    Task<bool> DeleteRoomImageAsync(int roomId);

    /// <summary>
    /// Validates image file for security and size constraints
    /// </summary>
    bool IsValidImageFile(IFormFile file, out string errorMessage);
}

public class ImageStorageService : IImageStorageService
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;
    private readonly string _userProfilesDirectory;
    private readonly string _roomImagesDirectory;
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
    // Spec v4: profile and room images restricted to .png/.jpg/.jpeg only.
    private readonly string[] _allowedExtensions = { ".jpg", ".jpeg", ".png" };
    private readonly string[] _allowedMimeTypes =
    {
        "image/jpeg", "image/pjpeg",
        "image/png"
    };

    public ImageStorageService(IConfiguration configuration, IWebHostEnvironment env)
    {
        _configuration = configuration;
        _env = env;

        // FIX: WebRootPath is null if the wwwroot folder doesn't physically exist yet.
        // This forces the server to find the right path anyway!
        var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");

        // Create storage directories if they don't exist
        _userProfilesDirectory = Path.Combine(webRoot, "images", "profiles");
        _roomImagesDirectory = Path.Combine(webRoot, "images", "rooms");

        // This will now successfully create wwwroot/images/profiles and wwwroot/images/rooms
        Directory.CreateDirectory(_userProfilesDirectory);
        Directory.CreateDirectory(_roomImagesDirectory);
    }

    public async Task<ImageUploadResult> SaveUserProfileImageAsync(int userId, IFormFile imageFile)
    {
        if (!IsValidImageFile(imageFile, out var errorMessage))
        {
            return new ImageUploadResult { Success = false, ErrorMessage = errorMessage };
        }

        try
        {
            // Delete old image if exists
            await DeleteUserProfileImageAsync(userId);

            // Generate unique filename
            var fileName = $"user_{userId}_{Guid.NewGuid()}{Path.GetExtension(imageFile.FileName)}";
            var filePath = Path.Combine(_userProfilesDirectory, fileName);

            // Save file to disk
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await imageFile.CopyToAsync(stream);
            }

            // Return metadata
            return new ImageUploadResult
            {
                Success = true,
                FileName = fileName,
                FilePath = filePath,
                Url = $"/images/profiles/{fileName}",
                ContentType = imageFile.ContentType,
                SizeBytes = imageFile.Length,
                UploadedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new ImageUploadResult 
            { 
                Success = false, 
                ErrorMessage = $"Failed to save profile image: {ex.Message}" 
            };
        }
    }

    public async Task<ImageUploadResult> SaveRoomImageAsync(int roomId, IFormFile imageFile)
    {
        if (!IsValidImageFile(imageFile, out var errorMessage))
        {
            return new ImageUploadResult { Success = false, ErrorMessage = errorMessage };
        }

        try
        {
            // Delete old image if exists
            await DeleteRoomImageAsync(roomId);

            // Generate unique filename
            var fileName = $"room_{roomId}_{Guid.NewGuid()}{Path.GetExtension(imageFile.FileName)}";
            var filePath = Path.Combine(_roomImagesDirectory, fileName);

            // Save file to disk
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await imageFile.CopyToAsync(stream);
            }

            // Return metadata
            return new ImageUploadResult
            {
                Success = true,
                FileName = fileName,
                FilePath = filePath,
                Url = $"/images/rooms/{fileName}",
                ContentType = imageFile.ContentType,
                SizeBytes = imageFile.Length,
                UploadedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new ImageUploadResult 
            { 
                Success = false, 
                ErrorMessage = $"Failed to save room image: {ex.Message}" 
            };
        }
    }

    public async Task<ImageRetrievalResult?> GetUserProfileImageAsync(int userId)
    {
        try
        {
            var files = Directory.GetFiles(_userProfilesDirectory, $"user_{userId}_*");
            if (files.Length == 0) return null;

            var filePath = files[0]; // Get most recent file
            var fileInfo = new FileInfo(filePath);

            var fileBytes = await File.ReadAllBytesAsync(filePath);
            var contentType = GetContentType(filePath);

            return new ImageRetrievalResult
            {
                FileBytes = fileBytes,
                ContentType = contentType,
                FileName = Path.GetFileName(filePath),
                SizeBytes = fileInfo.Length
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<ImageRetrievalResult?> GetRoomImageAsync(int roomId)
    {
        try
        {
            var files = Directory.GetFiles(_roomImagesDirectory, $"room_{roomId}_*");
            if (files.Length == 0) return null;

            var filePath = files[0]; // Get most recent file
            var fileInfo = new FileInfo(filePath);

            var fileBytes = await File.ReadAllBytesAsync(filePath);
            var contentType = GetContentType(filePath);

            return new ImageRetrievalResult
            {
                FileBytes = fileBytes,
                ContentType = contentType,
                FileName = Path.GetFileName(filePath),
                SizeBytes = fileInfo.Length
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DeleteUserProfileImageAsync(int userId)
    {
        try
        {
            var files = Directory.GetFiles(_userProfilesDirectory, $"user_{userId}_*");
            foreach (var file in files)
            {
                File.Delete(file);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> DeleteRoomImageAsync(int roomId)
    {
        try
        {
            var files = Directory.GetFiles(_roomImagesDirectory, $"room_{roomId}_*");
            foreach (var file in files)
            {
                File.Delete(file);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsValidImageFile(IFormFile file, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (file == null || file.Length == 0)
        {
            errorMessage = "File is empty.";
            return false;
        }

        if (file.Length > MaxFileSizeBytes)
        {
            errorMessage = $"File size exceeds maximum allowed size of 5 MB.";
            return false;
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!_allowedExtensions.Contains(extension))
        {
            errorMessage = $"File type '{extension}' is not allowed. Only JPG, PNG, GIF, and WebP are supported.";
            return false;
        }

        if (!_allowedMimeTypes.Contains(file.ContentType.ToLowerInvariant()))
        {
            errorMessage = $"Invalid file MIME type. Only image files are allowed.";
            return false;
        }

        return true;
    }

    private string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}

/// <summary>
/// Result returned when an image is uploaded
/// </summary>
public class ImageUploadResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public string? Url { get; set; }
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
}

/// <summary>
/// Result returned when an image is retrieved
/// </summary>
public class ImageRetrievalResult
{
    public byte[] FileBytes { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "application/octet-stream";
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}
