using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Configuration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigurationReviewEvent",
                schema: "configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReleaseNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationReviewEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DraftReview",
                schema: "configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SubmittedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    SubmissionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecidedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionComment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RollbackOfNumber = table.Column<int>(type: "int", nullable: true),
                    RollbackReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DraftReview", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseEvidence",
                schema: "configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LastEditedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SubmittedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SubmittedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    GovernanceMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseEvidence", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationReviewEvent_TenantId_DraftId_OccurredUtc",
                schema: "configuration",
                table: "ConfigurationReviewEvent",
                columns: new[] { "TenantId", "DraftId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DraftReview_TenantId_State",
                schema: "configuration",
                table: "DraftReview",
                columns: new[] { "TenantId", "State" });

            migrationBuilder.AddTenantPolicy("configuration", "DraftReview");
            migrationBuilder.AddTenantPolicy("configuration", "ReleaseEvidence");
            migrationBuilder.AddTenantPolicy("configuration", "ConfigurationReviewEvent");
            migrationBuilder.GrantRuntimeAccess("configuration");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("configuration", "DraftReview");
            migrationBuilder.DropTenantPolicy("configuration", "ReleaseEvidence");
            migrationBuilder.DropTenantPolicy("configuration", "ConfigurationReviewEvent");

            migrationBuilder.DropTable(
                name: "ConfigurationReviewEvent",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "DraftReview",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "ReleaseEvidence",
                schema: "configuration");
        }
    }
}
