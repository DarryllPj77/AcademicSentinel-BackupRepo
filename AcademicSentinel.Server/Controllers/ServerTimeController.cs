using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AcademicSentinel.Server.Controllers;

/// <summary>
/// Returns the server's authoritative UTC clock so the SAC can detect
/// system-time tampering on the student's machine (HAS module).
/// </summary>
[ApiController]
[Route("api/server")]
public class ServerTimeController : ControllerBase
{
    [HttpGet("time")]
    [Authorize] // any authenticated user (Student or Instructor)
    public IActionResult GetServerTime()
    {
        return Ok(new
        {
            utcNow = DateTime.UtcNow,
            // ISO-8601 with offset for clients that prefer string parsing.
            iso = DateTime.UtcNow.ToString("o")
        });
    }
}
