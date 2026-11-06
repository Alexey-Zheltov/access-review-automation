using AccessReviewPortal.Data;
using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Services;

/// <summary>Settings that can be changed in the UI. Stored in the AppSettings table; defaults come from configuration.</summary>
public sealed class SettingsService(IDbContextFactory<AppDbContext> dbFactory, IOptions<ReviewSettingsOptions> options)
{
    private const string ThresholdKey = "InactivityThresholdDays";

    public async Task<int> GetInactivityThresholdDaysAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == ThresholdKey, ct);
        return setting is not null && int.TryParse(setting.Value, out var days) && days > 0
            ? days
            : Math.Max(1, options.Value.InactivityThresholdDays);
    }

    public async Task SetInactivityThresholdDaysAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == ThresholdKey, ct);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = ThresholdKey, Value = days.ToString() });
        }
        else
        {
            setting.Value = days.ToString();
        }
        await db.SaveChangesAsync(ct);
    }
}
