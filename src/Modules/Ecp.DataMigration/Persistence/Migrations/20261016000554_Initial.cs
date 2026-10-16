using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.DataMigration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Microsoft.EntityFrameworkCore.Migrations.Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "migration");

            migrationBuilder.CreateTable(
                name: "MigrationProject",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceSystemVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DataOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ExtractionUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FreezeStartUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FreezeEndUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LegacyLoadEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LegacyAllowRegisteredDateImport = table.Column<bool>(type: "bit", nullable: false),
                    LegacyPolicyExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationProject", x => x.Id);
                    table.UniqueConstraint("AK_MigrationProject_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "MigrationBatch",
                schema: "migration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DuplicateStrategy = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DocumentDuplicateOption = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsLegacyLoad = table.Column<bool>(type: "bit", nullable: false),
                    AllowRegisteredDateImport = table.Column<bool>(type: "bit", nullable: false),
                    MappingProfileVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfigReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceRows = table.Column<long>(type: "bigint", nullable: false),
                    Accepted = table.Column<long>(type: "bigint", nullable: false),
                    Rejected = table.Column<long>(type: "bigint", nullable: false),
                    Skipped = table.Column<long>(type: "bigint", nullable: false),
                    Created = table.Column<long>(type: "bigint", nullable: false),
                    Updated = table.Column<long>(type: "bigint", nullable: false),
                    ExceptionsApproved = table.Column<bool>(type: "bit", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AcceptedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AcceptedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationBatch", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationBatch_MigrationProject_TenantId_ProjectId",
                        columns: x => new { x.TenantId, x.ProjectId },
                        principalSchema: "migration",
                        principalTable: "MigrationProject",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationBatch_TenantId_ProjectId",
                schema: "migration",
                table: "MigrationBatch",
                columns: new[] { "TenantId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationProject_TenantId_OrganisationId_Name",
                schema: "migration",
                table: "MigrationProject",
                columns: new[] { "TenantId", "OrganisationId", "Name" },
                unique: true);

            migrationBuilder.AddTenantPolicy("migration", "MigrationProject");
            migrationBuilder.AddTenantPolicy("migration", "MigrationBatch");
            migrationBuilder.GrantRuntimeAccess("migration");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("migration", "MigrationProject");
            migrationBuilder.DropTenantPolicy("migration", "MigrationBatch");
            migrationBuilder.DropTable(
                name: "MigrationBatch",
                schema: "migration");

            migrationBuilder.DropTable(
                name: "MigrationProject",
                schema: "migration");
        }
    }
}
