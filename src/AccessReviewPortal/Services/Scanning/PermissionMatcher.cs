using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AccessReviewPortal.Services.Scanning;

/// <summary>Evaluates Azure RBAC action patterns (with * wildcards) and classifies roles as read-only or write-capable.</summary>
public static class PermissionMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool Matches(string pattern, string action)
    {
        if (pattern == "*") return true;
        var regex = Cache.GetOrAdd(pattern, p =>
            new Regex("^" + Regex.Escape(p).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
        return regex.IsMatch(action);
    }

    /// <summary>True if the role grants the control plane action (Actions minus NotActions in any permission block).</summary>
    public static bool AllowsAction(RoleDefinitionInfo? role, string action)
    {
        if (role is null) return false;
        foreach (var block in role.Permissions)
        {
            if (block.Actions.Any(a => Matches(a, action)) && !block.NotActions.Any(n => Matches(n, action)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Azure role can change something (not only "*/read"). Support tickets and log queries are ignored.</summary>
    public static bool AzureRoleHasWrite(IEnumerable<PermissionBlock> blocks) =>
        blocks.Any(b => b.Actions.Concat(b.DataActions).Any(a => !IsReadOnlyAction(a)));

    /// <summary>Entra role can change something (any resource action that is not a read).</summary>
    public static bool EntraRoleHasWrite(IEnumerable<string> allowedResourceActions) =>
        allowedResourceActions.Any(a =>
            !a.EndsWith("/read", StringComparison.OrdinalIgnoreCase)
            && !a.Contains("supportTickets", StringComparison.OrdinalIgnoreCase)
            && !a.Contains("serviceHealth", StringComparison.OrdinalIgnoreCase)
            && !a.Contains("webPortal", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Azure actions that only read, although they do not end with "/read": log queries (Monitoring Reader,
    /// Log Analytics Reader) and support tickets. Used for the role classification and the zero-privilege checks.
    /// </summary>
    private static readonly string[] ReadOnlyActionPatterns =
    [
        "Microsoft.Support/*",
        "Microsoft.OperationalInsights/workspaces/search/action",
        "Microsoft.OperationalInsights/workspaces/analytics/query/action",
        "Microsoft.OperationalInsights/workspaces/query/*/read"
    ];

    public static bool IsReadOnlyAction(string action) =>
        action.EndsWith("/read", StringComparison.OrdinalIgnoreCase)
        || ReadOnlyActionPatterns.Any(p => Matches(p, action));
}
