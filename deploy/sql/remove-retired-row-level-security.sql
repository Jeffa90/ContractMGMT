-- Removes the retired SQL Server Row-Level Security objects and the ecp_runtime database role (ADR-0044).
-- For a database created by a version before ADR-0044 only. Run by a DBA (ALTER ANY SECURITY POLICY and control of the
-- ecp_runtime role are needed, so db_owner or equivalent) AFTER a verified backup and BEFORE the upgrade pre-flight.
-- Safe to run more than once: every statement is guarded by an existence check. It touches no table, row or audit event.
-- This text is the same as Ecp.Persistence.RowLevelSecurity.RemovalSql (a test keeps them identical).
DECLARE @sql nvarchar(max);
DECLARE policies CURSOR LOCAL FAST_FORWARD FOR
    SELECT N'DROP SECURITY POLICY ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
    FROM sys.security_policies WHERE name LIKE N'TenantPolicy[_]%';
OPEN policies; FETCH NEXT FROM policies INTO @sql;
WHILE @@FETCH_STATUS = 0 BEGIN EXEC sys.sp_executesql @sql; FETCH NEXT FROM policies INTO @sql; END;
CLOSE policies; DEALLOCATE policies;
IF OBJECT_ID(N'security.fn_TenantRegistryPredicate', N'IF') IS NOT NULL EXEC(N'DROP FUNCTION security.fn_TenantRegistryPredicate;');
IF OBJECT_ID(N'security.fn_TenantPredicate', N'IF') IS NOT NULL EXEC(N'DROP FUNCTION security.fn_TenantPredicate;');
IF SCHEMA_ID(N'security') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = SCHEMA_ID(N'security'))
    EXEC(N'DROP SCHEMA security;');
IF DATABASE_PRINCIPAL_ID(N'ecp_runtime') IS NOT NULL
BEGIN
    DECLARE members CURSOR LOCAL FAST_FORWARD FOR
        SELECT N'ALTER ROLE [ecp_runtime] DROP MEMBER ' + QUOTENAME(USER_NAME(member_principal_id)) + N';'
        FROM sys.database_role_members WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'ecp_runtime');
    OPEN members; FETCH NEXT FROM members INTO @sql;
    WHILE @@FETCH_STATUS = 0 BEGIN EXEC sys.sp_executesql @sql; FETCH NEXT FROM members INTO @sql; END;
    CLOSE members; DEALLOCATE members;
    EXEC(N'DROP ROLE [ecp_runtime];');
END;
