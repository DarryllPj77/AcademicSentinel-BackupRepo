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
/// STRICT TRANSITION CONTRACT (this rewrite):
///   The MonitoringEvent "STUDENT_DISCONNECTED" is inserted ONLY when this
///   loop iteration actually flips a participant row from "Connected" to
///   "Disconnected".  Any row that arrives already-Disconnected (because
///   OnDisconnectedAsync got there first) is left untouched and writes no
///   event — that path is responsible for its own audit row.  This makes
///   duplicate inserts structurally impossible without needing an extra
///   pre-flight SELECT against MonitoringEvents.
/// </summary>
public sealed class DisconnectSweeperService : BackgroundService
{
    // Tune the cadence and timeout here. 5s sweep + 15s timeout ≈ ~10–15s
    // worst-case detection latency, comfortably faster than SignalR's
    // default 30s ClientTimeoutInterval.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly ILogger<DisconnectSweeperService> _logger;

    public DisconnectSweeperService(
        IServiceScopeFactory serviceScopeFactory,
        IHubContext<MonitoringHub> hubContext,
        ILogger<DisconnectSweeperService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
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
            // Remove the heartbeat-map entry first so the next 5-second sweep
            // tick doesn't re-pick this student even if the DB call below is
            // slow or contended.
            MonitoringHub._activeStudentConnections.TryRemove(connectionId, out _);

            // =================================================================
            // HARDENED SCOPE BLOCK
            // =================================================================
            // Per the architectural review:
            //   * AppDbContext is a scoped service and MUST be resolved through
            //     an explicit IServiceScopeFactory.CreateScope() in a background
            //     service.  Injecting it via the constructor would resolve a
            //     singleton-captured instance and produce undefined behaviour.
            //   * Each stale entry gets its OWN scope.  If SaveChangesAsync
            //     throws on one student, EF's change tracker is discarded with
            //     the scope, so the next iteration starts with a clean context
            //     — no risk of carrying over poisoned tracked entities.
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                try
                {
                    // -----------------------------------------------------------
                    // 1. Fetch participant rows owned by THIS scope's DbContext.
                    // -----------------------------------------------------------
                    // Fetching through `db` means every returned entity is
                    // tracked by `db`'s change tracker.  Mutating their
                    // properties below will produce an UPDATE statement at
                    // SaveChangesAsync time.  Fetching from a different
                    // context (or _context outside the scope) would silently
                    // skip the UPDATE — that is the classic "ghost write".
                    var participants = await db.SessionParticipants
                        .Where(p => p.StudentId == ctx.StudentId
                                    && p.RoomId    == ctx.RoomId)
                        .ToListAsync(ct);

                    if (participants.Count == 0)
                    {
                        _logger.LogInformation(
                            "[Sweeper] No participant rows for studentId={StudentId} in roomId={RoomId}. Nothing to do.",
                            ctx.StudentId, ctx.RoomId);
                        continue;
                    }

                    // Track whether ANY row was actually transitioned in this
                    // iteration.  Only then do we add the MonitoringEvent and
                    // call SaveChangesAsync — otherwise we'd commit nothing
                    // and waste a DB round-trip.
                    bool transitionedThisIteration = false;
                    SessionParticipant? transitionedRow = null;

                    foreach (var participant in participants)
                    {
                        // STRICT TRANSITION GUARD
                        // Only flip rows that are currently "Connected".  A row
                        // that is "Disconnected" was already handled by
                        // OnDisconnectedAsync (or a previous sweep tick that
                        // succeeded); a "Completed" row left the session
                        // cleanly.  Either way: skip — do not log a duplicate.
                        if (!string.Equals(participant.ConnectionStatus, "Connected",
                                           StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // ---------------------------------------------------
                        // LOAD-BEFORE-MODIFY (Atomic + Idempotent)
                        // ---------------------------------------------------
                        // `participant` was loaded from THIS scope's db via
                        // .ToListAsync() above, so it is a fully populated,
                        // EF-tracked entity.  We mutate ONLY ConnectionStatus
                        // and DisconnectedAt.  All other columns — including
                        // the NOT NULL JoinApprovalStatus (default "Approved")
                        // — retain their existing DB values, because EF change
                        // tracking only writes columns whose properties were
                        // changed.  This eliminates the 23502 null-constraint
                        // violation at the source.
                        participant.ConnectionStatus = "Disconnected";
                        participant.DisconnectedAt   = DateTime.UtcNow;

                        transitionedThisIteration = true;
                        transitionedRow           = participant;
                    }

                    if (!transitionedThisIteration)
                    {
                        _logger.LogInformation(
                            "[Sweeper] studentId={StudentId} in roomId={RoomId} had no Connected→Disconnected transition; skipping insert.",
                            ctx.StudentId, ctx.RoomId);
                        continue;
                    }

                    // -----------------------------------------------------------
                    // 2. DE-DUPLICATION GUARD (10s window)
                    // -----------------------------------------------------------
                    // If OnDisconnectedAsync (or a previous sweep tick) already
                    // wrote a STUDENT_DISCONNECTED for this student/room inside
                    // the last 10 seconds, skip the Add.  The participant UPDATE
                    // we already staged above will still commit via the
                    // SaveChangesAsync below — only the duplicate event Add is
                    // suppressed.
                    bool recentlyLogged = await db.MonitoringEvents
                        .AnyAsync(e => e.StudentId == transitionedRow!.StudentId
                                    && e.RoomId    == transitionedRow!.RoomId
                                    && e.EventType == "STUDENT_DISCONNECTED"
                                    && e.Timestamp > DateTime.UtcNow.AddSeconds(-10), ct);

                    if (recentlyLogged)
                    {
                        Console.WriteLine(
                            $"[Sweeper] DEDUP skip — STUDENT_DISCONNECTED already logged within 10s " +
                            $"for studentId={ctx.StudentId}, roomId={ctx.RoomId}. " +
                            $"Participant UPDATE will still commit.");
                        _logger.LogInformation(
                            "[Sweeper] DEDUP skip for studentId={StudentId} roomId={RoomId}; participant UPDATE only.",
                            ctx.StudentId, ctx.RoomId);
                    }
                    else
                    {
                        // -----------------------------------------------------------
                        // 3. Queue the audit event.  Same db, same change tracker.
                        // -----------------------------------------------------------
                        db.MonitoringEvents.Add(new MonitoringEvent
                        {
                            EventType     = "STUDENT_DISCONNECTED",
                            Description   = "Connection lost. Heartbeat timed out (App forcefully closed or network drop).",
                            SeverityScore = 0,
                            RoomId        = transitionedRow!.RoomId,
                            StudentId     = transitionedRow!.StudentId,
                            Timestamp     = DateTime.UtcNow
                        });
                    }

                    // -----------------------------------------------------------
                    // 4. Atomic commit — UPDATE (+ optional INSERT) in one transaction.
                    // -----------------------------------------------------------
                    int rowsAffected = await db.SaveChangesAsync(ct);

                    Console.WriteLine(
                        $"[Sweeper] COMMIT OK — studentId={ctx.StudentId}, roomId={ctx.RoomId}, " +
                        $"rowsAffected={rowsAffected}");

                    _logger.LogInformation(
                        "Sweep flipped studentId={StudentId} in roomId={RoomId} to Disconnected after {Age:F1}s of silence. rowsAffected={Rows}",
                        ctx.StudentId, ctx.RoomId,
                        (DateTime.UtcNow - ctx.LastBeat).TotalSeconds, rowsAffected);

                    // -----------------------------------------------------------
                    // 4. Notify the room — only after the DB write committed.
                    // -----------------------------------------------------------
                    var roomGroup = ctx.RoomId.ToString();
                    await _hubContext.Clients.Group(roomGroup).SendAsync("StudentDisconnected",    ctx.StudentId, ct);
                    await _hubContext.Clients.Group(roomGroup).SendAsync("StudentConnectionLost", ctx.StudentId, ct);
                }
                // ---------------------------------------------------------------
                // AGGRESSIVE ERROR TRAPPING
                // ---------------------------------------------------------------
                // The whole point of this rewrite: if SaveChangesAsync ever
                // fails silently again, we WILL see the exact PostgreSQL error
                // in the console — SQL state code, table, column, constraint.
                catch (DbUpdateException dbEx)
                {
                    Console.WriteLine("======================================================");
                    Console.WriteLine($"[Sweeper] DbUpdateException — studentId={ctx.StudentId}, roomId={ctx.RoomId}");
                    Console.WriteLine($"[Sweeper] Outer: {dbEx.GetType().FullName}: {dbEx.Message}");

                    Exception? walk = dbEx.InnerException;
                    int depth = 1;
                    while (walk != null)
                    {
                        if (walk is PostgresException pg)
                        {
                            Console.WriteLine($"[Sweeper] [depth={depth}] PostgresException");
                            Console.WriteLine($"[Sweeper]   SqlState   = {pg.SqlState}");
                            Console.WriteLine($"[Sweeper]   Severity   = {pg.Severity}");
                            Console.WriteLine($"[Sweeper]   Message    = {pg.MessageText}");
                            Console.WriteLine($"[Sweeper]   Detail     = {pg.Detail}");
                            Console.WriteLine($"[Sweeper]   Schema     = {pg.SchemaName}");
                            Console.WriteLine($"[Sweeper]   Table      = {pg.TableName}");
                            Console.WriteLine($"[Sweeper]   Column     = {pg.ColumnName}");
                            Console.WriteLine($"[Sweeper]   Constraint = {pg.ConstraintName}");
                            Console.WriteLine($"[Sweeper]   DataType   = {pg.DataTypeName}");
                            Console.WriteLine($"[Sweeper]   Position   = {pg.Position}");
                            break;
                        }

                        if (walk is NpgsqlException np)
                        {
                            Console.WriteLine($"[Sweeper] [depth={depth}] NpgsqlException: {np.Message}");
                        }
                        else
                        {
                            Console.WriteLine($"[Sweeper] [depth={depth}] {walk.GetType().FullName}: {walk.Message}");
                        }

                        walk = walk.InnerException;
                        depth++;
                    }
                    Console.WriteLine("======================================================");

                    _logger.LogError(dbEx,
                        "Sweep DB write FAILED for studentId={StudentId} roomId={RoomId}.",
                        ctx.StudentId, ctx.RoomId);
                }
                catch (NpgsqlException npgsqlEx)
                {
                    // Some Npgsql errors (connection drop, broken pipe) escape
                    // EF without being wrapped in DbUpdateException.  Catch
                    // them explicitly so the diagnostic surface is identical.
                    Console.WriteLine("======================================================");
                    Console.WriteLine($"[Sweeper] NpgsqlException — studentId={ctx.StudentId}, roomId={ctx.RoomId}");
                    Console.WriteLine($"[Sweeper] {npgsqlEx.GetType().FullName}: {npgsqlEx.Message}");

                    if (npgsqlEx is PostgresException pg2)
                    {
                        Console.WriteLine($"[Sweeper]   SqlState   = {pg2.SqlState}");
                        Console.WriteLine($"[Sweeper]   Table      = {pg2.TableName}");
                        Console.WriteLine($"[Sweeper]   Column     = {pg2.ColumnName}");
                        Console.WriteLine($"[Sweeper]   Constraint = {pg2.ConstraintName}");
                    }
                    Console.WriteLine("======================================================");

                    _logger.LogError(npgsqlEx,
                        "Sweep Npgsql error for studentId={StudentId} roomId={RoomId}.",
                        ctx.StudentId, ctx.RoomId);
                }
                catch (Exception ex)
                {
                    // Non-DB failure (e.g. SignalR broadcast threw, cancellation).
                    Exception root = ex;
                    while (root.InnerException != null) root = root.InnerException;

                    Console.WriteLine($"[Sweeper] UNEXPECTED — studentId={ctx.StudentId}, roomId={ctx.RoomId}");
                    Console.WriteLine($"[Sweeper] {ex.GetType().Name}: {ex.Message}");
                    if (root != ex)
                        Console.WriteLine($"[Sweeper] Root [{root.GetType().Name}]: {root.Message}");

                    _logger.LogError(ex,
                        "Sweep failed for studentId={StudentId} roomId={RoomId}; will retry on next tick.",
                        ctx.StudentId, ctx.RoomId);
                }
            } // end using scope — DbContext disposed deterministically here
        }
    }
}
