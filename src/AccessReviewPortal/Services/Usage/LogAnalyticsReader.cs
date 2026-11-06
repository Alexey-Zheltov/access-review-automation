using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using AccessReviewPortal.Services.Scanning;
using global::Azure;
using global::Azure.Monitor.Query;
using global::Azure.Monitor.Query.Models;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Services.Usage;

/// <summary>
/// Optional source with 90+ days of history: a Log Analytics workspace that receives the Azure Activity Log
/// (AzureActivity, with authorization.evidence) and Entra sign-in logs (SigninLogs, AADNonInteractiveUserSignInLogs).
/// </summary>
public sealed class LogAnalyticsReader(AzureClients clients, IOptions<ReviewSettingsOptions> options)
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.LogAnalyticsWorkspaceId);

    public async Task<ActivityLogResult?> ReadActivityAsync(ScanContext ctx, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        var days = Math.Max(1, options.Value.LogAnalyticsLookbackDays);
        var subs = string.Join(",", ctx.Subscriptions.Select(s => $"'{s.SubscriptionId.ToLowerInvariant()}'"));
        var query = $$"""
            AzureActivity
            | where TimeGenerated > ago({{days}}d)
            | where tolower(SubscriptionId) in ({{subs}})
            | where CategoryValue == "Administrative" and ActivityStatusValue !in~ ("Failure", "Failed")
            | extend auth = parse_json(Authorization), claims = parse_json(Claims)
            | extend CallerObjectId = tolower(tostring(claims["{{ObjectIdClaim}}"])),
                     Action = tolower(tostring(auth.action)), Scope = tolower(tostring(auth.scope))
            | extend ev = auth.evidence
            | where isnotempty(CallerObjectId) and isnotempty(Action) and not(Action endswith "/read")
            | summarize arg_max(TimeGenerated, OperationNameValue) by CallerObjectId, Scope, Action,
                RoleAssignmentId = tolower(tostring(ev.roleAssignmentId)), EvPrincipalId = tolower(tostring(ev.principalId)),
                RoleDefinitionId = tolower(tostring(ev.roleDefinitionId)), RoleAssignmentScope = tolower(tostring(ev.roleAssignmentScope))
            """;

        try
        {
            var rows = await QueryAsync(query, days, ct);
            var result = new ActivityLogResult();
            foreach (var row in rows)
            {
                var time = row.GetDateTimeOffset("TimeGenerated")?.UtcDateTime;
                var caller = row.GetString("CallerObjectId");
                var scope = row.GetString("Scope");
                var action = row.GetString("Action");
                if (time is null || string.IsNullOrEmpty(caller) || string.IsNullOrEmpty(action)) continue;
                var operation = row.GetString("OperationNameValue") ?? action;

                result.EventsRead++;
                var key = new ActivityKey(Ids.ObjectId(caller), Ids.NormalizeScope(scope), action);
                var value = new ActivityValue(time.Value, operation);
                result.Events.AddOrUpdate(key, value, (_, old) => value.LastUtc > old.LastUtc ? value : old);

                var assignmentId = row.GetString("RoleAssignmentId");
                if (!string.IsNullOrEmpty(assignmentId))
                {
                    result.Evidence.Add(new EvidenceRecord(Ids.ObjectId(caller), Ids.GuidKey(assignmentId),
                        Ids.ObjectId(row.GetString("EvPrincipalId")), Ids.GuidKey(row.GetString("RoleDefinitionId")),
                        row.GetString("RoleAssignmentScope") ?? "", time.Value, operation));
                }
            }

            result.Source = result.IsExact
                ? $"Log Analytics AzureActivity, {days} days (exact: authorization.evidence)"
                : $"Log Analytics AzureActivity, {days} days (inferred: no evidence in workspace)";
            ctx.Info($"Log Analytics AzureActivity: {result.Events.Count} caller/scope/operation combinations, {result.Evidence.Count} with evidence.");
            return result.Events.IsEmpty ? null : result;
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException)
        {
            ctx.Warn($"Log Analytics AzureActivity query failed, using Activity Log REST instead: {ex.Message.Split('\n')[0]}");
            return null;
        }
    }

    public async Task<SignInData?> ReadSignInsAsync(ScanContext ctx, CancellationToken ct)
    {
        if (!IsConfigured) return null;
        var o = options.Value;
        var days = Math.Max(1, o.LogAnalyticsLookbackDays);
        var armApps = string.Join(",", o.EffectiveControlPlaneAppIds.Select(a => $"'{a}'"));
        var adminApps = string.Join(",", o.EffectiveAdminPortalAppIds.Select(a => $"'{a}'"));
        var query = $$"""
            union isfuzzy=true SigninLogs, AADNonInteractiveUserSignInLogs
            | where TimeGenerated > ago({{days}}d)
            | where tostring(ResultType) == "0"
            | extend IsArm = AppId in~ ({{armApps}}) or ResourceIdentity =~ "{{o.AzureControlPlaneResourceId}}"
            | extend IsAdmin = AppId in~ ({{adminApps}})
            | where IsArm or IsAdmin
            | summarize LastArm = maxif(TimeGenerated, IsArm), LastAdmin = maxif(TimeGenerated, IsAdmin),
                        ArmApp = take_anyif(AppDisplayName, IsArm), AdminApp = take_anyif(AppDisplayName, IsAdmin) by UserId
            """;
        try
        {
            var rows = await QueryAsync(query, days, ct);
            var data = new SignInData { Source = $"Log Analytics SigninLogs, {days} days" };
            foreach (var row in rows)
            {
                var user = row.GetString("UserId");
                if (string.IsNullOrEmpty(user)) continue;
                if (row.GetDateTimeOffset("LastArm") is { } arm)
                {
                    SignInData.Merge(data.AzureControlPlane, Ids.ObjectId(user), new SignInValue(arm.UtcDateTime, row.GetString("ArmApp") ?? ""));
                }
                if (row.GetDateTimeOffset("LastAdmin") is { } admin)
                {
                    SignInData.Merge(data.AdminPortals, Ids.ObjectId(user), new SignInValue(admin.UtcDateTime, row.GetString("AdminApp") ?? ""));
                }
            }
            ctx.Info($"Log Analytics sign-ins: {data.AzureControlPlane.Count} users with Azure sign-ins, {data.AdminPortals.Count} with admin portal sign-ins ({days} days).");
            return rows.Count == 0 ? null : data;
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException)
        {
            ctx.Warn($"Log Analytics sign-in query failed, using Graph sign-in logs instead: {ex.Message.Split('\n')[0]}");
            return null;
        }
    }

    private async Task<IReadOnlyList<LogsTableRow>> QueryAsync(string query, int days, CancellationToken ct)
    {
        var client = new LogsQueryClient(clients.Credential);
        var response = await client.QueryWorkspaceAsync(options.Value.LogAnalyticsWorkspaceId, query,
            new QueryTimeRange(TimeSpan.FromDays(days)), cancellationToken: ct);
        return response.Value.Table.Rows;
    }
}
