using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Contracts.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CoreMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SupplierType",
                schema: "contracts",
                table: "Supplier",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgreementType",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContractType",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FunctionCode",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LegalEntityId",
                schema: "contracts",
                table: "Contract",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NoticePeriodUnit",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NoticePeriodValue",
                schema: "contracts",
                table: "Contract",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskLevel",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SapOutlineAgreementNumber",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            // Existing contracts keep their notice period exactly as entered: the single number of days becomes a value in days. Nothing becomes mandatory.
            migrationBuilder.Sql("UPDATE [contracts].[Contract] SET [NoticePeriodValue] = [NoticePeriodDays], [NoticePeriodUnit] = N'Days' WHERE [NoticePeriodDays] IS NOT NULL AND [NoticePeriodValue] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_TenantId_OrganisationId_SupplierType",
                schema: "contracts",
                table: "Supplier",
                columns: new[] { "TenantId", "OrganisationId", "SupplierType" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_OrganisationId_BusinessUnitId",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "OrganisationId", "BusinessUnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_OrganisationId_ContractType",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "OrganisationId", "ContractType" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_OrganisationId_LegalEntityId",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "OrganisationId", "LegalEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_OrganisationId_RiskLevel",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "OrganisationId", "RiskLevel" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_OrganisationId_SiteId",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "OrganisationId", "SiteId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Supplier_TenantId_OrganisationId_SupplierType",
                schema: "contracts",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_OrganisationId_BusinessUnitId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_OrganisationId_ContractType",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_OrganisationId_LegalEntityId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_OrganisationId_RiskLevel",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_OrganisationId_SiteId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "SupplierType",
                schema: "contracts",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "AgreementType",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "ContractType",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "FunctionCode",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "LegalEntityId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "NoticePeriodUnit",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "NoticePeriodValue",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "RiskLevel",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "SapOutlineAgreementNumber",
                schema: "contracts",
                table: "Contract");
        }
    }
}
