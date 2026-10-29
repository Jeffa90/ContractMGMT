using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Access.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileApplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "access");

            migrationBuilder.CreateTable(
                name: "ProfileApplication",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileKey = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    ProfileVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AppliedByLabel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AppliedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AppliedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GrantIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    OwnershipIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileApplication", x => x.Id);
                });

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
            migrationBuilder.GrantRuntimeAccess("access");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("access", "ProfileApplication");

            migrationBuilder.DropTable(
                name: "ProfileApplication",
                schema: "access");
        }
    }
}
