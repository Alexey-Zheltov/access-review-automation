using System.Text.Json;
using System.Text.RegularExpressions;
using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using global::Azure;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Services.Scanning;

/// <summary>One unhealthy Defender for Cloud CIEM assessment for one identity in one subscription.</summary>
public sealed record DefenderFinding(
    DefenderCspmStatus Status,
    string SubscriptionId,
    string PrincipalId,
    string Title,
    string Details,
    string? RoleKey = null,
    string? Scope = null);

/// <summary>
/// Enriches assignments with Microsoft Defender for Cloud CSPM recommendations (Permissions Management / CIEM),
/// read with Azure Resource Graph from the "securityresources" table:
/// - Azure overprovisioned identities should have only the necessary permissions;
/// - Permissions of inactive identities in your Azure subscription should be revoked (no action for 45 days);
/// - Privileged roles should not have permanent access at the subscription and resource group level;
/// - Guest / blocked / deprecated accounts with owner, write or read permissions should be removed.
/// Needs Defender CSPM with the "Permissions management (CIEM)" extension. The scanner only needs Reader.
/// </summary>
public sealed partial class DefenderCspmCollector(AzureClients clients, ArmRestClient arm, IOptions<DefenderCspmOptions> options)
{
    public const string OverprovisionedKey = "d19d5a12-41e9-44e2-b7f5-ee2160f62d62";
    public const string InactiveIdentitiesKey = "8b0bd683-bcfe-4ab1-96b9-f15a60eaa89d";
    public const string PermanentPrivilegedKey = "706b33f0-129e-4ed0-a179-f450b9ee4145";

    private static readonly string[] CiemExtensionNames = ["EntraPermissionsManagement", "Ciem"];

    private static readonly string[] KnownKeys =
    [
        OverprovisionedKey, InactiveIdentitiesKey, PermanentPrivilegedKey,
        "0e59bbca-7472-4853-a5c8-a492d29574a7", "c9a0ec93-3531-4c69-a927-31ea92023812",
        "fde1c0c9-0fd2-4ecc-87b5-98956cbc1095", "0354476c-a12a-4fcc-a79d-f0ab7ffffdbb", "20606e75-05c4-48c0-9d97-add6daa2109a",
        "422107c6-5b9a-46a6-bb1d-26ef1cc52d65", "009678ce-adce-4c94-9cc8-cfc2bd0c6a06", "f2864482-b329-4310-8c06-3cf74fe880c5"
    ];

    private static readonly string AssessmentQuery = $$"""
        securityresources
        | where type == "microsoft.security/assessments"
        | where tostring(properties.status.code) == "Unhealthy"
        | extend key = name, dn = tostring(properties.displayName)
        | where key in~ ({{string.Join(", ", KnownKeys.Select(k => $"'{k}'"))}})
            or dn contains "overprovisioned identities" or dn contains "inactive identities" or dn contains "should not have permanent access"
            or dn contains "inactive role assignment" or dn contains "Guest accounts with" or dn contains "Blocked accounts with" or dn contains "Deprecated accounts"
        | where dn !contains "GCP" and dn !contains "AWS"
        | extend ad = properties.additionalData
        | project id, subscriptionId, key, dn,
            entityId = tostring(properties.resourceDetails.Id),
            identityType = tostring(ad.ResourceType), identityName = tostring(ad.ResourceName),
            roleAndScope = tostring(ad.RoleAndScope), unusedCount = tostring(ad.unusedCount), usedCount = tostring(ad.usedCount),
            unusedSample = tostring(array_slice(parse_json(tostring(ad.unused)), 0, 5))
        """;

    public async Task CollectAsync(ScanContext ctx, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            ctx.Info("Defender CSPM: disabled in configuration (DefenderCspm__Enabled=false).");
            return;
        }

        var subscriptions = ctx.Subscriptions.Select(s => s.SubscriptionId).ToList();
        if (subscriptions.Count == 0) return;

        var ciemEnabled = 0;
        foreach (var sub in subscriptions)
        {
            if (await IsCiemEnabledAsync(arm, sub, ct) is true) ciemEnabled++;
        }

