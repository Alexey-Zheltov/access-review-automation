using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Models;

namespace AccessReviewPortal.Services.Scanning;

/// <summary>
/// Reads decisions of native access reviews: Microsoft Entra ID Governance (Graph) for Entra roles and groups,
/// and PIM for Azure resources (ARM Microsoft.Authorization/accessReviewScheduleDefinitions).
/// This source is optional: errors and throttling only produce warnings, and the work stops after a time budget.
/// </summary>
public sealed partial class AccessReviewCollector(AzureClients clients, ArmRestClient rest, IOptions<ReviewSettingsOptions> options)
{
    private const string ArmReviewApi = "2021-12-01-preview";
    private const int Parallelism = 4;

    private static readonly HashSet<string> FinishedStatuses = new(StringComparer.OrdinalIgnoreCase) { "Completed", "Applied", "AutoReviewed" };

    public async Task CollectAsync(ScanContext ctx, CancellationToken ct)
    {
        var o = options.Value;
        var cutoff = ctx.NowUtc.AddDays(-Math.Max(1, o.AccessReviewLookbackDays));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, o.AccessReviewTimeBudgetMinutes)));
        try
        {
            await CollectEntraAsync(ctx, cutoff, budget.Token);
            await CollectArmAsync(ctx, cutoff, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Warn($"Access review collection stopped after {o.AccessReviewTimeBudgetMinutes} minutes (time budget). " +
                     $"{ctx.NativeDecisions.Count} decisions read so far are used.");
        }
    }

    private async Task CollectEntraAsync(ScanContext ctx, DateTime cutoff, CancellationToken ct)
    {
        var graph = clients.Graph;
        List<AccessReviewScheduleDefinition> definitions;
        try
        {
            var page = await graph.IdentityGovernance.AccessReviews.Definitions.GetAsync(r =>
            {
                r.QueryParameters.Top = 100;
                r.QueryParameters.Select = ["id", "displayName", "status", "scope", "instanceEnumerationScope", "lastModifiedDateTime"];
            }, ct);
            definitions = await graph.ReadAllAsync<AccessReviewScheduleDefinition, AccessReviewScheduleDefinitionCollectionResponse>(page, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ctx.Warn($"Entra ID Access Reviews are not readable ({ex.ShortMessage()}). Needs AccessReview.Read.All.");
            return;
        }

        // Only reviews of roles and groups that appear in the collected assignments can be matched to a row.
        var entraRoleKeys = ctx.Assignments.Where(a => a.IsEntra).Select(a => Ids.GuidKey(a.RoleKey)).ToHashSet(StringComparer.Ordinal);
        var groupIds = ctx.Assignments.Where(a => a.PrincipalType == "Group").Select(a => Ids.ObjectId(a.PrincipalId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = definitions
            .Where(d => d.Id is not null && !string.Equals(d.Status, "NotStarted", StringComparison.OrdinalIgnoreCase))
            .Where(d => !(FinishedStatuses.Contains(d.Status ?? "") && d.LastModifiedDateTime?.UtcDateTime < cutoff))
            .Select(d =>
            {
                var queries = Queries(d.Scope).Concat(Queries(d.InstanceEnumerationScope)).ToList();
                var roleId = queries.Select(q => RoleIdRegex().Match(q)).FirstOrDefault(m => m.Success)?.Groups["id"].Value;
                var groupId = queries.Select(q => GroupIdRegex().Match(q)).FirstOrDefault(m => m.Success)?.Groups["id"].Value;
                return (Definition: d, RoleId: roleId, GroupId: groupId);
            })
            .ToList();
        var relevant = candidates
            .Where(c => c.RoleId is not null ? entraRoleKeys.Contains(Ids.GuidKey(c.RoleId))
                : c.GroupId is null || groupIds.Contains(Ids.ObjectId(c.GroupId)))
            .ToList();

        var found = new ConcurrentBag<NativeDecisionInfo>();
        var failed = 0;
        string? lastError = null;

        await Parallel.ForEachAsync(relevant, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (item, token) =>
        {
            var (def, roleId, groupId) = item;
            try
            {
                var instPage = await graph.IdentityGovernance.AccessReviews.Definitions[def.Id].Instances.GetAsync(cancellationToken: token);
                var instances = await graph.ReadAllAsync<AccessReviewInstance, AccessReviewInstanceCollectionResponse>(instPage, token);
                foreach (var inst in instances.Where(i => i.Id is not null && (i.EndDateTime is null || i.EndDateTime.Value.UtcDateTime >= cutoff)))
                {
                    var decPage = await graph.IdentityGovernance.AccessReviews.Definitions[def.Id].Instances[inst.Id].Decisions
                        .GetAsync(r => r.QueryParameters.Top = 100, token);
                    var items = await graph.ReadAllAsync<AccessReviewInstanceDecisionItem, AccessReviewInstanceDecisionItemCollectionResponse>(decPage, token);
                    foreach (var d in items.Where(d => d.Principal?.Id is not null))
                    {
                        var resourceType = d.Resource?.Type ?? "";
                        found.Add(new NativeDecisionInfo
                        {
                            Source = "Entra ID Access Review",
                            ReviewName = def.DisplayName ?? def.Id!,
                            PrincipalId = Ids.ObjectId(d.Principal!.Id),
                            ResourceId = d.Resource?.Id,
                            RoleKey = roleId ?? (resourceType.Contains("role", StringComparison.OrdinalIgnoreCase) ? d.Resource?.Id : null),
                            GroupId = groupId ?? (resourceType.Contains("group", StringComparison.OrdinalIgnoreCase) ? d.Resource?.Id : null),
                            ScopeId = "/",
                            Decision = MapDecision(d.Decision),
                            RawDecision = d.Decision,
                            ReviewedUtc = d.ReviewedDateTime?.UtcDateTime,
                            ReviewedBy = d.ReviewedBy?.UserPrincipalName ?? d.ReviewedBy?.DisplayName,
                            Justification = d.Justification,
                            InstanceEndUtc = inst.EndDateTime?.UtcDateTime
                        });
                    }
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Interlocked.Increment(ref failed);
                lastError = ex.ShortMessage();
            }
        });

        ctx.NativeDecisions.AddRange(found);
        ctx.Info($"Entra ID Access Reviews: {definitions.Count} definitions, {candidates.Count} in the {options.Value.AccessReviewLookbackDays}-day window, " +
                 $"{relevant.Count} for roles or groups in this scan, {found.Count} decisions read.");
        if (failed > 0)
        {
            ctx.Warn($"{failed} Entra ID access review definition(s) could not be read. Last error: {lastError}");
        }
    }

    private async Task CollectArmAsync(ScanContext ctx, DateTime cutoff, CancellationToken ct)
    {
        var scopes = ctx.Subscriptions.Select(s => "/subscriptions/" + s.SubscriptionId)
            .Concat(ctx.ManagementGroups.Select(m => m.Id));
        var definitions = new List<(string Scope, JsonElement Definition)>();

        foreach (var scope in scopes)
        {
            try
            {
                foreach (var def in await rest.GetAllAsync($"{scope}/providers/Microsoft.Authorization/accessReviewScheduleDefinitions?api-version={ArmReviewApi}&$top=100", ct))
                {
                    definitions.Add((scope, def));
                }
            }
            catch (ArmRequestException ex)
            {
                if (ex.Status is not System.Net.HttpStatusCode.NotFound)
                {
                    ctx.Warn($"PIM access reviews at {scope} are not readable: {ex.Message}");
                }
            }
        }

        // PIM creates one definition per role ("all roles" reviews give hundreds). Only roles that are assigned matter.
        var azureRoleKeys = ctx.Assignments.Where(a => !a.IsEntra).Select(a => a.RoleKey).ToHashSet(StringComparer.Ordinal);
        var inWindow = definitions.Where(x =>
        {
            var status = x.Definition.Str("properties", "status") ?? "";
            var modified = x.Definition.Date("properties", "lastModifiedDateTime");
            return !(FinishedStatuses.Contains(status) && modified < cutoff);
        }).ToList();
        var relevant = inWindow
            .Where(x => azureRoleKeys.Contains(Ids.RoleKeyFromArmId(x.Definition.Str("properties", "scope", "roleDefinitionId"))))
            .ToList();

        var found = new ConcurrentBag<NativeDecisionInfo>();
        int instancesTotal = 0, instancesFailed = 0;
        string? lastError = null;

        await Parallel.ForEachAsync(relevant, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (item, token) =>
        {
            var (scope, def) = item;
            var name = def.Str("name");
            var props = def.Obj("properties");
            if (name is null || props is null) return;

            var roleKey = Ids.RoleKeyFromArmId(props.Value.Str("scope", "roleDefinitionId"));
            var resourceScope = props.Value.Str("scope", "resourceId") ?? scope;
            var includeBelow = props.Value.Bool("scope", "includeAccessBelowResource") ?? false;
            var reviewName = props.Value.Str("displayName") ?? name;
            var basePath = $"{scope}/providers/Microsoft.Authorization/accessReviewScheduleDefinitions/{name}";

            List<JsonElement> instances;
            try
            {
                instances = await rest.GetAllAsync($"{basePath}/instances?api-version={ArmReviewApi}", token, 0);
            }
            catch (ArmRequestException ex)
            {
                lastError = ex.Message;
                return;
            }

            foreach (var inst in instances)
            {
                var end = inst.Date("properties", "endDateTime");
                if (end is not null && end < cutoff) continue;
                Interlocked.Increment(ref instancesTotal);

                List<JsonElement> decisions;
                try
                {
                    // The service returns HTTP 500 for some completed reviews; these are not retried.
                    decisions = await rest.GetAllAsync($"{basePath}/instances/{inst.Str("name")}/decisions?api-version={ArmReviewApi}", token, 0);
                }
                catch (ArmRequestException ex)
                {
                    Interlocked.Increment(ref instancesFailed);
                    lastError = ex.Message;
                    continue;
                }

                foreach (var d in decisions)
                {
                    var p = d.Obj("properties");
                    if (p is null) continue;
                    var principalId = p.Value.Str("principal", "id") ?? p.Value.Str("target", "principalId");
                    if (principalId is null) continue;
                    var decision = p.Value.Str("decision");
                    found.Add(new NativeDecisionInfo
                    {
                        Source = "PIM Azure resource Access Review",
                        ReviewName = reviewName,
                        PrincipalId = Ids.ObjectId(principalId),
                        ResourceId = p.Value.Str("resource", "id"),
                        RoleKey = roleKey.Length > 0 ? roleKey : Ids.RoleKeyFromArmId(p.Value.Str("resource", "id")),
                        ScopeId = resourceScope,
                        IncludeBelowScope = includeBelow,
                        Decision = MapDecision(decision),
                        RawDecision = decision,
                        ReviewedUtc = p.Value.Date("reviewedDateTime"),
                        ReviewedBy = p.Value.Str("reviewedBy", "userPrincipalName") ?? p.Value.Str("reviewedBy", "principalName"),
                        Justification = p.Value.Str("justification"),
                        InstanceEndUtc = end
                    });
                }
            }
        });

        ctx.NativeDecisions.AddRange(found);
        ctx.Info($"PIM Azure resource Access Reviews: {definitions.Count} definitions, {inWindow.Count} in the window, " +
                 $"{relevant.Count} for assigned roles, {instancesTotal} instances, {found.Count} decisions read.");
        if (instancesFailed > 0)
        {
            ctx.Warn($"Decisions of {instancesFailed} PIM Azure resource review instance(s) could not be read. Last error: {lastError}");
        }
    }

    private static NativeReviewDecision MapDecision(string? decision) => (decision ?? "").ToLowerInvariant() switch
    {
        "approve" => NativeReviewDecision.Approve,
        "deny" => NativeReviewDecision.Deny,
        "" => NativeReviewDecision.None,
        _ => NativeReviewDecision.NotReviewed
    };

    private static IEnumerable<string> Queries(AccessReviewScope? scope) => scope switch
    {
        AccessReviewQueryScope q => [q.Query ?? ""],
        PrincipalResourceMembershipsScope p => (p.PrincipalScopes ?? []).SelectMany(Queries).Concat((p.ResourceScopes ?? []).SelectMany(Queries)),
        _ => []
    };

    [GeneratedRegex(@"roleDefinitionId\s+eq\s+'(?<id>[0-9a-fA-F-]{36})'|roleDefinitions/(?<id>[0-9a-fA-F-]{36})", RegexOptions.IgnoreCase)]
    private static partial Regex RoleIdRegex();

    [GeneratedRegex(@"/groups/(?<id>[0-9a-fA-F-]{36})", RegexOptions.IgnoreCase)]
    private static partial Regex GroupIdRegex();
}
