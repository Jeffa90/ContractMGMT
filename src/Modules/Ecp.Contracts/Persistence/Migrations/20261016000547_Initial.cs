using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Contracts.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "contracts");

            migrationBuilder.CreateTable(
                name: "Category",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceRecordId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MigrationBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Category", x => x.Id);
                    table.UniqueConstraint("AK_Category_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Category_Category_TenantId_ParentCategoryId",
                        columns: x => new { x.TenantId, x.ParentCategoryId },
                        principalSchema: "contracts",
                        principalTable: "Category",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Supplier",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NormalisedName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Abn = table.Column<string>(type: "nchar(11)", fixedLength: true, maxLength: 11, nullable: true),
                    VendorNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceRecordId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MigrationBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Supplier", x => x.Id);
                    table.UniqueConstraint("AK_Supplier_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Contract",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgreementReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceAgreementReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContractTitle = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RecordLocked = table.Column<bool>(type: "bit", nullable: false),
                    LockedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Classification = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    EstimatedValue = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    ExtensionType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LegalReviewRequired = table.Column<bool>(type: "bit", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReviewDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RegisteredDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RegisteredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RegisteredDateProvenance = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SourceRegisteredValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceRecordId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SourceStatus = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SourceExtractedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MigrationBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsLegacyLoad = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contract", x => x.Id);
                    table.UniqueConstraint("AK_Contract_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Contract_Category_TenantId_CategoryId",
                        columns: x => new { x.TenantId, x.CategoryId },
                        principalSchema: "contracts",
                        principalTable: "Category",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Contract_Supplier_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "contracts",
                        principalTable: "Supplier",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Category_TenantId_OrganisationId_Code",
                schema: "contracts",
                table: "Category",
                columns: new[] { "TenantId", "OrganisationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Category_TenantId_ParentCategoryId",
                schema: "contracts",
                table: "Category",
                columns: new[] { "TenantId", "ParentCategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_AgreementReference",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "AgreementReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_CategoryId",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_OrganisationId_CategoryId",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "OrganisationId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_OrganisationId_SourceSystem_SourceRecordId",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "OrganisationId", "SourceSystem", "SourceRecordId" },
                unique: true,
                filter: "[SourceRecordId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_SupplierId",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_TenantId_OrganisationId_NormalisedName",
                schema: "contracts",
                table: "Supplier",
                columns: new[] { "TenantId", "OrganisationId", "NormalisedName" });

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_TenantId_OrganisationId_VendorNumber",
                schema: "contracts",
                table: "Supplier",
                columns: new[] { "TenantId", "OrganisationId", "VendorNumber" },
                unique: true,
                filter: "[VendorNumber] IS NOT NULL");

            migrationBuilder.AddTenantPolicy("contracts", "Category");
            migrationBuilder.AddTenantPolicy("contracts", "Supplier");
            migrationBuilder.AddTenantPolicy("contracts", "Contract");
            migrationBuilder.GrantRuntimeAccess("contracts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("contracts", "Category");
            migrationBuilder.DropTenantPolicy("contracts", "Supplier");
            migrationBuilder.DropTenantPolicy("contracts", "Contract");
            migrationBuilder.DropTable(
                name: "Contract",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "Category",
                schema: "contracts");

            migrationBuilder.DropTable(
                name: "Supplier",
                schema: "contracts");
        }
    }
}
