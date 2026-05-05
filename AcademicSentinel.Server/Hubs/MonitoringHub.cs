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

        // 1. Update In-Memory Dictionary
        MonitoringStates[roomId] = isActive;

        // 2. UPDATE THE DATABASE FOR THE GATEKEEPER!
        var room = await _context.Rooms.FindAsync(roomId);
        if (room != null)
        {
            room.IsMonitoringActive = isActive;
            await _context.SaveChangesAsync();
        }

        await Clients.Group(roomId.ToString()).SendAsync("MonitoringStateChanged", isActive);
    }

    public async Task PauseSessionMonitoring(int roomId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        MonitoringStates[roomId] = false;

        // OPEN THE GATE (Optional, but aligns with paused state)
        var room = await _context.Rooms.FindAsync(roomId);
        if (room != null)
        {
            room.IsMonitoringActive = false;
            await _context.SaveChangesAsync();
        }

        await Clients.Group(roomId.ToString()).SendAsync("MonitoringPaused");
    }

    public async Task ResumeSessionMonitoring(int roomId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        MonitoringStates[roomId] = true;

        // LOCK THE GATE
        var room = await _context.Rooms.FindAsync(roomId);
        if (room != null)
        {
            room.IsMonitoringActive = true;
            await _context.SaveChangesAsync();
        }

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
        // Extract the Student's ID from their JWT
        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;
        int studentId = int.Parse(userIdString);

        var student = await _context.Users.FindAsync(studentId);

        // 1. Verify the room exists and is in Active state
        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null) return;

        if (room.Status != "Active")
        {
            // Notify the client that they cannot join yet
            await Clients.Caller.SendAsync("JoinFailed", "Cannot join room: the instructor has not started the session or has ended it.");
            return;
        }

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();

        // 2. Add connection to the SignalR Room Group
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString());

        // 3. Update Database: Mark as officially "Participating" and "Connected"
        var participant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();

        bool shouldCreateNewParticipant = participant == null
            || (activeSession != null && participant.JoinedAt < activeSession.StartTime);

        if (shouldCreateNewParticipant)
        {
            // First join for the current active session
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
            // Reconnecting after a drop
            participant.ConnectionStatus = "Connected";
            participant.JoinedAt = DateTime.UtcNow;
            participant.DisconnectedAt = null;
        }
        await _context.SaveChangesAsync();

        // 4. Notify the IMC Dashboard that the student is live!
        await Clients.Group(roomId.ToString()).SendAsync("StudentJoinedOrReconnected", student?.Id ?? studentId, student?.FullName ?? $"Student #{studentId}");
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

    // =======================================================
    // JOIN APPROVAL STATE MACHINE
    // =======================================================

    // SAC calls this after the request-join HTTP gate returns 202 Pending.
    // The hub re-validates against the DB so a malicious client cannot fake
    // a pending state, then surfaces the request to the IMC in real time.
    public async Task NotifyInstructorStudentPending(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Student", StringComparison.OrdinalIgnoreCase))
            return;

        var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null) return;
        int authenticatedStudentId = int.Parse(userIdString);
        if (authenticatedStudentId != studentId) return;

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (activeSession == null) return;

        var participant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();

        if (participant == null
            || participant.JoinedAt < activeSession.StartTime
            || !string.Equals(participant.JoinApprovalStatus, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var hadLeaveGranted = await _context.MonitoringEvents
            .AnyAsync(e => e.RoomId == roomId
                        && e.StudentId == studentId
                        && e.EventType == "LEAVE_GRANTED"
                        && e.Timestamp >= activeSession.StartTime);

        var student = await _context.Users.FindAsync(studentId);

        await Clients.Group(roomId.ToString()).SendAsync("StudentPendingApproval", new
        {
            roomId,
            studentId,
            participantId = participant.Id,
            studentName = student?.FullName ?? $"Student #{studentId}",
            studentEmail = student?.Email,
            profileImageUrl = student?.ProfileImageUrl,
            isRejoin = hadLeaveGranted,
            isLate = !hadLeaveGranted,
            requestedAt = DateTime.UtcNow
        });
    }

    public async Task ApproveStudentJoin(int roomId, int studentId)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        var instructorIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (instructorIdString == null || !int.TryParse(instructorIdString, out var instructorId))
            return;

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.Status != "Active" || room.InstructorId != instructorId) return;

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (activeSession == null) return;

        var participant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();
        if (participant == null || participant.JoinedAt < activeSession.StartTime) return;

        participant.JoinApprovalStatus = "Approved";
        participant.IsCurrentlyActive = true;

        // REJOIN_APPROVED supersedes any prior LEAVE_GRANTED for this student
        // in this session, so the HTTP gate will allow clean reconnects after
        // network drops without re-prompting the instructor.
        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId = roomId,
            StudentId = studentId,
            EventType = "REJOIN_APPROVED",
            SeverityScore = 0,
            Timestamp = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        await Clients.User(studentId.ToString()).SendAsync("OnJoinApproved", new
        {
            roomId,
            studentId,
            participantId = participant.Id
        });

        await Clients.Group(roomId.ToString()).SendAsync("StudentJoinApprovalResolved", new
        {
            roomId,
            studentId,
            decision = "Approved"
        });
    }

    public async Task DenyStudentJoin(int roomId, int studentId, string? reason)
    {
        var role = Context.User?.FindFirst(ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Instructor", StringComparison.OrdinalIgnoreCase))
            return;

        var instructorIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (instructorIdString == null || !int.TryParse(instructorIdString, out var instructorId))
            return;

        var room = await _context.Rooms.FindAsync(roomId);
        if (room == null || room.InstructorId != instructorId) return;

        var activeSession = await _context.ExamSessions
            .Where(s => s.RoomId == roomId && s.Status == "Active")
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefaultAsync();
        if (activeSession == null) return;

        var participant = await _context.SessionParticipants
            .Where(p => p.RoomId == roomId && p.StudentId == studentId)
            .OrderByDescending(p => p.JoinedAt)
            .FirstOrDefaultAsync();
        if (participant == null || participant.JoinedAt < activeSession.StartTime) return;

        participant.JoinApprovalStatus = "Denied";
        participant.IsCurrentlyActive = false;

        _context.MonitoringEvents.Add(new MonitoringEvent
        {
            RoomId = roomId,
            StudentId = studentId,
            EventType = "JOIN_DENIED",
            SeverityScore = 0,
            Timestamp = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        await Clients.User(studentId.ToString()).SendAsync("OnJoinDenied", new
        {
            roomId,
            studentId,
            reason = string.IsNullOrWhiteSpace(reason)
                ? "Your request to join was denied by the instructor."
                : reason
        });

        await Clients.Group(roomId.ToString()).SendAsync("StudentJoinApprovalResolved", new
        {
            roomId,
            studentId,
            decision = "Denied"
        });
    }
}