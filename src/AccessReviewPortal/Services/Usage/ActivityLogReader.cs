using System.Collections.Concurrent;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using AccessReviewPortal.Services.Scanning;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Services.Usage;

public sealed record ActivityKey(string CallerObjectId, string Scope, string Action);

public sealed record ActivityValue(DateTime LastUtc, string Operation);

/// <summary>Exact attribution from authorization.evidence (only available in exported logs, e.g. Log Analytics).</summary>
public sealed record EvidenceRecord(
    string CallerObjectId,
    string RoleAssignmentKey,
    string EvidencePrincipalId,
    string RoleKey,
    string Scope,
    DateTime LastUtc,
    string Operation);

public sealed class ActivityLogResult
{
    public ConcurrentDictionary<ActivityKey, ActivityValue> Events { get; } = new();
    public List<EvidenceRecord> Evidence { get; } = [];
    public long EventsRead;
    public string Source { get; set; } = "";
    public bool IsExact => Evidence.Count > 0;
}

/// <summary>
/// Reads the Azure Activity Log of each subscription through REST (eventtypes/management/values).
/// The REST API does not return authorization.evidence, so each write/action event is attributed later
/// to the caller's assignments whose scope contains the event scope and whose role allows the action.
/// </summary>
public sealed class ActivityLogReader(ArmRestClient rest, IOptions<ReviewSettingsOptions> options)
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string Select = "authorization,caller,claims,eventTimestamp,operationName,status,category";

    public async Task<ActivityLogResult> ReadAsync(ScanContext ctx, CancellationToken ct)
    {
        var o = options.Value;
        var days = Math.Clamp(o.ActivityLogLookbackDays, 1, 90);
        var to = ctx.NowUtc;
        var from = to.AddDays(-days).AddMinutes(5);
        var result = new ActivityLogResult { Source = $"Azure Activity Log REST, {days} days (inferred: caller + scope + role permissions)" };

        // Short slices let many requests run in parallel (each slice is paged sequentially by the API).
        var windows = new List<(DateTime From, DateTime To)>();
        for (var start = from; start < to; start = start.AddDays(2))
        {
            windows.Add((start, start.AddDays(2) < to ? start.AddDays(2) : to));
        }

        // Users appear as UPN in "caller"; service principals as object id. Claims hold the object id for both.
        var upnToId = ctx.Users.Values.Where(u => !string.IsNullOrEmpty(u.Upn))
            .GroupBy(u => u.Upn!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().UserId, StringComparer.OrdinalIgnoreCase);

        var activeSubscriptions = ctx.Subscriptions.Where(s => !s.State.Equals("Disabled", StringComparison.OrdinalIgnoreCase)).ToList();
        var work = activeSubscriptions.SelectMany(s => windows.Select(w => (Sub: s, Window: w))).ToList();
        var perSubscriptionCount = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var capped = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.ActivityLogParallelism), CancellationToken = ct },
            async (item, token) =>
            {
                var filter = Uri.EscapeDataString($"eventTimestamp ge '{item.Window.From:yyyy-MM-ddTHH:mm:ssZ}' and eventTimestamp le '{item.Window.To:yyyy-MM-ddTHH:mm:ssZ}'");
                var url = $"/subscriptions/{item.Sub.SubscriptionId}/providers/Microsoft.Insights/eventtypes/management/values" +
                          $"?api-version=2015-04-01&$filter={filter}&$select={Uri.EscapeDataString(Select)}";
                try
                {
                    await foreach (var e in rest.GetPagedAsync(url, token))
                    {
                        Interlocked.Increment(ref result.EventsRead);
                        var count = perSubscriptionCount.AddOrUpdate(item.Sub.SubscriptionId, 1, (_, c) => c + 1);
                        if (count > o.ActivityLogMaxEventsPerSubscription)
                        {
                            capped[item.Sub.SubscriptionId] = true;
                            break;
                        }
                        Process(e, result, upnToId);
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    failures.Add($"{item.Sub.DisplayName}: {ex.Message.Split('\n')[0]}");
                }
            });

        foreach (var f in failures.Distinct().Take(5))
        {
            ctx.Warn($"Activity Log read failed for {f}");
        }
        foreach (var sub in capped.Keys)
        {
            ctx.Warn($"Activity Log of subscription {sub} has more than {o.ActivityLogMaxEventsPerSubscription} events; the rest was skipped. Use Log Analytics for busy subscriptions.");
        }
        ctx.Info($"Activity Log: {result.EventsRead} events read from {activeSubscriptions.Count} subscription(s), " +
                 $"{result.Events.Count} distinct caller/scope/operation combinations.");
        return result;
    }

    private static void Process(System.Text.Json.JsonElement e, ActivityLogResult result, IReadOnlyDictionary<string, string> upnToId)
    {
        if (!string.Equals(e.Str("category", "value"), "Administrative", StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(e.Str("status", "value"), "Failed", StringComparison.OrdinalIgnoreCase)) return;

        var action = e.Str("authorization", "action");
        var scope = e.Str("authorization", "scope");
        var timestamp = e.Date("eventTimestamp");
        // Some read operations (e.g. operationStatuses/read) are logged too; they do not prove write usage.
        if (action is null || scope is null || timestamp is null || action.EndsWith("/read", StringComparison.OrdinalIgnoreCase)) return;

        var caller = e.Str("caller");
        var callerId = e.Str("claims", ObjectIdClaim);
        if (callerId is null && caller is not null)
        {
            callerId = Guid.TryParse(caller, out var callerGuid) ? callerGuid.ToString()
                : upnToId.TryGetValue(caller, out var userId) ? userId : null;
        }
        if (callerId is null) return;

        var key = new ActivityKey(Ids.ObjectId(callerId), Ids.NormalizeScope(scope), action.ToLowerInvariant());
        var value = new ActivityValue(timestamp.Value, e.Str("operationName", "value") ?? action);
        result.Events.AddOrUpdate(key, value, (_, old) => value.LastUtc > old.LastUtc ? value : old);
    }
}
