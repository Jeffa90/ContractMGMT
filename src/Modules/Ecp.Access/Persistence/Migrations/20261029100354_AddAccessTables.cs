using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Access.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "access");

            migrationBuilder.CreateTable(
                name: "PreviewSession",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RefusedWrites = table.Column<int>(type: "int", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreviewSession", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProfileApplication",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    ProfileVersion = table.Column<int>(type: "int", nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AppliedByLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AppliedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AppliedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GrantIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OwnershipIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileApplication", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PreviewSession_TenantId_PreviewerUserId_StartedUtc",
                schema: "access",
                table: "PreviewSession",
                columns: new[] { "TenantId", "PreviewerUserId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PreviewSession_TenantId_TargetUserId_StartedUtc",
                schema: "access",
                table: "PreviewSession",
                columns: new[] { "TenantId", "TargetUserId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileApplication_TenantId_OrganisationId_ProfileKey",
                schema: "access",
                table: "ProfileApplication",
                columns: new[] { "TenantId", "OrganisationId", "ProfileKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileApplication_TenantId_UserId",
                schema: "access",
                table: "ProfileApplication",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.AddTenantPolicy("access", "ProfileApplication");
            migrationBuilder.AddTenantPolicy("access", "PreviewSession");
            migrationBuilder.GrantRuntimeAccess("access");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("access", "ProfileApplication");
            migrationBuilder.DropTenantPolicy("access", "PreviewSession");

            migrationBuilder.DropTable(
                name: "PreviewSession",
                schema: "access");

            migrationBuilder.DropTable(
                name: "ProfileApplication",
                schema: "access");
        }
    }
}
