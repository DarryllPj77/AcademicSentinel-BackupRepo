using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.Hubs;
using AcademicSentinel.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AcademicSentinel.Server.Services;

/// <summary>
/// Background sweeper that detects student disconnects via heartbeat
/// liveness rather than SignalR's transport-level events.
///
/// Why heartbeat-based:
///   * SignalR's OnDisconnectedAsync is delayed (default 30s
///     ClientTimeoutInterval) and sometimes never fires at all on truly
///     abrupt drops (process kill, no internet, power loss).
///   * Heartbeats are an application-level liveness signal — if the SAC
///     can't pump a Hub method call, the student is unambiguously gone,
///     regardless of why.
///
/// Loop:
///   1. Every 5s, scan <see cref="MonitoringHub._activeStudentConnections"/>.
///   2. Any entry whose LastBeat is older than 15s → stale.
///   3. Drop the map entry. Mark every Active participant row for that
///      student in that room as Disconnected. Clear JoinApprovalStatus
///      so the next JoinLiveExam re-enters the rejoin-approval gate.
///   4. Write a STUDENT_DISCONNECTED MonitoringEvent (severity 0 — not
///      a violation, just a lifecycle log).
///   5. Broadcast StudentDisconnected + StudentConnectionLost to the
///      room group so the IMC flips the participant row red and writes
///      the Global Log Feed line.
/// </summary>
public sealed class DisconnectSweeperService : BackgroundService
{
    // Tune the cadence and timeout here. 5s sweep + 15s timeout ≈ ~10–15s
    // worst-case detection latency, comfortably faster than SignalR's
    // default 30s ClientTimeoutInterval.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly ILogger<DisconnectSweeperService> _logger;

    public DisconnectSweeperService(
        IServiceScopeFactory scopeFactory,
        IHubContext<MonitoringHub> hubContext,
        ILogger<DisconnectSweeperService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DisconnectSweeperService started. Sweep={Sweep}s, Timeout={Timeout}s.",
            SweepInterval.TotalSeconds, HeartbeatTimeout.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepStaleConnectionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DisconnectSweeper tick threw");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepStaleConnectionsAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.Subtract(HeartbeatTimeout);

        // Snapshot the stale entries up-front to avoid mutating the map
        // while we still hold references to its contents.
        var stale = MonitoringHub._activeStudentConnections
            .Where(kv => kv.Value.LastBeat < cutoff)
            .Select(kv => (ConnectionId: kv.Key, Ctx: kv.Value))
            .ToList();

        if (stale.Count == 0) return;

        foreach (var (connectionId, ctx) in stale)
        {
            // Remove the entry first so the next sweep doesn't double-fire
            // even if the DB write below is slow.
            MonitoringHub._activeStudentConnections.TryRemove(connectionId, out _);

            // Fresh scope per entry: if SaveChangesAsync throws on one
            // student the EF change tracker is discarded entirely, so the
            // next entry starts clean rather than inheriting corrupt state.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            try
            {
                // Fetch active rows. "Disconnected" rows are included here
                // (unlike the previous guard that skipped them) so we can
                // detect the OnDisconnectedAsync half-commit: participant
                // row already Disconnected but no matching event written.
                var participants = await db.SessionParticipants
                    .Where(p => p.StudentId == ctx.StudentId
                                && p.RoomId == ctx.RoomId
                                && p.ConnectionStatus != "Completed")
                    .ToListAsync(ct);

                if (participants.Count == 0)
                {
                    _logger.LogInformation(
                        "Sweep: studentId={StudentId} in roomId={RoomId} already cleaned up; map entry removed.",
                        ctx.StudentId, ctx.RoomId);
                    continue;
                }

                // Check whether a STUDENT_DISCONNECTED event already exists
                // for this student/room (written by OnDisconnectedAsync).
                // If the participant is already Disconnected but the event
                // is missing, we still insert the event — that is the
                // "half-commit" repair path.
                bool eventAlreadyWritten = await db.MonitoringEvents
                    .AnyAsync(e => e.StudentId == ctx.StudentId
                                   && e.RoomId == ctx.RoomId
                                   && e.EventType == "STUDENT_DISCONNECTED", ct);

                foreach (var p in participants)
                {
                    if (p.ConnectionStatus != "Disconnected")
                    {
                        p.ConnectionStatus = "Disconnected";
                        p.DisconnectedAt = DateTime.UtcNow;
                        p.JoinApprovalStatus = null;
                        p.IsCurrentlyActive = false;
                    }

                    if (!eventAlreadyWritten)
                    {
                        db.MonitoringEvents.Add(new MonitoringEvent
                        {
                            EventType = "STUDENT_DISCONNECTED",
                            Description = "Connection lost. Heartbeat timed out (App forcefully closed or network drop).",
                            SeverityScore = 0,
                            RoomId = p.RoomId,
                            StudentId = p.StudentId,
                            Timestamp = DateTime.UtcNow
                        });
                        // Only insert one event per student/room pair even
                        // if multiple participant rows exist.
                        eventAlreadyWritten = true;
                    }
                }

                await db.SaveChangesAsync(ct);

                var roomGroup = ctx.RoomId.ToString();
                await _hubContext.Clients.Group(roomGroup).SendAsync("StudentDisconnected", ctx.StudentId, ct);
                await _hubContext.Clients.Group(roomGroup).SendAsync("StudentConnectionLost", ctx.StudentId, ct);

                _logger.LogInformation(
                    "Sweep flipped studentId={StudentId} in roomId={RoomId} to Disconnected after {Age:F1}s of silence.",
                    ctx.StudentId, ctx.RoomId, (DateTime.UtcNow - ctx.LastBeat).TotalSeconds);
            }
            catch (Exception ex)
            {
                // Unwrap to the root cause so PostgreSQL constraint/column
                // errors (buried two levels inside DbUpdateException) are
                // visible in the log rather than showing only the EF wrapper.
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;

                _logger.LogError(ex,
                    "Sweep failed for studentId={StudentId} in roomId={RoomId}; will retry on next tick. " +
                    "Root cause [{RootType}]: {RootMessage}",
                    ctx.StudentId, ctx.RoomId, root.GetType().Name, root.Message);
            }
        }
    }
}
