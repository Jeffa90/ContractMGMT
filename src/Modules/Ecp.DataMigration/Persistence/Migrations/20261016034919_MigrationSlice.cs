using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.DataMigration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrationSlice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                schema: "migration",
                table: "MigrationProject",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "LegacyAllowOverwriteLocked",
                schema: "migration",
                table: "MigrationProject",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LegacyAllowedStatusCodes",
                schema: "migration",
                table: "MigrationProject",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LegacyOverwriteReason",
                schema: "migration",
                table: "MigrationProject",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferencePolicy",
                schema: "migration",
                table: "MigrationProject",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "migration",
                table: "MigrationProject",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "TargetFreezeEnabled",
                schema: "migration",
                table: "MigrationProject",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AcceptanceNote",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CommitCompletedUtc",
                schema: "migration",
                table: "MigrationBatch",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CommitStartedUtc",
                schema: "migration",
                table: "MigrationBatch",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommittedBy",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CommittedByUserId",
                schema: "migration",
                table: "MigrationBatch",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConfigReleaseNumber",
                schema: "migration",
                table: "MigrationBatch",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmedBy",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConfirmedByUserId",
                schema: "migration",
                table: "MigrationBatch",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmedFingerprint",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedUtc",
                schema: "migration",
                table: "MigrationBatch",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                schema: "migration",
                table: "MigrationBatch",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresUtc",
                schema: "migration",
                table: "MigrationBatch",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RollbackReason",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RolledBackBy",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RolledBackUtc",
                schema: "migration",
                table: "MigrationBatch",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidatedUtc",
                schema: "migration",
                table: "MigrationBatch",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValidationFingerprint",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValidationSummaryJson",
                schema: "migration",
                table: "MigrationBatch",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_MigrationBatch_TenantId_Id",
                schema: "migration",
                table: "MigrationBatch",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "AttachmentManifestEntry",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    RowNumber = table.Column<int>(type: "int", nullable: false),
                    ColumnsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceRecordId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AgreementReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RawRelativePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RelativePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    DocumentTypeRaw = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DocumentTypeCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    VersionLabel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IsExecutedCopy = table.Column<bool>(type: "bit", nullable: false),
                    ParentDocumentSourceId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DeclaredSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DocumentSourceId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StagedFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentManifestEntry", x => x.Id);
                    table.UniqueConstraint("AK_AttachmentManifestEntry_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_AttachmentManifestEntry_MigrationBatch_TenantId_BatchId",
                        columns: x => new { x.TenantId, x.BatchId },
                        principalSchema: "migration",
                        principalTable: "MigrationBatch",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Checkpoint",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    LastCompletedSequence = table.Column<long>(type: "bigint", nullable: false),
                    PhaseComplete = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checkpoint", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Checkpoint_MigrationBatch_TenantId_BatchId",
                        columns: x => new { x.TenantId, x.BatchId },
                        principalSchema: "migration",
                        principalTable: "MigrationBatch",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MappingProfile",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceSystemVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LatestVersionNumber = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MappingProfile", x => x.Id);
                    table.UniqueConstraint("AK_MappingProfile_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ProvenanceRecord",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetEntityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TargetEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    BeforeImageJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterStampUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceSystem = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceRecordId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    StagingRowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StagedFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceFileSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MappingProfileVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceExtractedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceStatus = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReversalState = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReversalNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReversedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvenanceRecord", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvenanceRecord_MigrationBatch_TenantId_BatchId",
                        columns: x => new { x.TenantId, x.BatchId },
                        principalSchema: "migration",
                        principalTable: "MigrationBatch",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationReport",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TotalsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileTotalsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DistributionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Balanced = table.Column<bool>(type: "bit", nullable: false),
                    ExceptionsApproved = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedExceptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovalNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationReport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReconciliationReport_MigrationBatch_TenantId_BatchId",
                        columns: x => new { x.TenantId, x.BatchId },
                        principalSchema: "migration",
                        principalTable: "MigrationBatch",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceFile",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Format = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: false),
                    SheetName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Encoding = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Delimiter = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: true),
                    ProfileJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UploadedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceFile", x => x.Id);
                    table.UniqueConstraint("AK_SourceFile_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SourceFile_MigrationBatch_TenantId_BatchId",
                        columns: x => new { x.TenantId, x.BatchId },
                        principalSchema: "migration",
                        principalTable: "MigrationBatch",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StagedFile",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Extension = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    QuarantineKey = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    MimeType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Scan = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    QueueReason = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ManifestEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContractStagingRowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MatchedBy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TargetDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentSourceId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleasedFromHold = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagedFile", x => x.Id);
                    table.UniqueConstraint("AK_StagedFile_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_StagedFile_MigrationBatch_TenantId_BatchId",
                        columns: x => new { x.TenantId, x.BatchId },
                        principalSchema: "migration",
                        principalTable: "MigrationBatch",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StagingMessage",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    FieldName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagingMessage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StagingMessage_MigrationBatch_TenantId_BatchId",
                        columns: x => new { x.TenantId, x.BatchId },
                        principalSchema: "migration",
                        principalTable: "MigrationBatch",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StagingRow",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    RowNumber = table.Column<int>(type: "int", nullable: false),
                    RawValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MappedValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SourceRecordId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AgreementReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlannedAction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReviewDecision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TargetContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ErrorCount = table.Column<int>(type: "int", nullable: false),
                    WarningCount = table.Column<int>(type: "int", nullable: false),
                    PlanSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagingRow", x => x.Id);
                    table.UniqueConstraint("AK_StagingRow_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_StagingRow_MigrationBatch_TenantId_BatchId",
                        columns: x => new { x.TenantId, x.BatchId },
                        principalSchema: "migration",
                        principalTable: "MigrationBatch",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MappingProfileVersion",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DefinitionHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PublishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MappingProfileVersion", x => x.Id);
                    table.UniqueConstraint("AK_MappingProfileVersion_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_MappingProfileVersion_MappingProfile_TenantId_ProfileId",
                        columns: x => new { x.TenantId, x.ProfileId },
                        principalSchema: "migration",
                        principalTable: "MappingProfile",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MatchProposal",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StagedFileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractStagingRowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CandidateCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ConfirmedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ConfirmedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchProposal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MatchProposal_StagedFile_TenantId_StagedFileId",
                        columns: x => new { x.TenantId, x.StagedFileId },
                        principalSchema: "migration",
                        principalTable: "StagedFile",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MatchProposal_StagingRow_TenantId_ContractStagingRowId",
                        columns: x => new { x.TenantId, x.ContractStagingRowId },
                        principalSchema: "migration",
                        principalTable: "StagingRow",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationBatch_TenantId_MappingProfileVersionId",
                schema: "migration",
                table: "MigrationBatch",
                columns: new[] { "TenantId", "MappingProfileVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentManifestEntry_TenantId_BatchId_Sequence",
                schema: "migration",
                table: "AttachmentManifestEntry",
                columns: new[] { "TenantId", "BatchId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Checkpoint_TenantId_BatchId_Phase",
                schema: "migration",
                table: "Checkpoint",
                columns: new[] { "TenantId", "BatchId", "Phase" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MappingProfile_TenantId_OrganisationId_Name",
                schema: "migration",
                table: "MappingProfile",
                columns: new[] { "TenantId", "OrganisationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MappingProfileVersion_TenantId_ProfileId_VersionNumber",
                schema: "migration",
                table: "MappingProfileVersion",
                columns: new[] { "TenantId", "ProfileId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MatchProposal_TenantId_BatchId_StagedFileId",
                schema: "migration",
                table: "MatchProposal",
                columns: new[] { "TenantId", "BatchId", "StagedFileId" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchProposal_TenantId_ContractStagingRowId",
                schema: "migration",
                table: "MatchProposal",
                columns: new[] { "TenantId", "ContractStagingRowId" });

            migrationBuilder.CreateIndex(
                name: "IX_MatchProposal_TenantId_StagedFileId",
                schema: "migration",
                table: "MatchProposal",
                columns: new[] { "TenantId", "StagedFileId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProvenanceRecord_TenantId_BatchId_ReversalState",
                schema: "migration",
                table: "ProvenanceRecord",
                columns: new[] { "TenantId", "BatchId", "ReversalState" });

            migrationBuilder.CreateIndex(
                name: "IX_ProvenanceRecord_TenantId_BatchId_TargetEntityType_TargetEntityId_Action",
                schema: "migration",
                table: "ProvenanceRecord",
                columns: new[] { "TenantId", "BatchId", "TargetEntityType", "TargetEntityId", "Action" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProvenanceRecord_TenantId_TargetEntityType_TargetEntityId",
                schema: "migration",
                table: "ProvenanceRecord",
                columns: new[] { "TenantId", "TargetEntityType", "TargetEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationReport_TenantId_BatchId_Sequence",
                schema: "migration",
                table: "ReconciliationReport",
                columns: new[] { "TenantId", "BatchId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceFile_TenantId_BatchId_Role",
                schema: "migration",
                table: "SourceFile",
                columns: new[] { "TenantId", "BatchId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_StagedFile_TenantId_BatchId_RelativePath",
                schema: "migration",
                table: "StagedFile",
                columns: new[] { "TenantId", "BatchId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StagedFile_TenantId_BatchId_State",
                schema: "migration",
                table: "StagedFile",
                columns: new[] { "TenantId", "BatchId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_StagedFile_TenantId_Sha256",
                schema: "migration",
                table: "StagedFile",
                columns: new[] { "TenantId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_StagingMessage_TenantId_BatchId_Phase_Severity_Code",
                schema: "migration",
                table: "StagingMessage",
                columns: new[] { "TenantId", "BatchId", "Phase", "Severity", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_StagingMessage_TenantId_BatchId_SubjectId",
                schema: "migration",
                table: "StagingMessage",
                columns: new[] { "TenantId", "BatchId", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_StagingRow_TenantId_BatchId_Outcome",
                schema: "migration",
                table: "StagingRow",
                columns: new[] { "TenantId", "BatchId", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_StagingRow_TenantId_BatchId_Sequence",
                schema: "migration",
                table: "StagingRow",
                columns: new[] { "TenantId", "BatchId", "Sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MigrationBatch_MappingProfileVersion_TenantId_MappingProfileVersionId",
                schema: "migration",
                table: "MigrationBatch",
                columns: new[] { "TenantId", "MappingProfileVersionId" },
                principalSchema: "migration",
                principalTable: "MappingProfileVersion",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddTenantPolicy("migration", "MappingProfile");
            migrationBuilder.AddTenantPolicy("migration", "MappingProfileVersion");
            migrationBuilder.AddTenantPolicy("migration", "SourceFile");
            migrationBuilder.AddTenantPolicy("migration", "StagingRow");
            migrationBuilder.AddTenantPolicy("migration", "StagingMessage");
            migrationBuilder.AddTenantPolicy("migration", "AttachmentManifestEntry");
            migrationBuilder.AddTenantPolicy("migration", "StagedFile");
            migrationBuilder.AddTenantPolicy("migration", "MatchProposal");
            migrationBuilder.AddTenantPolicy("migration", "Checkpoint");
            migrationBuilder.AddTenantPolicy("migration", "ProvenanceRecord");
            migrationBuilder.AddTenantPolicy("migration", "ReconciliationReport");
            migrationBuilder.GrantRuntimeAccess("migration");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("migration", "MappingProfile");
            migrationBuilder.DropTenantPolicy("migration", "MappingProfileVersion");
            migrationBuilder.DropTenantPolicy("migration", "SourceFile");
            migrationBuilder.DropTenantPolicy("migration", "StagingRow");
            migrationBuilder.DropTenantPolicy("migration", "StagingMessage");
            migrationBuilder.DropTenantPolicy("migration", "AttachmentManifestEntry");
            migrationBuilder.DropTenantPolicy("migration", "StagedFile");
            migrationBuilder.DropTenantPolicy("migration", "MatchProposal");
            migrationBuilder.DropTenantPolicy("migration", "Checkpoint");
            migrationBuilder.DropTenantPolicy("migration", "ProvenanceRecord");
            migrationBuilder.DropTenantPolicy("migration", "ReconciliationReport");

            migrationBuilder.DropForeignKey(
                name: "FK_MigrationBatch_MappingProfileVersion_TenantId_MappingProfileVersionId",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropTable(
                name: "AttachmentManifestEntry",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "Checkpoint",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "MappingProfileVersion",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "MatchProposal",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "ProvenanceRecord",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "ReconciliationReport",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "SourceFile",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "StagingMessage",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "MappingProfile",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "StagedFile",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "StagingRow",
                schema: "migration");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MigrationBatch_TenantId_Id",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropIndex(
                name: "IX_MigrationBatch_TenantId_MappingProfileVersionId",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                schema: "migration",
                table: "MigrationProject");

            migrationBuilder.DropColumn(
                name: "LegacyAllowOverwriteLocked",
                schema: "migration",
                table: "MigrationProject");

            migrationBuilder.DropColumn(
                name: "LegacyAllowedStatusCodes",
                schema: "migration",
                table: "MigrationProject");

            migrationBuilder.DropColumn(
                name: "LegacyOverwriteReason",
                schema: "migration",
                table: "MigrationProject");

            migrationBuilder.DropColumn(
                name: "ReferencePolicy",
                schema: "migration",
                table: "MigrationProject");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "migration",
                table: "MigrationProject");

            migrationBuilder.DropColumn(
                name: "TargetFreezeEnabled",
                schema: "migration",
                table: "MigrationProject");

            migrationBuilder.DropColumn(
                name: "AcceptanceNote",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "CommitCompletedUtc",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "CommitStartedUtc",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "CommittedBy",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "CommittedByUserId",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "ConfigReleaseNumber",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "ConfirmedBy",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "ConfirmedByUserId",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "ConfirmedFingerprint",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "ConfirmedUtc",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresUtc",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "Name",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "RollbackReason",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "RolledBackBy",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "RolledBackUtc",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "ValidatedUtc",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "ValidationFingerprint",
                schema: "migration",
                table: "MigrationBatch");

            migrationBuilder.DropColumn(
                name: "ValidationSummaryJson",
                schema: "migration",
                table: "MigrationBatch");
        }
    }
}
