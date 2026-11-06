using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AccessReviewPortal.Data;
using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Review;
using AccessReviewPortal.Services.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Services.Automation;

/// <summary>Schedule of the background scans. Stored in PostgreSQL (AppSettings), defaults come from the "Automation" section.</summary>
public sealed class AutomationSettings
{
    public bool MonthlySnapshotEnabled { get; set; }
    public int MonthlyDayOfMonth { get; set; } = 1;
    public string MonthlyTimeUtc { get; set; } = "02:00";
    public bool QuarterlyCampaignEnabled { get; set; }
    public int[] QuarterlyMonths { get; set; } = [1, 4, 7, 10];
    public int QuarterlyDayOfMonth { get; set; } = 1;
    public string QuarterlyTimeUtc { get; set; } = "04:00";
    public string? NotificationWebhookUrl { get; set; }
}

public sealed class AutomationState
{
    public DateTime? LastMonthlyRunUtc { get; set; }
    public DateTime? LastQuarterlyRunUtc { get; set; }

    /// <summary>Last start of a scheduled run (also failed ones). A failed run is retried after AutomationOptions.RetryAfterMinutes.</summary>
    public DateTime? LastMonthlyAttemptUtc { get; set; }
    public DateTime? LastQuarterlyAttemptUtc { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>Monthly activity snapshots and quarterly review campaigns (snapshot, baseline, recommendations, Excel archive, notification).</summary>
public sealed class AutomationService(
    IDbContextFactory<AppDbContext> dbFactory,
    ScanJobManager jobs,
    SnapshotService snapshots,
    GroupPermissionsService groups,
    ExcelExportService excel,
    SettingsService settings,
    IHttpClientFactory httpClientFactory,
    IOptions<AutomationOptions> defaults,
    IOptions<PortalOptions> portal,
    ILogger<AutomationService> logger)
{
    private const string SettingsKey = "Automation.Settings";
    private const string StateKey = "Automation.State";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AutomationSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var stored = await GetJsonAsync<AutomationSettings>(SettingsKey, ct);
        if (stored is not null) return stored;
        var d = defaults.Value;
        return new AutomationSettings
        {
            MonthlySnapshotEnabled = d.MonthlySnapshotEnabled,
            MonthlyDayOfMonth = d.MonthlyDayOfMonth,
            MonthlyTimeUtc = d.MonthlyTimeUtc,
            QuarterlyCampaignEnabled = d.QuarterlyCampaignEnabled,
            QuarterlyMonths = d.QuarterlyMonths,
            QuarterlyDayOfMonth = d.QuarterlyDayOfMonth,
            QuarterlyTimeUtc = d.QuarterlyTimeUtc,
            NotificationWebhookUrl = d.NotificationWebhookUrl
        };
    }

    public Task SaveSettingsAsync(AutomationSettings value, CancellationToken ct = default)
    {
        value.MonthlyDayOfMonth = Math.Clamp(value.MonthlyDayOfMonth, 1, 28);
        value.QuarterlyDayOfMonth = Math.Clamp(value.QuarterlyDayOfMonth, 1, 28);
        value.MonthlyTimeUtc = ParseTime(value.MonthlyTimeUtc).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        value.QuarterlyTimeUtc = ParseTime(value.QuarterlyTimeUtc).ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        value.QuarterlyMonths = value.QuarterlyMonths.Where(m => m is >= 1 and <= 12).Distinct().Order().ToArray();
        return SetJsonAsync(SettingsKey, value, ct);
    }

    public async Task<AutomationState> GetStateAsync(CancellationToken ct = default) => await GetJsonAsync<AutomationState>(StateKey, ct) ?? new AutomationState();

    public Task SaveStateAsync(AutomationState state, CancellationToken ct = default) => SetJsonAsync(StateKey, state, ct);

    public async Task<List<ReviewCampaign>> GetCampaignsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ReviewCampaigns.AsNoTracking().OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => new ReviewCampaign
            {
                Id = c.Id, Name = c.Name, Trigger = c.Trigger, StartedByUpn = c.StartedByUpn, CreatedAtUtc = c.CreatedAtUtc,
                CompletedAtUtc = c.CompletedAtUtc, Status = c.Status, ScanSnapshotId = c.ScanSnapshotId, BaselineSnapshotId = c.BaselineSnapshotId,
                TotalRows = c.TotalRows, RemoveCount = c.RemoveCount, ReviewCount = c.ReviewCount, ReportFileName = c.ReportFileName,
                ReportSizeBytes = c.ReportSizeBytes, NotificationStatus = c.NotificationStatus, Error = c.Error
            })
            .ToListAsync(ct);
    }

    public async Task<(string FileName, byte[] Content)?> GetReportAsync(int campaignId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var c = await db.ReviewCampaigns.AsNoTracking().Where(x => x.Id == campaignId)
            .Select(x => new { x.ReportFileName, x.ReportContent }).FirstOrDefaultAsync(ct);
        return c?.ReportContent is null ? null : (c.ReportFileName ?? $"campaign-{campaignId}.xlsx", c.ReportContent);
    }

    // ---------------------------------------------------------------- schedule

    /// <summary>Planned run time of this month, or null when the schedule does not run this month.</summary>
    public static DateTime? ScheduledThisMonth(DateTime nowUtc, int dayOfMonth, string timeUtc, int[]? months = null)
    {
        if (months is not null && !months.Contains(nowUtc.Month)) return null;
        var day = Math.Clamp(dayOfMonth, 1, DateTime.DaysInMonth(nowUtc.Year, nowUtc.Month));
        return new DateTime(nowUtc.Year, nowUtc.Month, day, 0, 0, 0, DateTimeKind.Utc) + ParseTime(timeUtc);
    }

    /// <summary>True when the run of this month is due: its time has passed (up to 7 days ago) and it did not run yet.</summary>
    public static bool IsDue(DateTime nowUtc, DateTime? scheduledUtc, DateTime? lastRunUtc) =>
        scheduledUtc is { } s && nowUtc >= s && nowUtc - s < TimeSpan.FromDays(7) && (lastRunUtc is null || lastRunUtc < s);

    /// <summary>Next planned run (for the Automation page).</summary>
    public static DateTime? NextRun(DateTime nowUtc, bool enabled, int dayOfMonth, string timeUtc, int[]? months, DateTime? lastRunUtc)
    {
        if (!enabled) return null;
        for (var i = 0; i < 13; i++)
        {
            var month = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(i);
            var planned = ScheduledThisMonth(month, dayOfMonth, timeUtc, months);
            if (planned is { } p && (p > nowUtc || IsDue(nowUtc, p, lastRunUtc))) return p;
        }
        return null;
    }

    public static string QuarterName(DateTime utc) => $"{utc.Year}-Q{(utc.Month - 1) / 3 + 1}";

    public static TimeSpan ParseTime(string? value) =>
        TimeSpan.TryParseExact(value?.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : TimeSpan.FromHours(2);

    // ---------------------------------------------------------------- runs

    public async Task<int?> RunMonthlySnapshotAsync(string? startedByUpn, CancellationToken ct)
    {
        var label = $"{DateTime.UtcNow:yyyy-MM} Monthly Snapshot";
        var snapshotId = await jobs.RunAndWaitAsync(label, ScanTrigger.MonthlySchedule, startedByUpn ?? "scheduler", ct);
        logger.LogInformation("Monthly snapshot {Result}", snapshotId is null ? "skipped: another scan is running" : $"#{snapshotId} done");
        return snapshotId;
    }

    /// <summary>Quarterly campaign: new snapshot, comparison with the baseline of the quarter, Excel archive and notification.</summary>
    public async Task<ReviewCampaign> RunQuarterlyCampaignAsync(ScanTrigger trigger, string? startedByUpn, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var campaign = new ReviewCampaign
        {
            Name = $"{QuarterName(now)} Quarterly Review",
            Trigger = trigger,
            StartedByUpn = startedByUpn ?? "scheduler",
            CreatedAtUtc = now,
            Status = CampaignStatus.Running
        };
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.ReviewCampaigns.Add(campaign);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            var snapshotId = await jobs.RunAndWaitAsync(campaign.Name, ScanTrigger.QuarterlyCampaign, campaign.StartedByUpn, ct)
                             ?? throw new InvalidOperationException("Another scan is running. The campaign will be retried at the next check.");

            var baselineId = await FindBaselineAsync(snapshotId, now, ct);
            var threshold = await settings.GetInactivityThresholdDaysAsync(ct);
            await snapshots.RecalculateAsync(snapshotId, baselineId, threshold, ct);

            var snapshot = (await snapshots.GetSnapshotAsync(snapshotId, ct))!;
            var rows = await snapshots.GetRowsAsync(snapshotId, null, ct);
            var users = await snapshots.GetUsersAsync(snapshotId, ct);
            var groupSummaries = await groups.GetGroupsAsync(snapshotId, ct);
            var report = excel.Build(snapshot, rows, users, prefillFromRecommendation: true, groupSummaries);

            campaign.ScanSnapshotId = snapshotId;
            campaign.BaselineSnapshotId = baselineId;
            campaign.TotalRows = rows.Count;
            campaign.RemoveCount = rows.Count(r => r.RecommendedAction == RecommendedAction.Remove);
            campaign.ReviewCount = rows.Count(r => r.RecommendedAction == RecommendedAction.Review);
            campaign.ReportFileName = $"AccessReview_{QuarterName(now)}_{now:yyyyMMdd}.xlsx";
            campaign.ReportContent = report;
            campaign.ReportSizeBytes = report.Length;
            campaign.NotificationStatus = await NotifyAsync(campaign, ct);
            campaign.Status = CampaignStatus.Completed;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Quarterly campaign failed");
            campaign.Status = CampaignStatus.Failed;
            campaign.Error = ex.Message;
        }

        campaign.CompletedAtUtc = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            db.ReviewCampaigns.Update(campaign);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return campaign;
    }

    /// <summary>Baseline = snapshot of the previous campaign, otherwise the last completed snapshot before the quarter started.</summary>
    private async Task<int?> FindBaselineAsync(int snapshotId, DateTime nowUtc, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var previousCampaign = await db.ReviewCampaigns.AsNoTracking()
            .Where(c => c.Status == CampaignStatus.Completed && c.ScanSnapshotId != null && c.ScanSnapshotId != snapshotId)
            .OrderByDescending(c => c.CreatedAtUtc).Select(c => c.ScanSnapshotId).FirstOrDefaultAsync(ct);
        if (previousCampaign is int id && await db.ScanSnapshots.AnyAsync(s => s.Id == id, ct)) return id;

        var quarterStart = new DateTime(nowUtc.Year, (nowUtc.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var before = await db.ScanSnapshots.AsNoTracking()
            .Where(s => s.Status == ScanStatus.Completed && s.Id != snapshotId && s.CreatedAtUtc < quarterStart)
            .OrderByDescending(s => s.CreatedAtUtc).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
        return before ?? await db.ScanSnapshots.AsNoTracking()
            .Where(s => s.Status == ScanStatus.Completed && s.Id != snapshotId)
            .OrderByDescending(s => s.CreatedAtUtc).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
    }

    private async Task<string> NotifyAsync(ReviewCampaign campaign, CancellationToken ct)
    {
        var url = (await GetSettingsAsync(ct)).NotificationWebhookUrl;
        if (string.IsNullOrWhiteSpace(url)) return "Not configured";

        var baseUrl = portal.Value.PublicBaseUrl?.TrimEnd('/');
        var payload = new
        {
            eventType = "AccessReviewCampaignReady",
            campaignId = campaign.Id,
            campaign = campaign.Name,
            snapshotId = campaign.ScanSnapshotId,
            baselineSnapshotId = campaign.BaselineSnapshotId,
            totalRows = campaign.TotalRows,
            removeRecommended = campaign.RemoveCount,
            reviewRecommended = campaign.ReviewCount,
            reportUrl = baseUrl is null ? null : $"{baseUrl}/campaigns/{campaign.Id}/report.xlsx",
            portalReviewUrl = baseUrl is null ? null : $"{baseUrl}/review/{campaign.ScanSnapshotId}",
            createdAtUtc = campaign.CreatedAtUtc
        };
        try
        {
            using var response = await httpClientFactory.CreateClient("webhook").PostAsJsonAsync(url, payload, ct);
            return $"Sent: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"Failed: {ex.Message}";
        }
    }

    private async Task<T?> GetJsonAsync<T>(string key, CancellationToken ct) where T : class
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var value = await db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return value is null ? null : JsonSerializer.Deserialize<T>(value, Json);
    }

    private async Task SetJsonAsync<T>(string key, T value, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(value, Json);
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync(ct);
    }
}
