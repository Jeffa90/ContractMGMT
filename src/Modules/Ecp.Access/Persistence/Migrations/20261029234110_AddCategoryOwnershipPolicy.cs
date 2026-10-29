using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Access.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryOwnershipPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CategoryOwnershipPolicy",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Primary = table.Column<bool>(type: "bit", nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AssignmentIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EndedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryOwnershipPolicy", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryOwnershipPolicy_TenantId_OrganisationId_UserId",
                schema: "access",
                table: "CategoryOwnershipPolicy",
                columns: new[] { "TenantId", "OrganisationId", "UserId" });

            migrationBuilder.AddTenantPolicy("access", "CategoryOwnershipPolicy");
            migrationBuilder.GrantRuntimeAccess("access");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("access", "CategoryOwnershipPolicy");

            migrationBuilder.DropTable(
                name: "CategoryOwnershipPolicy",
                schema: "access");
        }
    }
}
