using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Operations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailAndAcceptedExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AcceptedException",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AcceptedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AcceptedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AcceptedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcceptedException", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailConfiguration",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Port = table.Column<int>(type: "int", nullable: false),
                    UseTls = table.Column<bool>(type: "bit", nullable: false),
                    UserRef = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PasswordRef = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailConfiguration", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailTestResult",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TestedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TestedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RecipientMasked = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ConnectivityOutcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ConnectivityCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ConnectivityDetail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DeliveryOutcome = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DeliveryCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DeliveryDetail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Simulated = table.Column<bool>(type: "bit", nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTestResult", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedException_TenantId_Kind_Code",
                schema: "ops",
                table: "AcceptedException",
                columns: new[] { "TenantId", "Kind", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailTestResult_TenantId_TestedUtc",
                schema: "ops",
                table: "EmailTestResult",
                columns: new[] { "TenantId", "TestedUtc" });

            migrationBuilder.AddTenantPolicy("ops", "AcceptedException");
            migrationBuilder.AddTenantPolicy("ops", "EmailConfiguration");
            migrationBuilder.AddTenantPolicy("ops", "EmailTestResult");
            migrationBuilder.GrantRuntimeAccess("ops");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("ops", "AcceptedException");
            migrationBuilder.DropTenantPolicy("ops", "EmailConfiguration");
            migrationBuilder.DropTenantPolicy("ops", "EmailTestResult");

            migrationBuilder.DropTable(
                name: "AcceptedException",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "EmailConfiguration",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "EmailTestResult",
                schema: "ops");
        }
    }
}
