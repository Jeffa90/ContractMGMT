using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApproverGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApproverGroup",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    AllowDelegation = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RetiredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetiredReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApproverGroup", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApproverGroup_TenantId_Key",
                schema: "identity",
                table: "ApproverGroup",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.AddTenantPolicy("identity", "ApproverGroup");
            migrationBuilder.GrantRuntimeAccess("identity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("identity", "ApproverGroup");

            migrationBuilder.DropTable(
                name: "ApproverGroup",
                schema: "identity");
        }
    }
}
