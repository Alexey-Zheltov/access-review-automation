using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using ClosedXML.Excel;

namespace AccessReviewPortal.Services.Review;

/// <summary>Builds the quarterly review workbook in a review format (first sheet) plus details, groups and summary sheets.</summary>
public sealed class ExcelExportService
{
    public static readonly string[] CustomerColumns =
    [
        "Display Name", "UPN", "Scope Level", "Scope / Resource Name", "Role", "Assignment Type",
        "Expiration / End Date", "Usage Evidence (90d)", "Defender CSPM (CIEM)", "Department / Manager", "Justification / Reason", "Action"
    ];

    private static readonly XLColor RemovedFill = XLColor.FromHtml("#FFC000");
    private static readonly XLColor ReviewFill = XLColor.FromHtml("#FFF2CC");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#D9D9D9");

    public byte[] Build(ScanSnapshot snapshot, IReadOnlyList<RoleAssignmentSnapshot> rows,
        IReadOnlyDictionary<string, UserStateSnapshot> users, bool prefillFromRecommendation, IReadOnlyList<GroupSummary>? groups = null)
    {
        using var wb = new XLWorkbook();
        BuildReviewSheet(wb, rows, users, prefillFromRecommendation);
        BuildDetailsSheet(wb, rows, users);
        if (groups is not null)
        {
            BuildGroupSheet(wb, groups);
        }
        BuildSummarySheet(wb, snapshot, rows, prefillFromRecommendation);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static string DefenderText(RoleAssignmentSnapshot r) =>
        r.DefenderCspmStatus == DefenderCspmStatus.None
            ? "None"
            : string.Join(", ", (r.DefenderCspmFindings ?? r.DefenderCspmStatus.ToString()).Split(',')
                .Select(f => Enum.TryParse<DefenderCspmStatus>(f, out var s) ? DisplayText.Of(s) : f));

    public static string ActionText(RoleAssignmentSnapshot r, bool prefill) => r.ReviewerDecision switch
    {
        ReviewerDecision.Removed => "Removed",
        ReviewerDecision.NoAction => "No Action",
        _ when !prefill => "Pending",
        _ => r.RecommendedAction switch
        {
            RecommendedAction.Remove => "Removed",
            RecommendedAction.NoAction => "No Action",
            _ => "Review"
        }
    };

    public static string UsageText(RoleAssignmentSnapshot r) =>
        r.UsageEvidenceStatus.ToString() + (r.LastRoleUsedUtc is null
            ? " - no activity found"
            : $" - last {r.LastRoleUsedUtc:yyyy-MM-dd} ({r.DaysSinceLastActivity} days ago)");

    public static string AssignmentTypeText(RoleAssignmentSnapshot r) =>
        DisplayText.Of(r.AssignmentType) + (r.AssignedViaGroupName is null ? "" : $" (via group: {r.AssignedViaGroupName})");

    public static string ScopeText(RoleAssignmentSnapshot r) => r.ScopeLevel switch
    {
        ScopeLevel.ResourceGroup or ScopeLevel.Resource => $"{r.SubscriptionName} / {r.ScopeDisplayName}",
        _ => r.ScopeDisplayName ?? r.ScopeId
    };

    private static void BuildReviewSheet(XLWorkbook wb, IReadOnlyList<RoleAssignmentSnapshot> rows,
        IReadOnlyDictionary<string, UserStateSnapshot> users, bool prefill)
    {
        var ws = wb.Worksheets.Add("Access Review");
        WriteHeader(ws, CustomerColumns);

        var r = 2;
        foreach (var row in rows)
        {
            users.TryGetValue(row.PrincipalId, out var u);
            var action = ActionText(row, prefill);
            var reason = action == "No Action"
                ? row.Justification ?? row.RecommendationReason ?? ""
                : row.ReviewerComment ?? row.RecommendationReason ?? row.Justification ?? "";

            ws.Cell(r, 1).Value = row.PrincipalDisplayName ?? u?.DisplayName ?? row.PrincipalId;
            ws.Cell(r, 2).Value = row.PrincipalUpn ?? u?.Upn ?? (row.PrincipalType == "User" ? "" : row.PrincipalType);
            ws.Cell(r, 3).Value = DisplayText.Of(row.ScopeLevel);
            ws.Cell(r, 4).Value = ScopeText(row);
            ws.Cell(r, 5).Value = row.RoleName;
            ws.Cell(r, 6).Value = AssignmentTypeText(row);
            ws.Cell(r, 7).Value = row.EndDateTimeUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? "Permanent";
            ws.Cell(r, 8).Value = UsageText(row);
            ws.Cell(r, 9).Value = DefenderText(row);
            ws.Cell(r, 10).Value = u is null ? "" : RecommendationEngine.Describe(u);
            ws.Cell(r, 11).Value = reason;
            ws.Cell(r, 12).Value = action;

            if (action == "Removed")
            {
                ws.Range(r, 1, r, CustomerColumns.Length).Style.Fill.BackgroundColor = RemovedFill;
            }
            else if (action == "Review")
            {
                ws.Range(r, 1, r, CustomerColumns.Length).Style.Fill.BackgroundColor = ReviewFill;
            }
            r++;
        }
        Finish(ws, CustomerColumns.Length, r - 1);
    }

    private static void BuildDetailsSheet(XLWorkbook wb, IReadOnlyList<RoleAssignmentSnapshot> rows, IReadOnlyDictionary<string, UserStateSnapshot> users)
    {
        string[] columns =
        [
            "Display Name", "UPN", "Principal Type", "Principal Id", "Account Enabled", "Department", "Manager",
            "Via Group", "Via Group Synced From AD", "Scope Level", "Scope Id", "Subscription", "Role", "Role Can Write",
            "Assignment Type", "Start", "End", "Justification", "Usage Status", "Last Role Used", "Days Since Last Activity",
            "Last PIM Activation", "Last Write Operation", "Last Write", "Write Evidence Source", "Last Azure/Admin Sign-in",
            "Usage Details", "Native Review Decision", "Native Review Details", "Defender CSPM (CIEM)", "Defender CSPM Findings",
            "Defender CSPM Details", "New Since Baseline", "Recommendation", "Recommendation Reason", "Reviewer Decision", "Reviewer Comment",
            "Reviewed By", "Reviewed At", "Remediation Status", "Remediated By", "Remediated At", "Assignment Id"
        ];
        var ws = wb.Worksheets.Add("Details");
        WriteHeader(ws, columns);

        var r = 2;
        foreach (var row in rows)
        {
            users.TryGetValue(row.PrincipalId, out var u);
            object?[] values =
            [
                row.PrincipalDisplayName, row.PrincipalUpn, row.PrincipalType, row.PrincipalId,
                u is null ? "" : u.ExistsInDirectory ? (u.AccountEnabled ? "Yes" : "No") : "Deleted",
                u?.Department, u?.ManagerDisplayName ?? u?.ManagerUpn,
                row.AssignedViaGroupName, row.AssignedViaGroupId is null ? "" : row.AssignedViaGroupOnPremises ? "Yes" : "No",
                DisplayText.Of(row.ScopeLevel), row.ScopeId, row.SubscriptionName, row.RoleName, row.RoleHasWritePermissions ? "Yes" : "No",
                DisplayText.Of(row.AssignmentType), row.StartDateTimeUtc, row.EndDateTimeUtc, row.Justification,
                row.UsageEvidenceStatus.ToString(), row.LastRoleUsedUtc, row.DaysSinceLastActivity,
                row.LastPimActivationUtc, row.LastWriteOperation, row.LastWriteEvidenceUtc, row.WriteEvidenceSource, row.LastSignInUtc,
                row.UsageEvidenceDetails, row.NativeSelfReviewDecision.ToString(), row.NativeReviewDetails,
                DisplayText.Of(row.DefenderCspmStatus), row.DefenderCspmFindingCount, row.DefenderCspmDetails, row.IsNewSinceBaseline ? "Yes" : "No",
                DisplayText.Of(row.RecommendedAction), row.RecommendationReason, DisplayText.Of(row.ReviewerDecision), row.ReviewerComment,
                row.ReviewedByUpn, row.ReviewedAtUtc, row.RemediationStatus, row.RemediatedByUpn, row.RemediatedAtUtc, row.SourceAssignmentId
            ];
            for (var c = 0; c < values.Length; c++)
            {
                ws.Cell(r, c + 1).Value = values[c] switch
                {
                    null => Blank.Value,
                    DateTime d => d,
                    int i => i,
                    _ => values[c]!.ToString()
                };
                if (values[c] is DateTime) ws.Cell(r, c + 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            }
            r++;
        }
        Finish(ws, columns.Length, r - 1);
    }

    private static void BuildGroupSheet(XLWorkbook wb, IReadOnlyList<GroupSummary> groups)
    {
        string[] columns =
        [
            "Group", "Object Id", "Source", "Owners", "Role", "Scope Level", "Scope / Resource Name", "Assignment Type",
            "Total Transitive Members", "Active Members (90d)", "Sign-in Only Members", "Unused Members (90d)", "Usage %",
            "Movers / Disabled Members", "Defender CSPM Flags", "Group Assignment Decision", "Members Marked Remove from Group"
        ];
        var ws = wb.Worksheets.Add("Group Permissions");
        WriteHeader(ws, columns);

        var r = 2;
        foreach (var g in groups)
        {
            var removeMembers = g.Members.Count(m => m.Decision?.Decision == GroupMemberDecisionType.RemoveFromGroup);
            var defenderFlags = string.Join("; ", new[] { g.GroupDefenderFlags, g.DefenderFlaggedMembers > 0 ? $"{g.DefenderFlaggedMembers} member(s) flagged" : null }
                .Where(x => !string.IsNullOrEmpty(x)));
            var assignments = g.Assignments.Count > 0 ? g.Assignments.Cast<GroupAssignmentInfo?>() : [null];
            foreach (var a in assignments)
            {
                object?[] values =
                [
                    g.Group.DisplayName, g.Group.GroupId, g.Source, g.Group.Owners,
                    a?.GroupRow.RoleName ?? "(roles of a nested parent group)",
                    a is null ? "" : DisplayText.Of(a.GroupRow.ScopeLevel),
                    a is null ? "" : ScopeText(a.GroupRow),
                    a is null ? "" : DisplayText.Of(a.GroupRow.AssignmentType),
                    g.TotalMembers,
                    a?.ActiveMembers ?? g.ActiveMembers,
                    a?.SignInOnlyMembers ?? g.SignInOnlyMembers,
                    a?.UnusedMembers ?? g.UnusedMembers,
                    a?.UsagePercent,
                    g.MoversOrDisabled,
                    defenderFlags,
                    a is null ? "" : DisplayText.Of(a.GroupRow.ReviewerDecision),
                    removeMembers
                ];
                for (var c = 0; c < values.Length; c++)
                {
                    ws.Cell(r, c + 1).Value = values[c] switch
                    {
                        null => Blank.Value,
                        int i => i,
                        _ => values[c]!.ToString()
                    };
                }
                if (a?.GroupRow.ReviewerDecision == ReviewerDecision.Removed)
                {
                    ws.Range(r, 1, r, columns.Length).Style.Fill.BackgroundColor = RemovedFill;
                }
                r++;
            }
        }
        Finish(ws, columns.Length, r - 1);
    }

    private static void BuildSummarySheet(XLWorkbook wb, ScanSnapshot snapshot, IReadOnlyList<RoleAssignmentSnapshot> rows, bool prefill)
    {
        var ws = wb.Worksheets.Add("Summary");
        var r = 1;
        void Line(string name, object? value)
        {
            ws.Cell(r, 1).Value = name;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = value?.ToString() ?? "";
            r++;
        }

        Line("Snapshot", $"#{snapshot.Id} {snapshot.Label}");
        Line("Scanned (UTC)", snapshot.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm"));
        Line("Baseline snapshot", snapshot.BaselineSnapshotId?.ToString() ?? "none");
        Line("Inactivity threshold (days)", snapshot.InactivityThresholdDays);
        Line("Rows exported", rows.Count);
        Line("Action column", prefill ? "Reviewer decision, otherwise pre-filled from the recommendation" : "Reviewer decision only");
        r++;
        foreach (var g in rows.GroupBy(x => x.ScopeLevel).OrderBy(g => g.Key)) Line($"Level: {DisplayText.Of(g.Key)}", g.Count());
        r++;
        foreach (var g in rows.GroupBy(x => x.UsageEvidenceStatus).OrderBy(g => g.Key)) Line($"Usage: {g.Key}", g.Count());
        r++;
        foreach (var g in rows.Where(x => x.DefenderCspmStatus != DefenderCspmStatus.None).GroupBy(x => x.DefenderCspmStatus).OrderBy(g => g.Key))
        {
            Line($"Defender CSPM: {DisplayText.Of(g.Key)}", g.Count());
        }
        r++;
        foreach (var g in rows.Where(x => x.RecommendedAction != RecommendedAction.NoAction)
                     .GroupBy(x => $"{DisplayText.Of(x.RecommendedAction)}: {Reasons.GroupOf(x.RecommendationReason)}")
                     .OrderByDescending(g => g.Count()))
        {
            Line(g.Key, g.Count());
        }
        r++;
        Line("Usage sources", snapshot.UsageSources?.Replace("\n", "; "));
        ws.Column(1).Width = 60;
        ws.Column(2).Width = 80;
    }

    private static void WriteHeader(IXLWorksheet ws, IReadOnlyList<string> columns)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            ws.Cell(1, i + 1).Value = columns[i];
        }
        var header = ws.Range(1, 1, 1, columns.Count);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = HeaderFill;
        ws.SheetView.FreezeRows(1);
    }

    private static void Finish(IXLWorksheet ws, int columns, int lastRow)
    {
        ws.Range(1, 1, Math.Max(1, lastRow), columns).SetAutoFilter();
        ws.Columns(1, columns).AdjustToContents(1, Math.Min(lastRow, 500));
        foreach (var col in ws.Columns(1, columns))
        {
            if (col.Width > 60) col.Width = 60;
        }
    }
}
