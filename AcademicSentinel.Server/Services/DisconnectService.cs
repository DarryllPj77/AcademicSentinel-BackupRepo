using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.Hubs;
using AcademicSentinel.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Collections.Concurrent;

namespace AcademicSentinel.Server.Services;

/// <summary>
/// The single master for student-disconnect handling.
///
/// Both <c>MonitoringHub.OnDisconnectedAsync</c> (SignalR transport drop) and
/// <see cref="DisconnectSweeperService"/> (heartbeat timeout) call into this
/// service.  Every participant <c>UPDATE</c>, every <c>STUDENT_DISCONNECTED</c>
/// audit row, and every room broadcast originates here.
///
/// Bugs fixed by this rewrite:
///
///   1. MILLISECOND RACE — Two callers passing the <see cref="_lastProcessTime"/>
///      <c>TryGetValue</c> read-check before either thread writes the timestamp.
///      Now wrapped in a per-student <see cref="_studentLocks"/> monitor so
///      "check + stamp" is a single critical section.  Only one thread per
///      <c>studentId</c> can be inside the dedup window at any given time.
///
///   2. GRACEFUL-EXIT PARADOX — When the instructor clicks "End Session" every
///      SAC closes and SignalR fires <c>OnDisconnectedAsync</c> for every
///      student, falsely flagging them as having dropped mid-exam.  Two new
///      guards: (a) abort if the participant is already <c>"Completed"</c>;
///      (b) abort if the <c>ExamSessions</c> table has no <c>EndTime == null</c>
///      row for this room (i.e. the session is over).  A socket drop AFTER
///      the session ends is not a violation.
///
/// Concurrency contract:
///   * <see cref="_lastProcessTime"/> writes happen ONLY inside the lock.
///   * <see cref="_lastProcessTime"/> reads happen ONLY inside the lock.
///   * No <c>await</c> appears inside the lock (C# enforces this anyway).
///   * The lock is released BEFORE any DB call so the EF context never blocks.
///
/// Lifetime: Singleton.  The lock map and the dedup map are process-local.
/// </summary>
public sealed class DisconnectService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly ILogger<DisconnectService> _logger;

    // ------------------------------------------------------------------
    // PER-STUDENT MONITOR LOCKS
    // ------------------------------------------------------------------
    // Resolving a studentId to a sync-root via GetOrAdd is itself
    // thread-safe (ConcurrentDictionary semantics).  Inside the lock we
    // perform exactly two operations against _lastProcessTime: a read and
    // a conditional write.  Both are O(1) and guaranteed to be ordered.
    private static readonly ConcurrentDictionary<int, object>   _studentLocks    = new();
    private static readonly ConcurrentDictionary<int, DateTime> _lastProcessTime = new();

    private const int DedupWindowSeconds = 10;

    public DisconnectService(
        IServiceScopeFactory scopeFactory,
        IHubContext<MonitoringHub> hubContext,
        ILogger<DisconnectService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext   = hubContext;
        _logger       = logger;
    }

    /// <summary>
    /// Idempotently transition the student's participant row to
    /// <c>Disconnected</c> AND write a <c>STUDENT_DISCONNECTED</c> audit
    /// event AND broadcast — only when:
    ///   • another call hasn't claimed the 10-second window already, AND
    ///   • the participant isn't already <c>"Completed"</c> or
    ///     <c>"Disconnected"</c>, AND
    ///   • an active <c>ExamSession</c> still exists for this room.
    /// </summary>
    public async Task HandleDisconnectAsync(
        int studentId,
        int roomId,
        string reason,
        CancellationToken ct = default)
    {
        // ================================================================
        // 1. ATOMIC DEDUP CHECK (per-student lock)
        // ================================================================
        // GetOrAdd is itself thread-safe; the per-student `object` is
        // cheap (16-24 bytes on x64) so we tolerate the map growing as
        // students come and go.  The lock block contains ONLY synchronous
        // code — read, compare, write — and is released before any DB
        // I/O, so the EF pipeline never observes a held lock.
        var syncLock = _studentLocks.GetOrAdd(studentId, static _ => new object());

        lock (syncLock)
        {
            var nowInLock = DateTime.UtcNow;

            if (_lastProcessTime.TryGetValue(studentId, out var lastTime)
                && (nowInLock - lastTime).TotalSeconds < DedupWindowSeconds)
            {
                _logger.LogInformation(
                    "[DisconnectService] DEDUP — studentId={StudentId} processed {Elapsed:F3}s ago; skipping.",
                    studentId, (nowInLock - lastTime).TotalSeconds);
                return;
            }

            // Stamp INSIDE the lock so a concurrent caller racing the same
            // microsecond observes our write and falls into the dedup
            // branch above.
            _lastProcessTime[studentId] = nowInLock;
        }

        // ================================================================
        // 2. FRESH SCOPE + DbContext
        // ================================================================
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            // ------------------------------------------------------------
            // 3. LOAD-BEFORE-MODIFY  (single most-recent active row)
            // ------------------------------------------------------------
            // OrderByDescending(JoinedAt) handles students who rejoined
            // mid-session and therefore have multiple participant rows.
            // The IsCurrentlyActive filter ignores rows that an instructor
            // explicitly removed via the kick-out flow.
            var participant = await db.SessionParticipants
                .Where(p => p.StudentId == studentId
                            && p.RoomId    == roomId
                            && p.IsCurrentlyActive)
                .OrderByDescending(p => p.JoinedAt)
                .FirstOrDefaultAsync(ct);

            if (participant == null)
            {
                _logger.LogInformation(
                    "[DisconnectService] No active participant for studentId={StudentId} roomId={RoomId}.",
                    studentId, roomId);
                return;
            }

            // ------------------------------------------------------------
            // 4. GRACEFUL-EXIT GUARD #1 — participant already Completed
            // ------------------------------------------------------------
            // "Completed" is set by NotifyStudentLeftSafely (the student
            // cleanly left) or by the teacher-dismiss flow.  A subsequent
            // socket drop is NOT a violation — the student was already
            // released from the exam.
            if (string.Equals(participant.ConnectionStatus, "Completed",
                              StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[DisconnectService] GRACEFUL EXIT — studentId={StudentId} roomId={RoomId} already Completed; not logging a violation.",
                    studentId, roomId);
                return;
            }

            // ------------------------------------------------------------
            // 5. STATE SHORT-CIRCUIT — already Disconnected
            // ------------------------------------------------------------
            // Another path already handled this student.  Belt to the
            // dedup-lock suspenders above — survives if some caller bypasses
            // the time stamp (e.g. process restart with stale rows).
            if (string.Equals(participant.ConnectionStatus, "Disconnected",
                              StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "[DisconnectService] studentId={StudentId} roomId={RoomId} already Disconnected; no-op.",
                    studentId, roomId);
                return;
            }

            // ------------------------------------------------------------
            // 6. GRACEFUL-EXIT GUARD #2 — session has ended
            // ------------------------------------------------------------
            // If the ExamSessions table has no live row for this room,
            // the instructor (or the scheduled end) already closed the
            // session.  A SignalR drop AFTER that moment is normal — the
            // SAC closing because it received SessionEnded.  Do NOT log
            // a STUDENT_DISCONNECTED in that case.
            var activeSession = await db.ExamSessions
                .Where(s => s.RoomId == roomId && s.EndTime == null)
                .OrderByDescending(s => s.StartTime)
                .FirstOrDefaultAsync(ct);

            if (activeSession == null)
            {
                _logger.LogInformation(
                    "[DisconnectService] GRACEFUL EXIT — no active ExamSession for roomId={RoomId}; socket drop is not a violation.",
                    roomId);
                return;
            }

            // ------------------------------------------------------------
            // 7. MUTATE  (only the two disconnect-state fields)
            // ------------------------------------------------------------
            // EF change tracking emits an UPDATE for only these two
            // columns.  JoinApprovalStatus (NOT NULL, default "Approved")
            // is never written and cannot trip PostgreSQL 23502.
            participant.ConnectionStatus = "Disconnected";
            participant.DisconnectedAt   = DateTime.UtcNow;

            // ------------------------------------------------------------
            // 8. AUDIT EVENT  (atomic with the UPDATE)
            // ------------------------------------------------------------
            db.MonitoringEvents.Add(new MonitoringEvent
            {
                EventType     = "STUDENT_DISCONNECTED",
                Description   = string.IsNullOrWhiteSpace(reason)
                                    ? "Connection lost."
                                    : reason,
                SeverityScore = 0,
                RoomId        = roomId,
                StudentId     = studentId,
                Timestamp     = DateTime.UtcNow
            });

            // ------------------------------------------------------------
            // 9. ATOMIC COMMIT
            // ------------------------------------------------------------
            int rowsAffected = await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[DisconnectService] COMMIT OK — studentId={StudentId} roomId={RoomId} rows={Rows} reason='{Reason}'",
                studentId, roomId, rowsAffected, reason);

            // ------------------------------------------------------------
            // 10. BROADCAST  (after successful commit, exactly once)
            // ------------------------------------------------------------
            var roomGroup = roomId.ToString();
            await _hubContext.Clients.Group(roomGroup).SendAsync("StudentDisconnected",    studentId, ct);
            await _hubContext.Clients.Group(roomGroup).SendAsync("StudentConnectionLost", studentId, ct);
        }
        catch (DbUpdateException dbEx)
        {
            // Roll back the dedup stamp so the next sweep tick (or a hub
            // retry) can re-attempt the write.  We acquire the lock again
            // for the rollback so the write happens under the same
            // ordering discipline as the original stamp.
            lock (syncLock)
            {
                _lastProcessTime.TryRemove(studentId, out _);
            }

            // Unwrap to the Postgres root cause for diagnostics.
            Exception? walk = dbEx.InnerException;
            while (walk != null)
            {
                if (walk is PostgresException pg)
                {
                    _logger.LogError(dbEx,
                        "[DisconnectService] DB FAILED studentId={StudentId} roomId={RoomId}. " +
                        "PostgresException SqlState={SqlState} Table={Table} Column={Column} Constraint={Constraint}: {Message}",
                        studentId, roomId,
                        pg.SqlState, pg.TableName, pg.ColumnName, pg.ConstraintName, pg.MessageText);
                    return;
                }
                walk = walk.InnerException;
            }
            _logger.LogError(dbEx,
                "[DisconnectService] DB FAILED for studentId={StudentId} roomId={RoomId}.",
                studentId, roomId);
        }
        catch (NpgsqlException npgEx)
        {
            lock (syncLock)
            {
                _lastProcessTime.TryRemove(studentId, out _);
            }
            _logger.LogError(npgEx,
                "[DisconnectService] Npgsql failure for studentId={StudentId} roomId={RoomId}.",
                studentId, roomId);
        }
        catch (Exception ex)
        {
            lock (syncLock)
            {
                _lastProcessTime.TryRemove(studentId, out _);
            }
            _logger.LogError(ex,
                "[DisconnectService] Unexpected failure for studentId={StudentId} roomId={RoomId}.",
                studentId, roomId);
        }
    }
}
