using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Contracts.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomFieldValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContractCustomValue",
                schema: "contracts",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TextValue = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LongTextValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NumberValue = table.Column<decimal>(type: "decimal(28,8)", precision: 28, scale: 8, nullable: true),
                    DateValue = table.Column<DateOnly>(type: "date", nullable: true),
                    DateTimeValue = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BoolValue = table.Column<bool>(type: "bit", nullable: true),
                    RefValue = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefinitionRelease = table.Column<int>(type: "int", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MigrationBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourceValue = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractCustomValue", x => new { x.TenantId, x.ContractId, x.FieldKey, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_ContractCustomValue_Contract_TenantId_ContractId",
                        columns: x => new { x.TenantId, x.ContractId },
                        principalSchema: "contracts",
                        principalTable: "Contract",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractCustomValue_TenantId_MigrationBatchId",
                schema: "contracts",
                table: "ContractCustomValue",
                columns: new[] { "TenantId", "MigrationBatchId" },
                filter: "[MigrationBatchId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContractCustomValue_TenantId_OrganisationId_FieldKey",
                schema: "contracts",
                table: "ContractCustomValue",
                columns: new[] { "TenantId", "OrganisationId", "FieldKey" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomValue_Bool",
                schema: "contracts",
                table: "ContractCustomValue",
                columns: new[] { "TenantId", "FieldKey", "BoolValue" },
                filter: "[BoolValue] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomValue_Date",
                schema: "contracts",
                table: "ContractCustomValue",
                columns: new[] { "TenantId", "FieldKey", "DateValue" },
                filter: "[DateValue] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomValue_DateTime",
                schema: "contracts",
                table: "ContractCustomValue",
                columns: new[] { "TenantId", "FieldKey", "DateTimeValue" },
                filter: "[DateTimeValue] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomValue_Number",
                schema: "contracts",
                table: "ContractCustomValue",
                columns: new[] { "TenantId", "FieldKey", "NumberValue" },
                filter: "[NumberValue] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomValue_Ref",
                schema: "contracts",
                table: "ContractCustomValue",
                columns: new[] { "TenantId", "FieldKey", "RefValue" },
                filter: "[RefValue] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomValue_Text",
                schema: "contracts",
                table: "ContractCustomValue",
                columns: new[] { "TenantId", "FieldKey", "TextValue" },
                filter: "[TextValue] IS NOT NULL");

            migrationBuilder.AddTenantPolicy("contracts", "ContractCustomValue");
            migrationBuilder.GrantRuntimeAccess("contracts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("contracts", "ContractCustomValue");

            migrationBuilder.DropTable(
                name: "ContractCustomValue",
                schema: "contracts");
        }
    }
}
