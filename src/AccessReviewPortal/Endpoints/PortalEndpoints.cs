using System.Text;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Services.Automation;
using AccessReviewPortal.Services.Identity;
using AccessReviewPortal.Services.Reporting;
using AccessReviewPortal.Services.Review;

namespace AccessReviewPortal.Endpoints;

public static class PortalEndpoints
{
    public static void MapPortalEndpoints(this WebApplication app)
    {
        // Commit of the container image (Dockerfile, build argument SOURCE_REVISION). The deployment workflow waits for it,
        // because App Service keeps the old container running until the new one answers.
        var revision = Environment.GetEnvironmentVariable("PORTAL_REVISION");
        app.MapGet("/healthz", (HttpResponse response) =>
        {
            if (!string.IsNullOrEmpty(revision)) response.Headers["X-Portal-Revision"] = revision;
            return Results.Text("ok");
        }).AllowAnonymous();

        var group = app.MapGroup("").RequireAuthorization(PortalPolicies.Reviewer);

        group.MapGet("/export/{snapshotId:int}/xlsx", async (int snapshotId, HttpRequest request, SnapshotService snapshots,
            GroupPermissionsService groups, ExcelExportService excel, CancellationToken ct) =>
        {
            var snapshot = await snapshots.GetSnapshotAsync(snapshotId, ct);
            if (snapshot is null) return Results.NotFound();
            var filter = FilterFromQuery(request.Query);
            var prefill = !string.Equals(request.Query["prefill"], "false", StringComparison.OrdinalIgnoreCase);
            var rows = await snapshots.GetRowsAsync(snapshotId, filter, ct);
            var users = await snapshots.GetUsersAsync(snapshotId, ct);
            var groupSummaries = await groups.GetGroupsAsync(snapshotId, ct);
            var bytes = excel.Build(snapshot, rows, users, prefill, groupSummaries);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"AccessReview_{FileSafe(snapshot.Label)}_{snapshot.CreatedAtUtc:yyyyMMdd}.xlsx");
        });

        group.MapGet("/export/{snapshotId:int}/remediation.ps1", async (int snapshotId, SnapshotService snapshots, IConfiguration config, CancellationToken ct) =>
        {
            var snapshot = await snapshots.GetSnapshotAsync(snapshotId, ct);
            if (snapshot is null) return Results.NotFound();
            var rows = await snapshots.GetRowsAsync(snapshotId, new ReviewFilter { Decision = "Removed" }, ct);
            var decisions = await snapshots.GetMemberDecisionsAsync(snapshotId, ct);
            var groups = await snapshots.GetGroupsAsync(snapshotId, ct);
            var script = RemediationService.BuildScript(snapshot, rows, decisions, groups, config["AzureAd:TenantId"]);
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(script)).ToArray(), "text/plain",
                $"Remediation_{FileSafe(snapshot.Label)}_{snapshot.CreatedAtUtc:yyyyMMdd}.ps1");
        });

        group.MapGet("/campaigns/{campaignId:int}/report.xlsx", async (int campaignId, AutomationService automation, CancellationToken ct) =>
        {
            var report = await automation.GetReportAsync(campaignId, ct);
            return report is null
                ? Results.NotFound()
                : Results.File(report.Value.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", report.Value.FileName);
        });

        group.MapGet("/reports/xlsx", async (HttpContext http, KpiReportService reports, TimeProvider time, CancellationToken ct) =>
        {
            var period = ReportPeriod.Parse(http.Request.Query["from"], http.Request.Query["to"], DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
            var report = await reports.BuildAsync(period, http.User.GetUpn(), ct);
            return Results.File(KpiReportExcel.Build(report), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                KpiReportExcel.FileName(period));
        });
    }

    public static ReviewFilter FilterFromQuery(IQueryCollection q)
    {
        var filter = new ReviewFilter
        {
            SubscriptionId = NullIfEmpty(q["sub"]),
            RoleName = NullIfEmpty(q["role"]),
            AssignmentFilter = NullIfEmpty(q["type"]) ?? "All",
            Recommendation = NullIfEmpty(q["rec"]) ?? "All",
            Decision = NullIfEmpty(q["dec"]) ?? "All",
            PrincipalType = NullIfEmpty(q["ptype"]),
            Search = NullIfEmpty(q["q"]),
            Defender = NullIfEmpty(q["defender"]) ?? "All",
            OnlyNewSinceBaseline = string.Equals(q["new"], "true", StringComparison.OrdinalIgnoreCase)
        };
        if (Enum.TryParse<ScopeLevel>(q["scope"], out var scope)) filter.ScopeLevel = scope;
        if (Enum.TryParse<UsageEvidenceStatus>(q["usage"], out var usage)) filter.Usage = usage;
        return filter;
    }

    public static string ToQuery(ReviewFilter f)
    {
        var parts = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value) && value != "All") parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
        Add("scope", f.ScopeLevel?.ToString());
        Add("sub", f.SubscriptionId);
        Add("role", f.RoleName);
        Add("type", f.AssignmentFilter);
        Add("usage", f.Usage?.ToString());
        Add("rec", f.Recommendation);
        Add("dec", f.Decision);
        Add("ptype", f.PrincipalType);
        Add("q", f.Search);
        Add("defender", f.Defender);
        if (f.OnlyNewSinceBaseline) Add("new", "true");
        return string.Join("&", parts);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string FileSafe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
}
