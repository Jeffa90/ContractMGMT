using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Operations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMigrationRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MigrationRecord",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Module = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MigrationId = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Checksum = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Generator = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PlatformVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UpgradeRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationRecord", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationRecord_TenantId_Module_MigrationId",
                schema: "ops",
                table: "MigrationRecord",
                columns: new[] { "TenantId", "Module", "MigrationId" },
                unique: true);

            migrationBuilder.AddTenantPolicy("ops", "MigrationRecord");
            migrationBuilder.GrantRuntimeAccess("ops");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("ops", "MigrationRecord");

            migrationBuilder.DropTable(
                name: "MigrationRecord",
                schema: "ops");
        }
    }
}
