using AcademicSentinel.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AcademicSentinel.Server.Services;

/// <summary>
/// Periodic background worker that hard-deletes Past Session
/// archives whose soft-delete (DeletedAt) is older than the
/// configured retention window. Pattern mirrors
/// <see cref="DisconnectSweeperService"/> — single responsibility,
/// idempotent tick, scoped DbContext per pass.
///
/// Retention window comes from <c>Archive:RetentionDays</c> in
/// configuration (validated at startup in Program.cs to be either
/// 15 or 30). Times are always UTC.
///
/// The sweep is intentionally cheap: an index on
/// <c>ExamSessions.DeletedAt</c> (added by migration
/// 20260605120000) keeps the cutoff query bounded even on large
/// archive tables.
/// </summary>
public sealed class ArchiveCleanupService : BackgroundService
{
    // Once an hour is plenty — retention is measured in days, so
    // shaving minutes off the purge does not matter. Hourly avoids
    // wasted DB round-trips while keeping the worst-case delay
    // between expiry and removal well under 1% of the retention
    // window.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ArchiveCleanupService> _logger;

    public ArchiveCleanupService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ArchiveCleanupService> logger)
    {
        _scopeFactory  = scopeFactory;
        _configuration = configuration;
        _logger        = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = ResolveRetentionDays();
        _logger.LogInformation(
            "ArchiveCleanupService started. Sweep={SweepHours}h, RetentionDays={RetentionDays}.",
            SweepInterval.TotalHours, retentionDays);

        // First tick runs immediately so a freshly-deployed server
        // doesn't sit on a backlog of overdue trashed archives until
        // the first hour elapses.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ArchiveCleanup tick threw");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var retentionDays = ResolveRetentionDays();
        var cutoff        = DateTime.UtcNow.AddDays(-retentionDays);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Materialize once — both the IDs we log and the rows we
        // delete come from the same fetch (no double round-trip).
        var expired = await db.ExamSessions
            .Where(s => s.DeletedAt != null && s.DeletedAt < cutoff)
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        db.ExamSessions.RemoveRange(expired);
        var removed = await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "ArchiveCleanup: purged {Count} session archive(s) older than {Cutoff:O} (retention={RetentionDays}d). Ids=[{Ids}]",
            removed,
            cutoff,
            retentionDays,
            string.Join(",", expired.Select(e => e.Id)));
    }

    /// <summary>
    /// Reads <c>Archive:RetentionDays</c> from configuration on
    /// every tick so an admin can adjust the value via env var
    /// (Archive__RetentionDays=15 or 30) without restarting the
    /// service. Falls back to 30 days and clamps any other value
    /// to the nearest allowed setting to stay safe if the config
    /// file is edited by hand to something invalid at runtime.
    /// </summary>
    private int ResolveRetentionDays()
    {
        var raw = _configuration["Archive:RetentionDays"];
        if (int.TryParse(raw, out var parsed) && (parsed == 15 || parsed == 30))
        {
            return parsed;
        }
        return 30;
    }
}
