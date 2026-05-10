using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

namespace AcademicSentinel.Server.Services;

/// <summary>
/// Cloud-backed image storage that survives Render/DigitalOcean ephemeral
/// filesystems. Each user/room owns a deterministic public_id so re-uploads
/// overwrite the previous asset and the URL stays stable.
///
/// Configured via CLOUDINARY_URL env var
/// (format: cloudinary://api_key:api_secret@cloud_name).
/// </summary>
public class CloudinaryImageStorageService : IImageStorageService
{
    private readonly Cloudinary _cloudinary;
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;
    private readonly string[] _allowedExtensions = { ".jpg", ".jpeg", ".png" };
    private readonly string[] _allowedMimeTypes =
    {
        "image/jpeg", "image/pjpeg", "image/png"
    };

    private const string ProfileFolder = "academicsentinel/profiles";
    private const string RoomFolder    = "academicsentinel/rooms";

    public CloudinaryImageStorageService(IConfiguration configuration)
    {
        var url = Environment.GetEnvironmentVariable("CLOUDINARY_URL")
                  ?? configuration["Cloudinary:Url"];
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "CLOUDINARY_URL is not configured but CloudinaryImageStorageService was registered.");

        _cloudinary = new Cloudinary(url) { Api = { Secure = true } };
    }

    public Task<ImageUploadResult> SaveUserProfileImageAsync(int userId, IFormFile imageFile) =>
        UploadAsync(imageFile, ProfileFolder, $"user_{userId}");

    public Task<ImageUploadResult> SaveRoomImageAsync(int roomId, IFormFile imageFile) =>
        UploadAsync(imageFile, RoomFolder, $"room_{roomId}");

    private async Task<ImageUploadResult> UploadAsync(IFormFile imageFile, string folder, string publicId)
    {
        if (!IsValidImageFile(imageFile, out var error))
            return new ImageUploadResult { Success = false, ErrorMessage = error };

        try
        {
            await using var stream = imageFile.OpenReadStream();
            var uploadParams = new ImageUploadParams
            {
                File = new FileDescription(imageFile.FileName, stream),
                PublicId = publicId,
                Folder = folder,
                Overwrite = true,
                // Strip EXIF and re-encode to a sane size — defends against
                // megapixel uploads chewing through bandwidth quota.
                Transformation = new Transformation()
                    .Width(1024).Height(1024).Crop("limit").Quality("auto")
            };

            var result = await _cloudinary.UploadAsync(uploadParams);
            if (result.Error != null)
                return new ImageUploadResult { Success = false, ErrorMessage = result.Error.Message };

            return new ImageUploadResult
            {
                Success = true,
                FileName = result.PublicId,
                FilePath = result.PublicId,
                Url = result.SecureUrl?.ToString(),
                ContentType = imageFile.ContentType,
                SizeBytes = result.Bytes,
                UploadedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new ImageUploadResult
            {
                Success = false,
                ErrorMessage = $"Cloudinary upload failed: {ex.Message}"
            };
        }
    }

    public Task<ImageRetrievalResult?> GetUserProfileImageAsync(int userId) =>
        FetchAsync($"{ProfileFolder}/user_{userId}");

    public Task<ImageRetrievalResult?> GetRoomImageAsync(int roomId) =>
        FetchAsync($"{RoomFolder}/room_{roomId}");

    private async Task<ImageRetrievalResult?> FetchAsync(string publicId)
    {
        try
        {
            var resource = await _cloudinary.GetResourceAsync(publicId);
            if (resource == null || string.IsNullOrEmpty(resource.SecureUrl))
                return null;

            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(resource.SecureUrl);
            return new ImageRetrievalResult
            {
                FileBytes = bytes,
                ContentType = $"image/{resource.Format}",
                FileName = resource.PublicId,
                SizeBytes = resource.Bytes
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DeleteUserProfileImageAsync(int userId) =>
        await DeleteAsync($"{ProfileFolder}/user_{userId}");

    public async Task<bool> DeleteRoomImageAsync(int roomId) =>
        await DeleteAsync($"{RoomFolder}/room_{roomId}");

    private async Task<bool> DeleteAsync(string publicId)
    {
        try
        {
            var result = await _cloudinary.DestroyAsync(new DeletionParams(publicId));
            return result.Result == "ok" || result.Result == "not found";
        }
        catch
        {
            return false;
        }
    }

    public bool IsValidImageFile(IFormFile file, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (file == null || file.Length == 0) { errorMessage = "File is empty."; return false; }
        if (file.Length > MaxFileSizeBytes) { errorMessage = "File exceeds 5 MB."; return false; }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!_allowedExtensions.Contains(ext))
        {
            errorMessage = $"File type '{ext}' not allowed. Only JPG, JPEG, PNG.";
            return false;
        }
        if (!_allowedMimeTypes.Contains(file.ContentType.ToLowerInvariant()))
        {
            errorMessage = "Invalid MIME type.";
            return false;
        }
        return true;
    }
}
