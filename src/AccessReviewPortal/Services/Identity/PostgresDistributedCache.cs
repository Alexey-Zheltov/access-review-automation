using AccessReviewPortal.Data;
using AccessReviewPortal.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace AccessReviewPortal.Services.Identity;

/// <summary>
/// IDistributedCache stored in PostgreSQL. Used for the MSAL token cache (encrypted by Microsoft.Identity.Web with
/// Data Protection), so signed-in users keep their tokens after a container restart and across instances.
/// </summary>
public sealed class PostgresDistributedCache(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time) : IDistributedCache
{
    private static readonly TimeSpan DefaultSlidingExpiration = TimeSpan.FromDays(14);
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;

    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var now = time.GetUtcNow();
        var entry = await db.DistributedCache.FirstOrDefaultAsync(e => e.Id == key, token);
        if (entry is null) return null;
        if (entry.ExpiresAtTime <= now)
        {
            db.DistributedCache.Remove(entry);
            await db.SaveChangesAsync(token);
            return null;
        }

        if (Slide(entry, now))
        {
            await db.SaveChangesAsync(token);
        }
        await CleanupIfDueAsync(db, now, token);
        return entry.Value;
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => SetAsync(key, value, options).GetAwaiter().GetResult();

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        var now = time.GetUtcNow();
        DateTimeOffset? absolute = options.AbsoluteExpiration
                                   ?? (options.AbsoluteExpirationRelativeToNow is { } rel ? now + rel : null);
        var sliding = options.SlidingExpiration ?? (absolute is null ? DefaultSlidingExpiration : null);
        var expires = sliding is null ? absolute!.Value : Min(now + sliding.Value, absolute);
        long? slidingSeconds = sliding is null ? null : (long)sliding.Value.TotalSeconds;

        await using var db = await dbFactory.CreateDbContextAsync(token);
        var entry = await db.DistributedCache.FirstOrDefaultAsync(e => e.Id == key, token);
        if (entry is null)
        {
            entry = new DistributedCacheEntry { Id = key };
            db.DistributedCache.Add(entry);
        }
        entry.Value = value;
        entry.AbsoluteExpiration = absolute;
        entry.SlidingExpirationInSeconds = slidingSeconds;
        entry.ExpiresAtTime = expires;

        try
        {
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateException)
        {
            // Another request inserted the same key at the same time: overwrite it.
            await using var retry = await dbFactory.CreateDbContextAsync(token);
            await retry.DistributedCache.Where(e => e.Id == key).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Value, value)
                .SetProperty(e => e.AbsoluteExpiration, absolute)
                .SetProperty(e => e.SlidingExpirationInSeconds, slidingSeconds)
                .SetProperty(e => e.ExpiresAtTime, expires), token);
        }
    }

    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var entry = await db.DistributedCache.FirstOrDefaultAsync(e => e.Id == key, token);
        if (entry is not null && Slide(entry, time.GetUtcNow()))
        {
            await db.SaveChangesAsync(token);
        }
    }

    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        await db.DistributedCache.Where(e => e.Id == key).ExecuteDeleteAsync(token);
    }

    private static bool Slide(DistributedCacheEntry entry, DateTimeOffset now)
    {
        if (entry.SlidingExpirationInSeconds is not { } seconds) return false;
        var next = Min(now.AddSeconds(seconds), entry.AbsoluteExpiration);
        if (next - entry.ExpiresAtTime < TimeSpan.FromMinutes(1)) return false;
        entry.ExpiresAtTime = next;
        return true;
    }

    private async Task CleanupIfDueAsync(AppDbContext db, DateTimeOffset now, CancellationToken token)
    {
        if (now < _nextCleanup) return;
        _nextCleanup = now.AddMinutes(30);
        await db.DistributedCache.Where(e => e.ExpiresAtTime <= now).ExecuteDeleteAsync(token);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset? b) => b is { } v && v < a ? v : a;
}
