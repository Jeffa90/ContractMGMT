using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Tenancy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganisationStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Division",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Division", x => x.Id);
                    table.UniqueConstraint("AK_Division_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_Division_Organisation_TenantId_OrganisationId",
                        columns: x => new { x.TenantId, x.OrganisationId },
                        principalSchema: "tenancy",
                        principalTable: "Organisation",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegalEntity",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistrationNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalEntity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LegalEntity_Organisation_TenantId_OrganisationId",
                        columns: x => new { x.TenantId, x.OrganisationId },
                        principalSchema: "tenancy",
                        principalTable: "Organisation",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Site",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Site", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Site_Organisation_TenantId_OrganisationId",
                        columns: x => new { x.TenantId, x.OrganisationId },
                        principalSchema: "tenancy",
                        principalTable: "Organisation",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BusinessUnit",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DivisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessUnit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessUnit_Division_TenantId_DivisionId",
                        columns: x => new { x.TenantId, x.DivisionId },
                        principalSchema: "tenancy",
                        principalTable: "Division",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BusinessUnit_Organisation_TenantId_OrganisationId",
                        columns: x => new { x.TenantId, x.OrganisationId },
                        principalSchema: "tenancy",
                        principalTable: "Organisation",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessUnit_TenantId_DivisionId",
                schema: "tenancy",
                table: "BusinessUnit",
                columns: new[] { "TenantId", "DivisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessUnit_TenantId_OrganisationId_Key",
                schema: "tenancy",
                table: "BusinessUnit",
                columns: new[] { "TenantId", "OrganisationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Division_TenantId_OrganisationId_Key",
                schema: "tenancy",
                table: "Division",
                columns: new[] { "TenantId", "OrganisationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntity_TenantId_OrganisationId_Key",
                schema: "tenancy",
                table: "LegalEntity",
                columns: new[] { "TenantId", "OrganisationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Site_TenantId_OrganisationId_Key",
                schema: "tenancy",
                table: "Site",
                columns: new[] { "TenantId", "OrganisationId", "Key" },
                unique: true);

            migrationBuilder.AddTenantPolicy("tenancy", "LegalEntity");
            migrationBuilder.AddTenantPolicy("tenancy", "Division");
            migrationBuilder.AddTenantPolicy("tenancy", "BusinessUnit");
            migrationBuilder.AddTenantPolicy("tenancy", "Site");
            migrationBuilder.GrantRuntimeAccess("tenancy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("tenancy", "LegalEntity");
            migrationBuilder.DropTenantPolicy("tenancy", "Division");
            migrationBuilder.DropTenantPolicy("tenancy", "BusinessUnit");
            migrationBuilder.DropTenantPolicy("tenancy", "Site");

            migrationBuilder.DropTable(
                name: "BusinessUnit",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "LegalEntity",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "Site",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "Division",
                schema: "tenancy");
        }
    }
}
