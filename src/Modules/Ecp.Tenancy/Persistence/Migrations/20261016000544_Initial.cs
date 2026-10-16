using System;
using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Tenancy.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tenancy");

            migrationBuilder.CreateTable(
                name: "Organisation",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CultureCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DefaultCurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    ReferencePrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ReferenceSeparator = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false),
                    ReferenceDigits = table.Column<int>(type: "int", nullable: false),
                    SolutionPackKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organisation", x => x.Id);
                    table.UniqueConstraint("AK_Organisation_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Tenant",
                schema: "tenancy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TenancyMode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ConfigurationMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenant", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Organisation_TenantId_Name",
                schema: "tenancy",
                table: "Organisation",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Organisation_TenantId_ReferencePrefix",
                schema: "tenancy",
                table: "Organisation",
                columns: new[] { "TenantId", "ReferencePrefix" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenant_Name",
                schema: "tenancy",
                table: "Tenant",
                column: "Name",
                unique: true);

            migrationBuilder.AddTenantPolicy("tenancy", "Tenant", "Id", registry: true);
            migrationBuilder.AddTenantPolicy("tenancy", "Organisation");
            migrationBuilder.GrantRuntimeAccess("tenancy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTenantPolicy("tenancy", "Tenant");
            migrationBuilder.DropTenantPolicy("tenancy", "Organisation");
            migrationBuilder.DropTable(
                name: "Organisation",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "Tenant",
                schema: "tenancy");
        }
    }
}
