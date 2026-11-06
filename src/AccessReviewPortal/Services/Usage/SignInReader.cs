using System.Collections.Concurrent;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using AccessReviewPortal.Services.Scanning;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace AccessReviewPortal.Services.Usage;

public sealed record SignInValue(DateTime LastUtc, string App);

public sealed class SignInData
{
    /// <summary>Latest successful sign-in to Azure Portal / Azure Resource Manager per user.</summary>
    public Dictionary<string, SignInValue> AzureControlPlane { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Latest successful sign-in to admin portals per user (activity signal for Entra roles).</summary>
    public Dictionary<string, SignInValue> AdminPortals { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Source { get; set; } = "";
    public bool Available { get; set; } = true;

    public static void Merge(Dictionary<string, SignInValue> target, string userId, SignInValue value)
    {
        if (!target.TryGetValue(userId, out var old) || value.LastUtc > old.LastUtc)
        {
            target[userId] = value;
        }
    }
}

/// <summary>
/// Reads Entra sign-in logs through Microsoft Graph (auditLogs/signIns, interactive sign-ins, 30 days).
/// One tenant-wide query per day: "appId in (Azure Portal, admin portals) or resourceId = Azure Resource Manager".
/// Day slices keep each query fast; they run in parallel and the whole step has a time budget.
/// For 90 days of sign-ins use a Log Analytics workspace (ReviewSettings__LogAnalyticsWorkspaceId).
/// </summary>
public sealed class SignInReader(AzureClients clients, IOptions<ReviewSettingsOptions> options)
{
    public const int GraphRetentionDays = 30;

    public async Task<SignInData> ReadAsync(ScanContext ctx, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var o = options.Value;
        var data = new SignInData { Source = $"Entra sign-in logs (Graph, last {GraphRetentionDays} days)" };
        var users = userIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var armApps = o.EffectiveControlPlaneAppIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var adminApps = o.EffectiveAdminPortalAppIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clause = string.Join(" or ", armApps.Union(adminApps, StringComparer.OrdinalIgnoreCase).Select(a => $"appId eq '{a}'")
            .Append($"resourceId eq '{o.AzureControlPlaneResourceId}'"));

        var end = ctx.NowUtc;
        var start = end.AddDays(-GraphRetentionDays).AddMinutes(5);
        var windows = new List<(DateTime From, DateTime To)>();
        for (var to = end; to > start; to = to.AddDays(-1))
        {
            windows.Add((to.AddDays(-1) > start ? to.AddDays(-1) : start, to));
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, o.SignInTimeBudgetMinutes)));

        var arm = new ConcurrentDictionary<string, SignInValue>(StringComparer.OrdinalIgnoreCase);
        var admin = new ConcurrentDictionary<string, SignInValue>(StringComparer.OrdinalIgnoreCase);
        int done = 0, failed = 0, rows = 0;
        var forbidden = false;
        string? lastError = null;

        try
        {
            await Parallel.ForEachAsync(windows, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token }, async (w, token) =>
            {
                if (forbidden) return;
                try
                {
                    var page = await clients.Graph.AuditLogs.SignIns.GetAsync(r =>
                    {
                        r.QueryParameters.Filter = $"createdDateTime ge {w.From:yyyy-MM-ddTHH:mm:ssZ} and createdDateTime lt {w.To:yyyy-MM-ddTHH:mm:ssZ} and ({clause})";
                        r.QueryParameters.Top = 999;
                        r.QueryParameters.Select = ["userId", "createdDateTime", "appId", "appDisplayName", "resourceId", "resourceDisplayName", "status"];
                    }, token);
                    if (page is null) return;

                    var iterator = PageIterator<SignIn, SignInCollectionResponse>.CreatePageIterator(clients.Graph, page, s =>
                    {
                        Interlocked.Increment(ref rows);
                        var userId = Ids.ObjectId(s.UserId);
                        if (s.CreatedDateTime is null || s.Status?.ErrorCode is not (null or 0) || !users.Contains(userId)) return true;

                        var when = s.CreatedDateTime.Value.UtcDateTime;
                        var app = string.IsNullOrEmpty(s.ResourceDisplayName) || s.ResourceDisplayName == s.AppDisplayName
                            ? s.AppDisplayName ?? s.AppId ?? ""
                            : $"{s.AppDisplayName} -> {s.ResourceDisplayName}";
                        var value = new SignInValue(when, app);
                        if (armApps.Contains(s.AppId ?? "") || string.Equals(s.ResourceId, o.AzureControlPlaneResourceId, StringComparison.OrdinalIgnoreCase))
                        {
                            arm.AddOrUpdate(userId, value, (_, old) => value.LastUtc > old.LastUtc ? value : old);
                        }
                        if (adminApps.Contains(s.AppId ?? ""))
                        {
                            admin.AddOrUpdate(userId, value, (_, old) => value.LastUtc > old.LastUtc ? value : old);
                        }
                        return true;
                    });
                    await iterator.IterateAsync(token);
                    Interlocked.Increment(ref done);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    if (ex.StatusCode() == 403) forbidden = true;
                    Interlocked.Increment(ref failed);
                    lastError = ex.ShortMessage();
                }
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Warn($"Sign-in log reading stopped after {o.SignInTimeBudgetMinutes} minutes (time budget): {done} of {windows.Count} days read. " +
                     "Use Log Analytics for large tenants.");
        }

        if (forbidden)
        {
            data.Available = false;
            ctx.Warn($"Sign-in logs are not readable ({lastError}). Needs AuditLog.Read.All and Entra ID P1/P2.");
            return data;
        }
        if (failed > 0)
        {
            ctx.Warn($"{failed} of {windows.Count} days of sign-in logs could not be read (throttling or timeout). Last error: {lastError}");
        }

        foreach (var (user, value) in arm) data.AzureControlPlane[user] = value;
        foreach (var (user, value) in admin) data.AdminPortals[user] = value;
        ctx.Info($"Sign-in logs: {done} of {windows.Count} days read, {rows} sign-ins to Azure/admin portals, " +
                 $"{data.AzureControlPlane.Count} users with Azure Portal/ARM sign-ins, {data.AdminPortals.Count} with admin portal sign-ins.");
        return data;
    }
}
