using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Access.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPreviewSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PreviewSession",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PreviewerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrganisationIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RefusedWrites = table.Column<int>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreviewSession", x => x.Id);
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

            migrationBuilder.AddTenantPolicy("access", "PreviewSession");
            migrationBuilder.GrantRuntimeAccess("access");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("access", "PreviewSession");

            migrationBuilder.DropTable(
                name: "PreviewSession",
                schema: "access");
        }
    }
}
