using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Contracts.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LifecycleSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActiveUnlockId",
                schema: "contracts",
                table: "Contract",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConfigReleaseId",
                schema: "contracts",
                table: "Contract",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContentFrozen",
                schema: "contracts",
                table: "Contract",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CriticalNotice",
                schema: "contracts",
                table: "Contract",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DocumentPolicy",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Open");

            migrationBuilder.AddColumn<string>(
                name: "LockReason",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockedBy",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NoticePeriodDays",
                schema: "contracts",
                table: "Contract",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PredecessorContractId",
                schema: "contracts",
                table: "Contract",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderLeadDays",
                schema: "contracts",
                table: "Contract",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SuccessorContractId",
                schema: "contracts",
                table: "Contract",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UnlockExpiresUtc",
                schema: "contracts",
                table: "Contract",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowRef",
                schema: "contracts",
                table: "Contract",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_EndDate",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_OrganisationId_Status",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "OrganisationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_PredecessorContractId",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "PredecessorContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_ReviewDate",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "ReviewDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Contract_TenantId_SuccessorContractId",
                schema: "contracts",
                table: "Contract",
                columns: new[] { "TenantId", "SuccessorContractId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_EndDate",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_OrganisationId_Status",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_PredecessorContractId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_ReviewDate",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropIndex(
                name: "IX_Contract_TenantId_SuccessorContractId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "ActiveUnlockId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "ConfigReleaseId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "ContentFrozen",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "CriticalNotice",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "DocumentPolicy",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "LockReason",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "LockedBy",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "NoticePeriodDays",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "PredecessorContractId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "ReminderLeadDays",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "SuccessorContractId",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "UnlockExpiresUtc",
                schema: "contracts",
                table: "Contract");

            migrationBuilder.DropColumn(
                name: "WorkflowRef",
                schema: "contracts",
                table: "Contract");
        }
    }
}
