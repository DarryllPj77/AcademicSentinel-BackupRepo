using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.Hubs;
using AcademicSentinel.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
///   3. Drop the map entry. Mark every non-Completed participant row for that
///      student in that room as Disconnected. Clear JoinApprovalStatus so the
///      next JoinLiveExam re-enters the rejoin-approval gate.
///   4. Write a STUDENT_DISCONNECTED MonitoringEvent if one doesn't already
///      exist (deduplication guard against the OnDisconnectedAsync path).
///   5. Broadcast StudentDisconnected + StudentConnectionLost to the room
///      group so the IMC flips the participant row red.
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
            // Remove the entry first so the next sweep tick doesn't
            // double-fire even if the DB write below is slow.
            MonitoringHub._activeStudentConnections.TryRemove(connectionId, out _);

            // One isolated scope per stale entry.  This guarantees:
            //   a) A completely fresh AppDbContext with an empty change
            //      tracker — no poisoned state carried over from a previous
            //      iteration that may have thrown mid-save.
            //   b) The participant rows we fetch ARE tracked by this exact
            //      db instance, so SaveChangesAsync sees their mutations.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            try
            {
                // --- STEP 1: Fetch participant rows owned by this db scope ---
                // Include already-Disconnected rows so we can repair the
                // "half-commit" case: OnDisconnectedAsync updated the row but
                // failed to insert the MonitoringEvent.
                var participants = await db.SessionParticipants
                    .Where(p => p.StudentId == ctx.StudentId
                                && p.RoomId == ctx.RoomId
                                && p.ConnectionStatus != "Completed")
                    .ToListAsync(ct);

                if (participants.Count == 0)
                {
                    // All rows are "Completed" — student cleanly finished the
                    // session via NotifyStudentLeftSafely. Nothing to do.
                    _logger.LogInformation(
                        "Sweep: studentId={StudentId} in roomId={RoomId} is Completed; skipping.",
                        ctx.StudentId, ctx.RoomId);
                    continue;
                }

                // --- STEP 2: Deduplication guard ---
                // If OnDisconnectedAsync already wrote the event we must not
                // insert a second one. One event per student/room pair.
                bool eventAlreadyWritten = await db.MonitoringEvents
                    .AnyAsync(e => e.StudentId == ctx.StudentId
                                   && e.RoomId == ctx.RoomId
                                   && e.EventType == "STUDENT_DISCONNECTED", ct);

                // --- STEP 3: Mutate rows + queue the event ---
                foreach (var participant in participants)
                {
                    // Transition the row only if it isn't already Disconnected.
                    // The db.Entry(participant).State will be Modified automatically
                    // because the entity was fetched from this same db instance.
                    if (participant.ConnectionStatus != "Disconnected")
                    {
                        participant.ConnectionStatus = "Disconnected";
                        participant.DisconnectedAt = DateTime.UtcNow;
                        participant.JoinApprovalStatus = null;
                        participant.IsCurrentlyActive = false;
                    }

                    // Insert one event per student/room pair, even if multiple
                    // participant rows exist (e.g. a rejoin creates a second row).
                    if (!eventAlreadyWritten)
                    {
                        db.MonitoringEvents.Add(new MonitoringEvent
                        {
                            EventType    = "STUDENT_DISCONNECTED",
                            Description  = "Connection lost. Heartbeat timed out (App forcefully closed or network drop).",
                            SeverityScore = 0,
                            RoomId       = participant.RoomId,
                            StudentId    = participant.StudentId,
                            Timestamp    = DateTime.UtcNow
                        });
                        eventAlreadyWritten = true;
                    }
                }

                // --- STEP 4: Atomic commit ---
                // Both the participant UPDATE and the MonitoringEvent INSERT are
                // flushed inside a single transaction. Either both land or neither
                // does — no half-commit possible from this path.
                await db.SaveChangesAsync(ct);

                Console.WriteLine(
                    $"[Sweeper] SUCCESS: Disconnect committed — " +
                    $"studentId={ctx.StudentId}, roomId={ctx.RoomId}");

                _logger.LogInformation(
                    "Sweep flipped studentId={StudentId} in roomId={RoomId} to Disconnected " +
                    "after {Age:F1}s of silence.",
                    ctx.StudentId, ctx.RoomId, (DateTime.UtcNow - ctx.LastBeat).TotalSeconds);

                // --- STEP 5: Notify the room ---
                var roomGroup = ctx.RoomId.ToString();
                await _hubContext.Clients.Group(roomGroup).SendAsync("StudentDisconnected",    ctx.StudentId, ct);
                await _hubContext.Clients.Group(roomGroup).SendAsync("StudentConnectionLost", ctx.StudentId, ct);
            }
            catch (DbUpdateException dbEx)
            {
                // DbUpdateException wraps the raw Npgsql error. Walk the chain
                // to find PostgresException which carries the SQL state code,
                // constraint name, and offending column — the exact data needed
                // to diagnose a schema/migration mismatch at a glance.
                Console.WriteLine(
                    $"[Sweeper] FATAL DB ERROR — " +
                    $"studentId={ctx.StudentId}, roomId={ctx.RoomId}");
                Console.WriteLine($"[Sweeper] DbUpdateException: {dbEx.Message}");

                Exception inner = dbEx.InnerException!;
                while (inner != null)
                {
                    if (inner is PostgresException pgEx)
                    {
                        Console.WriteLine(
                            $"[Sweeper] PostgresException " +
                            $"SqlState={pgEx.SqlState} | {pgEx.MessageText}");
                        Console.WriteLine(
                            $"[Sweeper] Table={pgEx.TableName} | " +
                            $"Column={pgEx.ColumnName} | " +
                            $"Constraint={pgEx.ConstraintName}");
                        break;
                    }
                    Console.WriteLine($"[Sweeper] InnerException [{inner.GetType().Name}]: {inner.Message}");
                    inner = inner.InnerException!;
                }

                _logger.LogError(dbEx,
                    "Sweep DB write failed for studentId={StudentId} roomId={RoomId}.",
                    ctx.StudentId, ctx.RoomId);
            }
            catch (Exception ex)
            {
                // Non-DB failure (e.g. SignalR broadcast, cancellation).
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;

                Console.WriteLine(
                    $"[Sweeper] UNEXPECTED ERROR — " +
                    $"studentId={ctx.StudentId}, roomId={ctx.RoomId}");
                Console.WriteLine($"[Sweeper] {ex.GetType().Name}: {ex.Message}");
                if (root != ex)
                    Console.WriteLine($"[Sweeper] Root [{root.GetType().Name}]: {root.Message}");

                _logger.LogError(ex,
                    "Sweep failed for studentId={StudentId} roomId={RoomId}; will retry on next tick.",
                    ctx.StudentId, ctx.RoomId);
            }
        }
    }
}
