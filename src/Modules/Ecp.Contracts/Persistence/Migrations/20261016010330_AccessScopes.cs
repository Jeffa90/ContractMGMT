using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Contracts.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccessScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BusinessUnitId",
                schema: "contracts",
                table: "Contract",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SiteId",
                schema: "contracts",
                table: "Contract",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DivisionId",
                schema: "contracts",
                table: "Category",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CategoryAssignment",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AssignedByLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryAssignment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryAssignment_Category_TenantId_CategoryId",
                        columns: x => new { x.TenantId, x.CategoryId },
                        principalSchema: "contracts",
                        principalTable: "Category",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceCounter",
                schema: "contracts",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastValue = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceCounter", x => new { x.TenantId, x.OrganisationId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryAssignment_TenantId_CategoryId_EffectiveToUtc",
                schema: "contracts",
                table: "CategoryAssignment",
                columns: new[] { "TenantId", "CategoryId", "EffectiveToUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryAssignment_TenantId_UserId",
                schema: "contracts",
                table: "CategoryAssignment",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.AddTenantPolicy("contracts", "CategoryAssignment");
            migrationBuilder.AddTenantPolicy("contracts", "ReferenceCounter");
            migrationBuilder.GrantRuntimeAccess("contracts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("contracts", "CategoryAssignment");
            migrationBuilder.DropTenantPolicy("contracts", "ReferenceCounter");

            migrationBuilder.DropTable(
                name: "CategoryAssignment",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "ReferenceCounter",
                schema: "contracts");

            migrationBuilder.DropColumn(
                name: "BusinessUnitId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "SiteId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "DivisionId",
                schema: "contracts",
                table: "Category");
        }
    }
}
