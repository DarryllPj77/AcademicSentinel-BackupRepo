using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.Models;
using AcademicSentinel.Server.DTOs;
using System.Security.Claims;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace AcademicSentinel.Server.Hubs;

[Authorize] // 1. Secure the Hub so only logged-in apps can connect!
public class MonitoringHub : Hub
{
    private readonly AppDbContext _context;
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly ConcurrentDictionary<int, bool> MonitoringStates = new();

    public MonitoringHub(AppDbContext context, IServiceScopeFactory scopeFactory)
    {
        _context = context;
        _scopeFactory = scopeFactory;
    }

    // =======================================================
    // IMC (TEACHER) CALLS THIS TO LISTEN FOR ALERTS
    // =======================================================
    public async Task JoinRoom(string roomId)
    {
        // Adds the teacher to the SignalR group for this specific exam
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
    }

    public async Task SetMonitoringState(int roomId, bool isActive)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        MonitoringStates[roomId] = isActive;
        await Clients.Group(roomId.ToString()).SendAsync("MonitoringStateChanged", isActive);
    }

    public Task<bool> GetMonitoringState(int roomId)
    {
        return Task.FromResult(MonitoringStates.TryGetValue(roomId, out var isActive) && isActive);
    }

    public async Task PauseSessionMonitoring(int roomId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        await Clients.Group(roomId.ToString()).SendAsync("MonitoringPaused");
    }

    public async Task ResumeSessionMonitoring(int roomId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        await Clients.Group(roomId.ToString()).SendAsync("MonitoringResumed");
    }

    public async Task BeginMonitoringCountdown(int roomId, int delaySeconds, int monitoringDurationSeconds)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        MonitoringStates[roomId] = false;
        await Clients.Group(roomId.ToString()).SendAsync("MonitoringCountdownStarted", delaySeconds, monitoringDurationSeconds);

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, delaySeconds)));
            MonitoringStates[roomId] = true;
            await Clients.Group(roomId.ToString()).SendAsync("MonitoringStateChanged", true);
        });
    }
    // 1
    public async Task EndSessionOnDisconnect(int roomId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.Status != "Active")
            return;

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        if (activeSession == null)
            return;

        activeSession.Status = "Completed";
        activeSession.EndTime = DateTime.UtcNow;
        room.Status = "Pending";

        MonitoringStates[roomId] = false;
        await _context.SaveChangesAsync();

        await Clients.Group(roomId.ToString()).SendAsync("MonitoringStateChanged", false);
        await Clients.Group(roomId.ToString()).SendAsync("SessionEnded");
    }

    // SAC calls this when the student enters the active exam room
    public async Task JoinLiveExam(int roomId)
    {
        try
        {
            var userIdString = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (userIdString == null)
            {
                await Clients.Caller.SendAsync("JoinFailed", "User identity not found.");
                return;
            }

            int studentId = int.Parse(userIdString);

            var studentUser = await _context.Users.FindAsync(studentId);
            if (studentUser == null)
            {
                await Clients.Caller.SendAsync("JoinFailed", "Student record not found in database.");
                return;
            }

            var room = await _context.Rooms.FindAsync(roomId);
            if (room == null || room.Status != "Active")
            {
                await Clients.Caller.SendAsync("JoinFailed", "Cannot join room: session is inactive.");
                return;
            }

            var activeSession = await _context.ExamSessions
                .Where(s => s.RoomId == roomId && s.Status == "Active")
                .OrderByDescending(s => s.StartTime)
                .FirstOrDefaultAsync();

            await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString());

            var participant = await _context.SessionParticipants
                .Where(p => p.RoomId == roomId && p.StudentId == studentId && (activeSession == null || p.JoinedAt >= activeSession.StartTime))
                .OrderByDescending(p => p.JoinedAt)
                .FirstOrDefaultAsync();

            if (participant != null && participant.ConnectionStatus == "Completed")
            {
                await Clients.Caller.SendAsync("JoinFailed", "You have already completed and exited this active session.");
                return;
            }

            if (participant == null)
            {
                participant = new SessionParticipant
                {
                    RoomId = roomId,
                    StudentId = studentId,
                    ConnectionStatus = "Connected",
                    JoinedAt = DateTime.UtcNow
                };
                _context.SessionParticipants.Add(participant);
            }
            else
            {
                // Update existing record for Reconnection
                participant.ConnectionStatus = "Connected";
                participant.JoinedAt = DateTime.UtcNow;
                participant.DisconnectedAt = null;
            }

            _context.MonitoringEvents.Add(new MonitoringEvent
            {
                RoomId = roomId,
                StudentId = studentId,
                EventType = "SYSTEM",
                Description = $"✅ SESSION JOINED / CONNECTION RESTORED. ({studentUser.Email})",
                SeverityScore = 0,
                Timestamp = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();

            await Clients.Group(roomId.ToString()).SendAsync("StudentJoined", studentId);
            string studentDisplayName = string.IsNullOrWhiteSpace(studentUser.FullName) ? studentUser.Email : studentUser.FullName;
            await Clients.Group(roomId.ToString()).SendAsync("StudentJoinedOrReconnected", studentId, studentDisplayName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"JoinLiveExam FAILED for Room {roomId} / Student {Context.User?.Identity?.Name}: {ex.ToString()}");
            await Clients.Caller.SendAsync("JoinFailed", "An unexpected internal server error occurred while finalizing your join.");
        }
    }

    // SignalR AUTOMATICALLY triggers this if a user's app closes or internet drops
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;

        if (string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase)
            && userIdString != null
            && int.TryParse(userIdString, out var instructorId))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            try
            {
                var activeRoom = await db.Rooms
                    .Where(r => r.InstructorId == instructorId && r.Status == "Active")
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefaultAsync();

                if (activeRoom != null)
                {
                    var activeSession = await db.ExamSessions
                        .Where(s => s.RoomId == activeRoom.Id && s.Status == "Active")
                        .OrderByDescending(s => s.StartTime)
                        .FirstOrDefaultAsync();

                    activeRoom.Status = "Ended";
                    if (activeSession != null)
                    {
                        activeSession.Status = "Ended";
                        activeSession.EndTime = DateTime.UtcNow;
                    }

                    await db.SaveChangesAsync();

                    await Clients.Group(activeRoom.Id.ToString()).SendAsync("SessionInterrupted", activeRoom.Id);
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                // best-effort instructor disconnect handling under concurrent drops
            }
        }
        else if (userIdString != null && int.TryParse(userIdString, out var studentId))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            try
            {
                var hasCompletedSessionParticipant = await db.SessionParticipants
                    .AnyAsync(p => p.StudentId == studentId && p.ConnectionStatus == "Completed");

                if (hasCompletedSessionParticipant)
                {
                    await base.OnDisconnectedAsync(exception);
                    return;
                }

                var activeParticipants = await db.SessionParticipants
                    .Where(p => p.StudentId == studentId && p.ConnectionStatus == "Connected")
                    .ToListAsync();

                foreach (var participant in activeParticipants)
                {
                    participant.ConnectionStatus = "Disconnected";
                    participant.DisconnectedAt = DateTime.UtcNow;

                    db.MonitoringEvents.Add(new MonitoringEvent
                    {
                        EventType = "SYSTEM",
                        Description = "⚠️ CONNECTION LOST. Student dropped offline.",
                        SeverityScore = 0,
                        RoomId = participant.RoomId,
                        StudentId = studentId,
                        Timestamp = DateTime.UtcNow
                    });
                }

                await db.SaveChangesAsync();

                foreach (var participant in activeParticipants)
                {
                    var roomGroup = participant.RoomId.ToString();
                    await Clients.Group(roomGroup).SendAsync("StudentDisconnected", studentId);
                    await Clients.Group(roomGroup).SendAsync("StudentConnectionLost", studentId);
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                // best-effort disconnect update under concurrent drops
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task ReSyncState(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;

        int authenticatedStudentId = int.Parse(userIdString);
        if (authenticatedStudentId != studentId) return;

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null)
            return;

        var isSessionEnded = !string.Equals(room.Status, "Active", StringComparison.OrdinalIgnoreCase);

        var leaveAlreadyGranted = await _context.MonitoringEvents
            .AnyAsync(e => e.RoomId == roomId
                        && e.StudentId == studentId
                        && e.EventType == "LEAVE_GRANTED");

        if (isSessionEnded || leaveAlreadyGranted)
        {
            await Clients.Client(Context.ConnectionId).SendAsync("LeaveGranted", studentId);
        }
    }

    /// <summary>
    /// SAC calls this to send monitoring events in real-time
    /// This method receives detected violations from the Secure Assessment Client
    /// and stores them in the database, then relays alerts to the Instructor Monitoring Console
    /// </summary>
    public async Task SendMonitoringEvent(int roomId, int studentId, MonitoringEventDto eventData)
    {
        // Extract the Student's ID from their JWT to verify they're sending their own data
        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;

        int authenticatedStudentId = int.Parse(userIdString);

        // Security check: Students can only report their own events
        if (authenticatedStudentId != studentId) return;

        var latestParticipantState = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .Select(p => p.ConnectionStatus)
            .FirstOrDefaultAsync();

        if (string.Equals(latestParticipantState, "Completed", StringComparison.OrdinalIgnoreCase))
            return;

        // Verify the room exists
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return;

        // Create the monitoring event record
        var monitoringEvent = new MonitoringEvent
        {
            RoomId = roomId,
            StudentId = studentId,
            EventType = eventData.EventType,
            Description = eventData.Description,
            SeverityScore = eventData.SeverityScore,
            Timestamp = DateTime.UtcNow
        };

        _context.MonitoringEvents.Add(monitoringEvent);
        await _context.SaveChangesAsync();

        // Broadcast violation alert to the Instructor Monitoring Console
        // The IMC will display this as a real-time violation alert
        await Clients.Group(roomId.ToString()).SendAsync("ViolationDetected", new
        {
            studentId = studentId,
            eventType = eventData.EventType,
            severityScore = eventData.SeverityScore,
            description = eventData.Description,
            timestamp = DateTime.UtcNow
        });
    }

    public async Task UpdateHardwareState(int roomId, int studentId, bool isVm, bool isRemote)
    {
        await Clients.Group(roomId.ToString()).SendAsync("ReceiveHardwareStateUpdate", studentId, isVm, isRemote);
    }

    public async Task RequestLeave(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;

        int authenticatedStudentId = int.Parse(userIdString);
        if (authenticatedStudentId != studentId) return;

        var isParticipantInRoom = await _context.SessionParticipants
            .AnyAsync(p => p.RoomId == roomId && p.StudentId == studentId);

        if (!isParticipantInRoom)
            return;

        await Clients.Group(roomId.ToString()).SendAsync("LeaveRequested", studentId);
    }

    public async Task GrantLeave(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var isParticipantInRoom = await db.SessionParticipants
                .AnyAsync(p => p.RoomId == roomId && p.StudentId == studentId);

            if (!isParticipantInRoom)
                return;

            var leaveGrantedEvent = new MonitoringEvent
            {
                RoomId = roomId,
                StudentId = studentId,
                EventType = "LEAVE_GRANTED",
                SeverityScore = 0,
                Timestamp = DateTime.UtcNow
            };

            db.MonitoringEvents.Add(leaveGrantedEvent);

            var participant = await db.SessionParticipants
                .Where(p => p.RoomId == roomId && p.StudentId == studentId)
                .OrderByDescending(p => p.JoinedAt)
                .FirstOrDefaultAsync();

            if (participant != null)
            {
                participant.ConnectionStatus = "Disconnected";
                participant.DisconnectedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
        }

        await Clients.User(studentId.ToString()).SendAsync("LeaveGranted", studentId);
        await Clients.Group(roomId.ToString()).SendAsync("StudentLeftSession", studentId);
        await Clients.Group(roomId.ToString()).SendAsync("LeaveApprovalUpdated", studentId, true);
    }

    public async Task NotifyStudentLeftSafely(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null)
            return;

        int authenticatedStudentId = int.Parse(userIdString);
        if (authenticatedStudentId != studentId)
            return;

        var participant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();

        if (participant != null)
        {
            participant.ConnectionStatus = "Completed";
            participant.DisconnectedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        await Clients.Group(roomId.ToString()).SendAsync("StudentLeftSession", studentId);
    }
}