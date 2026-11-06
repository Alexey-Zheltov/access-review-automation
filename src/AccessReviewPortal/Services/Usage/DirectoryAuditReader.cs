using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using AccessReviewPortal.Services.Scanning;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;

namespace AccessReviewPortal.Services.Usage;

public sealed record DirectoryActivity(DateTime LastUtc, string Activity);

/// <summary>
/// Reads the Entra directory audit log (Graph, 30 days):
/// - admin activity initiated by users with Entra roles (usage signal for write-capable Entra roles);
/// - PIM events: activations and assignment justifications when PIM requests are not readable.
/// </summary>
public sealed class DirectoryAuditReader(AzureClients clients, IOptions<ReviewSettingsOptions> options)
{
    // Self-service events do not prove that an admin role was used.
    private static readonly HashSet<string> SelfServiceServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "Self-service Password Management", "Authentication Methods", "Self-service Group Management",
        "Access Reviews", "Entitlement Management", "MyApps", "Device Registration Service", "Account Provisioning"
    };

    public async Task<Dictionary<string, DirectoryActivity>> ReadAdminActivityAsync(ScanContext ctx, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var result = new Dictionary<string, DirectoryActivity>(StringComparer.OrdinalIgnoreCase);
        if (userIds.Count == 0) return result;

        var since = ctx.NowUtc.AddDays(-SignInReader.GraphRetentionDays).AddMinutes(5);
        var forbidden = false;
        using var gate = new SemaphoreSlim(Math.Max(1, Math.Min(4, options.Value.MaxParallelism)));
        var tasks = userIds.Select(async userId =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (forbidden) return;
                var page = await clients.Graph.AuditLogs.DirectoryAudits.GetAsync(r =>
                {
                    r.QueryParameters.Filter = $"initiatedBy/user/id eq '{userId}' and activityDateTime ge {since:yyyy-MM-ddTHH:mm:ssZ}";
                    r.QueryParameters.Top = 100;
                }, ct);

                var latest = (page?.Value ?? [])
                    .Where(a => IsAdminActivity(a, userId))
                    .OrderByDescending(a => a.ActivityDateTime)
                    .FirstOrDefault();
                if (latest?.ActivityDateTime is not null)
                {
                    lock (result)
                    {
                        result[userId] = new DirectoryActivity(latest.ActivityDateTime.Value.UtcDateTime,
                            $"{latest.ActivityDisplayName} ({latest.LoggedByService})");
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (!forbidden)
                {
                    forbidden = true;
                    ctx.Warn($"Directory audit log is not readable ({ex.ShortMessage()}). Needs AuditLog.Read.All.");
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        ctx.Info($"Directory audit: admin activity found for {result.Count} of {userIds.Count} users with Entra roles (last {SignInReader.GraphRetentionDays} days).");
        return result;
    }

    /// <summary>PIM events from the audit log: adds activations to the context and returns assignment justifications.</summary>
    public async Task<Dictionary<string, string>> ReadPimEventsAsync(ScanContext ctx, CancellationToken ct)
    {
        var justifications = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var since = ctx.NowUtc.AddDays(-SignInReader.GraphRetentionDays).AddMinutes(5);
        try
        {
            var page = await clients.Graph.AuditLogs.DirectoryAudits.GetAsync(r =>
            {
                r.QueryParameters.Filter = $"loggedByService eq 'PIM' and activityDateTime ge {since:yyyy-MM-ddTHH:mm:ssZ}";
                r.QueryParameters.Top = 999;
            }, ct);
            var events = await clients.Graph.ReadAllAsync<DirectoryAudit, DirectoryAuditCollectionResponse>(page, ct);
            var activations = 0;

            foreach (var e in events.Where(e => e.ActivityDateTime.HasValue && string.Equals(e.Result?.ToString(), "Success", StringComparison.OrdinalIgnoreCase)))
            {
                var name = e.ActivityDisplayName ?? "";
                if (!name.Contains("completed", StringComparison.OrdinalIgnoreCase)) continue;

                var userId = e.TargetResources?.FirstOrDefault(t => string.Equals(t.Type, "User", StringComparison.OrdinalIgnoreCase))?.Id
                             ?? e.InitiatedBy?.User?.Id;
                var roleId = e.TargetResources?.FirstOrDefault(t => string.Equals(t.Type, "Role", StringComparison.OrdinalIgnoreCase))?.Id;
                if (userId is null || roleId is null) continue;

                var isAzure = string.Equals(e.Category, "ResourceManagement", StringComparison.OrdinalIgnoreCase);
                var scope = isAzure ? Detail(e, "ResourceOriginId") : "/";
                var roleKey = isAzure ? Ids.GuidKey(roleId) : roleId;

                if (name.Contains("PIM activation", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.PimActivations.Add(new PimActivation(Ids.ObjectId(userId), roleKey, scope, e.ActivityDateTime!.Value.UtcDateTime,
                        null, NullIfEmpty(e.ResultReason), "PIM activation (directory audit)"));
                    activations++;
                }
                else if (name.Contains("member to role", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(e.ResultReason))
                {
                    justifications[JustificationKey(userId, roleKey, scope)] = e.ResultReason!;
                }
            }

            ctx.Info($"Directory audit (PIM): {events.Count} events, {activations} activations, {justifications.Count} justifications (last {SignInReader.GraphRetentionDays} days).");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ctx.Warn($"PIM events in the directory audit log could not be read ({ex.ShortMessage()}).");
        }
        return justifications;
    }

    public static string JustificationKey(string principalId, string roleKey, string? scope) =>
        $"{Ids.ObjectId(principalId)}|{Ids.GuidKey(roleKey)}|{Ids.NormalizeScope(scope ?? "/")}";

    private static bool IsAdminActivity(DirectoryAudit a, string userId)
    {
        if (!string.Equals(a.Result?.ToString(), "Success", StringComparison.OrdinalIgnoreCase)) return false;
        if (SelfServiceServices.Contains(a.LoggedByService ?? "")) return false;
        if ((a.ActivityDisplayName ?? "").Contains("PIM activation", StringComparison.OrdinalIgnoreCase)) return false;
        return a.TargetResources?.Any(t => t.Id is not null && !string.Equals(Ids.ObjectId(t.Id), userId, StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    private static string? Detail(DirectoryAudit e, string key) =>
        e.AdditionalDetails?.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
