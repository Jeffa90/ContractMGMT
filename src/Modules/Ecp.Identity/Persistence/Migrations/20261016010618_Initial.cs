using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "GroupRoleMapping",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    GroupKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RoleCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ViewerKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ScopeLevel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OrganisationIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BusinessUnitIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DivisionIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SiteIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CategoryIds = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClassificationCeiling = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ApproverGroupKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupRoleMapping", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityProvider",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Authority = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClientSecretRef = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EntraTenantId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Scopes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    GroupsClaim = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    TestSignInUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProvider", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfile",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    EmailNormalised = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActivatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSignInUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfile", x => x.Id);
                    table.UniqueConstraint("AK_UserProfile_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "ExternalIdentity",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Issuer = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    LinkedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalIdentity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalIdentity_UserProfile_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalSchema: "identity",
                        principalTable: "UserProfile",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoleAssignment",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ViewerKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ScopeLevel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OrganisationIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BusinessUnitIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DivisionIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SiteIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CategoryIds = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClassificationCeiling = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ApproverGroupKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EffectiveFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    GrantedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GrantedByLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RoleAssignment_UserProfile_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalSchema: "identity",
                        principalTable: "UserProfile",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentity_TenantId_Issuer_Subject",
                schema: "identity",
                table: "ExternalIdentity",
                columns: new[] { "TenantId", "Issuer", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentity_TenantId_UserId",
                schema: "identity",
                table: "ExternalIdentity",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupRoleMapping_TenantId_ProviderKey_GroupKey",
                schema: "identity",
                table: "GroupRoleMapping",
                columns: new[] { "TenantId", "ProviderKey", "GroupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProvider_TenantId_Key",
                schema: "identity",
                table: "IdentityProvider",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignment_TenantId_RoleCode",
                schema: "identity",
                table: "RoleAssignment",
                columns: new[] { "TenantId", "RoleCode" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignment_TenantId_UserId",
                schema: "identity",
                table: "RoleAssignment",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserProfile_TenantId_EmailNormalised",
                schema: "identity",
                table: "UserProfile",
                columns: new[] { "TenantId", "EmailNormalised" },
                unique: true);

            migrationBuilder.AddTenantPolicy("identity", "UserProfile");
            migrationBuilder.AddTenantPolicy("identity", "ExternalIdentity");
            migrationBuilder.AddTenantPolicy("identity", "RoleAssignment");
            migrationBuilder.AddTenantPolicy("identity", "GroupRoleMapping");
            migrationBuilder.AddTenantPolicy("identity", "IdentityProvider");
            migrationBuilder.GrantRuntimeAccess("identity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("identity", "UserProfile");
            migrationBuilder.DropTenantPolicy("identity", "ExternalIdentity");
            migrationBuilder.DropTenantPolicy("identity", "RoleAssignment");
            migrationBuilder.DropTenantPolicy("identity", "GroupRoleMapping");
            migrationBuilder.DropTenantPolicy("identity", "IdentityProvider");

            migrationBuilder.DropTable(
                name: "ExternalIdentity",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "GroupRoleMapping",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "IdentityProvider",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "RoleAssignment",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "UserProfile",
                schema: "identity");
        }
    }
}
