using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Documents.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrationSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MigrationBatchId",
                schema: "documents",
                table: "DocumentVersion",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchiveReason",
                schema: "documents",
                table: "Document",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentDocumentId",
                schema: "documents",
                table: "Document",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersion_TenantId_MigrationBatchId",
                schema: "documents",
                table: "DocumentVersion",
                columns: new[] { "TenantId", "MigrationBatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_Document_TenantId_MigrationBatchId",
                schema: "documents",
                table: "Document",
                columns: new[] { "TenantId", "MigrationBatchId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocumentVersion_TenantId_MigrationBatchId",
                schema: "documents",
                table: "DocumentVersion");

            migrationBuilder.DropIndex(
                name: "IX_Document_TenantId_MigrationBatchId",
                schema: "documents",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "MigrationBatchId",
                schema: "documents",
                table: "DocumentVersion");

            migrationBuilder.DropColumn(
                name: "ArchiveReason",
                schema: "documents",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "ParentDocumentId",
                schema: "documents",
                table: "Document");
        }
    }
}