        List<JsonElement> rows;
        try
        {
            rows = await ResourceGraph.QueryAsync(clients, AssessmentQuery, subscriptions, ct);
        }
        catch (RequestFailedException ex)
        {
            ctx.Warn($"Defender CSPM: Azure Resource Graph query failed: {ex.Message.Split('\n')[0]}");
            return;
        }

        var knownPrincipals = ctx.Assignments.Select(a => a.PrincipalId)
            .Concat(ctx.Lines.Select(l => l.Row.PrincipalId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var findings = new List<DefenderFinding>();
        var subscriptionLevel = new List<(JsonElement Row, DefenderCspmStatus Status)>();
        foreach (var row in rows)
        {
            var status = Classify(row.Str("key") ?? "", row.Str("dn") ?? "");
            if (status == DefenderCspmStatus.None) continue;
            var principal = PrincipalFromEntityId(row.Str("entityId"));
            if (principal is null)
            {
                subscriptionLevel.Add((row, status));
                continue;
            }
            findings.AddRange(ToFindings(row, status, principal, SubscriptionName(ctx, row.Str("subscriptionId"))));
        }

        // Older guest/blocked recommendations are evaluated on the subscription and list the accounts as sub-assessments.
        foreach (var (row, status) in subscriptionLevel)
        {
            findings.AddRange(await SubAssessmentFindingsAsync(row, status, knownPrincipals, ct));
        }

        var lines = ctx.Lines.Select(l => l.Row).ToList();
        var matched = Apply(lines, findings, (mgScope, sub) => ManagementGroupContains(ctx, mgScope, sub));

        ctx.UsageSources.Add($"Defender CSPM (CIEM): Azure Resource Graph, {findings.Count} findings");
        ctx.Info($"Defender CSPM: CIEM enabled in {ciemEnabled} of {subscriptions.Count} subscription(s); {rows.Count} unhealthy identity assessments, " +
                 $"{findings.Count} findings, {matched} review rows flagged " +
                 $"({string.Join(", ", findings.GroupBy(f => f.Status).Select(g => $"{DisplayText.Of(g.Key)} {g.Count()}"))}).");
        if (ciemEnabled < subscriptions.Count)
        {
            ctx.Warn($"Defender CSPM: the CIEM extension (Permissions management) is not enabled in {subscriptions.Count - ciemEnabled} subscription(s). " +
                     "Overprovisioned / inactive identity findings are missing there.");
        }
    }

    /// <summary>Maps the assessment to the Defender CSPM status shown in the portal.</summary>
    public static DefenderCspmStatus Classify(string key, string displayName)
    {
        var dn = displayName.ToLowerInvariant();
        if (key.Equals(OverprovisionedKey, StringComparison.OrdinalIgnoreCase) || dn.Contains("overprovisioned")) return DefenderCspmStatus.Overprovisioned;
        if (key.Equals(InactiveIdentitiesKey, StringComparison.OrdinalIgnoreCase) || dn.Contains("inactive identities") || dn.Contains("inactive role assignment"))
            return DefenderCspmStatus.Inactive45d;
        if (key.Equals(PermanentPrivilegedKey, StringComparison.OrdinalIgnoreCase) || dn.Contains("permanent access")) return DefenderCspmStatus.PermanentPrivileged;
        if (dn.Contains("guest accounts") || dn.Contains("blocked accounts") || dn.Contains("deprecated accounts")) return DefenderCspmStatus.BlockedOrGuest;
        return DefenderCspmStatus.None;
    }

    /// <summary>CIEM assessments point to ".../pricings/CloudPosture/securityentitydata/{objectId}".</summary>
    public static string? PrincipalFromEntityId(string? entityId)
    {
        if (string.IsNullOrEmpty(entityId)) return null;
        var marker = "/securityentitydata/";
        var i = entityId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var id = entityId[(i + marker.Length)..].Split('/')[0];
        return Guid.TryParse(id, out var g) ? g.ToString() : null;
    }

    /// <summary>Builds findings from one assessment row (permanent privileged findings carry role and scope).</summary>
    public static IEnumerable<DefenderFinding> ToFindings(JsonElement row, DefenderCspmStatus status, string principalId, string subscriptionName)
    {
        var sub = row.Str("subscriptionId") ?? "";
        var title = row.Str("dn") ?? status.ToString();
        switch (status)
        {
            case DefenderCspmStatus.PermanentPrivileged:
                var any = false;
                foreach (var rs in ParseRoleAndScope(row.Str("roleAndScope")))
                {
                    any = true;
                    yield return new DefenderFinding(status, sub, principalId, title,
                        $"Permanent privileged role {rs.RoleName} on {rs.ScopeName}: move it to PIM (Defender CSPM).",
                        Ids.RoleKeyFromArmId(rs.RoleDefinitionId), rs.Scope);
                }
                if (!any)
                {
                    yield return new DefenderFinding(status, sub, principalId, title, $"Permanent privileged role in {subscriptionName} (Defender CSPM).");
                }
                break;

            case DefenderCspmStatus.Overprovisioned:
                var unused = ParseInt(row.Str("unusedCount"));
                var used = ParseInt(row.Str("usedCount"));
                var sample = ParseSample(row.Str("unusedSample"));
                var counts = unused is null ? "" : $" {unused} of {unused + (used ?? 0)} granted permissions were not used.";
                yield return new DefenderFinding(status, sub, principalId, title,
                    $"Overprovisioned identity in {subscriptionName}.{counts}" + (sample.Length > 0 ? $" Unused, for example: {string.Join(", ", sample)}." : "") +
                    " (Defender CSPM: right-size the role)");
                break;

            case DefenderCspmStatus.Inactive45d:
                yield return new DefenderFinding(status, sub, principalId, title,
                    $"Inactive identity: no action in {subscriptionName} for 45 days (Defender CSPM).");
                break;

            default:
                yield return new DefenderFinding(status, sub, principalId, title, $"{title} (Defender CSPM).");
                break;
        }
    }

    /// <summary>Writes the findings to the review rows. Returns the number of rows with at least one finding.</summary>
    public static int Apply(IReadOnlyList<RoleAssignmentSnapshot> rows, IReadOnlyList<DefenderFinding> findings, Func<string, string, bool> managementGroupContains)
    {
        var byPrincipal = findings.GroupBy(f => f.PrincipalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var flagged = 0;

        foreach (var row in rows)
        {
            row.DefenderCspmStatus = DefenderCspmStatus.None;
            row.DefenderCspmFindings = null;
            row.DefenderCspmFindingCount = 0;
            row.DefenderCspmDetails = null;
            if (row.ScopeLevel == ScopeLevel.EntraRole || !byPrincipal.TryGetValue(row.PrincipalId, out var candidates)) continue;

            var matches = candidates.Where(f => Matches(row, f, managementGroupContains)).ToList();
            if (matches.Count == 0) continue;

            flagged++;
            var statuses = matches.Select(m => m.Status).Distinct().ToList();
            row.DefenderCspmStatus = statuses.OrderBy(Priority).First();
            row.DefenderCspmFindings = string.Join(",", statuses.OrderBy(Priority));
            row.DefenderCspmFindingCount = matches.Count;
            row.DefenderCspmDetails = string.Join(" ", matches.Select(m => m.Details).Distinct());
        }
        return flagged;
    }

    private static bool Matches(RoleAssignmentSnapshot row, DefenderFinding f, Func<string, string, bool> managementGroupContains)
    {
        if (f.RoleKey is not null && f.Scope is not null)
        {
            return Ids.RoleKeyFromArmId(row.RoleDefinitionId) == f.RoleKey && Ids.NormalizeScope(row.ScopeId) == Ids.NormalizeScope(f.Scope);
        }

        // Identity-level finding: every Azure assignment of the identity that gives access inside the subscription.
        if (row.ScopeId == "/") return true;
        if (row.ScopeLevel == ScopeLevel.ManagementGroup) return managementGroupContains(row.ScopeId, f.SubscriptionId);
        return string.Equals(row.SubscriptionId, f.SubscriptionId, StringComparison.OrdinalIgnoreCase);
    }

    private static int Priority(DefenderCspmStatus s) => s switch
    {
        DefenderCspmStatus.BlockedOrGuest => 0,
        DefenderCspmStatus.Inactive45d => 1,
        DefenderCspmStatus.PermanentPrivileged => 2,
        DefenderCspmStatus.Overprovisioned => 3,
        _ => 9
    };

    public sealed record RoleScope(string RoleDefinitionId, string RoleName, string Scope, string ScopeName);

    public static List<RoleScope> ParseRoleAndScope(string? json)
    {
        var result = new List<RoleScope>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var role = e.Str("RoleDefinitionId");
                var scope = e.Str("AssignedScope");
                if (role is null || scope is null) continue;
                result.Add(new RoleScope(role, e.Str("RoleName") ?? role, scope, e.Str("AssignedScopeResourceName") ?? scope));
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    private static string[] ParseSample(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(e => e.Str("externalId") ?? "").Where(s => s.Length > 0).ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int? ParseInt(string? s) => int.TryParse(s, out var v) ? v : null;

    private async Task<IEnumerable<DefenderFinding>> SubAssessmentFindingsAsync(JsonElement row, DefenderCspmStatus status, HashSet<string> knownPrincipals, CancellationToken ct)
    {
        var assessmentId = row.Str("id");
        var sub = row.Str("subscriptionId");
        if (assessmentId is null || sub is null) return [];

        var query = $$"""
            securityresources
            | where type == "microsoft.security/assessments/subassessments"
            | where id startswith "{{assessmentId.ToLowerInvariant()}}/" or tostring(properties.id) startswith "{{assessmentId.ToLowerInvariant()}}"
            | project id, subscriptionId, props = tostring(properties)
            """;
        try
        {
            var subRows = await ResourceGraph.QueryAsync(clients, query, [sub], ct);
            var title = row.Str("dn") ?? status.ToString();
            return subRows
                .SelectMany(r => GuidRegex().Matches(r.Str("props") ?? "").Select(m => m.Value.ToLowerInvariant()))
                .Where(knownPrincipals.Contains)
                .Distinct()
                .Select(p => new DefenderFinding(status, sub, p, title, $"{title} (Defender CSPM)."))
                .ToList();
        }
        catch (RequestFailedException)
        {
            return [];
        }
    }

    /// <summary>
    /// True when Defender CSPM (CloudPosture, Standard) with the Permissions management (CIEM) extension is on.
    /// The extension is reported as "EntraPermissionsManagement" (current API) or "Ciem", depending on the tenant.
    /// </summary>
    public static async Task<bool?> IsCiemEnabledAsync(ArmRestClient arm, string subscriptionId, CancellationToken ct)
    {
        try
        {
            var pricing = await arm.GetJsonAsync($"/subscriptions/{subscriptionId}/providers/Microsoft.Security/pricings/CloudPosture?api-version=2024-01-01", ct);
            if (!string.Equals(pricing.Str("properties", "pricingTier"), "Standard", StringComparison.OrdinalIgnoreCase)) return false;
            return pricing.Obj("properties", "extensions") is { ValueKind: JsonValueKind.Array } ext
                   && ext.EnumerateArray().Any(e => CiemExtensionNames.Contains(e.Str("name") ?? "", StringComparer.OrdinalIgnoreCase)
                                                    && string.Equals(e.Str("isEnabled"), "True", StringComparison.OrdinalIgnoreCase));
        }
        catch (ArmRequestException)
        {
            return null;
        }
    }

    public static async Task<bool?> IsDefenderCspmEnabledAsync(ArmRestClient arm, string subscriptionId, CancellationToken ct)
    {
        try
        {
            var pricing = await arm.GetJsonAsync($"/subscriptions/{subscriptionId}/providers/Microsoft.Security/pricings/CloudPosture?api-version=2024-01-01", ct);
            return string.Equals(pricing.Str("properties", "pricingTier"), "Standard", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArmRequestException)
        {
            return null;
        }
    }

    private static string SubscriptionName(ScanContext ctx, string? subscriptionId) =>
        ctx.Subscriptions.FirstOrDefault(s => s.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? subscriptionId ?? "";

    private static bool ManagementGroupContains(ScanContext ctx, string mgScope, string subscriptionId)
    {
        if (mgScope == "/") return true;
        var mg = ctx.ManagementGroups.FirstOrDefault(m => string.Equals(m.Id, mgScope, StringComparison.OrdinalIgnoreCase));
        return mg is not null && mg.DescendantSubscriptionIds.Contains(subscriptionId);
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex();
}
