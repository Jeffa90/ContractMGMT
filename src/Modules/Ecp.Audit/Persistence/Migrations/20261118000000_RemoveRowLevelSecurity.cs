using Ecp.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecp.Audit.Persistence.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Removes SQL Server Row-Level Security and the ecp_runtime database role (ADR-0044, owner decision for the single-tenant IIS deployment). Every statement is guarded by
    /// an existence check, so on a database that never had them (every fresh installation of this version) nothing runs. On a database created by an earlier
    /// version it drops every tenant security policy, the two predicate functions, the empty security schema, and the role with its members, grants and DENY.
    /// No table, column, row, index, foreign key, TenantId value, audit event or the audit append-only trigger is touched. The Audit module migrates first
    /// (tier 1), so the policies are gone before any later module's pending migration reads or rewrites data.
    /// </remarks>
    public partial class RemoveRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RowLevelSecurity.RemovalSql);
        }

        /// <inheritdoc />
        /// <remarks>Nothing to undo: the removed objects are not part of this version. Roll back by restoring the verified pre-upgrade backup.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
