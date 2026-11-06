using AccessReviewPortal.Domain;
using ClosedXML.Excel;

namespace AccessReviewPortal.Services.Reporting;

/// <summary>Excel version of the KPI dashboard: summary, reviewers, departments, snapshots, exceptions and removals.</summary>
public static class KpiReportExcel
{
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#D9D9D9");

    public static string FileName(ReportPeriod period) =>
        $"AccessReview_KPI_{ReportPeriod.Iso(period.From).Replace("-", "")}-{ReportPeriod.Iso(period.To).Replace("-", "")}.xlsx";

    public static byte[] Build(KpiReport report)
    {
        using var wb = new XLWorkbook();
        BuildSummary(wb, report);

        Table(wb, "Reviewers",
            ["Reviewer", "Decisions", "Approve Removal", "Keep", "Exceptions", "Users approved for removal", "Users cleaned up (Apply)", "Changes applied", "Last activity (UTC)"],
            report.Reviewers,
            r => [r.Reviewer, r.Decisions, r.ApprovedRemoval, r.Kept, r.Exceptions, r.UsersApprovedForRemoval, r.UsersCleanedUp, r.ChangesApplied, r.LastActivityUtc]);

        Table(wb, "Departments",
            ["Department", "Users with roles", "Users with unused roles", "% of users", "Unused assignments", "Users with unused roles at start", "Users cleaned up", "Users with exceptions"],
            report.Departments,
            d => [d.Department, d.UsersWithRoles, d.UsersWithUnusedRoles, d.UnusedPercent, d.UnusedAssignments, d.UsersWithUnusedRolesAtStart, d.UsersCleanedUp, d.UsersWithExceptions]);

        Table(wb, "Snapshots",
            ["Snapshot", "Label", "Scanned (UTC)", "Threshold (days)", "Users with roles", "Users with unused roles", "% of users", "Unused user assignments",
             "Service principals with roles", "Service principals with unused roles", "Rows to review", "Rows reviewed", "Changes applied"],
            report.Snapshots,
            s => [s.Snapshot.Id, s.Snapshot.Label, s.Snapshot.CreatedAtUtc, s.Snapshot.ThresholdDays, s.UsersWithRoles, s.UsersWithUnusedRoles, s.UnusedPercent,
                  s.UnusedUserAssignments, s.ServicePrincipalsWithRoles, s.ServicePrincipalsWithUnusedRoles, s.RowsToReview, s.RowsReviewed, s.ChangesApplied]);

        Table(wb, "Activity",
            ["From", "To", "Approve Removal", "Keep", "Exceptions (in Keep)", "Changes applied", "Users cleaned up (to date)"],
            report.Activity,
            a => [ReportPeriod.Iso(a.Start), ReportPeriod.Iso(a.End), a.ApprovedRemoval, a.Kept, a.Exceptions, a.ChangesApplied, a.UsersCleanedUpToDate]);

        Table(wb, "Exceptions",
            ["Display Name", "UPN", "Principal Type", "Department", "Access", "Usage Evidence", "Last used (UTC)", "Recommendation", "Reason", "Kept by", "Decided (UTC)", "Justification", "Snapshot"],
            report.Exceptions,
            f => [f.DisplayName, f.Upn, f.PrincipalType, f.Department, f.Access, f.Usage.ToString(), f.LastUsedUtc, DisplayText.Of(f.Recommendation), f.Reason,
                  f.ReviewedBy ?? KpiCalculator.NotRecorded, f.ReviewedAtUtc, f.Comment, f.SnapshotId]);

        Table(wb, "Removals",
            ["Display Name", "UPN", "Principal Type", "Department", "Access removed", "Approved by", "Approved (UTC)", "Applied by", "Applied (UTC)", "Snapshot"],
            report.Removals,
            f => [f.DisplayName, f.Upn, f.PrincipalType, f.Department, f.ChangeText, f.ReviewedBy ?? KpiCalculator.NotRecorded, f.ReviewedAtUtc,
                  f.AppliedBy ?? KpiCalculator.NotRecorded, f.AppliedAtUtc, f.SnapshotId]);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void BuildSummary(XLWorkbook wb, KpiReport report)
    {
        var ws = wb.Worksheets.Add("Summary");
        ws.Cell(1, 1).Value = "Access review KPI report";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;

        var r = 3;
        void Line(string name, string value)
        {
            ws.Cell(r, 1).Value = name;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = value;
            r++;
        }

        Line("Period (UTC)", report.Period.ToString());
        Line("Generated (UTC)", $"{report.GeneratedAtUtc:yyyy-MM-dd HH:mm}" + (string.IsNullOrEmpty(report.GeneratedBy) ? "" : $" by {report.GeneratedBy}"));
        Line("State from snapshot", report.End?.Snapshot.Text ?? "No snapshot up to the end of the period");
        if (report.End is { } endSnapshot) Line("Inactivity threshold", $"{endSnapshot.Snapshot.ThresholdDays} days");
        Line("Start of the period", report.Start?.Snapshot.Text ?? "-");

        r++;
        var header = r;
        ws.Cell(r, 1).Value = "Metric";
        ws.Cell(r, 2).Value = "Value";
        ws.Cell(r, 3).Value = "How it is counted";
        ws.Range(r, 1, r, 3).Style.Font.Bold = true;
        ws.Range(r, 1, r, 3).Style.Fill.BackgroundColor = HeaderFill;
        r++;

        void Kpi(string name, int value, string note)
        {
            ws.Cell(r, 1).Value = name;
            ws.Cell(r, 2).Value = value;
            ws.Cell(r, 3).Value = note;
            r++;
        }

        if (report.End is { } e)
        {
            var at = $"snapshot #{e.Snapshot.Id}";
            Kpi("Users with role assignments", e.UsersWithRoles, at);
            Kpi("Users with unused roles", e.UsersWithUnusedRoles,
                $"{e.UnusedPercent}% of users with roles; at least one assignment without use in {e.Snapshot.ThresholdDays} days; {at}");
            if (report.Start is { } s)
            {
                Kpi("Users with unused roles at the start", s.UsersWithUnusedRoles, $"snapshot #{s.Snapshot.Id}");
            }
            Kpi("Service principals with unused roles", e.ServicePrincipalsWithUnusedRoles, $"of {e.ServicePrincipalsWithRoles} service principals with roles; {at}");
        }
        Kpi("Reviewer decisions", report.Decisions, "Approve Removal, Keep, Remove from Group and Keep in Group made in the period");
        Kpi("Users cleaned up", report.UsersCleanedUp, "Users with at least one change applied in the period");
        Kpi("Changes applied", report.ChangesApplied, $"{report.RoleAssignmentsRemoved} role assignments, {report.GroupMembershipsRemoved} group memberships");
        if (report.ServicePrincipalsCleanedUp > 0)
        {
            Kpi("Service principals cleaned up", report.ServicePrincipalsCleanedUp, "Service principals with at least one change applied in the period");
        }
        Kpi("Users with exceptions", report.UsersWithExceptions, "Kept although the role was unused or removal was recommended");
        Kpi("Exceptions", report.Exceptions.Count, "Role assignments and group memberships kept as an exception (once per item)");
        Kpi("Exceptions without justification", report.ExceptionsWithoutJustification, "No reviewer comment");
        Kpi("Approved, waiting for Apply", report.ChangesWaitingForApply, "Changes approved in the period and not applied yet");
        if (report.End is { } p)
        {
            Kpi("Review progress", p.RowsReviewed, $"of {p.RowsToReview} rows with a Remove or Review recommendation ({p.ReviewedPercent}%); snapshot #{p.Snapshot.Id}");
        }

        ws.Range(header, 1, r - 1, 3).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Column(1).Width = 40;
        ws.Column(2).Width = 12;
        ws.Column(3).Width = 90;
        ws.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
    }

    private static void Table<T>(XLWorkbook wb, string name, string[] columns, IEnumerable<T> items, Func<T, object?[]> values)
    {
        var ws = wb.Worksheets.Add(name);
        for (var i = 0; i < columns.Length; i++)
        {
            ws.Cell(1, i + 1).Value = columns[i];
        }
        var header = ws.Range(1, 1, 1, columns.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = HeaderFill;
        ws.SheetView.FreezeRows(1);

        var r = 2;
        foreach (var item in items)
        {
            var v = values(item);
            for (var c = 0; c < v.Length; c++)
            {
                Put(ws.Cell(r, c + 1), v[c]);
            }
            r++;
        }

        ws.Range(1, 1, Math.Max(1, r - 1), columns.Length).SetAutoFilter();
        ws.Columns(1, columns.Length).AdjustToContents(1, Math.Min(r - 1, 500));
        foreach (var col in ws.Columns(1, columns.Length))
        {
            if (col.Width > 60) col.Width = 60;
        }
    }

    private static void Put(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case int i:
                cell.Value = i;
                break;
            case DateTime t:
                cell.Value = t;
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }
}
