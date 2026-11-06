using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;

namespace AccessReviewPortal.Services.Scanning;

public sealed class PermissionBlock
{
    public List<string> Actions { get; init; } = [];
    public List<string> NotActions { get; init; } = [];
    public List<string> DataActions { get; init; } = [];
    public List<string> NotDataActions { get; init; } = [];
}

public sealed class RoleDefinitionInfo
{
    /// <summary>ARM: role definition GUID without dashes. Entra: role definition id.</summary>
    public required string Key { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsBuiltIn { get; init; }
    public List<PermissionBlock> Permissions { get; init; } = [];
    public bool HasWritePermissions { get; init; }
}

public sealed record SubscriptionInfo(string SubscriptionId, string DisplayName, string State);

public sealed class ManagementGroupInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Id { get; init; }
    public HashSet<string> DescendantSubscriptionIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PrincipalInfo
{
    public required string Id { get; init; }
    public string Type { get; set; } = "Unknown";
    public string? DisplayName { get; set; }
    public string? Upn { get; set; }
    public string? AppId { get; set; }
    public bool Exists { get; set; } = true;
    public bool OnPremisesSyncEnabled { get; set; }
    public string? OnPremisesSamAccountName { get; set; }
}

public sealed class GroupExpansion
{
    public required PrincipalInfo Group { get; init; }
    public List<PrincipalInfo> Members { get; } = [];
    public List<PrincipalInfo> Owners { get; } = [];
    public bool IsAssignableToRole { get; set; }
    public string? Error { get; set; }
}

/// <summary>One role assignment as returned by ARM or Graph. The principal can be a group.</summary>
public sealed class RawAssignment
{
    public required ScopeLevel ScopeLevel { get; init; }
    public required string ScopeId { get; init; }
    public string? ScopeDisplayName { get; set; }
    public string? SubscriptionId { get; set; }
    public string? SubscriptionName { get; set; }

    public required string RoleDefinitionId { get; init; }
    public required string RoleKey { get; init; }
    public string RoleName { get; set; } = "";
    public RoleDefinitionInfo? Role { get; set; }

    public required AssignmentType AssignmentType { get; init; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public string? Justification { get; set; }

    public required string PrincipalId { get; init; }
    public required string PrincipalType { get; set; }
    public string? PrincipalDisplayName { get; set; }
    public string? PrincipalUpn { get; set; }

    public string? SourceId { get; set; }
    public string? EligibilityScheduleId { get; set; }

    public bool IsEntra => ScopeLevel == ScopeLevel.EntraRole;

    /// <summary>Last segment of the ARM role assignment id (GUID without dashes), used to match Activity Log evidence.</summary>
    public string? AssignmentNameKey => SourceId is null ? null : Ids.GuidKey(SourceId.Split('/').Last());
}

/// <summary>One row of the review: an assignment for a concrete principal (direct or via group member).</summary>
public sealed class ReviewLine
{
    public required RawAssignment Raw { get; init; }
    public required RoleAssignmentSnapshot Row { get; init; }
    public PrincipalInfo? ViaGroup { get; init; }

    /// <summary>True for the row of the group itself (its members have their own rows).</summary>
    public bool IsGroupRow => ViaGroup is null && string.Equals(Row.PrincipalType, "Group", StringComparison.OrdinalIgnoreCase);
}

public sealed record PimActivation(
    string PrincipalId,
    string RoleKey,
    string? ScopeId,
    DateTime ActivatedUtc,
    string? LinkedEligibilityScheduleId,
    string? Justification,
    string Source);

public sealed class NativeDecisionInfo
{
    public required string Source { get; init; }
    public required string ReviewName { get; init; }
    public required string PrincipalId { get; init; }
    public string? ResourceId { get; init; }
    public string? RoleKey { get; init; }
    public string? GroupId { get; init; }
    public string? ScopeId { get; init; }
    public bool IncludeBelowScope { get; init; }
    public NativeReviewDecision Decision { get; init; }
    public string? RawDecision { get; init; }
    public DateTime? ReviewedUtc { get; init; }
    public string? ReviewedBy { get; init; }
    public string? Justification { get; init; }
    public DateTime? InstanceEndUtc { get; init; }
}

public sealed record ScanProgressUpdate(int Percent, string Step);

/// <summary>Everything collected during one scan.</summary>
public sealed class ScanContext(int snapshotId, DateTime nowUtc, Action<ScanProgressUpdate>? onProgress, Action<ScanLogEntry>? onLog)
{
    public int SnapshotId { get; } = snapshotId;
    public DateTime NowUtc { get; } = nowUtc;

