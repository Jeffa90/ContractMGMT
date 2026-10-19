using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.DataMigration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMaskedDataDeclarations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BatchDataClassification",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataClass = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DeclarationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LinkedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LinkedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchDataClassification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaskedDataDeclaration",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatasetOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MaskingMethod = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    FieldsMasked = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AttachmentsIncluded = table.Column<bool>(type: "bit", nullable: false),
                    PersonalDataIncluded = table.Column<bool>(type: "bit", nullable: false),
                    ConfidentialDataIncluded = table.Column<bool>(type: "bit", nullable: false),
                    ApprovalReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ApprovedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewByUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PermittedEnvironment = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PermittedUsers = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DeclaredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DeclaredByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeclaredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaskedDataDeclaration", x => x.Id);
                    table.UniqueConstraint("AK_MaskedDataDeclaration_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_BatchDataClassification_TenantId_BatchId",
                schema: "migration",
                table: "BatchDataClassification",
                columns: new[] { "TenantId", "BatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BatchDataClassification_TenantId_DeclarationId",
                schema: "migration",
                table: "BatchDataClassification",
                columns: new[] { "TenantId", "DeclarationId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaskedDataDeclaration_TenantId_OrganisationId_DeclaredUtc",
                schema: "migration",
                table: "MaskedDataDeclaration",
                columns: new[] { "TenantId", "OrganisationId", "DeclaredUtc" });

            migrationBuilder.AddTenantPolicy("migration", "MaskedDataDeclaration");
            migrationBuilder.AddTenantPolicy("migration", "BatchDataClassification");
            migrationBuilder.GrantRuntimeAccess("migration");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("migration", "MaskedDataDeclaration");
            migrationBuilder.DropTenantPolicy("migration", "BatchDataClassification");

            migrationBuilder.DropTable(
                name: "BatchDataClassification",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "MaskedDataDeclaration",
                schema: "migration");
        }
    }
}
