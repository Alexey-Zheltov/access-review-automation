using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using Microsoft.Extensions.Options;
using Microsoft.Graph.DirectoryObjects.GetByIds;
using Microsoft.Graph.Models;

namespace AccessReviewPortal.Services.Scanning;

/// <summary>Resolves principals, expands groups (transitive members) and loads user profiles from Microsoft Graph.</summary>
public sealed class DirectoryCollector(AzureClients clients, IOptions<ReviewSettingsOptions> options)
{
    private static readonly string[] UserSelect =
    [
        "id", "displayName", "userPrincipalName", "mail", "accountEnabled", "department", "jobTitle",
        "createdDateTime", "onPremisesSyncEnabled", "onPremisesSamAccountName", "signInActivity"
    ];

    public async Task ResolvePrincipalsAsync(ScanContext ctx, CancellationToken ct)
    {
        var ids = ctx.Assignments.Select(a => a.PrincipalId)
            .Where(id => Guid.TryParse(id, out _) && !ctx.Principals.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var chunk in ids.Chunk(1000))
        {
            var response = await clients.Graph.DirectoryObjects.GetByIds.PostAsGetByIdsPostResponseAsync(
                new GetByIdsPostRequestBody { Ids = chunk.ToList(), Types = ["user", "group", "servicePrincipal"] },
                cancellationToken: ct);
            foreach (var o in response?.Value ?? [])
            {
                var p = ToPrincipal(o);
                ctx.Principals[p.Id] = p;
            }
        }

        foreach (var id in ids.Where(id => !ctx.Principals.ContainsKey(id)))
        {
            ctx.Principals[id] = new PrincipalInfo { Id = id, Exists = false };
        }

        foreach (var a in ctx.Assignments)
        {
            if (!ctx.Principals.TryGetValue(a.PrincipalId, out var p)) continue;
            if (p.Exists)
            {
                a.PrincipalType = p.Type;
                a.PrincipalDisplayName = p.DisplayName ?? a.PrincipalDisplayName;
                a.PrincipalUpn = p.Upn ?? a.PrincipalUpn;
            }
            else
            {
                p.Type = a.PrincipalType;
                p.DisplayName ??= a.PrincipalDisplayName;
            }
        }

        var missing = ctx.Principals.Values.Count(p => !p.Exists);
        ctx.Info($"Principals resolved: {ctx.Principals.Count} ({missing} not found in Entra ID).");
    }

    public async Task ExpandGroupsAsync(ScanContext ctx, CancellationToken ct)
    {
        var groupIds = ctx.Assignments
            .Where(a => a.PrincipalType == "Group" && ctx.Principals.TryGetValue(a.PrincipalId, out var p) && p.Exists)
            .Select(a => a.PrincipalId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxParallelism));
        var expansions = await Task.WhenAll(groupIds.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await ExpandGroupAsync(ctx, id, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var e in expansions)
        {
            ctx.Groups[e.Group.Id] = e;
            if (e.Error is not null)
            {
                ctx.Warn($"Group '{e.Group.DisplayName ?? e.Group.Id}' could not be expanded: {e.Error}");
            }
        }
        ctx.Info($"Groups expanded: {expansions.Length}, members found: {expansions.Sum(e => e.Members.Count)}.");
    }

    private async Task<GroupExpansion> ExpandGroupAsync(ScanContext ctx, string groupId, CancellationToken ct)
    {
        ctx.Principals.TryGetValue(groupId, out var known);
        var expansion = new GroupExpansion { Group = known ?? new PrincipalInfo { Id = groupId, Type = "Group" } };
        try
        {
            var g = await clients.Graph.Groups[groupId].GetAsync(r =>
                r.QueryParameters.Select = ["id", "displayName", "onPremisesSyncEnabled", "onPremisesSamAccountName", "isAssignableToRole"], ct);
            expansion.Group.DisplayName = g?.DisplayName ?? expansion.Group.DisplayName;
            expansion.Group.OnPremisesSyncEnabled = g?.OnPremisesSyncEnabled ?? false;
            expansion.Group.OnPremisesSamAccountName = g?.OnPremisesSamAccountName;
            expansion.IsAssignableToRole = g?.IsAssignableToRole ?? false;

            var page = await clients.Graph.Groups[groupId].TransitiveMembers.GetAsync(r => r.QueryParameters.Top = 999, ct);
            var members = await clients.Graph.ReadAllAsync<DirectoryObject, DirectoryObjectCollectionResponse>(page, ct);
            foreach (var m in members)
            {
                if (m is User or ServicePrincipal)
                {
                    expansion.Members.Add(ToPrincipal(m));
                }
            }

            var ownersPage = await clients.Graph.Groups[groupId].Owners.GetAsync(r => r.QueryParameters.Top = 100, ct);
            var owners = await clients.Graph.ReadAllAsync<DirectoryObject, DirectoryObjectCollectionResponse>(ownersPage, ct);
            expansion.Owners.AddRange(owners.Select(ToPrincipal));
        }
        catch (Exception ex) when (ex.StatusCode() > 0)
        {
            expansion.Error = ex.ShortMessage();
        }
        return expansion;
    }

