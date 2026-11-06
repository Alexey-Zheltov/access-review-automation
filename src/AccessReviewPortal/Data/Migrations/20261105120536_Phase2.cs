using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AccessReviewPortal.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StartedByUpn",
                table: "ScanSnapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Trigger",
                table: "ScanSnapshots",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<string>(
                name: "DefenderCspmDetails",
                table: "RoleAssignmentSnapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefenderCspmFindingCount",
                table: "RoleAssignmentSnapshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DefenderCspmFindings",
                table: "RoleAssignmentSnapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefenderCspmStatus",
                table: "RoleAssignmentSnapshots",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "RemediatedByUpn",
                table: "RoleAssignmentSnapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedByUpn",
                table: "RoleAssignmentSnapshots",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DistributedCache",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(449)", maxLength: 449, nullable: false),
                    Value = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAtTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SlidingExpirationInSeconds = table.Column<long>(type: "bigint", nullable: true),
                    AbsoluteExpiration = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DistributedCache", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupMemberDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanSnapshotId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<string>(type: "text", nullable: false),
                    MemberId = table.Column<string>(type: "text", nullable: false),
                    MemberDisplayName = table.Column<string>(type: "text", nullable: true),
                    MemberUpn = table.Column<string>(type: "text", nullable: true),
                    Decision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true),
                    ReviewedByUpn = table.Column<string>(type: "text", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemediationStatus = table.Column<string>(type: "text", nullable: true),
                    RemediatedByUpn = table.Column<string>(type: "text", nullable: true),
                    RemediatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMemberDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupMemberDecisions_ScanSnapshots_ScanSnapshotId",
                        column: x => x.ScanSnapshotId,
                        principalTable: "ScanSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanSnapshotId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    OnPremisesSyncEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    OnPremisesSamAccountName = table.Column<string>(type: "text", nullable: true),
                    IsAssignableToRole = table.Column<bool>(type: "boolean", nullable: false),
                    Owners = table.Column<string>(type: "text", nullable: true),
                    OwnerCount = table.Column<int>(type: "integer", nullable: false),
                    OwnerIds = table.Column<string>(type: "text", nullable: true),
                    TotalTransitiveMembers = table.Column<int>(type: "integer", nullable: false),
                    UserMembers = table.Column<int>(type: "integer", nullable: false),
                    ServicePrincipalMembers = table.Column<int>(type: "integer", nullable: false),
                    ExpansionError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupSnapshots_ScanSnapshots_ScanSnapshotId",
                        column: x => x.ScanSnapshotId,
                        principalTable: "ScanSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitoredSubscriptions",
                columns: table => new
                {
                    SubscriptionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    ManagementGroupId = table.Column<string>(type: "text", nullable: true),
                    ManagementGroupName = table.Column<string>(type: "text", nullable: true),
                    ManagementGroupPath = table.Column<string>(type: "text", nullable: true),
                    IsMonitored = table.Column<bool>(type: "boolean", nullable: false),
                    VisibleToScanner = table.Column<bool>(type: "boolean", nullable: false),
                    ScannerReaderOk = table.Column<bool>(type: "boolean", nullable: false),
                    ScannerMonitoringReaderOk = table.Column<bool>(type: "boolean", nullable: false),
                    ActivityLogReadable = table.Column<bool>(type: "boolean", nullable: false),
                    ScannerRoles = table.Column<string>(type: "text", nullable: true),
                    ScannerHasPrivilegedRoles = table.Column<bool>(type: "boolean", nullable: false),
                    DefenderCspmEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CiemEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ResourceCount = table.Column<int>(type: "integer", nullable: false),
                    LastDiscoveredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastScannedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DiscoveryError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoredSubscriptions", x => x.SubscriptionId);
                });

            migrationBuilder.CreateTable(
                name: "ReviewCampaigns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StartedByUpn = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ScanSnapshotId = table.Column<int>(type: "integer", nullable: true),
                    BaselineSnapshotId = table.Column<int>(type: "integer", nullable: true),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    RemoveCount = table.Column<int>(type: "integer", nullable: false),
                    ReviewCount = table.Column<int>(type: "integer", nullable: false),
                    ReportFileName = table.Column<string>(type: "text", nullable: true),
                    ReportContent = table.Column<byte[]>(type: "bytea", nullable: true),
                    ReportSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    NotificationStatus = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewCampaigns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_AssignedViaGroupId",
                table: "RoleAssignmentSnapshots",
                columns: new[] { "ScanSnapshotId", "AssignedViaGroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_RecommendedAction",
                table: "RoleAssignmentSnapshots",
                columns: new[] { "ScanSnapshotId", "RecommendedAction" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_ReviewerDecision",
                table: "RoleAssignmentSnapshots",
                columns: new[] { "ScanSnapshotId", "ReviewerDecision" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_ScopeLevel",
                table: "RoleAssignmentSnapshots",
                columns: new[] { "ScanSnapshotId", "ScopeLevel" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_SubscriptionId",
                table: "RoleAssignmentSnapshots",
                columns: new[] { "ScanSnapshotId", "SubscriptionId" });

            migrationBuilder.CreateIndex(
                name: "IX_DistributedCache_ExpiresAtTime",
                table: "DistributedCache",
                column: "ExpiresAtTime");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMemberDecisions_ScanSnapshotId_GroupId_MemberId",
                table: "GroupMemberDecisions",
                columns: new[] { "ScanSnapshotId", "GroupId", "MemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupSnapshots_ScanSnapshotId_GroupId",
                table: "GroupSnapshots",
                columns: new[] { "ScanSnapshotId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReviewCampaigns_CreatedAtUtc",
                table: "ReviewCampaigns",
                column: "CreatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "DistributedCache");

            migrationBuilder.DropTable(
                name: "GroupMemberDecisions");

            migrationBuilder.DropTable(
                name: "GroupSnapshots");

            migrationBuilder.DropTable(
                name: "MonitoredSubscriptions");

            migrationBuilder.DropTable(
                name: "ReviewCampaigns");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_AssignedViaGroupId",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_RecommendedAction",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_ReviewerDecision",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_ScopeLevel",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_SubscriptionId",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropColumn(
                name: "StartedByUpn",
                table: "ScanSnapshots");

            migrationBuilder.DropColumn(
                name: "Trigger",
                table: "ScanSnapshots");

            migrationBuilder.DropColumn(
                name: "DefenderCspmDetails",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropColumn(
                name: "DefenderCspmFindingCount",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropColumn(
                name: "DefenderCspmFindings",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropColumn(
                name: "DefenderCspmStatus",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropColumn(
                name: "RemediatedByUpn",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropColumn(
                name: "ReviewedByUpn",
                table: "RoleAssignmentSnapshots");
        }
    }
}
