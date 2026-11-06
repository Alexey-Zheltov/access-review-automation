using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;

namespace AccessReviewPortal.Services.Review;

/// <summary>
/// Derives the usage status and the recommended action of one assignment. Pure functions:
/// the same code runs after a scan and when the reviewer changes the baseline or the inactivity threshold.
/// </summary>
public static class RecommendationEngine
{
    public static void Evaluate(RoleAssignmentSnapshot row, UserStateSnapshot? user, UserStateSnapshot? baselineUser,
        ISet<string>? baselineKeys, DateTime evaluatedAtUtc, int thresholdDays)
    {
        ClassifyUsage(row, evaluatedAtUtc, thresholdDays);
        row.IsNewSinceBaseline = baselineKeys is not null && !baselineKeys.Contains(row.AssignmentKey);
        Recommend(row, user, baselineUser, thresholdDays);
    }

    public static void ClassifyUsage(RoleAssignmentSnapshot r, DateTime nowUtc, int thresholdDays)
    {
        var since = nowUtc.AddDays(-thresholdDays);
        var activated = r.LastPimActivationUtc >= since;
        var wrote = r.LastWriteEvidenceUtc >= since;
        // For eligible roles only an activation proves use: a sign-in without activation does not use the role.
        var signedIn = r.AssignmentType != AssignmentType.PimEligible && r.LastSignInUtc >= since;

        var evidence = new List<string>();
        if (r.LastPimActivationUtc is not null) evidence.Add(r.PimActivationDetails ?? $"PIM activation {r.LastPimActivationUtc:yyyy-MM-dd}");
        if (r.LastWriteEvidenceUtc is not null) evidence.Add($"Write: {r.LastWriteOperation} on {r.LastWriteEvidenceUtc:yyyy-MM-dd} [{r.WriteEvidenceSource}]");
        if (r.LastSignInUtc is not null) evidence.Add($"{r.SignInSource ?? "Sign-in"} on {r.LastSignInUtc:yyyy-MM-dd}");

        if (activated || wrote)
        {
            r.UsageEvidenceStatus = UsageEvidenceStatus.ConfirmedUsed;
            r.LastRoleUsedUtc = Max(activated ? r.LastPimActivationUtc : null, wrote ? r.LastWriteEvidenceUtc : null);
        }
        else if (signedIn)
        {
            r.UsageEvidenceStatus = UsageEvidenceStatus.PortalSignInOnlyNoWrite;
            r.LastRoleUsedUtc = r.LastSignInUtc;
            evidence.Insert(0, $"No write operation or PIM activation in {thresholdDays}d");
        }
        else
        {
            r.UsageEvidenceStatus = UsageEvidenceStatus.Unused90Days;
            r.LastRoleUsedUtc = Max(r.LastPimActivationUtc, Max(r.LastWriteEvidenceUtc,
                r.AssignmentType == AssignmentType.PimEligible ? null : r.LastSignInUtc));
            evidence.Insert(0, r.AssignmentType == AssignmentType.PimEligible
                ? $"No PIM activation in {thresholdDays}d"
                : $"No PIM activation, write operation or Azure sign-in in {thresholdDays}d");
        }

        r.DaysSinceLastActivity = r.LastRoleUsedUtc is null ? null : Math.Max(0, (int)(nowUtc - r.LastRoleUsedUtc.Value).TotalDays);
        r.UsageEvidenceDetails = string.Join("; ", evidence);
    }

    public static void Recommend(RoleAssignmentSnapshot r, UserStateSnapshot? user, UserStateSnapshot? baselineUser, int thresholdDays)
    {
        // 1. Left the organization / account disabled / principal deleted.
        if (user is not null && (!user.ExistsInDirectory || !user.AccountEnabled))
        {
            Set(r, RecommendedAction.Remove, Reasons.LeftOrganization + (user.ExistsInDirectory ? " (account disabled)" : " (user deleted from Entra ID)"));
            return;
        }
        if (!r.PrincipalExists)
        {
            Set(r, RecommendedAction.Remove, Reasons.LeftOrganization + " (principal not found in Entra ID)");
            return;
        }

        // 2. Mover: department or manager changed since the baseline snapshot.
        if (user is not null && baselineUser is not null && baselineUser.ExistsInDirectory)
        {
            var deptChanged = !string.Equals(Norm(user.Department), Norm(baselineUser.Department), StringComparison.OrdinalIgnoreCase);
            var managerChanged = !string.Equals(Norm(user.ManagerId), Norm(baselineUser.ManagerId), StringComparison.OrdinalIgnoreCase);
            if (deptChanged || managerChanged)
            {
                Set(r, RecommendedAction.Remove, $"{Reasons.DepartmentManagerChanged} ({Describe(baselineUser)} -> {Describe(user)})");
                return;
            }
        }

        // 3. Denied by the user (or reviewer) in a native access review.
        if (r.NativeSelfReviewDecision == NativeReviewDecision.Deny)
        {
            Set(r, RecommendedAction.Remove, Reasons.DeniedInSelfReview);
            return;
        }

        // 4. No usage at all.
        if (r.UsageEvidenceStatus == UsageEvidenceStatus.Unused90Days)
        {
            if (r.PrincipalType == "ServicePrincipal")
            {
                Set(r, RecommendedAction.Review, $"{Reasons.ServicePrincipalNoActivity} in {thresholdDays}d (read operations are not logged)");
            }
            else
            {
                Set(r, RecommendedAction.Remove, $"{Reasons.NoRoleUsage} > {thresholdDays} days");
            }
            return;
        }

        // 5. Used, but Defender for Cloud CSPM (CIEM) reports too many permissions or a permanent privileged role.
        if (HasFinding(r, DefenderCspmStatus.Overprovisioned) || HasFinding(r, DefenderCspmStatus.PermanentPrivileged))
        {
            Set(r, RecommendedAction.Review, Reasons.DefenderCspm);
            return;
        }

        // 6. Write-capable role, but only sign-ins (no writes).
        if (r.UsageEvidenceStatus == UsageEvidenceStatus.PortalSignInOnlyNoWrite && r.RoleHasWritePermissions)
        {
            Set(r, RecommendedAction.Review, $"{Reasons.NoWriteActions} in {thresholdDays}d (Consider Reader or PIM Eligible)");
            return;
        }

        // 7. Keep. The original justification stays as the reason.
        Set(r, RecommendedAction.NoAction, r.Justification);
    }

    public static bool HasFinding(RoleAssignmentSnapshot r, DefenderCspmStatus status) =>
        r.DefenderCspmStatus == status
        || (r.DefenderCspmFindings?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(status.ToString(), StringComparer.Ordinal) ?? false);

    public static string Describe(UserStateSnapshot u) =>
        $"{(string.IsNullOrWhiteSpace(u.Department) ? "no department" : u.Department)} / " +
        $"{(u.ManagerDisplayName ?? u.ManagerUpn ?? (string.IsNullOrEmpty(u.ManagerId) ? "no manager" : u.ManagerId))}";

    private static void Set(RoleAssignmentSnapshot r, RecommendedAction action, string? reason)
    {
        r.RecommendedAction = action;
        r.RecommendationReason = reason;
    }

    private static string Norm(string? s) => (s ?? "").Trim();

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