    /// <summary>Creates one review line per assignment and principal; group assignments get one extra line per member.</summary>
    public static void BuildLines(ScanContext ctx)
    {
        foreach (var a in ctx.Assignments)
        {
            ctx.Principals.TryGetValue(a.PrincipalId, out var principal);
            principal ??= new PrincipalInfo
            {
                Id = a.PrincipalId, Type = a.PrincipalType, DisplayName = a.PrincipalDisplayName, Upn = a.PrincipalUpn,
                Exists = a.PrincipalType != "Unknown"
            };
            AddLine(ctx, a, principal, null);

            if (a.PrincipalType == "Group" && ctx.Groups.TryGetValue(a.PrincipalId, out var expansion))
            {
                foreach (var member in expansion.Members)
                {
                    AddLine(ctx, a, member, expansion.Group);
                }
            }
        }
    }

    private static void AddLine(ScanContext ctx, RawAssignment a, PrincipalInfo principal, PrincipalInfo? viaGroup)
    {
        var row = new RoleAssignmentSnapshot
        {
            ScanSnapshotId = ctx.SnapshotId,
            AssignmentKey = Ids.StableKey(a.ScopeLevel.ToString(), Ids.NormalizeScope(a.ScopeId), a.RoleKey, principal.Id,
                a.AssignmentType.ToString(), viaGroup?.Id),
            SourceAssignmentId = a.SourceId,
            PrincipalId = principal.Id,
            PrincipalType = principal.Type,
            PrincipalDisplayName = principal.DisplayName,
            PrincipalUpn = principal.Upn,
            PrincipalExists = principal.Exists,
            AssignedViaGroupId = viaGroup?.Id,
            AssignedViaGroupName = viaGroup?.DisplayName,
            AssignedViaGroupOnPremises = viaGroup?.OnPremisesSyncEnabled ?? false,
            AssignedViaGroupSamAccountName = viaGroup?.OnPremisesSamAccountName,
            ScopeLevel = a.ScopeLevel,
            ScopeId = a.ScopeId,
            ScopeDisplayName = a.ScopeDisplayName,
            SubscriptionId = a.SubscriptionId,
            SubscriptionName = a.SubscriptionName,
            RoleDefinitionId = a.RoleDefinitionId,
            RoleName = a.RoleName,
            RoleHasWritePermissions = a.Role?.HasWritePermissions ?? true,
            AssignmentType = a.AssignmentType,
            StartDateTimeUtc = a.StartUtc,
            EndDateTimeUtc = a.EndUtc,
            Justification = a.Justification
        };
        ctx.Lines.Add(new ReviewLine { Raw = a, Row = row, ViaGroup = viaGroup });
    }

    public async Task LoadUsersAsync(ScanContext ctx, CancellationToken ct)
    {
        var userIds = ctx.Lines.Where(l => l.Row.PrincipalType == "User")
            .Select(l => l.Row.PrincipalId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxParallelism));
        var users = await Task.WhenAll(userIds.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await LoadUserAsync(ctx, id, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var u in users)
        {
            ctx.Users[u.UserId] = u;
        }

        if (!ctx.SignInActivityAvailable)
        {
            ctx.Warn("signInActivity is not available (needs AuditLog.Read.All and Entra ID P1/P2). Last Entra sign-in is empty.");
        }
        ctx.Info($"User profiles loaded: {users.Length} ({users.Count(u => !u.ExistsInDirectory)} deleted, {users.Count(u => u.ExistsInDirectory && !u.AccountEnabled)} disabled).");
    }

