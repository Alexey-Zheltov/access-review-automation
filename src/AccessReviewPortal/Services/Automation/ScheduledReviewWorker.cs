using AccessReviewPortal.Data;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Services.Automation;

/// <summary>
/// Background worker for regular scans:
/// - Monthly Activity Snapshot: keeps sign-in and audit evidence (Graph keeps only 30 days) so 90 days are always covered;
/// - Quarterly Campaign Run: snapshot + baseline comparison + Excel archive + "review ready" notification.
/// A PostgreSQL advisory lock makes sure only one instance runs the schedule (App Service / Container Apps scale-out).
/// </summary>
public sealed class ScheduledReviewWorker(
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<AutomationOptions> options,
    ILogger<ScheduledReviewWorker> logger) : BackgroundService
{
    private const long AdvisoryLockKey = 0x41_52_50_01; // "ARP" + 1

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        var interval = TimeSpan.FromSeconds(Math.Max(15, options.Value.CheckIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Scheduled review check failed");
            }
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var lockDb = await dbFactory.CreateDbContextAsync(ct);
        await lockDb.Database.OpenConnectionAsync(ct);
        var acquired = await lockDb.Database.SqlQueryRaw<bool>($"SELECT pg_try_advisory_lock({AdvisoryLockKey}) AS \"Value\"").SingleAsync(ct);
        if (!acquired) return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var automation = scope.ServiceProvider.GetRequiredService<AutomationService>();
            var settings = await automation.GetSettingsAsync(ct);
            var state = await automation.GetStateAsync(ct);
            var now = DateTime.UtcNow;
            state.LastCheckUtc = now;

            var quarterly = settings.QuarterlyCampaignEnabled
                ? AutomationService.ScheduledThisMonth(now, settings.QuarterlyDayOfMonth, settings.QuarterlyTimeUtc, settings.QuarterlyMonths)
                : null;
            var monthly = settings.MonthlySnapshotEnabled
                ? AutomationService.ScheduledThisMonth(now, settings.MonthlyDayOfMonth, settings.MonthlyTimeUtc)
                : null;

            var retryAfter = TimeSpan.FromMinutes(Math.Max(1, options.Value.RetryAfterMinutes));
            bool CanTry(DateTime? lastAttempt) => lastAttempt is null || now - lastAttempt >= retryAfter;

            if (AutomationService.IsDue(now, quarterly, state.LastQuarterlyRunUtc))
            {
                if (!CanTry(state.LastQuarterlyAttemptUtc)) return;
                logger.LogInformation("Quarterly campaign is due ({Planned:u}).", quarterly);
                state.LastQuarterlyAttemptUtc = now;
                await automation.SaveStateAsync(state, ct);
                var campaign = await automation.RunQuarterlyCampaignAsync(ScanTrigger.QuarterlyCampaign, null, ct);
                if (campaign.Status == CampaignStatus.Completed)
                {
                    state.LastQuarterlyRunUtc = now;
                    // The campaign snapshot also counts as this month's activity snapshot.
                    if (AutomationService.IsDue(now, monthly, state.LastMonthlyRunUtc)) state.LastMonthlyRunUtc = now;
                }
                state.LastResult = $"{now:yyyy-MM-dd HH:mm} UTC quarterly campaign #{campaign.Id}: {campaign.Status} {campaign.Error}";
            }
            else if (AutomationService.IsDue(now, monthly, state.LastMonthlyRunUtc))
            {
                if (!CanTry(state.LastMonthlyAttemptUtc)) return;
                logger.LogInformation("Monthly snapshot is due ({Planned:u}).", monthly);
                state.LastMonthlyAttemptUtc = now;
                await automation.SaveStateAsync(state, ct);
                int? snapshotId = null;
                string? error = null;
                try
                {
                    snapshotId = await automation.RunMonthlySnapshotAsync(null, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    error = ex.Message;
                }
                if (snapshotId is not null) state.LastMonthlyRunUtc = now;
                state.LastResult = $"{now:yyyy-MM-dd HH:mm} UTC monthly snapshot: " +
                                   (snapshotId is not null ? $"#{snapshotId}" : error is not null ? "failed: " + error : "skipped, another scan was running");
            }
            await automation.SaveStateAsync(state, ct);
        }
        finally
        {
            await lockDb.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }
}
