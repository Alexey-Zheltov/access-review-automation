using System.Text.Json;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Services.Scanning;

namespace AccessReviewPortal.Tests;

public class DefenderCspmCollectorTests
{
    private const string Principal = "5c7f2a1e-8a43-4d1b-9a3e-0f6c5e7d8b90";

    [Theory]
    [InlineData(DefenderCspmCollector.OverprovisionedKey, "x", DefenderCspmStatus.Overprovisioned)]
    [InlineData(DefenderCspmCollector.InactiveIdentitiesKey, "x", DefenderCspmStatus.Inactive45d)]
    [InlineData(DefenderCspmCollector.PermanentPrivilegedKey, "x", DefenderCspmStatus.PermanentPrivileged)]
    [InlineData("other", "Azure overprovisioned identities should have only the necessary permissions", DefenderCspmStatus.Overprovisioned)]
    [InlineData("other", "Permissions of inactive identities in your Azure subscription should be revoked", DefenderCspmStatus.Inactive45d)]
    [InlineData("other", "Privileged roles should not have permanent access at the subscription and resource group level", DefenderCspmStatus.PermanentPrivileged)]
    [InlineData("other", "Guest accounts with owner permissions on Azure resources should be removed", DefenderCspmStatus.BlockedOrGuest)]
    [InlineData("other", "Blocked accounts with read and write permissions on Azure resources should be removed", DefenderCspmStatus.BlockedOrGuest)]
    [InlineData("other", "Storage accounts should restrict network access", DefenderCspmStatus.None)]
    public void Classify_maps_assessments(string key, string displayName, DefenderCspmStatus expected) =>
        Assert.Equal(expected, DefenderCspmCollector.Classify(key, displayName));

    [Fact]
    public void Principal_is_read_from_the_security_entity_id()
    {
        var id = $"/subscriptions/{TestData.SubscriptionId}/providers/Microsoft.Security/pricings/CloudPosture/securityentitydata/{Principal.ToUpperInvariant()}";
        Assert.Equal(Principal, DefenderCspmCollector.PrincipalFromEntityId(id));
        Assert.Null(DefenderCspmCollector.PrincipalFromEntityId($"/subscriptions/{TestData.SubscriptionId}/resourceGroups/rg"));
        Assert.Null(DefenderCspmCollector.PrincipalFromEntityId(null));
    }

    [Fact]
    public void RoleAndScope_is_parsed()
    {
        var json = """
            [{"RoleDefinitionId":"/subscriptions/11111111-2222-3333-4444-555555555555/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c",
              "RoleName":"Contributor","AssignedScope":"/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-accessreview-poc",
              "AssignedScopeResourceName":"rg-accessreview-poc"}]
            """;
        var result = DefenderCspmCollector.ParseRoleAndScope(json);

        var rs = Assert.Single(result);
        Assert.Equal("Contributor", rs.RoleName);
        Assert.Equal("rg-accessreview-poc", rs.ScopeName);
        Assert.Empty(DefenderCspmCollector.ParseRoleAndScope("not json"));
        Assert.Empty(DefenderCspmCollector.ParseRoleAndScope(null));
    }

    [Fact]
    public void Overprovisioned_finding_contains_counts_and_sample()
    {
        using var doc = JsonDocument.Parse($$"""
            {"subscriptionId":"{{TestData.SubscriptionId}}","dn":"Azure overprovisioned identities should have only the necessary permissions",
             "unusedCount":"120","usedCount":"3",
             "unusedSample":"[{\"externalId\":\"microsoft.compute/virtualmachines/delete\",\"usedDateTime\":null}]"}
            """);
        var finding = Assert.Single(DefenderCspmCollector.ToFindings(doc.RootElement, DefenderCspmStatus.Overprovisioned, Principal, "Contoso-Sandbox"));

        Assert.Contains("120 of 123", finding.Details);
        Assert.Contains("microsoft.compute/virtualmachines/delete", finding.Details);
        Assert.Null(finding.RoleKey);
    }

    [Fact]
    public void Permanent_privileged_finding_matches_only_its_role_and_scope()
    {
        var rgScope = $"/subscriptions/{TestData.SubscriptionId}/resourceGroups/rg-accessreview-poc";
        var match = TestData.Row(Principal, scope: rgScope);
        var otherScope = TestData.Row(Principal, scope: $"/subscriptions/{TestData.SubscriptionId}/resourceGroups/rg-other");
        var finding = new DefenderFinding(DefenderCspmStatus.PermanentPrivileged, TestData.SubscriptionId, Principal, "t", "Permanent Contributor",
            "b24988ac618042a0ab8820f7382dd24c", rgScope.ToUpperInvariant());

        var flagged = DefenderCspmCollector.Apply([match, otherScope], [finding], (_, _) => false);

        Assert.Equal(1, flagged);
        Assert.Equal(DefenderCspmStatus.PermanentPrivileged, match.DefenderCspmStatus);
        Assert.Equal(DefenderCspmStatus.None, otherScope.DefenderCspmStatus);
    }

    [Fact]
    public void Identity_findings_use_priority_and_keep_all_statuses()
    {
        var row = TestData.Row(Principal);
        var findings = new[]
        {
            new DefenderFinding(DefenderCspmStatus.Overprovisioned, TestData.SubscriptionId, Principal, "t", "over"),
            new DefenderFinding(DefenderCspmStatus.Inactive45d, TestData.SubscriptionId, Principal, "t", "inactive")
        };

        DefenderCspmCollector.Apply([row], findings, (_, _) => false);

        Assert.Equal(DefenderCspmStatus.Inactive45d, row.DefenderCspmStatus);
        Assert.Equal("Inactive45d,Overprovisioned", row.DefenderCspmFindings);
        Assert.Equal(2, row.DefenderCspmFindingCount);
    }

    [Fact]
    public void Management_group_rows_use_the_containment_callback_and_entra_rows_are_skipped()
    {
        var mgRow = TestData.Row(Principal, ScopeLevel.ManagementGroup, "/providers/Microsoft.Management/managementGroups/mg-1");
        var entraRow = TestData.Row(Principal, ScopeLevel.EntraRole);
        var finding = new DefenderFinding(DefenderCspmStatus.Inactive45d, TestData.SubscriptionId, Principal, "t", "inactive");

        DefenderCspmCollector.Apply([mgRow, entraRow], [finding], (mg, sub) => mg.EndsWith("mg-1") && sub == TestData.SubscriptionId);

        Assert.Equal(DefenderCspmStatus.Inactive45d, mgRow.DefenderCspmStatus);
        Assert.Equal(DefenderCspmStatus.None, entraRow.DefenderCspmStatus);
    }

    [Fact]
    public void Apply_clears_old_findings()
    {
        var row = TestData.Row(Principal);
        row.DefenderCspmStatus = DefenderCspmStatus.Overprovisioned;
        row.DefenderCspmFindings = "Overprovisioned";
        row.DefenderCspmFindingCount = 1;

        DefenderCspmCollector.Apply([row], [], (_, _) => false);

        Assert.Equal(DefenderCspmStatus.None, row.DefenderCspmStatus);
        Assert.Null(row.DefenderCspmFindings);
        Assert.Equal(0, row.DefenderCspmFindingCount);
    }
}