    private async Task<UserStateSnapshot> LoadUserAsync(ScanContext ctx, string id, CancellationToken ct)
    {
        var state = new UserStateSnapshot { ScanSnapshotId = ctx.SnapshotId, UserId = id };
        User? user = null;
        var notFound = false;
        try
        {
            user = await GetUserAsync(id, ctx.SignInActivityAvailable, ct);
        }
        catch (Exception ex) when (ex.StatusCode() == 404)
        {
            notFound = true;
        }
        catch (Exception ex) when (ex.StatusCode() is 400 or 403 && ctx.SignInActivityAvailable)
        {
            // signInActivity needs AuditLog.Read.All and Entra ID P1/P2. Continue without it.
            ctx.SignInActivityAvailable = false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex.StatusCode() is not (400 or 403))
        {
            // Transient error (throttling, 5xx): one more attempt below.
        }

        if (user is null && !notFound)
        {
            try
            {
                user = await GetUserAsync(id, false, ct);
            }
            catch (Exception ex) when (ex.StatusCode() == 404)
            {
                notFound = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Unknown state is not treated as "left the organization".
                ctx.Warn($"Profile of user {id} could not be read ({ex.ShortMessage()}); the user is treated as active.");
                state.AccountEnabled = true;
                if (ctx.Principals.TryGetValue(id, out var known))
                {
                    state.DisplayName = known.DisplayName;
                    state.Upn = known.Upn;
                }
                return state;
            }
        }

        if (notFound || user is null)
        {
            state.ExistsInDirectory = false;
            state.AccountEnabled = false;
            if (ctx.Principals.TryGetValue(id, out var p))
            {
                state.DisplayName = p.DisplayName;
                state.Upn = p.Upn;
            }
            return state;
        }

        state.Upn = user.UserPrincipalName;
        state.DisplayName = user.DisplayName;
        state.Mail = user.Mail;
        state.AccountEnabled = user.AccountEnabled ?? false;
        state.Department = user.Department;
        state.JobTitle = user.JobTitle;
        state.CreatedDateTimeUtc = user.CreatedDateTime?.UtcDateTime;
        state.OnPremisesSyncEnabled = user.OnPremisesSyncEnabled ?? false;
        state.OnPremisesSamAccountName = user.OnPremisesSamAccountName;

        if (user.Manager is User manager)
        {
            state.ManagerId = manager.Id;
            state.ManagerUpn = manager.UserPrincipalName;
            state.ManagerDisplayName = manager.DisplayName;
        }
        else if (user.Manager is not null)
        {
            state.ManagerId = user.Manager.Id;
        }

        var activity = user.SignInActivity;
        var last = new[] { activity?.LastSignInDateTime, activity?.LastNonInteractiveSignInDateTime, activity?.LastSuccessfulSignInDateTime }
            .Where(d => d.HasValue)
            .Select(d => d!.Value.UtcDateTime)
            .DefaultIfEmpty()
            .Max();
        state.LastEntraSignInUtc = last == default ? null : last;
        return state;
    }

    private Task<User?> GetUserAsync(string id, bool withSignInActivity, CancellationToken ct) =>
        clients.Graph.Users[id].GetAsync(r =>
        {
            r.QueryParameters.Select = withSignInActivity ? UserSelect : UserSelect.Where(s => s != "signInActivity").ToArray();
            r.QueryParameters.Expand = ["manager($select=id,displayName,userPrincipalName)"];
        }, ct);

    /// <summary>Groups with role assignments, stored with the snapshot for the Group Permissions view.</summary>
    public static List<GroupSnapshot> BuildGroupSnapshots(ScanContext ctx) =>
        ctx.Groups.Values.Select(e => new GroupSnapshot
        {
            ScanSnapshotId = ctx.SnapshotId,
            GroupId = e.Group.Id,
            DisplayName = e.Group.DisplayName,
            OnPremisesSyncEnabled = e.Group.OnPremisesSyncEnabled,
            OnPremisesSamAccountName = e.Group.OnPremisesSamAccountName,
            IsAssignableToRole = e.IsAssignableToRole,
            Owners = string.Join("; ", e.Owners.Select(o => string.IsNullOrEmpty(o.Upn) ? o.DisplayName ?? o.Id : $"{o.DisplayName} ({o.Upn})")),
            OwnerCount = e.Owners.Count,
            OwnerIds = string.Join(",", e.Owners.Select(o => o.Id)),
            TotalTransitiveMembers = e.Members.Count,
            UserMembers = e.Members.Count(m => m.Type == "User"),
            ServicePrincipalMembers = e.Members.Count(m => m.Type == "ServicePrincipal"),
            ExpansionError = e.Error
        }).ToList();

    private static PrincipalInfo ToPrincipal(DirectoryObject o) => o switch
    {
        User u => new PrincipalInfo
        {
            Id = Ids.ObjectId(u.Id), Type = "User", DisplayName = u.DisplayName, Upn = u.UserPrincipalName,
            OnPremisesSyncEnabled = u.OnPremisesSyncEnabled ?? false, OnPremisesSamAccountName = u.OnPremisesSamAccountName
        },
        Group g => new PrincipalInfo
        {
            Id = Ids.ObjectId(g.Id), Type = "Group", DisplayName = g.DisplayName,
            OnPremisesSyncEnabled = g.OnPremisesSyncEnabled ?? false, OnPremisesSamAccountName = g.OnPremisesSamAccountName
        },
        ServicePrincipal sp => new PrincipalInfo
        {
            Id = Ids.ObjectId(sp.Id), Type = "ServicePrincipal", DisplayName = sp.DisplayName, AppId = sp.AppId
        },
        _ => new PrincipalInfo { Id = Ids.ObjectId(o.Id), Type = EntraRoleCollector.PrincipalTypeOf(o) }
    };
}
