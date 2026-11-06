using AccessReviewPortal.Components;
using AccessReviewPortal.Data;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Endpoints;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services;
using AccessReviewPortal.Services.Automation;
using AccessReviewPortal.Services.Azure;
using AccessReviewPortal.Services.Discovery;
using AccessReviewPortal.Services.Identity;
using AccessReviewPortal.Services.Reporting;
using AccessReviewPortal.Services.Review;
using AccessReviewPortal.Services.Scanning;
using AccessReviewPortal.Services.Usage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AzureAdOptions>(builder.Configuration.GetSection(AzureAdOptions.Section));
builder.Services.Configure<ReviewSettingsOptions>(builder.Configuration.GetSection(ReviewSettingsOptions.Section));
builder.Services.Configure<PortalOptions>(builder.Configuration.GetSection(PortalOptions.Section));
builder.Services.Configure<PortalAuthOptions>(builder.Configuration.GetSection(PortalAuthOptions.Section));
builder.Services.Configure<PortalAuthorizationOptions>(builder.Configuration.GetSection(PortalAuthorizationOptions.Section));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
builder.Services.Configure<DiscoveryOptions>(builder.Configuration.GetSection(DiscoveryOptions.Section));
builder.Services.Configure<DefenderCspmOptions>(builder.Configuration.GetSection(DefenderCspmOptions.Section));
builder.Services.Configure<AutomationOptions>(builder.Configuration.GetSection(AutomationOptions.Section));

var authOptions = builder.Configuration.GetSection(PortalAuthOptions.Section).Get<PortalAuthOptions>() ?? new PortalAuthOptions();
var authorizationOptions = builder.Configuration.GetSection(PortalAuthorizationOptions.Section).Get<PortalAuthorizationOptions>() ?? new PortalAuthorizationOptions();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings__DefaultConnection is not configured.");
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

builder.Services.AddHttpClient("arm", c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient("graph", c => c.Timeout = TimeSpan.FromMinutes(3));
builder.Services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);

// Login cookies and the MSAL token cache survive container restarts and work across instances:
// Data Protection keys and the distributed cache are stored in PostgreSQL.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AppDbContext>()
    .SetApplicationName("AccessReviewPortal");
builder.Services.AddSingleton<IDistributedCache, PostgresDistributedCache>();

builder.Services.AddPortalAuthentication(builder.Configuration, authOptions, authorizationOptions);
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton<AzureClients>();
builder.Services.AddSingleton<ArmRestClient>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<SnapshotService>();
builder.Services.AddSingleton<GroupPermissionsService>();
builder.Services.AddSingleton<ExcelExportService>();
builder.Services.AddSingleton<KpiReportService>();
builder.Services.AddSingleton<ScanJobManager>();
builder.Services.AddSingleton<DeviceCodeLoginService>();

builder.Services.AddScoped<UserTokenService>();
builder.Services.AddScoped<UserCapabilityChecker>();
builder.Services.AddScoped<ConsentRedirector>();
builder.Services.AddScoped<PendingChangesNotifier>();
builder.Services.AddScoped<ArmCollector>();
builder.Services.AddScoped<EntraRoleCollector>();
builder.Services.AddScoped<DirectoryCollector>();
builder.Services.AddScoped<AccessReviewCollector>();
builder.Services.AddScoped<DefenderCspmCollector>();
builder.Services.AddScoped<SubscriptionDiscoveryService>();
builder.Services.AddScoped<ActivityLogReader>();
builder.Services.AddScoped<SignInReader>();
builder.Services.AddScoped<DirectoryAuditReader>();
builder.Services.AddScoped<LogAnalyticsReader>();
builder.Services.AddScoped<RoleUsageAnalyzerService>();
builder.Services.AddScoped<AccessReviewScannerService>();
builder.Services.AddScoped<RemediationService>();
builder.Services.AddScoped<DiagnosticsService>();
builder.Services.AddScoped<AutomationService>();
builder.Services.AddHostedService<ScheduledReviewWorker>();

var razor = builder.Services.AddRazorComponents().AddInteractiveServerComponents();
if (authOptions.IsEntraId)
{
    razor.AddMicrosoftIdentityConsentHandler();
}

var app = builder.Build();

await PrepareDatabaseAsync(app);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
if (!string.IsNullOrEmpty(app.Configuration["ASPNETCORE_HTTPS_PORTS"]))
{
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Behind App Service / Container Apps / a reverse proxy set ASPNETCORE_FORWARDEDHEADERS_ENABLED=true so the
// OpenID Connect redirect URI uses the public https address.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapAccountEndpoints();
app.MapPortalEndpoints();
if (authOptions.IsEntraId)
{
    app.MapControllers();
}
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static async Task PrepareDatabaseAsync(WebApplication app)
{
    var apply = app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", true);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var factory = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            if (apply)
            {
                await db.Database.MigrateAsync();
            }
            else
            {
                var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
                if (pending.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"The database schema is not up to date ({string.Join(", ", pending)}). Run the migration bundle (efbundle) first.");
                }
            }

            // A scan or campaign cannot survive a restart of the container.
            var interrupted = await db.ScanSnapshots.Where(s => s.Status == ScanStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, ScanStatus.Failed)
                    .SetProperty(x => x.Error, "Interrupted: the application was restarted during the scan."));
            await db.ReviewCampaigns.Where(c => c.Status == CampaignStatus.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, CampaignStatus.Failed)
                    .SetProperty(x => x.Error, "Interrupted: the application was restarted during the campaign."));
            app.Logger.LogInformation("Database schema is up to date (migrations applied at start-up: {Apply}). Interrupted scans marked as failed: {Count}.",
                apply, interrupted);
            return;
        }
        catch (Exception ex) when (attempt < 10 && ex is not InvalidOperationException)
        {
            app.Logger.LogWarning("Database is not ready ({Message}). Retry {Attempt}/10.", ex.Message.Split('\n')[0], attempt);
            await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
        }
    }
}