    public List<ManagementGroupInfo> ManagementGroups { get; } = [];
    public List<SubscriptionInfo> Subscriptions { get; } = [];

    /// <summary>Subscriptions switched on in Subscription Discovery. Null = no discovery data, use configuration.</summary>
    public HashSet<string>? MonitoredSubscriptionIds { get; set; }

    public int ResourceGroupCount { get; set; }
    public int ResourceCount { get; set; }
    public Dictionary<string, string> ResourceTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, RoleDefinitionInfo> AzureRoles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RoleDefinitionInfo> EntraRoles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<RawAssignment> Assignments { get; } = [];
    public List<PimActivation> PimActivations { get; } = [];
    public bool EntraPimRequestsAvailable { get; set; }

    public Dictionary<string, PrincipalInfo> Principals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GroupExpansion> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, UserStateSnapshot> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool SignInActivityAvailable { get; set; } = true;

    public List<ReviewLine> Lines { get; } = [];
    public List<NativeDecisionInfo> NativeDecisions { get; } = [];
    public List<string> UsageSources { get; } = [];

    /// <summary>Values from the previous completed snapshot, used to keep evidence older than log retention.</summary>
    public Dictionary<string, UserStateSnapshot> PreviousUsers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RoleAssignmentSnapshot> PreviousRows { get; } = new(StringComparer.Ordinal);
    public int? PreviousSnapshotId { get; set; }

    public ConcurrentQueue<ScanLogEntry> LogEntries { get; } = new();

    public void Info(string message) => Add("Info", message);
    public void Warn(string message) => Add("Warning", message);

    public void Progress(int percent, string step)
    {
        Info(step);
        onProgress?.Invoke(new ScanProgressUpdate(percent, step));
    }

    private void Add(string level, string message)
    {
        var entry = new ScanLogEntry { ScanSnapshotId = SnapshotId, TimestampUtc = DateTime.UtcNow, Level = level, Message = message };
        LogEntries.Enqueue(entry);
        onLog?.Invoke(entry);
    }
}

public static class Ids
{
    /// <summary>Directory object id in Graph format (lowercase GUID with dashes).</summary>
    public static string ObjectId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Guid.TryParse(value.Trim(), out var g) ? g.ToString("D") : value.Trim().ToLowerInvariant();
    }

    /// <summary>Normalizes a GUID (with or without dashes) to lowercase "N" format. Non-GUIDs are lower-cased.</summary>
    public static string GuidKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Guid.TryParse(value.Trim(), out var g) ? g.ToString("N") : value.Trim().ToLowerInvariant();
    }

    /// <summary>Role definition key from a full ARM role definition id or a GUID.</summary>
    public static string RoleKeyFromArmId(string? roleDefinitionId)
    {
        if (string.IsNullOrWhiteSpace(roleDefinitionId)) return "";
        return GuidKey(roleDefinitionId.TrimEnd('/').Split('/').Last());
    }

    public static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return "";
        var s = scope.Trim();
        return s == "/" ? "/" : s.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>True if <paramref name="inner"/> is the same scope as or a child scope of <paramref name="outer"/>.</summary>
    public static bool ScopeContains(string outer, string inner)
    {
        var o = NormalizeScope(outer);
        var i = NormalizeScope(inner);
        if (o == "/") return true;
        return i == o || i.StartsWith(o + "/", StringComparison.Ordinal);
    }

    public static string StableKey(params string?[] parts)
    {
        var text = string.Join("|", parts.Select(p => (p ?? "").ToLowerInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }
}

public static class JsonExt
{
    public static string? Str(this JsonElement e, params string[] path)
    {
        var cur = e;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => cur.GetRawText(),
            _ => null
        };
    }

    public static DateTime? Date(this JsonElement e, params string[] path)
    {
        var s = e.Str(path);
        return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
    }

    public static bool? Bool(this JsonElement e, params string[] path)
    {
        var s = e.Str(path);
        return bool.TryParse(s, out var b) ? b : null;
    }

    public static JsonElement? Obj(this JsonElement e, params string[] path)
    {
        var cur = e;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null;
        }
        return cur;
    }
}
