using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Contracts.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrationSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArchiveReason",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ArchivedByBatchId",
                schema: "contracts",
                table: "Contract",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedUtc",
                schema: "contracts",
                table: "Contract",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                schema: "contracts",
                table: "Contract",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_TenantId_MigrationBatchId",
                schema: "contracts",
                table: "Supplier",
                columns: new[] { "TenantId", "MigrationBatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_MigrationBatchId",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "MigrationBatchId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Supplier_TenantId_MigrationBatchId",
                schema: "contracts",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_MigrationBatchId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "ArchiveReason",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "ArchivedByBatchId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "ArchivedUtc",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                schema: "contracts",
                table: "Contract");
        }
    }
}
