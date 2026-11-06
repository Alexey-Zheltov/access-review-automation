using AccessReviewPortal.Domain;
using AccessReviewPortal.Services.Scanning;

namespace AccessReviewPortal.Services.Usage;

/// <summary>
/// Collects role usage evidence for every review line from three sources:
/// A) PIM activations (ARM / Graph PIM requests, directory audit),
/// B) write/action operations (Azure Activity Log; directory audit for Entra roles),
/// C) sign-ins to Azure Portal / ARM (Azure roles) or admin portals (Entra roles).
/// The final status is derived by <see cref="Review.RecommendationEngine"/> from these timestamps.
/// </summary>
public sealed class RoleUsageAnalyzerService(
    ActivityLogReader activityLogReader,
    SignInReader signInReader,
    DirectoryAuditReader auditReader,
    LogAnalyticsReader logAnalytics)
{
    public async Task AnalyzeAsync(ScanContext ctx, CancellationToken ct)
    {
        // Source C: sign-ins.
        var signInUsers = ctx.Users.Values.Where(u => u.ExistsInDirectory).Select(u => u.UserId).ToList();
        ctx.Progress(66, $"Reading sign-in logs for {signInUsers.Count} users (Azure Portal / ARM and admin portals)");
        var signIns = await logAnalytics.ReadSignInsAsync(ctx, ct) ?? await signInReader.ReadAsync(ctx, signInUsers, ct);
        ctx.UsageSources.Add("Sign-ins: " + signIns.Source);

        // Source A (Entra fallback) and Entra justification from the directory audit log.
        var entraUsers = ctx.Lines.Where(l => l.Raw.IsEntra && l.Row.PrincipalType == "User")
            .Select(l => l.Row.PrincipalId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ctx.Progress(74, $"Reading directory audit log (admin activity of {entraUsers.Count} users with Entra roles, PIM events)");
        var adminActivity = await auditReader.ReadAdminActivityAsync(ctx, entraUsers, ct);
        var auditJustifications = await auditReader.ReadPimEventsAsync(ctx, ct);
        if (!ctx.EntraPimRequestsAvailable)
        {
            ctx.UsageSources.Add("Entra PIM activations: directory audit (30 days)");
        }
        ApplyAuditJustifications(ctx, auditJustifications);

        // Source B: Azure write/action operations.
        ctx.Progress(80, "Reading Azure Activity Log (write and action operations)");
        var activity = await logAnalytics.ReadActivityAsync(ctx, ct) ?? await activityLogReader.ReadAsync(ctx, ct);
        ctx.UsageSources.Add("Azure write operations: " + activity.Source);
        ctx.UsageSources.Add("Entra write operations: directory audit (inferred, 30 days)");

        ctx.Progress(88, "Matching usage evidence to assignments");

        UpdateUsers(ctx, signIns, adminActivity);
        ApplyActivations(ctx);
        ApplyActivity(ctx, activity);
        ApplyUserSignals(ctx);
        CarryForward(ctx);
        AggregateGroupRows(ctx);
    }

    private static void ApplyAuditJustifications(ScanContext ctx, Dictionary<string, string> justifications)
    {
        if (justifications.Count == 0) return;
        foreach (var a in ctx.Assignments.Where(a => string.IsNullOrWhiteSpace(a.Justification)))
        {
            if (justifications.TryGetValue(DirectoryAuditReader.JustificationKey(a.PrincipalId, a.RoleKey, a.IsEntra ? "/" : a.ScopeId), out var j))
            {
                a.Justification = j;
            }
        }
        foreach (var line in ctx.Lines.Where(l => string.IsNullOrWhiteSpace(l.Row.Justification)))
        {
            line.Row.Justification = line.Raw.Justification;
        }
    }

    private static void UpdateUsers(ScanContext ctx, SignInData signIns, Dictionary<string, DirectoryActivity> adminActivity)
    {
        foreach (var user in ctx.Users.Values)
        {
            if (signIns.AzureControlPlane.TryGetValue(user.UserId, out var arm)) user.LastAzurePortalSignInUtc = arm.LastUtc;
            if (signIns.AdminPortals.TryGetValue(user.UserId, out var admin)) user.LastAdminPortalSignInUtc = admin.LastUtc;
            if (adminActivity.TryGetValue(user.UserId, out var act))
            {
                user.LastDirectoryActivityUtc = act.LastUtc;
                user.LastDirectoryActivity = act.Activity;
            }

            // Keep older evidence from the previous snapshot: logs read through Graph only cover 30 days.
            if (ctx.PreviousUsers.TryGetValue(user.UserId, out var prev))
            {
                user.LastAzurePortalSignInUtc = Max(user.LastAzurePortalSignInUtc, prev.LastAzurePortalSignInUtc);
                user.LastAdminPortalSignInUtc = Max(user.LastAdminPortalSignInUtc, prev.LastAdminPortalSignInUtc);
                if (prev.LastDirectoryActivityUtc > (user.LastDirectoryActivityUtc ?? DateTime.MinValue))
                {
                    user.LastDirectoryActivityUtc = prev.LastDirectoryActivityUtc;
                    user.LastDirectoryActivity = prev.LastDirectoryActivity;
                }
            }
        }
    }

    private static void ApplyActivations(ScanContext ctx)
    {
        var byPrincipal = ctx.PimActivations
            .GroupBy(a => a.PrincipalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var line in ctx.Lines.Where(l => !l.IsGroupRow))
        {
            if (!byPrincipal.TryGetValue(line.Row.PrincipalId, out var candidates)) continue;

            foreach (var act in candidates.Where(a => Matches(line, a)))
            {
                if (act.ActivatedUtc > (line.Row.LastPimActivationUtc ?? DateTime.MinValue))
                {
                    line.Row.LastPimActivationUtc = act.ActivatedUtc;
                    line.Row.PimActivationDetails = $"{act.Source} {act.ActivatedUtc:yyyy-MM-dd HH:mm} UTC" +
                                                    (string.IsNullOrWhiteSpace(act.Justification) ? "" : $" - \"{act.Justification}\"");
                }
            }
        }
    }

    private static bool Matches(ReviewLine line, PimActivation act)
    {
        if (line.Raw.IsEntra)
        {
            var actIsEntra = act.ScopeId is null || act.ScopeId == "/" || act.ScopeId.StartsWith("/administrativeUnits", StringComparison.OrdinalIgnoreCase);
            return actIsEntra && Ids.GuidKey(act.RoleKey) == Ids.GuidKey(line.Raw.RoleKey)
                   && (act.ScopeId is null || Ids.NormalizeScope(act.ScopeId) == Ids.NormalizeScope(line.Raw.ScopeId));
        }

        if (act.LinkedEligibilityScheduleId is not null && line.Raw.EligibilityScheduleId is not null)
        {
            return string.Equals(act.LinkedEligibilityScheduleId, line.Raw.EligibilityScheduleId, StringComparison.OrdinalIgnoreCase);
        }
        return Ids.GuidKey(act.RoleKey) == line.Raw.RoleKey && act.ScopeId is not null
               && Ids.NormalizeScope(act.ScopeId) == Ids.NormalizeScope(line.Raw.ScopeId);
    }

    private static void ApplyActivity(ScanContext ctx, ActivityLogResult activity)
    {
        var azureLines = ctx.Lines.Where(l => !l.Raw.IsEntra && !l.IsGroupRow).ToList();
        var byPrincipal = azureLines.GroupBy(l => l.Row.PrincipalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Exact attribution (authorization.evidence from Log Analytics).
        var exactMatched = new HashSet<ReviewLine>();
        foreach (var ev in activity.Evidence)
        {
            if (!byPrincipal.TryGetValue(ev.CallerObjectId, out var lines)) continue;
            foreach (var line in lines)
            {
                var sameAssignment = line.Raw.AssignmentNameKey == ev.RoleAssignmentKey;
                var sameRoleScope = line.Raw.RoleKey == ev.RoleKey
                                    && Ids.NormalizeScope(line.Raw.ScopeId) == Ids.NormalizeScope(ev.Scope)
                                    && string.Equals(Ids.ObjectId(line.Raw.PrincipalId), ev.EvidencePrincipalId, StringComparison.OrdinalIgnoreCase);
                if (sameAssignment || sameRoleScope)
                {
                    Update(line, ev.LastUtc, ev.Operation, "Activity Log (exact: authorization.evidence)");
                    exactMatched.Add(line);
                }
            }
        }

        // Inferred attribution: the narrowest assignment of the caller that covers the scope and allows the action.
        foreach (var (key, value) in activity.Events)
        {
            if (!byPrincipal.TryGetValue(key.CallerObjectId, out var lines)) continue;
            var matches = lines.Where(l => Covers(ctx, l.Raw.ScopeId, key.Scope) && PermissionMatcher.AllowsAction(l.Raw.Role, key.Action)).ToList();
            if (matches.Count == 0) continue;

            var depth = matches.Max(l => Depth(l.Raw.ScopeId));
            foreach (var line in matches.Where(l => Depth(l.Raw.ScopeId) == depth && !exactMatched.Contains(l)))
            {
                Update(line, value.LastUtc, value.Operation, "Activity Log (inferred: caller + scope + role permissions)");
            }
        }
    }

    private static void ApplyUserSignals(ScanContext ctx)
    {
        foreach (var line in ctx.Lines.Where(l => l.Row.PrincipalType == "User"))
        {
            if (!ctx.Users.TryGetValue(line.Row.PrincipalId, out var user)) continue;
            if (line.Raw.IsEntra)
            {
                if (user.LastAdminPortalSignInUtc is not null)
                {
                    line.Row.LastSignInUtc = user.LastAdminPortalSignInUtc;
                    line.Row.SignInSource = "Admin portal sign-in";
                }
                // Directory changes made by the user can only come from a write-capable admin role.
                if (line.Row.RoleHasWritePermissions && user.LastDirectoryActivityUtc is not null)
                {
                    Update(line, user.LastDirectoryActivityUtc.Value, user.LastDirectoryActivity ?? "directory change",
                        "Directory audit (inferred: admin activity by the user)");
                }
            }
            else if (user.LastAzurePortalSignInUtc is not null)
            {
                line.Row.LastSignInUtc = user.LastAzurePortalSignInUtc;
                line.Row.SignInSource = "Azure Portal / ARM sign-in";
            }
        }
    }

    private static void CarryForward(ScanContext ctx)
    {
        if (ctx.PreviousSnapshotId is null) return;
        foreach (var line in ctx.Lines)
        {
            if (!ctx.PreviousRows.TryGetValue(line.Row.AssignmentKey, out var prev)) continue;
            var suffix = $" (from snapshot #{ctx.PreviousSnapshotId})";
            if (prev.LastWriteEvidenceUtc > (line.Row.LastWriteEvidenceUtc ?? DateTime.MinValue))
            {
                line.Row.LastWriteEvidenceUtc = prev.LastWriteEvidenceUtc;
                line.Row.LastWriteOperation = prev.LastWriteOperation;
                line.Row.WriteEvidenceSource = (prev.WriteEvidenceSource ?? "").Replace(" (from snapshot", "|").Split('|')[0] + suffix;
            }
            if (prev.LastPimActivationUtc > (line.Row.LastPimActivationUtc ?? DateTime.MinValue))
            {
                line.Row.LastPimActivationUtc = prev.LastPimActivationUtc;
                line.Row.PimActivationDetails = prev.PimActivationDetails + suffix;
            }
            if (prev.LastSignInUtc > (line.Row.LastSignInUtc ?? DateTime.MinValue))
            {
                line.Row.LastSignInUtc = prev.LastSignInUtc;
                line.Row.SignInSource = prev.SignInSource;
            }
        }
    }

    /// <summary>The row of a group gets the latest evidence of its members.</summary>
    private static void AggregateGroupRows(ScanContext ctx)
    {
        var membersByAssignment = ctx.Lines.Where(l => l.ViaGroup is not null)
            .GroupBy(l => l.Raw)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var groupLine in ctx.Lines.Where(l => l.IsGroupRow))
        {
            if (!membersByAssignment.TryGetValue(groupLine.Raw, out var members)) continue;
            var g = groupLine.Row;

            var write = members.Where(m => m.Row.LastWriteEvidenceUtc is not null).MaxBy(m => m.Row.LastWriteEvidenceUtc);
            if (write is not null && write.Row.LastWriteEvidenceUtc > (g.LastWriteEvidenceUtc ?? DateTime.MinValue))
            {
                g.LastWriteEvidenceUtc = write.Row.LastWriteEvidenceUtc;
                g.LastWriteOperation = $"{write.Row.PrincipalDisplayName}: {write.Row.LastWriteOperation}";
                g.WriteEvidenceSource = write.Row.WriteEvidenceSource;
            }

            var activation = members.Where(m => m.Row.LastPimActivationUtc is not null).MaxBy(m => m.Row.LastPimActivationUtc);
            if (activation is not null && activation.Row.LastPimActivationUtc > (g.LastPimActivationUtc ?? DateTime.MinValue))
            {
                g.LastPimActivationUtc = activation.Row.LastPimActivationUtc;
                g.PimActivationDetails = $"{activation.Row.PrincipalDisplayName}: {activation.Row.PimActivationDetails}";
            }

            var signIn = members.Where(m => m.Row.LastSignInUtc is not null).MaxBy(m => m.Row.LastSignInUtc);
            if (signIn is not null && signIn.Row.LastSignInUtc > (g.LastSignInUtc ?? DateTime.MinValue))
            {
                g.LastSignInUtc = signIn.Row.LastSignInUtc;
                g.SignInSource = $"{signIn.Row.PrincipalDisplayName}: {signIn.Row.SignInSource}";
            }
        }
    }

    private static void Update(ReviewLine line, DateTime when, string operation, string source)
    {
        if (when <= (line.Row.LastWriteEvidenceUtc ?? DateTime.MinValue)) return;
        line.Row.LastWriteEvidenceUtc = when;
        line.Row.LastWriteOperation = operation;
        line.Row.WriteEvidenceSource = source;
    }

    /// <summary>True if an assignment at <paramref name="assignmentScope"/> covers an operation at <paramref name="eventScope"/>.</summary>
    private static bool Covers(ScanContext ctx, string assignmentScope, string eventScope)
    {
        if (assignmentScope == "/") return true;
        if (ArmCollector.ClassifyScope(assignmentScope) == ScopeLevel.ManagementGroup)
        {
            if (Ids.ScopeContains(assignmentScope, eventScope)) return true;
            var sub = ArmCollector.SubscriptionOf(eventScope);
            var mg = ctx.ManagementGroups.FirstOrDefault(m => string.Equals(m.Id, assignmentScope, StringComparison.OrdinalIgnoreCase));
            return sub is not null && mg is not null && mg.DescendantSubscriptionIds.Contains(sub);
        }
        return Ids.ScopeContains(assignmentScope, eventScope);
    }

    private static int Depth(string scope) =>
        scope == "/" ? 0 : ArmCollector.ClassifyScope(scope) == ScopeLevel.ManagementGroup ? 1 : scope.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
