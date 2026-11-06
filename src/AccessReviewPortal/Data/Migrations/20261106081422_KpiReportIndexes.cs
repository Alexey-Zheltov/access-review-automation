using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessReviewPortal.Data.Migrations
{
    /// <inheritdoc />
    public partial class KpiReportIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_RemediatedAtUtc",
                table: "RoleAssignmentSnapshots",
                column: "RemediatedAtUtc",
                filter: "\"RemediatedAtUtc\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentSnapshots_ReviewedAtUtc",
                table: "RoleAssignmentSnapshots",
                column: "ReviewedAtUtc",
                filter: "\"ReviewedAtUtc\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoleAssignmentSnapshots_RemediatedAtUtc",
                table: "RoleAssignmentSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_RoleAssignmentSnapshots_ReviewedAtUtc",
                table: "RoleAssignmentSnapshots");
        }
    }
}
