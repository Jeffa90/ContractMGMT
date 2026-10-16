using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Setup.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnboardingItem",
                schema: "setup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ItemKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RefId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedRelease = table.Column<int>(type: "int", nullable: false),
                    EndedRelease = table.Column<int>(type: "int", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingItem", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingItem_TenantId_OrganisationId_ItemType_ItemKey",
                schema: "setup",
                table: "OnboardingItem",
                columns: new[] { "TenantId", "OrganisationId", "ItemType", "ItemKey" },
                unique: true);

            migrationBuilder.AddTenantPolicy("setup", "OnboardingItem");
            migrationBuilder.GrantRuntimeAccess("setup");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("setup", "OnboardingItem");

            migrationBuilder.DropTable(
                name: "OnboardingItem",
                schema: "setup");
        }
    }
}
