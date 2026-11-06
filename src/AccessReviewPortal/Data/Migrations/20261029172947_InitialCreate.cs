using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AccessReviewPortal.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ScanSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<string>(type: "text", nullable: true),
                    TotalUsers = table.Column<int>(type: "integer", nullable: false),
                    TotalAssignments = table.Column<int>(type: "integer", nullable: false),
                    DatabaseSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    InactivityThresholdDays = table.Column<int>(type: "integer", nullable: false),
                    BaselineSnapshotId = table.Column<int>(type: "integer", nullable: true),
                    UsageSources = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignmentSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanSnapshotId = table.Column<int>(type: "integer", nullable: false),
                    AssignmentKey = table.Column<string>(type: "text", nullable: false),
                    SourceAssignmentId = table.Column<string>(type: "text", nullable: true),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    PrincipalType = table.Column<string>(type: "text", nullable: false),
                    PrincipalDisplayName = table.Column<string>(type: "text", nullable: true),
                    PrincipalUpn = table.Column<string>(type: "text", nullable: true),
                    PrincipalExists = table.Column<bool>(type: "boolean", nullable: false),
                    AssignedViaGroupId = table.Column<string>(type: "text", nullable: true),
                    AssignedViaGroupName = table.Column<string>(type: "text", nullable: true),
                    AssignedViaGroupOnPremises = table.Column<bool>(type: "boolean", nullable: false),
                    AssignedViaGroupSamAccountName = table.Column<string>(type: "text", nullable: true),
                    ScopeLevel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ScopeId = table.Column<string>(type: "text", nullable: false),
                    ScopeDisplayName = table.Column<string>(type: "text", nullable: true),
                    SubscriptionId = table.Column<string>(type: "text", nullable: true),
                    SubscriptionName = table.Column<string>(type: "text", nullable: true),
                    RoleDefinitionId = table.Column<string>(type: "text", nullable: false),
                    RoleName = table.Column<string>(type: "text", nullable: false),
                    RoleHasWritePermissions = table.Column<bool>(type: "boolean", nullable: false),
                    AssignmentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StartDateTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndDateTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Justification = table.Column<string>(type: "text", nullable: true),
                    LastWriteEvidenceUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastWriteOperation = table.Column<string>(type: "text", nullable: true),
                    WriteEvidenceSource = table.Column<string>(type: "text", nullable: true),
                    LastPimActivationUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PimActivationDetails = table.Column<string>(type: "text", nullable: true),
                    LastSignInUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SignInSource = table.Column<string>(type: "text", nullable: true),
                    UsageEvidenceStatus = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LastRoleUsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DaysSinceLastActivity = table.Column<int>(type: "integer", nullable: true),
                    UsageEvidenceDetails = table.Column<string>(type: "text", nullable: true),
                    NativeSelfReviewDecision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NativeReviewDetails = table.Column<string>(type: "text", nullable: true),
                    IsNewSinceBaseline = table.Column<bool>(type: "boolean", nullable: false),
                    RecommendedAction = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RecommendationReason = table.Column<string>(type: "text", nullable: true),
                    ReviewerDecision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ReviewerComment = table.Column<string>(type: "text", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemediationStatus = table.Column<string>(type: "text", nullable: true),
                    RemediatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignmentSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignmentSnapshots_ScanSnapshots_ScanSnapshotId",
                        column: x => x.ScanSnapshotId,
                        principalTable: "ScanSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScanLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanSnapshotId = table.Column<int>(type: "integer", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Level = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanLogEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanLogEntries_ScanSnapshots_ScanSnapshotId",
                        column: x => x.ScanSnapshotId,
                        principalTable: "ScanSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserStateSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanSnapshotId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Upn = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    Mail = table.Column<string>(type: "text", nullable: true),
                    AccountEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ExistsInDirectory = table.Column<bool>(type: "boolean", nullable: false),
                    Department = table.Column<string>(type: "text", nullable: true),
                    JobTitle = table.Column<string>(type: "text", nullable: true),
                    ManagerId = table.Column<string>(type: "text", nullable: true),
                    ManagerUpn = table.Column<string>(type: "text", nullable: true),
                    ManagerDisplayName = table.Column<string>(type: "text", nullable: true),
                    CreatedDateTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OnPremisesSyncEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    OnPremisesSamAccountName = table.Column<string>(type: "text", nullable: true),
                    LastEntraSignInUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAzurePortalSignInUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAdminPortalSignInUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDirectoryActivityUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDirectoryActivity = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserStateSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserStateSnapshots_ScanSnapshots_ScanSnapshotId",
                        column: x => x.ScanSnapshotId,
                        principalTable: "ScanSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId",
                table: "RoleAssignmentSnapshots",
                column: "ScanSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_AssignmentKey",
                table: "RoleAssignmentSnapshots",
                columns: new[] { "ScanSnapshotId", "AssignmentKey" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_ScanSnapshotId_PrincipalId",
                table: "RoleAssignmentSnapshots",
                columns: new[] { "ScanSnapshotId", "PrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanLogEntries_ScanSnapshotId",
                table: "ScanLogEntries",
                column: "ScanSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_UserStateSnapshots_ScanSnapshotId_UserId",
                table: "UserStateSnapshots",
                columns: new[] { "ScanSnapshotId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "RoleAssignmentSnapshots");

            migrationBuilder.DropTable(
                name: "ScanLogEntries");

            migrationBuilder.DropTable(
                name: "UserStateSnapshots");

            migrationBuilder.DropTable(
                name: "ScanSnapshots");
        }
    }
}
