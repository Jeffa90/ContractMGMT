using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Lifecycle.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignatureRequestVocabulary : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Gives the signature boundary a provider-neutral vocabulary (portability slice S3, ADR-0027). Every existing row is kept: the table and four columns are renamed in place, new columns are added empty, and
        /// no value is rewritten, so contract state, approval records, executed-document references and audit history are untouched. The security policy is dropped and re-created because it is named after the table.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("lifecycle", "ExecutionRecord");

            // The filtered index names the column that is renamed below, so it is dropped and created again under the new names.
            migrationBuilder.DropIndex(
                name: "IX_ExecutionRecord_TenantId_Provider_EnvelopeId",
                schema: "lifecycle",
                table: "ExecutionRecord");

            migrationBuilder.RenameTable(
                name: "ExecutionRecord",
                schema: "lifecycle",
                newName: "SignatureRequest",
                newSchema: "lifecycle");

            // A table rename leaves its constraint names behind; keep them in step with the model so later migrations can find them.
            migrationBuilder.Sql("IF OBJECT_ID(N'[lifecycle].[PK_ExecutionRecord]', N'PK') IS NOT NULL EXEC sp_rename N'[lifecycle].[PK_ExecutionRecord]', N'PK_SignatureRequest', N'OBJECT';");

            migrationBuilder.RenameIndex(
                name: "IX_ExecutionRecord_TenantId_SubjectKind_SubjectId_IsCurrent",
                schema: "lifecycle",
                table: "SignatureRequest",
                newName: "IX_SignatureRequest_TenantId_SubjectKind_SubjectId_IsCurrent");

            migrationBuilder.RenameColumn(name: "EnvelopeId", schema: "lifecycle", table: "SignatureRequest", newName: "ProviderRequestId");
            migrationBuilder.RenameColumn(name: "StartedBy", schema: "lifecycle", table: "SignatureRequest", newName: "RequestedBy");
            migrationBuilder.RenameColumn(name: "StartedUtc", schema: "lifecycle", table: "SignatureRequest", newName: "RequestedUtc");
            migrationBuilder.RenameColumn(name: "ExecutedUtc", schema: "lifecycle", table: "SignatureRequest", newName: "CompletedUtc");

            migrationBuilder.AddColumn<string>(name: "ProviderStatus", schema: "lifecycle", table: "SignatureRequest", type: "nvarchar(100)", maxLength: 100, nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "LastProviderEventUtc", schema: "lifecycle", table: "SignatureRequest", type: "datetime2", nullable: true);
            migrationBuilder.AddColumn<string>(name: "CorrelationId", schema: "lifecycle", table: "SignatureRequest", type: "nvarchar(64)", maxLength: 64, nullable: true);
            migrationBuilder.AddColumn<string>(name: "IdempotencyKey", schema: "lifecycle", table: "SignatureRequest", type: "nvarchar(100)", maxLength: 100, nullable: true);
            migrationBuilder.AddColumn<string>(name: "FailureCode", schema: "lifecycle", table: "SignatureRequest", type: "nvarchar(60)", maxLength: 60, nullable: true);
            migrationBuilder.AddColumn<string>(name: "FailureMessage", schema: "lifecycle", table: "SignatureRequest", type: "nvarchar(500)", maxLength: 500, nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequest_TenantId_Provider_ProviderRequestId",
                schema: "lifecycle",
                table: "SignatureRequest",
                columns: new[] { "TenantId", "Provider", "ProviderRequestId" },
                filter: "[ProviderRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequest_TenantId_IdempotencyKey",
                schema: "lifecycle",
                table: "SignatureRequest",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateTable(
                name: "SignatureProviderEvent",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SignatureRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ProviderEventId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ProviderRequestId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ProviderStatus = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OutcomeDetail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureProviderEvent", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureProviderEvent_TenantId_Outcome_ReceivedUtc",
                schema: "lifecycle",
                table: "SignatureProviderEvent",
                columns: new[] { "TenantId", "Outcome", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureProviderEvent_TenantId_Provider_ProviderEventId",
                schema: "lifecycle",
                table: "SignatureProviderEvent",
                columns: new[] { "TenantId", "Provider", "ProviderEventId" },
                unique: true);

            migrationBuilder.AddTenantPolicy("lifecycle", "SignatureRequest");
            migrationBuilder.AddTenantPolicy("lifecycle", "SignatureProviderEvent");
            migrationBuilder.GrantRuntimeAccess("lifecycle");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("lifecycle", "SignatureProviderEvent");
            migrationBuilder.DropTenantPolicy("lifecycle", "SignatureRequest");

            migrationBuilder.DropTable(
                name: "SignatureProviderEvent",
                schema: "lifecycle");

            migrationBuilder.DropIndex(name: "IX_SignatureRequest_TenantId_IdempotencyKey", schema: "lifecycle", table: "SignatureRequest");
            migrationBuilder.DropIndex(name: "IX_SignatureRequest_TenantId_Provider_ProviderRequestId", schema: "lifecycle", table: "SignatureRequest");

            migrationBuilder.DropColumn(name: "FailureMessage", schema: "lifecycle", table: "SignatureRequest");
            migrationBuilder.DropColumn(name: "FailureCode", schema: "lifecycle", table: "SignatureRequest");
            migrationBuilder.DropColumn(name: "IdempotencyKey", schema: "lifecycle", table: "SignatureRequest");
            migrationBuilder.DropColumn(name: "CorrelationId", schema: "lifecycle", table: "SignatureRequest");
            migrationBuilder.DropColumn(name: "LastProviderEventUtc", schema: "lifecycle", table: "SignatureRequest");
            migrationBuilder.DropColumn(name: "ProviderStatus", schema: "lifecycle", table: "SignatureRequest");

            migrationBuilder.RenameColumn(name: "CompletedUtc", schema: "lifecycle", table: "SignatureRequest", newName: "ExecutedUtc");
            migrationBuilder.RenameColumn(name: "RequestedUtc", schema: "lifecycle", table: "SignatureRequest", newName: "StartedUtc");
            migrationBuilder.RenameColumn(name: "RequestedBy", schema: "lifecycle", table: "SignatureRequest", newName: "StartedBy");
            migrationBuilder.RenameColumn(name: "ProviderRequestId", schema: "lifecycle", table: "SignatureRequest", newName: "EnvelopeId");

            migrationBuilder.RenameIndex(
                name: "IX_SignatureRequest_TenantId_SubjectKind_SubjectId_IsCurrent",
                schema: "lifecycle",
                table: "SignatureRequest",
                newName: "IX_ExecutionRecord_TenantId_SubjectKind_SubjectId_IsCurrent");

            migrationBuilder.RenameTable(
                name: "SignatureRequest",
                schema: "lifecycle",
                newName: "ExecutionRecord",
                newSchema: "lifecycle");

            migrationBuilder.Sql("IF OBJECT_ID(N'[lifecycle].[PK_SignatureRequest]', N'PK') IS NOT NULL EXEC sp_rename N'[lifecycle].[PK_SignatureRequest]', N'PK_ExecutionRecord', N'OBJECT';");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionRecord_TenantId_Provider_EnvelopeId",
                schema: "lifecycle",
                table: "ExecutionRecord",
                columns: new[] { "TenantId", "Provider", "EnvelopeId" },
                filter: "[EnvelopeId] IS NOT NULL");

            migrationBuilder.AddTenantPolicy("lifecycle", "ExecutionRecord");
        }
    }
}
