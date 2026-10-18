using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Operations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ops");

            migrationBuilder.CreateTable(
                name: "MaintenanceState",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Cause = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SinceUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChangedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationLease",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OwnerDescription = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AcquiredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReleasedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationLease", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationRun",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OperatorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OperatorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationRun", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationLease_TenantId_Name_ScopeKey",
                schema: "ops",
                table: "OperationLease",
                columns: new[] { "TenantId", "Name", "ScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationRun_TenantId_Kind_StartedUtc",
                schema: "ops",
                table: "OperationRun",
                columns: new[] { "TenantId", "Kind", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationRun_TenantId_Reference",
                schema: "ops",
                table: "OperationRun",
                columns: new[] { "TenantId", "Reference" });

            migrationBuilder.AddTenantPolicy("ops", "OperationLease");
            migrationBuilder.AddTenantPolicy("ops", "MaintenanceState");
            migrationBuilder.AddTenantPolicy("ops", "OperationRun");
            migrationBuilder.GrantRuntimeAccess("ops");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("ops", "OperationLease");
            migrationBuilder.DropTenantPolicy("ops", "MaintenanceState");
            migrationBuilder.DropTenantPolicy("ops", "OperationRun");

            migrationBuilder.DropTable(
                name: "MaintenanceState",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "OperationLease",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "OperationRun",
                schema: "ops");
        }
    }
}
