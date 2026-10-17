using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Contracts.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupplierIdentifiers : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Portability slice S10 (ADR-0033). Counterparty identifiers become a model: a new table, and optional address columns on the counterparty. The single tax-identifier column (still named <c>Abn</c>) is widened from
        /// nchar(11) to nvarchar(64); every stored value is kept exactly as it is and the column now holds the normalised value of the organisation's primary identifier type. Each existing value is also copied into the new
        /// table as a legacy row of type <c>au.abn</c> with the status NotChecked, so no value is lost or reinterpreted; whether an organisation shows it under an ABN label is up to that organisation's identifier profile.
        /// Nothing is deleted or rewritten, audit history is untouched, and rolling back means restoring the backup taken before the upgrade (the narrowing in Down could truncate longer values).
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Abn",
                schema: "contracts",
                table: "Supplier",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nchar(11)",
                oldFixedLength: true,
                oldMaxLength: 11,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressCountry",
                schema: "contracts",
                table: "Supplier",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressLine1",
                schema: "contracts",
                table: "Supplier",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressLine2",
                schema: "contracts",
                table: "Supplier",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressLocality",
                schema: "contracts",
                table: "Supplier",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressPostal",
                schema: "contracts",
                table: "Supplier",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressRegion",
                schema: "contracts",
                table: "Supplier",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SupplierIdentifier",
                schema: "contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TypeKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    NormalisedValue = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    VerificationStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    VerifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VerificationProviderKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Classification = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MigrationBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EffectiveFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierIdentifier", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierIdentifier_TenantId_MigrationBatchId",
                schema: "contracts",
                table: "SupplierIdentifier",
                columns: new[] { "TenantId", "MigrationBatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierIdentifier_TenantId_OrganisationId_TypeKey_NormalisedValue",
                schema: "contracts",
                table: "SupplierIdentifier",
                columns: new[] { "TenantId", "OrganisationId", "TypeKey", "NormalisedValue" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierIdentifier_TenantId_SupplierId_TypeKey",
                schema: "contracts",
                table: "SupplierIdentifier",
                columns: new[] { "TenantId", "SupplierId", "TypeKey" });

            migrationBuilder.Sql(@"
INSERT INTO [contracts].[SupplierIdentifier] ([Id], [TenantId], [OrganisationId], [SupplierId], [TypeKey], [Value], [NormalisedValue], [VerificationStatus], [Classification], [SourceKind], [EffectiveFromUtc], [CreatedUtc], [CreatedBy])
SELECT NEWID(), s.[TenantId], s.[OrganisationId], s.[Id], N'au.abn', LTRIM(RTRIM(s.[Abn])), LTRIM(RTRIM(s.[Abn])), N'NotChecked', N'Internal', N'Legacy', s.[CreatedUtc], s.[CreatedUtc], N'migration:SupplierIdentifiers'
FROM [contracts].[Supplier] s
WHERE s.[Abn] IS NOT NULL AND LEN(LTRIM(RTRIM(s.[Abn]))) > 0;");

            migrationBuilder.AddTenantPolicy("contracts", "SupplierIdentifier");
            migrationBuilder.GrantRuntimeAccess("contracts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("contracts", "SupplierIdentifier");

            migrationBuilder.DropTable(
                name: "SupplierIdentifier",
                schema: "contracts");

            migrationBuilder.DropColumn(
                name: "AddressCountry",
                schema: "contracts",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "AddressLine1",
                schema: "contracts",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "AddressLine2",
                schema: "contracts",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "AddressLocality",
                schema: "contracts",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "AddressPostal",
                schema: "contracts",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "AddressRegion",
                schema: "contracts",
                table: "Supplier");

            migrationBuilder.AlterColumn<string>(
                name: "Abn",
                schema: "contracts",
                table: "Supplier",
                type: "nchar(11)",
                fixedLength: true,
                maxLength: 11,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }
    }
}
