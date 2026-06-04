using AcademicSentinel.Server.Hubs;

namespace AcademicSentinel.Server.Services;

/// <summary>
/// Background sweeper that detects student disconnects via heartbeat
/// liveness rather than SignalR's transport-level events.
///
/// THIS SERVICE NO LONGER OWNS DISCONNECT LOGIC.  Everything DB-related
/// and every SignalR broadcast has moved to <see cref="DisconnectService"/>.
/// The sweeper's responsibilities are now strictly:
///
///   1. Every 5 seconds, scan <c>MonitoringHub._activeStudentConnections</c>.
///   2. Any entry whose <c>LastBeat</c> is older than 15 seconds is stale.
///   3. Drop the map entry and delegate to
///      <c>DisconnectService.HandleDisconnectAsync</c>, which is idempotent
///      — if <c>OnDisconnectedAsync</c> already handled the same student
///      within the dedup window, the service returns immediately.
/// </summary>
public sealed class DisconnectSweeperService : BackgroundService
{
    // Tuned for near-real-time disconnect detection. The SAC heartbeats
    // every 3s, so a 10s timeout tolerates ~3 missed beats (jitter / a brief
    // UI-thread stall) before flagging a drop, and a 2s sweep means the
    // instructor's IMC is notified ~10-12s after a real disconnect instead
    // of the previous ~15-30s. A false positive is self-healing (the student
    // simply rejoins), so we bias toward fast detection.
    private static readonly TimeSpan SweepInterval    = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(10);

    private readonly DisconnectService _disconnectService;
    private readonly ILogger<DisconnectSweeperService> _logger;

    public DisconnectSweeperService(
        DisconnectService disconnectService,
        ILogger<DisconnectSweeperService> logger)
    {
        _disconnectService = disconnectService;
        _logger            = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DisconnectSweeperService started. Sweep={Sweep}s, Timeout={Timeout}s.",
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

        // Snapshot stale entries so we don't iterate while mutating the map.
        var stale = MonitoringHub._activeStudentConnections
            .Where(kv => kv.Value.LastBeat < cutoff)
            .Select(kv => (ConnectionId: kv.Key, Ctx: kv.Value))
            .ToList();

        if (stale.Count == 0) return;

        foreach (var (connectionId, ctx) in stale)
        {
            // Drain the heartbeat-map entry first so a subsequent tick
            // doesn't re-pick this student even if the delegated call
            // below takes its time.
            MonitoringHub._activeStudentConnections.TryRemove(connectionId, out _);

            // Hand off to the single master.  Idempotent by design:
            //   • If OnDisconnectedAsync (SignalR) already processed this
            //     student in the last 10 seconds, the call returns
            //     instantly via the in-memory dedup guard inside
            //     DisconnectService — no UPDATE, no INSERT, no broadcast.
            //   • If this sweeper is the first observer, the service
            //     performs the atomic UPDATE + INSERT + broadcast.
            const string reason = "Connection lost. Heartbeat timed out (App forcefully closed or network drop).";
            await _disconnectService.HandleDisconnectAsync(
                ctx.StudentId, ctx.RoomId, reason, ct);

            _logger.LogInformation(
                "[Sweeper] Handed studentId={StudentId} roomId={RoomId} to DisconnectService after {Age:F1}s of silence.",
                ctx.StudentId, ctx.RoomId, (DateTime.UtcNow - ctx.LastBeat).TotalSeconds);
        }
    }
}
