<#
.SYNOPSIS
    One-off switch-over from the old Enterprise Contract Platform to JBS Contracts: empties the old app's tables
    from the database (and its old data sub-folders), so JBS Contracts can install into the same database.

.DESCRIPTION
    Safe by design (decision D-20):
      - The old app is recognised by its Entity Framework history tables (__EFMigrationsHistory, one per old schema).
        JBS Contracts never creates such a table. (A schema name is NOT a reliable sign: the old app also used a
        "contracts" schema, which hid it from the first version of this script.)
      - Does NOTHING when no old-app table is present, so once the old app is gone, leaving the variable set by
        mistake can never wipe JBS Contracts.
      - Otherwise it stops the deploy, before anything changes, unless -Confirm equals the database name.
        When confirmed it removes everything, including any JBS Contracts tables installed alongside the old ones;
        the JBS Contracts scripts then run again on the empty database. Set the QA environment variable RESET_DATABASE_CONFIRM to the
        database name (JBSContracts) for one deploy, then delete the variable.
    When confirmed it drops, in this order: security policies, foreign keys, views, procedures, functions, tables,
    sequences, synonyms and user types in every user schema, then the empty non-dbo user schemas. Then it removes
    everything in the data folder except Keys and fonts.

    Uses System.Data.SqlClient (built into Windows PowerShell 5.1) and integrated authentication: it runs as the
    runner's account (the migration identity: db_ddladmin, db_datareader, db_datawriter). Keep this file ASCII-only.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ConnectionString,

    [string]$Confirm = "",

    [string]$DataPath = ""
)

$ErrorActionPreference = "Stop"

function Invoke-Scalar($connection, [string]$sql) {
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 600
    $command.CommandText = $sql
    return $command.ExecuteScalar()
}

function Invoke-NonQuery($connection, [string]$sql) {
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 600
    $command.CommandText = $sql
    [void]$command.ExecuteNonQuery()
}

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
$database = $builder.InitialCatalog
$connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
$connection.add_InfoMessage({ param($sender, $e) Write-Host "      $($e.Message)" })
$connection.Open()
try {
    $oldApp = [int](Invoke-Scalar $connection "SELECT COUNT(*) FROM sys.tables WHERE name = N'__EFMigrationsHistory'")
    if ($oldApp -eq 0) {
        Write-Host "==> No tables from the old app in $database. No reset needed."
        return
    }
    $tables = [int](Invoke-Scalar $connection "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0")
    if ($Confirm -ne $database) {
        throw "The database $database still holds the old app ($tables tables). To replace it with JBS Contracts, set the QA environment variable RESET_DATABASE_CONFIRM to $database, run the deploy again, then delete the variable. Nothing has been changed."
    }

    Write-Host "==> Removing the old app's $tables tables from $database (RESET_DATABASE_CONFIRM matched)"
    $reset = @"
SET NOCOUNT ON;
DECLARE @sql NVARCHAR(MAX);
DECLARE @steps TABLE (Seq INT, Stmt NVARCHAR(MAX));

INSERT @steps SELECT 1, N'DROP SECURITY POLICY ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) FROM sys.security_policies;
INSERT @steps SELECT 2, N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(fk.name)
    FROM sys.foreign_keys fk JOIN sys.tables t ON t.object_id = fk.parent_object_id WHERE t.is_ms_shipped = 0;
INSERT @steps SELECT 3, N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' SET (SYSTEM_VERSIONING = OFF)'
    FROM sys.tables WHERE temporal_type = 2;
INSERT @steps SELECT 4, N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) FROM sys.views WHERE is_ms_shipped = 0;
INSERT @steps SELECT 5, N'DROP PROCEDURE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) FROM sys.procedures WHERE is_ms_shipped = 0;
INSERT @steps SELECT 6, N'DROP FUNCTION ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name)
    FROM sys.objects WHERE type IN ('FN', 'IF', 'TF', 'FS', 'FT') AND is_ms_shipped = 0;
INSERT @steps SELECT 7, N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) FROM sys.tables WHERE is_ms_shipped = 0;
INSERT @steps SELECT 8, N'DROP SEQUENCE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) FROM sys.sequences;
INSERT @steps SELECT 9, N'DROP SYNONYM ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) FROM sys.synonyms;
INSERT @steps SELECT 10, N'DROP TYPE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) FROM sys.types WHERE is_user_defined = 1;
INSERT @steps SELECT 11, N'DROP SCHEMA ' + QUOTENAME(s.name) FROM sys.schemas s
    WHERE s.schema_id BETWEEN 5 AND 16383 AND s.name NOT IN (N'dbo', N'guest', N'sys', N'INFORMATION_SCHEMA')
      AND NOT EXISTS (SELECT 1 FROM sys.database_principals p WHERE p.default_schema_name = s.name)
      AND NOT EXISTS (SELECT 1 FROM sys.database_principals p WHERE p.name = s.name);

DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Stmt FROM @steps ORDER BY Seq;
OPEN c;
FETCH NEXT FROM c INTO @sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC sys.sp_executesql @sql;
    FETCH NEXT FROM c INTO @sql;
END
CLOSE c;
DEALLOCATE c;
"@
    # The temporal tables need their history tables dropped too: step 7 covers them once versioning is off (step 3).
    Invoke-NonQuery $connection $reset
    $left = [int](Invoke-Scalar $connection "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0")
    if ($left -ne 0) { throw "The reset left $left table(s) in $database. Check the log above; nothing else was changed." }
    Write-Host "    old tables removed"
} finally {
    $connection.Close()
}

if ($DataPath -and (Test-Path $DataPath)) {
    Write-Host "==> Clearing the old app's data folders in $DataPath (keeping Keys and fonts)"
    Get-ChildItem -Path $DataPath -Force | Where-Object { @("Keys", "fonts") -notcontains $_.Name } | ForEach-Object {
        Remove-Item -Path $_.FullName -Recurse -Force
        Write-Host "    removed $($_.Name)"
    }
}
Write-Host "==> Reset complete. Delete the RESET_DATABASE_CONFIRM variable from the QA environment now."
