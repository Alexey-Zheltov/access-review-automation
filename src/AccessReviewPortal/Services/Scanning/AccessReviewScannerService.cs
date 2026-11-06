using AccessReviewPortal.Data;
using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using AccessReviewPortal.Services.Discovery;
using AccessReviewPortal.Services.Review;
using AccessReviewPortal.Services.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Services.Scanning;

/// <summary>Runs one live scan of the tenant and stores it as a new snapshot.</summary>
public sealed class AccessReviewScannerService(
    IDbContextFactory<AppDbContext> dbFactory,
    AzureClients clients,
    ArmCollector arm,
    EntraRoleCollector entra,
    DirectoryCollector directory,
    AccessReviewCollector accessReviews,
    RoleUsageAnalyzerService usage,
    DefenderCspmCollector defender,
    SubscriptionDiscoveryService discovery,
    SettingsService settings,
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<AccessReviewScannerService> logger)
{
    public async Task<int> RunAsync(string label, ScanTrigger trigger, string? startedByUpn,
        Action<ScanProgressUpdate>? onProgress, Action<ScanLogEntry>? onLog, CancellationToken ct)
    {
        var threshold = await settings.GetInactivityThresholdDaysAsync(ct);
        var snapshot = new ScanSnapshot
        {
            CreatedAtUtc = DateTime.UtcNow,
            Label = string.IsNullOrWhiteSpace(label) ? $"Scan {DateTime.UtcNow:yyyy-MM-dd HH:mm}" : label.Trim(),
            Status = ScanStatus.Running,
            InactivityThresholdDays = threshold,
            TenantId = clients.TenantId,
            Trigger = trigger,
            StartedByUpn = startedByUpn
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.ScanSnapshots.Add(snapshot);
            await db.SaveChangesAsync(ct);
        }

        var ctx = new ScanContext(snapshot.Id, snapshot.CreatedAtUtc, onProgress, onLog);
        try
        {
            ctx.Info($"Scan started ({trigger}{(startedByUpn is null ? "" : $" by {startedByUpn}")}). Credential: {clients.AuthMode}. " +
                     $"Inactivity threshold: {threshold} days.");
            var previous = await LoadPreviousAsync(ctx, ct);

            ctx.Progress(3, "Subscription discovery (Azure Resource Graph)");
            try
            {
                var found = await discovery.RefreshAsync(null, ct);
                foreach (var m in found.Messages) ctx.Warn("Discovery: " + m);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ctx.Warn($"Subscription discovery failed, the last known list is used: {ex.ShortMessage()}");
            }
            ctx.MonitoredSubscriptionIds = await discovery.GetMonitoredSubscriptionIdsAsync(ct);
            ctx.Info(ctx.MonitoredSubscriptionIds is null
                ? "Subscription discovery has no data: all subscriptions visible to the scanner are scanned."
                : $"Monitored subscriptions (Subscriptions page): {ctx.MonitoredSubscriptionIds.Count}.");

            ctx.Progress(5, "Reading Azure hierarchy (management groups, subscriptions, resource groups, resources)");
            await arm.CollectHierarchyAsync(ctx, ct);
            ctx.Info($"Hierarchy: {ctx.ManagementGroups.Count} management groups, {ctx.Subscriptions.Count} subscriptions, " +
                     $"{ctx.ResourceGroupCount} resource groups, {ctx.ResourceCount} resources.");

            ctx.Progress(12, "Reading Azure role definitions");
            await arm.CollectRoleDefinitionsAsync(ctx, ct);

            ctx.Progress(18, "Reading Azure RBAC assignments and PIM schedules on all levels");
            await arm.CollectAssignmentsAsync(ctx, ct);

            ctx.Progress(30, "Reading Entra ID role assignments (PIM active and eligible)");
            await entra.CollectAsync(ctx, ct);

            ctx.Progress(38, "Resolving principals in Entra ID");
            await directory.ResolvePrincipalsAsync(ctx, ct);

            ctx.Progress(42, "Expanding group memberships (transitive) and group owners");
            await directory.ExpandGroupsAsync(ctx, ct);
            DirectoryCollector.BuildLines(ctx);

            ctx.Progress(50, "Loading user profiles (department, manager, sign-in activity)");
            await directory.LoadUsersAsync(ctx, ct);

            ctx.Progress(58, "Reading native Access Review decisions");
            await accessReviews.CollectAsync(ctx, ct);
            ApplyNativeDecisions(ctx);

            ctx.Progress(65, "Analyzing role usage: PIM activations, Activity Log, sign-ins, directory audit");
            await usage.AnalyzeAsync(ctx, ct);

            ctx.Progress(89, "Reading Microsoft Defender for Cloud CSPM (CIEM) findings");
            await defender.CollectAsync(ctx, ct);

            ctx.Progress(91, "Calculating recommendations");
            var baselineKeys = previous is null ? null : await LoadBaselineKeysAsync(previous.Id, ct);
            foreach (var line in ctx.Lines)
            {
                ctx.Users.TryGetValue(line.Row.PrincipalId, out var user);
                ctx.PreviousUsers.TryGetValue(line.Row.PrincipalId, out var baselineUser);
                RecommendationEngine.Evaluate(line.Row, user, baselineUser, baselineKeys, snapshot.CreatedAtUtc, threshold);
            }

            ctx.Progress(95, "Saving snapshot to PostgreSQL");
            await SaveInChunksAsync(ctx.Users.Values, ct);
            await SaveInChunksAsync(ctx.Lines.Select(l => l.Row), ct);
            await SaveInChunksAsync(DirectoryCollector.BuildGroupSnapshots(ctx), ct);

            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var s = await db.ScanSnapshots.FirstAsync(x => x.Id == snapshot.Id, ct);
                s.TotalUsers = ctx.Users.Count;
                s.TotalAssignments = ctx.Lines.Count;
                s.BaselineSnapshotId = previous?.Id;
                s.UsageSources = string.Join("\n", ctx.UsageSources.Distinct());
                s.Status = ScanStatus.Completed;
                s.CompletedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                s.DatabaseSizeBytes = await SnapshotService.GetDatabaseSizeAsync(db, ct);
                await db.SaveChangesAsync(ct);
            }
            await discovery.MarkScannedAsync(ctx.Subscriptions.Select(x => x.SubscriptionId), snapshot.CreatedAtUtc, ct);

            ctx.Progress(100, $"Scan completed: {ctx.Lines.Count} review rows, {ctx.Users.Count} users, {ctx.Groups.Count} groups" +
                              (previous is null ? ". No baseline yet (first snapshot)." : $". Baseline: snapshot #{previous.Id} '{previous.Label}'."));
            return snapshot.Id;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scan {Id} failed", snapshot.Id);
            ctx.Warn($"Scan failed: {ex.GetType().Name}: {ex.ShortMessage()}");
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var s = await db.ScanSnapshots.FirstAsync(x => x.Id == snapshot.Id, CancellationToken.None);
            s.Status = ScanStatus.Failed;
            s.Error = ex.ShortMessage();
            s.CompletedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            await SaveLogAsync(ctx);
        }
    }

    private async Task<ScanSnapshot?> LoadPreviousAsync(ScanContext ctx, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var previous = await db.ScanSnapshots.AsNoTracking()
            .Where(s => s.Status == ScanStatus.Completed && s.Id != ctx.SnapshotId)
            .OrderByDescending(s => s.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (previous is null) return null;

        ctx.PreviousSnapshotId = previous.Id;
        foreach (var u in await db.UserStateSnapshots.AsNoTracking().Where(u => u.ScanSnapshotId == previous.Id).ToListAsync(ct))
        {
            ctx.PreviousUsers[u.UserId] = u;
        }
        var rows = await db.RoleAssignmentSnapshots.AsNoTracking()
            .Where(r => r.ScanSnapshotId == previous.Id)
            .Select(r => new RoleAssignmentSnapshot
            {
                AssignmentKey = r.AssignmentKey,
                LastWriteEvidenceUtc = r.LastWriteEvidenceUtc,
                LastWriteOperation = r.LastWriteOperation,
                WriteEvidenceSource = r.WriteEvidenceSource,
                LastPimActivationUtc = r.LastPimActivationUtc,
                PimActivationDetails = r.PimActivationDetails,
                LastSignInUtc = r.LastSignInUtc,
                SignInSource = r.SignInSource
            })
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            ctx.PreviousRows.TryAdd(r.AssignmentKey, r);
        }
        ctx.Info($"Previous snapshot #{previous.Id} '{previous.Label}' is the default baseline ({ctx.PreviousUsers.Count} users).");
        return previous;
    }

    private async Task<HashSet<string>> LoadBaselineKeysAsync(int baselineId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keys = await db.RoleAssignmentSnapshots.AsNoTracking()
            .Where(r => r.ScanSnapshotId == baselineId)
            .Select(r => r.AssignmentKey)
            .ToListAsync(ct);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Attaches the latest native access review decision to each user/service principal row.</summary>
    private static void ApplyNativeDecisions(ScanContext ctx)
    {
        if (ctx.NativeDecisions.Count == 0) return;
        var byPrincipal = ctx.NativeDecisions.GroupBy(d => d.PrincipalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var matched = 0;
        foreach (var line in ctx.Lines.Where(l => !l.IsGroupRow))
        {
            if (!byPrincipal.TryGetValue(line.Row.PrincipalId, out var decisions)) continue;
            var match = decisions
                .Where(d => Matches(line, d))
                .OrderByDescending(d => d.ReviewedUtc ?? d.InstanceEndUtc ?? DateTime.MinValue)
                .FirstOrDefault();
            if (match is null) continue;

            line.Row.NativeSelfReviewDecision = match.Decision;
            line.Row.NativeReviewDetails = $"{match.Source} '{match.ReviewName}': {match.RawDecision}" +
                                           (match.ReviewedBy is null ? "" : $" by {match.ReviewedBy}") +
                                           (match.ReviewedUtc is null ? "" : $" on {match.ReviewedUtc:yyyy-MM-dd}") +
                                           (string.IsNullOrWhiteSpace(match.Justification) ? "" : $" - \"{match.Justification}\"");
            matched++;
        }
        ctx.Info($"Native access review decisions matched to {matched} review rows.");
    }

    private static bool Matches(ReviewLine line, NativeDecisionInfo d)
    {
        if (d.Source.StartsWith("Entra", StringComparison.OrdinalIgnoreCase))
        {
            if (line.ViaGroup is not null && (string.Equals(d.GroupId, line.ViaGroup.Id, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(d.ResourceId, line.ViaGroup.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            return line.Raw.IsEntra && (Ids.GuidKey(d.RoleKey) == Ids.GuidKey(line.Raw.RoleKey) || Ids.GuidKey(d.ResourceId) == Ids.GuidKey(line.Raw.RoleKey));
        }

        if (line.Raw.IsEntra || d.RoleKey != line.Raw.RoleKey || d.ScopeId is null) return false;
        return Ids.NormalizeScope(d.ScopeId) == Ids.NormalizeScope(line.Raw.ScopeId)
               || (d.IncludeBelowScope && Ids.ScopeContains(d.ScopeId, line.Raw.ScopeId));
    }

    /// <summary>Inserts rows in chunks with a fresh change tracker per chunk (large tenants: 100,000+ rows per scan).</summary>
    private async Task SaveInChunksAsync<T>(IEnumerable<T> items, CancellationToken ct) where T : class
    {
        var size = Math.Max(100, databaseOptions.Value.InsertChunkSize);
        foreach (var chunk in items.Chunk(size))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            await db.Set<T>().AddRangeAsync(chunk, ct);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task SaveLogAsync(ScanContext ctx)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            db.ScanLogEntries.AddRange(ctx.LogEntries);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save scan log for snapshot {Id}", ctx.SnapshotId);
        }
    }
}
