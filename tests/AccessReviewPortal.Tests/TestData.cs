using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;

namespace AccessReviewPortal.Tests;

internal static class TestData
{
    public const string SubscriptionId = "11111111-2222-3333-4444-555555555555";
    public const string ContributorId = "/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c";
    public static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static long _nextId = 1;

    public static RoleAssignmentSnapshot Row(
        string principalId = "11111111-1111-1111-1111-111111111111",
        ScopeLevel level = ScopeLevel.ResourceGroup,
        string? scope = null,
        AssignmentType type = AssignmentType.Permanent,
        string principalType = "User",
        bool write = true,
        string? viaGroup = null,
        bool viaGroupOnPrem = false) => new()
    {
        Id = _nextId++,
        AssignmentKey = Guid.NewGuid().ToString("N"),
        SourceAssignmentId = $"/subscriptions/{SubscriptionId}/providers/Microsoft.Authorization/roleAssignments/{Guid.NewGuid()}",
        PrincipalId = principalId,
        PrincipalType = principalType,
        PrincipalDisplayName = "Test " + principalId[..4],
        PrincipalUpn = $"test{principalId[..4]}@contoso.com",
        AssignedViaGroupId = viaGroup,
        AssignedViaGroupName = viaGroup is null ? null : "Group " + viaGroup[..4],
        AssignedViaGroupOnPremises = viaGroupOnPrem,
        ScopeLevel = level,
        ScopeId = scope ?? (level == ScopeLevel.EntraRole ? "/" : $"/subscriptions/{SubscriptionId}/resourceGroups/rg-test"),
        ScopeDisplayName = "rg-test",
        SubscriptionId = level is ScopeLevel.ManagementGroup or ScopeLevel.EntraRole ? null : SubscriptionId,
        SubscriptionName = "Contoso-Sandbox",
        RoleDefinitionId = ContributorId,
        RoleName = "Contributor",
        RoleHasWritePermissions = write,
        AssignmentType = type
    };

    public static UserStateSnapshot User(string id, string? department = "IT", string? managerId = "m1", bool enabled = true, bool exists = true) => new()
    {
        UserId = id,
        Upn = $"{id[..4]}@contoso.com",
        DisplayName = "User " + id[..4],
        AccountEnabled = enabled,
        ExistsInDirectory = exists,
        Department = department,
        ManagerId = managerId,
        ManagerDisplayName = managerId is null ? null : "Manager " + managerId
    };
}
