using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScanAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentScanAttempt",
                schema: "documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentScanAttempt", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentScanAttempt_TenantId_Outcome_AttemptedUtc",
                schema: "documents",
                table: "DocumentScanAttempt",
                columns: new[] { "TenantId", "Outcome", "AttemptedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentScanAttempt_TenantId_VersionId_AttemptedUtc",
                schema: "documents",
                table: "DocumentScanAttempt",
                columns: new[] { "TenantId", "VersionId", "AttemptedUtc" });

            migrationBuilder.AddTenantPolicy("documents", "DocumentScanAttempt");
            migrationBuilder.GrantRuntimeAccess("documents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("documents", "DocumentScanAttempt");

            migrationBuilder.DropTable(
                name: "DocumentScanAttempt",
                schema: "documents");
        }
    }
}
