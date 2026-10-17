using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Configuration.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSolutionPacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstalledPack",
                schema: "configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PackageId = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    PublisherId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PublisherName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Synthetic = table.Column<bool>(type: "bit", nullable: false),
                    PackageSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ContentDigest = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ManifestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SignatureStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SignatureAlgorithm = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TrustKeyId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    VerificationStatus = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StorageReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ValidationOutcome = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ValidationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CompatibilityJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    InstalledUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    InstalledBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RetiredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetiredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RetiredReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstalledPack", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PackAssignment",
                schema: "configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PackageSha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AssignedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AssignedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OutcomeJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackAssignment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrustedPublisherKey",
                schema: "configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Algorithm = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PublicKeyBase64 = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    NotBeforeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NotAfterUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RegisteredBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RegisteredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustedPublisherKey", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstalledPack_TenantId_PackageSha256",
                schema: "configuration",
                table: "InstalledPack",
                columns: new[] { "TenantId", "PackageSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_InstalledPack_TenantId_PackKey_Version",
                schema: "configuration",
                table: "InstalledPack",
                columns: new[] { "TenantId", "PackKey", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackAssignment_TenantId_OrganisationId_Status",
                schema: "configuration",
                table: "PackAssignment",
                columns: new[] { "TenantId", "OrganisationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PackAssignment_TenantId_PackKey_Version",
                schema: "configuration",
                table: "PackAssignment",
                columns: new[] { "TenantId", "PackKey", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_TrustedPublisherKey_TenantId_KeyId",
                schema: "configuration",
                table: "TrustedPublisherKey",
                columns: new[] { "TenantId", "KeyId" },
                unique: true);

            migrationBuilder.AddTenantPolicy("configuration", "InstalledPack");
            migrationBuilder.AddTenantPolicy("configuration", "PackAssignment");
            migrationBuilder.AddTenantPolicy("configuration", "TrustedPublisherKey");
            migrationBuilder.GrantRuntimeAccess("configuration");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("configuration", "InstalledPack");
            migrationBuilder.DropTenantPolicy("configuration", "PackAssignment");
            migrationBuilder.DropTenantPolicy("configuration", "TrustedPublisherKey");

            migrationBuilder.DropTable(
                name: "InstalledPack",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "PackAssignment",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "TrustedPublisherKey",
                schema: "configuration");
        }
    }
}
