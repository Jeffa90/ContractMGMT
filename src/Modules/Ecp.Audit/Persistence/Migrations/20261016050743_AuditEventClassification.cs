using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Audit.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditEventClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Classification",
                schema: "audit",
                table: "AuditEvent",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Classification",
                schema: "audit",
                table: "AuditEvent");
        }
    }
}
