using AccessReviewPortal.Data.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AccessReviewPortal.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<ScanSnapshot> ScanSnapshots => Set<ScanSnapshot>();
    public DbSet<UserStateSnapshot> UserStateSnapshots => Set<UserStateSnapshot>();
    public DbSet<RoleAssignmentSnapshot> RoleAssignmentSnapshots => Set<RoleAssignmentSnapshot>();
    public DbSet<ScanLogEntry> ScanLogEntries => Set<ScanLogEntry>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<GroupSnapshot> GroupSnapshots => Set<GroupSnapshot>();
    public DbSet<GroupMemberDecision> GroupMemberDecisions => Set<GroupMemberDecision>();
    public DbSet<MonitoredSubscription> MonitoredSubscriptions => Set<MonitoredSubscription>();
    public DbSet<ReviewCampaign> ReviewCampaigns => Set<ReviewCampaign>();
    public DbSet<DistributedCacheEntry> DistributedCache => Set<DistributedCacheEntry>();

    /// <summary>ASP.NET Core Data Protection keys: login cookies stay valid after a container restart or scale-out.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as text so the database stays readable for ad-hoc SQL reports.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ScanSnapshot>(e =>
        {
            e.Property(x => x.Label).HasMaxLength(200);
            e.HasMany(x => x.Users).WithOne().HasForeignKey(x => x.ScanSnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Assignments).WithOne().HasForeignKey(x => x.ScanSnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Log).WithOne().HasForeignKey(x => x.ScanSnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Groups).WithOne().HasForeignKey(x => x.ScanSnapshotId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserStateSnapshot>(e =>
        {
            e.HasIndex(x => new { x.ScanSnapshotId, x.UserId }).IsUnique();
        });

        // Composite indexes for large tenants: every review query filters by snapshot first.
        b.Entity<RoleAssignmentSnapshot>(e =>
        {
            e.HasIndex(x => x.ScanSnapshotId);
            e.HasIndex(x => new { x.ScanSnapshotId, x.PrincipalId });
            e.HasIndex(x => new { x.ScanSnapshotId, x.AssignmentKey });
            e.HasIndex(x => new { x.ScanSnapshotId, x.ScopeLevel });
            e.HasIndex(x => new { x.ScanSnapshotId, x.SubscriptionId });
            e.HasIndex(x => new { x.ScanSnapshotId, x.AssignedViaGroupId });
            e.HasIndex(x => new { x.ScanSnapshotId, x.RecommendedAction });
            e.HasIndex(x => new { x.ScanSnapshotId, x.ReviewerDecision });
            // Small partial index for the "Changes Pending" count over all snapshots.
            e.HasIndex(x => x.ScanSnapshotId, "IX_RoleAssignmentSnapshots_ApprovedRemovals").HasFilter("\"ReviewerDecision\" = 'Removed'");
            // KPI report: decisions and applied changes in a period. Only rows with a decision or a change are indexed.
            e.HasIndex(x => x.ReviewedAtUtc, "IX_RoleAssignmentSnapshots_ReviewedAtUtc").HasFilter("\"ReviewedAtUtc\" IS NOT NULL");
            e.HasIndex(x => x.RemediatedAtUtc, "IX_RoleAssignmentSnapshots_RemediatedAtUtc").HasFilter("\"RemediatedAtUtc\" IS NOT NULL");
        });

        b.Entity<GroupSnapshot>(e => e.HasIndex(x => new { x.ScanSnapshotId, x.GroupId }).IsUnique());

        b.Entity<GroupMemberDecision>(e =>
        {
            e.HasIndex(x => new { x.ScanSnapshotId, x.GroupId, x.MemberId }).IsUnique();
            e.HasOne<ScanSnapshot>().WithMany().HasForeignKey(x => x.ScanSnapshotId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ScanLogEntry>(e => e.HasIndex(x => x.ScanSnapshotId));

        b.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });

        b.Entity<MonitoredSubscription>(e =>
        {
            e.HasKey(x => x.SubscriptionId);
            e.Property(x => x.SubscriptionId).HasMaxLength(64);
        });

        b.Entity<ReviewCampaign>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        b.Entity<DistributedCacheEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(449);
            e.HasIndex(x => x.ExpiresAtTime);
        });
    }
}

/// <summary>Used by "dotnet ef" and the migration bundle (efbundle). The bundle reads ConnectionStrings__DefaultConnection from the environment, or takes --connection.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") ?? "Host=localhost;Database=accessreview;Username=postgres;Password=postgres")
            .Options;
        return new AppDbContext(options);
    }
}
