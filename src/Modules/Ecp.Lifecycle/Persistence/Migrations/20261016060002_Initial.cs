using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Lifecycle.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "lifecycle");

            migrationBuilder.CreateTable(
                name: "ApprovalRequest",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApproverGroupKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SubmittedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExecutedDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExecutedDocumentSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SnapshotSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EligibleApproversJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DecidedAsRole = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    OnBehalfOfUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OnBehalfOfName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DecisionComment = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EvidenceDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionRecord",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EnvelopeId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    ExecutedDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExecutedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StartedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StatusUpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegalReview",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CurrentRound = table.Column<int>(type: "int", nullable: false),
                    AssignedToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedToName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SubmittedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClearedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClearedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClearedContractStampUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastOutcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalReview", x => x.Id);
                    table.UniqueConstraint("AK_LegalReview_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "LifecycleEvent",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SubjectKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Classification = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DispatchedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DispatchAttempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecordUnlock",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpenedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    FieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OpenedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReauthenticatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CloseKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ChangeCount = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordUnlock", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Renewal",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PredecessorContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuccessorContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StartedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CopiedFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CopiedDocumentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClosedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CloseReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Renewal", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StatusHistory",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ToStatus = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TransitionCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    WorkflowRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ConfigReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    ClientRequestId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ElevationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubjectDocument",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AddedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubjectDocument", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Variation",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ProposedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsOpen = table.Column<bool>(type: "bit", nullable: false),
                    LegalReviewRequired = table.Column<bool>(type: "bit", nullable: false),
                    FinancialImpactAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    FinancialImpactCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    FinancialImpactNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProposedChangesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AppliedBeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AppliedAfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    OutcomeReason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    WorkflowRef = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ConfigReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ParentStatusBefore = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Variation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LegalEvent",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoundId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RefId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegalEvent_LegalReview_TenantId_ReviewId",
                        columns: x => new { x.TenantId, x.ReviewId },
                        principalSchema: "lifecycle",
                        principalTable: "LegalReview",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegalRound",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubmittedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SubmissionNote = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ReviewerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReworkReason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ClosedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClosedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DocumentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalRound", x => x.Id);
                    table.UniqueConstraint("AK_LegalRound_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_LegalRound_LegalReview_TenantId_ReviewId",
                        columns: x => new { x.TenantId, x.ReviewId },
                        principalSchema: "lifecycle",
                        principalTable: "LegalReview",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegalAttachment",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoundId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    AddedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalAttachment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegalAttachment_LegalRound_TenantId_RoundId",
                        columns: x => new { x.TenantId, x.RoundId },
                        principalSchema: "lifecycle",
                        principalTable: "LegalRound",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegalComment",
                schema: "lifecycle",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoundId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentCommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Side = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    Classification = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalComment", x => x.Id);
                    table.UniqueConstraint("AK_LegalComment_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_LegalComment_LegalRound_TenantId_RoundId",
                        columns: x => new { x.TenantId, x.RoundId },
                        principalSchema: "lifecycle",
                        principalTable: "LegalRound",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequest_TenantId_ContractId_SubmittedUtc",
                schema: "lifecycle",
                table: "ApprovalRequest",
                columns: new[] { "TenantId", "ContractId", "SubmittedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequest_TenantId_Status_SubmittedUtc",
                schema: "lifecycle",
                table: "ApprovalRequest",
                columns: new[] { "TenantId", "Status", "SubmittedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequest_TenantId_SubjectKind_SubjectId",
                schema: "lifecycle",
                table: "ApprovalRequest",
                columns: new[] { "TenantId", "SubjectKind", "SubjectId" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionRecord_TenantId_Provider_EnvelopeId",
                schema: "lifecycle",
                table: "ExecutionRecord",
                columns: new[] { "TenantId", "Provider", "EnvelopeId" },
                filter: "[EnvelopeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionRecord_TenantId_SubjectKind_SubjectId_IsCurrent",
                schema: "lifecycle",
                table: "ExecutionRecord",
                columns: new[] { "TenantId", "SubjectKind", "SubjectId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalAttachment_TenantId_RoundId",
                schema: "lifecycle",
                table: "LegalAttachment",
                columns: new[] { "TenantId", "RoundId" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalComment_TenantId_RoundId_CreatedUtc",
                schema: "lifecycle",
                table: "LegalComment",
                columns: new[] { "TenantId", "RoundId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalEvent_TenantId_ReviewId_Sequence",
                schema: "lifecycle",
                table: "LegalEvent",
                columns: new[] { "TenantId", "ReviewId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegalReview_TenantId_ContractId",
                schema: "lifecycle",
                table: "LegalReview",
                columns: new[] { "TenantId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalReview_TenantId_Status_LastActivityUtc",
                schema: "lifecycle",
                table: "LegalReview",
                columns: new[] { "TenantId", "Status", "LastActivityUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalReview_TenantId_SubjectKind_SubjectId",
                schema: "lifecycle",
                table: "LegalReview",
                columns: new[] { "TenantId", "SubjectKind", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegalRound_TenantId_ReviewId_Number",
                schema: "lifecycle",
                table: "LegalRound",
                columns: new[] { "TenantId", "ReviewId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleEvent_TenantId_ContractId_CreatedUtc",
                schema: "lifecycle",
                table: "LifecycleEvent",
                columns: new[] { "TenantId", "ContractId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleEvent_TenantId_DispatchedUtc_CreatedUtc",
                schema: "lifecycle",
                table: "LifecycleEvent",
                columns: new[] { "TenantId", "DispatchedUtc", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordUnlock_TenantId_ClosedUtc_ExpiresUtc",
                schema: "lifecycle",
                table: "RecordUnlock",
                columns: new[] { "TenantId", "ClosedUtc", "ExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordUnlock_TenantId_ContractId_OpenedUtc",
                schema: "lifecycle",
                table: "RecordUnlock",
                columns: new[] { "TenantId", "ContractId", "OpenedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Renewal_TenantId_PredecessorContractId_Status",
                schema: "lifecycle",
                table: "Renewal",
                columns: new[] { "TenantId", "PredecessorContractId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Renewal_TenantId_SuccessorContractId",
                schema: "lifecycle",
                table: "Renewal",
                columns: new[] { "TenantId", "SuccessorContractId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Renewal_OneInProgress",
                schema: "lifecycle",
                table: "Renewal",
                columns: new[] { "TenantId", "PredecessorContractId" },
                unique: true,
                filter: "[Status] = 'InProgress'");

            migrationBuilder.CreateIndex(
                name: "IX_StatusHistory_TenantId_ContractId_Sequence",
                schema: "lifecycle",
                table: "StatusHistory",
                columns: new[] { "TenantId", "ContractId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusHistory_TenantId_SubjectId_ClientRequestId",
                schema: "lifecycle",
                table: "StatusHistory",
                columns: new[] { "TenantId", "SubjectId", "ClientRequestId" },
                unique: true,
                filter: "[ClientRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StatusHistory_TenantId_SubjectId_OccurredUtc",
                schema: "lifecycle",
                table: "StatusHistory",
                columns: new[] { "TenantId", "SubjectId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SubjectDocument_TenantId_SubjectKind_SubjectId_DocumentId",
                schema: "lifecycle",
                table: "SubjectDocument",
                columns: new[] { "TenantId", "SubjectKind", "SubjectId", "DocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Variation_TenantId_ContractId_Sequence",
                schema: "lifecycle",
                table: "Variation",
                columns: new[] { "TenantId", "ContractId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Variation_TenantId_IsOpen_UpdatedUtc",
                schema: "lifecycle",
                table: "Variation",
                columns: new[] { "TenantId", "IsOpen", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Variation_TenantId_Reference",
                schema: "lifecycle",
                table: "Variation",
                columns: new[] { "TenantId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Variation_OneOpen",
                schema: "lifecycle",
                table: "Variation",
                columns: new[] { "TenantId", "ContractId" },
                unique: true,
                filter: "[IsOpen] = 1");

            migrationBuilder.AddTenantPolicy("lifecycle", "StatusHistory");
            migrationBuilder.AddTenantPolicy("lifecycle", "LegalReview");
            migrationBuilder.AddTenantPolicy("lifecycle", "LegalRound");
            migrationBuilder.AddTenantPolicy("lifecycle", "LegalComment");
            migrationBuilder.AddTenantPolicy("lifecycle", "LegalAttachment");
            migrationBuilder.AddTenantPolicy("lifecycle", "LegalEvent");
            migrationBuilder.AddTenantPolicy("lifecycle", "ExecutionRecord");
            migrationBuilder.AddTenantPolicy("lifecycle", "ApprovalRequest");
            migrationBuilder.AddTenantPolicy("lifecycle", "RecordUnlock");
            migrationBuilder.AddTenantPolicy("lifecycle", "Variation");
            migrationBuilder.AddTenantPolicy("lifecycle", "Renewal");
            migrationBuilder.AddTenantPolicy("lifecycle", "LifecycleEvent");
            migrationBuilder.AddTenantPolicy("lifecycle", "SubjectDocument");
            migrationBuilder.GrantRuntimeAccess("lifecycle");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("lifecycle", "StatusHistory");
            migrationBuilder.DropTenantPolicy("lifecycle", "LegalReview");
            migrationBuilder.DropTenantPolicy("lifecycle", "LegalRound");
            migrationBuilder.DropTenantPolicy("lifecycle", "LegalComment");
            migrationBuilder.DropTenantPolicy("lifecycle", "LegalAttachment");
            migrationBuilder.DropTenantPolicy("lifecycle", "LegalEvent");
            migrationBuilder.DropTenantPolicy("lifecycle", "ExecutionRecord");
            migrationBuilder.DropTenantPolicy("lifecycle", "ApprovalRequest");
            migrationBuilder.DropTenantPolicy("lifecycle", "RecordUnlock");
            migrationBuilder.DropTenantPolicy("lifecycle", "Variation");
            migrationBuilder.DropTenantPolicy("lifecycle", "Renewal");
            migrationBuilder.DropTenantPolicy("lifecycle", "LifecycleEvent");
            migrationBuilder.DropTenantPolicy("lifecycle", "SubjectDocument");

            migrationBuilder.DropTable(
                name: "ApprovalRequest",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "ExecutionRecord",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "LegalAttachment",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "LegalComment",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "LegalEvent",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "LifecycleEvent",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "RecordUnlock",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "Renewal",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "StatusHistory",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "SubjectDocument",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "Variation",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "LegalRound",
                schema: "lifecycle");

            migrationBuilder.DropTable(
                name: "LegalReview",
                schema: "lifecycle");
        }
    }
}
