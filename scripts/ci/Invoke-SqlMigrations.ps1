<#
.SYNOPSIS
    Applies numbered *.sql scripts to a SQL Server database exactly once each,
    tracking what has run in a dbo.SchemaVersions table.

.DESCRIPTION
    Generic - reusable by any app that keeps its schema as ordered SQL files
    (01_schema.sql, 02_..., ...). On each run it:
      1. Creates dbo.SchemaVersions if it doesn't exist.
      2. Lists *.sql files in -ScriptsPath (top level only), sorted by name.
      3. Runs each one not already recorded, splitting batches on GO lines, inside one
         transaction per script. A failing script rolls back and stops the run.
      4. Records the script name, SHA-256 checksum and time applied.

    A script whose checksum has changed since it was applied is reported as a warning
    but not re-run - the record of what ran is the source of truth.

    Uses System.Data.SqlClient (built into Windows PowerShell 5.1), so nothing needs
    installing on the deploy runner. With Integrated Security the script connects as
    whoever runs it - the self-hosted runner's service account in the pipeline.

.PARAMETER ConnectionString
    Target database connection string.

.PARAMETER ScriptsPath
    Folder containing the *.sql files.

.PARAMETER Exclude
    File names to never run or record (e.g. 03_sample_data.sql for demo-only seed data).

.PARAMETER BaselineUpTo
    One-time adoption on an existing database: records every script whose leading number
    is <= this value as applied, WITHOUT running it, then exits. Use it once per database
    that already has the scripts applied by hand.

.PARAMETER WhatIf
    Lists what would run (or be baselined) without changing anything.

.EXAMPLE
    ./scripts/ci/Invoke-SqlMigrations.ps1 -ConnectionString "Server=.;Database=JBSAI;Integrated Security=True;TrustServerCertificate=True" -ScriptsPath ./sql -Exclude 03_sample_data.sql

.EXAMPLE
    # Existing QA database where 01-37 were already applied by hand:
    ./scripts/ci/Invoke-SqlMigrations.ps1 -ConnectionString $cs -ScriptsPath ./sql -BaselineUpTo 37
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConnectionString,

    [Parameter(Mandatory = $true)]
    [string]$ScriptsPath,

    [string[]]$Exclude = @(),

    [int]$BaselineUpTo = -1,

    [int]$CommandTimeoutSeconds = 600
)

$ErrorActionPreference = "Stop"

function Get-TextChecksum([string]$text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))
        return -join ($bytes | ForEach-Object { $_.ToString("X2") })
    } finally {
        $sha.Dispose()
    }
}

function Get-FileChecksum([string]$path) {
    # Hash with line endings normalised to LF, so the same script hashes the same whether it
    # came from a Windows working copy (CRLF) or a pipeline checkout (LF).
    Get-TextChecksum ([System.IO.File]::ReadAllText($path) -replace "`r`n", "`n")
}

function Get-LegacyChecksums([string]$path) {
    # Checksums recorded before line endings were normalised: a hash of the raw file bytes,
    # which depends on how the file was checked out. Matching either one means "unchanged".
    $text = [System.IO.File]::ReadAllText($path)
    $lf = $text -replace "`r`n", "`n"
    @(
        (Get-FileHash -Path $path -Algorithm SHA256).Hash
        (Get-TextChecksum ($lf -replace "`n", "`r`n"))
    )
}

function Get-ScriptNumber([string]$name) {
    if ($name -match '^(\d+)') { return [int]$Matches[1] }
    return $null
}

function Split-SqlBatches([string]$sql) {
    # A batch separator is a line containing only GO (optionally followed by a comment).
    [regex]::Split($sql, '^\s*GO\s*(?:--[^\r\n]*)?\r?$', 'Multiline, IgnoreCase') |
        Where-Object { $_.Trim().Length -gt 0 }
}

function Invoke-NonQuery($connection, $transaction, [string]$sql) {
    $command = $connection.CreateCommand()
    $command.Transaction = $transaction
    $command.CommandTimeout = $CommandTimeoutSeconds
    $command.CommandText = $sql
    [void]$command.ExecuteNonQuery()
}

$ScriptsPath = (Resolve-Path $ScriptsPath).Path
$scripts = Get-ChildItem -Path $ScriptsPath -Filter *.sql -File |
    Where-Object { $Exclude -notcontains $_.Name } |
    Sort-Object Name

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $ConnectionString
Write-Host "==> SQL migrations: $($builder.DataSource) / $($builder.InitialCatalog) from $ScriptsPath"

$connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
# Surface PRINT output from the scripts in the pipeline log.
$connection.add_InfoMessage({ param($sender, $e) Write-Host "      $($e.Message)" })
$connection.Open()
try {
    $ensureTable = @"
IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
CREATE TABLE dbo.SchemaVersions (
    ScriptName NVARCHAR(255) NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
    Checksum   CHAR(64)      NOT NULL,
    AppliedAt  DATETIME2(0)  NOT NULL CONSTRAINT DF_SchemaVersions_AppliedAt DEFAULT SYSUTCDATETIME(),
    AppliedBy  NVARCHAR(128) NOT NULL CONSTRAINT DF_SchemaVersions_AppliedBy DEFAULT SUSER_SNAME(),
    Baselined  BIT           NOT NULL CONSTRAINT DF_SchemaVersions_Baselined DEFAULT 0
);
"@
    if ($PSCmdlet.ShouldProcess("dbo.SchemaVersions", "Create if missing")) {
        Invoke-NonQuery $connection $null $ensureTable
    }

    $applied = @{}
    $tableExists = $connection.CreateCommand()
    $tableExists.CommandText = "SELECT OBJECT_ID(N'dbo.SchemaVersions', N'U')"
    if ($tableExists.ExecuteScalar() -isnot [DBNull]) {
        $read = $connection.CreateCommand()
        $read.CommandText = "SELECT ScriptName, Checksum FROM dbo.SchemaVersions"
        $reader = $read.ExecuteReader()
        try {
            while ($reader.Read()) { $applied[$reader.GetString(0)] = $reader.GetString(1) }
        } finally {
            $reader.Close()
        }
    }

    $record = "INSERT INTO dbo.SchemaVersions (ScriptName, Checksum, Baselined) VALUES (@name, @checksum, @baselined)"

    if ($BaselineUpTo -ge 0) {
        foreach ($script in $scripts) {
            $number = Get-ScriptNumber $script.Name
            if ($null -eq $number -or $number -gt $BaselineUpTo -or $applied.ContainsKey($script.Name)) { continue }
            if ($PSCmdlet.ShouldProcess($script.Name, "Baseline (record as applied without running)")) {
                $command = $connection.CreateCommand()
                $command.CommandText = $record
                [void]$command.Parameters.AddWithValue("@name", $script.Name)
                [void]$command.Parameters.AddWithValue("@checksum", (Get-FileChecksum $script.FullName))
                [void]$command.Parameters.AddWithValue("@baselined", $true)
                [void]$command.ExecuteNonQuery()
                Write-Host "    baselined $($script.Name)"
            }
        }
        Write-Host "==> Baseline complete."
        return
    }

    $pending = @()
    foreach ($script in $scripts) {
        if ($applied.ContainsKey($script.Name)) {
            $current = Get-FileChecksum $script.FullName
            if ($applied[$script.Name] -ne $current) {
                if ((Get-LegacyChecksums $script.FullName) -contains $applied[$script.Name]) {
                    # Same script, recorded under the old line-ending-sensitive checksum: update it in place.
                    if ($PSCmdlet.ShouldProcess($script.Name, "Update recorded checksum to the line-ending-neutral form")) {
                        $command = $connection.CreateCommand()
                        $command.CommandText = "UPDATE dbo.SchemaVersions SET Checksum = @checksum WHERE ScriptName = @name"
                        [void]$command.Parameters.AddWithValue("@name", $script.Name)
                        [void]$command.Parameters.AddWithValue("@checksum", $current)
                        [void]$command.ExecuteNonQuery()
                    }
                } else {
                    Write-Warning "$($script.Name) has changed since it was applied; it will not be re-run. Put schema changes in a new numbered script."
                }
            }
            continue
        }
        $pending += $script
    }

    if ($pending.Count -eq 0) {
        Write-Host "==> Database is up to date."
        return
    }

    $appliedCount = 0
    foreach ($script in $pending) {
        if (-not $PSCmdlet.ShouldProcess($script.Name, "Apply")) { continue }

        Write-Host "--> Applying $($script.Name)"
        $checksum = Get-FileChecksum $script.FullName
        $sql = [System.IO.File]::ReadAllText($script.FullName)
        $transaction = $connection.BeginTransaction()
        try {
            foreach ($batch in (Split-SqlBatches $sql)) {
                Invoke-NonQuery $connection $transaction $batch
            }
            $command = $connection.CreateCommand()
            $command.Transaction = $transaction
            $command.CommandText = $record
            [void]$command.Parameters.AddWithValue("@name", $script.Name)
            [void]$command.Parameters.AddWithValue("@checksum", $checksum)
            [void]$command.Parameters.AddWithValue("@baselined", $false)
            [void]$command.ExecuteNonQuery()
            $transaction.Commit()
            $appliedCount++
        } catch {
            # The script may already have rolled back its own work (XACT_ABORT etc.).
            if ($transaction.Connection) { $transaction.Rollback() }
            throw "Migration $($script.Name) failed and was rolled back: $($_.Exception.Message)"
        }
    }

    Write-Host "==> Applied $appliedCount of $($pending.Count) pending script(s)."
} finally {
    $connection.Close()
}
