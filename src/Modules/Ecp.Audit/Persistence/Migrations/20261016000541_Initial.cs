using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Audit.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Row-Level Security infrastructure shared by every module (created by the first module to migrate).
            migrationBuilder.CreateInfrastructure();

            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "AuditEvent",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ElevationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    MigrationBatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfigReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OldValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PreviousHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvent", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_TenantId_CorrelationId",
                schema: "audit",
                table: "AuditEvent",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_TenantId_EntityType_EntityId",
                schema: "audit",
                table: "AuditEvent",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvent_TenantId_Sequence",
                schema: "audit",
                table: "AuditEvent",
                columns: new[] { "TenantId", "Sequence" },
                unique: true);

            migrationBuilder.AddTenantPolicy("audit", "AuditEvent");
            migrationBuilder.GrantRuntimeAccess("audit");
            // Append-only (INV-50): no UPDATE or DELETE, even for privileged logins. Tests disable the trigger only to simulate tampering.
            migrationBuilder.Sql(@"CREATE TRIGGER audit.TR_AuditEvent_AppendOnly ON audit.AuditEvent INSTEAD OF UPDATE, DELETE AS BEGIN SET NOCOUNT ON; THROW 51000, N'audit.AuditEvent is append-only.', 1; END");
            migrationBuilder.Sql($"DENY UPDATE, DELETE ON audit.AuditEvent TO [{RowLevelSecurity.RuntimeRole}];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit.TR_AuditEvent_AppendOnly;");
            migrationBuilder.DropTenantPolicy("audit", "AuditEvent");
            migrationBuilder.DropInfrastructure();
            migrationBuilder.DropTable(
                name: "AuditEvent",
                schema: "audit");
        }
    }
}
