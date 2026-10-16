using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Approvals.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "approvals");

            migrationBuilder.CreateTable(
                name: "ApprovalDelegation",
                schema: "approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FromUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidToUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RevokeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDelegation", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegation_TenantId_FromUserId_ValidToUtc",
                schema: "approvals",
                table: "ApprovalDelegation",
                columns: new[] { "TenantId", "FromUserId", "ValidToUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegation_TenantId_OrganisationId_GroupKey_ValidToUtc",
                schema: "approvals",
                table: "ApprovalDelegation",
                columns: new[] { "TenantId", "OrganisationId", "GroupKey", "ValidToUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegation_TenantId_ToUserId_ValidToUtc",
                schema: "approvals",
                table: "ApprovalDelegation",
                columns: new[] { "TenantId", "ToUserId", "ValidToUtc" });

            migrationBuilder.AddTenantPolicy("approvals", "ApprovalDelegation");
            migrationBuilder.GrantRuntimeAccess("approvals");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("approvals", "ApprovalDelegation");

            migrationBuilder.DropTable(
                name: "ApprovalDelegation",
                schema: "approvals");
        }
    }
}
