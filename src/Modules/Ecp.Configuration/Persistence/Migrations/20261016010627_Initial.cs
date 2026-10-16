using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Configuration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "configuration");

            migrationBuilder.CreateTable(
                name: "ConfigurationDraft",
                schema: "configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ValidatedHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ValidationJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BasedOnReleaseNumber = table.Column<int>(type: "int", nullable: true),
                    LastEditedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationDraft", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationRelease",
                schema: "configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RollbackOfNumber = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    PublishedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationRelease", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationDraft_TenantId_OrganisationId_Kind_Status",
                schema: "configuration",
                table: "ConfigurationDraft",
                columns: new[] { "TenantId", "OrganisationId", "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationRelease_TenantId_OrganisationId_Kind_Number",
                schema: "configuration",
                table: "ConfigurationRelease",
                columns: new[] { "TenantId", "OrganisationId", "Kind", "Number" },
                unique: true);

            migrationBuilder.AddTenantPolicy("configuration", "ConfigurationDraft");
            migrationBuilder.AddTenantPolicy("configuration", "ConfigurationRelease");
            migrationBuilder.GrantRuntimeAccess("configuration");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("configuration", "ConfigurationDraft");
            migrationBuilder.DropTenantPolicy("configuration", "ConfigurationRelease");

            migrationBuilder.DropTable(
                name: "ConfigurationDraft",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "ConfigurationRelease",
                schema: "configuration");
        }
    }
}
