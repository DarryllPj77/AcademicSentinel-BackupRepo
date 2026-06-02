using AcademicSentinel.Server.Data;
using AcademicSentinel.Server.Hubs;
using Microsoft.EntityFrameworkCore;

namespace AcademicSentinel.Server.Services;

/// <summary>
/// Background sweeper that proactively releases stale single-device
/// login locks (<c>User.IsLoggedIn</c> + <c>User.CurrentDeviceId</c>)
/// caused by improper logouts — typically force-killed clients, power
/// loss, or anyone walking away from the keyboard without signing out.
///
/// WHY THIS EXISTS (in addition to <see cref="DisconnectSweeperService"/>):
///   * <c>DisconnectSweeperService</c> only fires when a student already
///     joined a SignalR session and then went silent. A user who
///     authenticated against <c>/api/auth/login</c> but force-quit BEFORE
///     joining any room never enters <c>_activeStudentConnections</c>, so
///     their lock would otherwise persist until the next contested
///     login attempt triggered <see cref="AuthController"/>'s passive
///     15-minute window check.
///   * Instructors don't have a heartbeat path at all — their only
///     liveness signal is the SignalR connection itself.
///   * Proactive release makes admin dashboards and analytics reflect
///     reality without waiting for someone to try to log in.
///
/// CADENCE: every 5 minutes. The window is short enough that a student
/// whose machine crashed can hop to another PC within a few minutes,
/// but long enough that we don't hammer Postgres needlessly.
///
/// STALE CRITERIA:
///   * <c>IsLoggedIn = true</c> AND
///   * <c>LastLoginAt &lt; now - 15 min</c> (matches
///     <c>AuthController.Login</c>'s own stale-lock window) AND
///   * the user is NOT currently alive on the SignalR hub (heartbeat
///     map check via <see cref="MonitoringHub._activeStudentConnections"/>).
///
/// The hub-presence check prevents the dangerous failure mode where
/// an active student in a live exam — who is producing heartbeats but
/// hasn't refreshed <c>LastLoginAt</c> for over 15 minutes (because
/// LastLoginAt is only stamped at login time) — gets their lock
/// released by this sweeper, which would let someone else log in
/// concurrently and violate the 1-device rule.
///
/// CONTRACT: this service ONLY touches <c>IsLoggedIn</c> and
/// <c>CurrentDeviceId</c>. It does NOT invalidate JWTs (those expire on
/// their own 8h schedule), terminate SignalR connections, or modify
/// session/participant rows. <see cref="DisconnectService"/> retains
/// ownership of all session-state mutations.
///
/// Lifetime: <c>BackgroundService</c>. Resolves a fresh
/// <see cref="AppDbContext"/> scope per tick.
/// </summary>
public sealed class GhostLockSweeperService : BackgroundService
{
    private static readonly TimeSpan SweepInterval     = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StaleLockDuration = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GhostLockSweeperService> _logger;

    public GhostLockSweeperService(
        IServiceScopeFactory scopeFactory,
        ILogger<GhostLockSweeperService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "GhostLockSweeperService started. Sweep={Sweep}min, StaleAfter={Stale}min.",
            SweepInterval.TotalMinutes, StaleLockDuration.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReleaseStaleLocksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GhostLockSweeper tick threw — continuing.");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task ReleaseStaleLocksAsync(CancellationToken ct)
    {
        // Snapshot live-on-hub student IDs ONCE per tick so a slow query
        // doesn't see a moving target. ConcurrentDictionary enumeration is
        // safe but inconsistent; the snapshot keeps the comparison simple.
        var liveStudentIds = MonitoringHub._activeStudentConnections.Values
            .Select(c => c.StudentId)
            .ToHashSet();

        var cutoff = DateTime.UtcNow - StaleLockDuration;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Pull candidates first; filter the hub-live ones in memory because
        // EF can't translate a HashSet<int>.Contains on a runtime set
        // backed by an in-memory ConcurrentDictionary into SQL anyway.
        var candidates = await db.Users
            .Where(u => u.IsLoggedIn
                        && u.LastLoginAt.HasValue
                        && u.LastLoginAt.Value < cutoff)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        int releasedCount = 0;
        foreach (var user in candidates)
        {
            // Belt + suspenders: an instructor row would not appear in the
            // student heartbeat map, but a student who's mid-exam absolutely
            // would — skipping them protects against the false-positive
            // release that would let a second device hijack their session.
            if (liveStudentIds.Contains(user.Id))
            {
                _logger.LogDebug(
                    "[GhostLockSweeper] Skipping {Email} (id={UserId}) — still alive on hub.",
                    user.Email, user.Id);
                continue;
            }

            user.IsLoggedIn      = false;
            user.CurrentDeviceId = null;
            releasedCount++;

            _logger.LogInformation(
                "[GhostLockSweeper] Released ghost-lock for {Email} (id={UserId}) — " +
                "idle since {LastLogin:o}, device was '{DeviceId}'.",
                user.Email,
                user.Id,
                user.LastLoginAt,
                string.IsNullOrEmpty(user.CurrentDeviceId) ? "(unknown)" : user.CurrentDeviceId);
        }

        if (releasedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "[GhostLockSweeper] Tick complete — released {Count} stale lock(s).",
                releasedCount);
        }
    }
}
