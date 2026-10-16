using Microsoft.EntityFrameworkCore.Migrations;

namespace Ecp.Persistence;

/// <summary>
/// SQL Server Row-Level Security helpers used by module migrations (ADR-0003). Predicates read
/// <c>SESSION_CONTEXT</c> values that <see cref="ScopedConnection"/> sets before every command. With no tenant in the
/// session the predicate matches nothing, so access fails closed even if an application filter is missing.
/// </summary>
public static class RowLevelSecurity
{
    public const string RuntimeRole = "ecp_runtime";

    /// <summary>Creates the security schema, predicate functions and the runtime role. Called once, by the first module migration.</summary>
    public static void CreateInfrastructure(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("IF SCHEMA_ID(N'security') IS NULL EXEC(N'CREATE SCHEMA security');");
        migrationBuilder.Sql($"IF DATABASE_PRINCIPAL_ID(N'{RuntimeRole}') IS NULL CREATE ROLE [{RuntimeRole}];");
        migrationBuilder.Sql(@"
CREATE FUNCTION security.fn_TenantPredicate(@TenantId uniqueidentifier)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN SELECT 1 AS allowed
WHERE @TenantId = TRY_CAST(CAST(SESSION_CONTEXT(N'TenantId') AS nvarchar(36)) AS uniqueidentifier);");
        migrationBuilder.Sql(@"
CREATE FUNCTION security.fn_TenantRegistryPredicate(@TenantId uniqueidentifier)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN SELECT 1 AS allowed
WHERE @TenantId = TRY_CAST(CAST(SESSION_CONTEXT(N'TenantId') AS nvarchar(36)) AS uniqueidentifier)
   OR TRY_CAST(SESSION_CONTEXT(N'IsInstallationScope') AS bit) = 1;");
        // Predicate functions run with the caller's permissions, so the runtime role must be able to read them.
        migrationBuilder.Sql($"GRANT SELECT ON OBJECT::security.fn_TenantPredicate TO [{RuntimeRole}];");
        migrationBuilder.Sql($"GRANT SELECT ON OBJECT::security.fn_TenantRegistryPredicate TO [{RuntimeRole}];");
    }

    public static void DropInfrastructure(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS security.fn_TenantRegistryPredicate;");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS security.fn_TenantPredicate;");
    }

    /// <summary>Protects a table that has a TenantId column: rows are visible and writable only in the matching tenant scope.</summary>
    public static void AddTenantPolicy(this MigrationBuilder migrationBuilder, string schema, string table, string tenantColumn = "TenantId", bool registry = false)
    {
        var fn = registry ? "security.fn_TenantRegistryPredicate" : "security.fn_TenantPredicate";
        migrationBuilder.Sql($@"
CREATE SECURITY POLICY security.[{PolicyName(schema, table)}]
    ADD FILTER PREDICATE {fn}([{tenantColumn}]) ON [{schema}].[{table}],
    ADD BLOCK PREDICATE {fn}([{tenantColumn}]) ON [{schema}].[{table}] AFTER INSERT,
    ADD BLOCK PREDICATE {fn}([{tenantColumn}]) ON [{schema}].[{table}] AFTER UPDATE
    WITH (STATE = ON, SCHEMABINDING = OFF);");
    }

    public static void DropTenantPolicy(this MigrationBuilder migrationBuilder, string schema, string table)
        => migrationBuilder.Sql($"DROP SECURITY POLICY IF EXISTS security.[{PolicyName(schema, table)}];");

    /// <summary>Grants the runtime role data access on a module schema (no schema-owner rights).</summary>
    public static void GrantRuntimeAccess(this MigrationBuilder migrationBuilder, string schema)
        => migrationBuilder.Sql($"GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[{schema}] TO [{RuntimeRole}];");

    public static string PolicyName(string schema, string table) => $"TenantPolicy_{schema}_{table}";
}
