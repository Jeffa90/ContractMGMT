using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ElevationAndPrivilegedAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PrivilegedAccess",
                schema: "identity",
                table: "RoleAssignment",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Elevation",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InitiatorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InitiatorLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ActingUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActingLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ClassificationCeiling = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PermissionsCsv = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    ConfirmationRequired = table.Column<bool>(type: "bit", nullable: false),
                    RequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConfirmedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfirmedByLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EndedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedByLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Elevation", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Elevation_TenantId_RequestedUtc",
                schema: "identity",
                table: "Elevation",
                columns: new[] { "TenantId", "RequestedUtc" });

            migrationBuilder.AddTenantPolicy("identity", "Elevation");
            migrationBuilder.GrantRuntimeAccess("identity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("identity", "Elevation");

            migrationBuilder.DropTable(
                name: "Elevation",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "PrivilegedAccess",
                schema: "identity",
                table: "RoleAssignment");
        }
    }
}
