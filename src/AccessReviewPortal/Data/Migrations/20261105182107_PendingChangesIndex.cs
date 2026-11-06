using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessReviewPortal.Data.Migrations
{
    /// <inheritdoc />
    public partial class PendingChangesIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_ApprovedRemovals",
                table: "RoleAssignmentSnapshots",
                column: "ScanSnapshotId",
                filter: "\"ReviewerDecision\" = 'Removed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoleAssignmentSnapshots_ApprovedRemovals",
                table: "RoleAssignmentSnapshots");
        }
    }
}
