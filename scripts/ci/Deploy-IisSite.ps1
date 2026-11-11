<#
.SYNOPSIS
    Deploys a published ASP.NET Core app (and optionally its SQL migrations) onto an
    existing IIS site on this machine. Run by .github/workflows/iis-deploy.yml on a
    self-hosted runner, but can also be run by hand on the server.

.DESCRIPTION
    Steps, in order:
      1. Stamp ASPNETCORE_ENVIRONMENT and the app's env vars into the package's web.config.
      2. Apply SQL migrations (if any), resolving each connection string by name from the
         package's appsettings.json + appsettings.{Environment}.json, overridable by a
         ConnectionStrings__{Name} entry in -EnvironmentVariables.
      3. Take the app offline (app_offline.htm), snapshot the current site to
         "{SitePath}.previous", then mirror the package into the site, leaving runtime
         folders (-PreserveFolders) untouched.
      4. If -SiteName is given and the IIS site or app pool doesn't exist yet, create them
         (Initialize-IisSite.ps1). Only this first-time setup needs local admin rights.
      5. Bring the app back online and smoke-test it with the runner's Windows credentials.

    Migrations run before the file swap, so every script must be backward-compatible with
    the version of the app that is still running.

    Rollback: robocopy "{SitePath}.previous" "{SitePath}" /MIR /XD <PreserveFolders>
    (or re-run an earlier successful pipeline run's deploy job).

.PARAMETER PackagePath
    dotnet publish output (the "app" artifact).

.PARAMETER SitePath
    IIS site physical path. Created if missing.

.PARAMETER SiteName
    IIS site name. When given, the site and app pool are created if missing (see
    Initialize-IisSite.ps1 for -AppPoolName, -Binding, -AppPoolUser, -AppPoolPassword).
    When empty, the site must already exist.
    For an https -Binding, the certificate is checked every deploy by Set-IisCertificate.ps1:
    -CertificateAuto requests and renews it from the CA (-CertificateAuthority,
    -CertificateTemplate, -CertificateDnsNames override the JBS defaults), or
    -CertificateThumbprint binds one installed by hand.

.PARAMETER Environment
    ASPNETCORE_ENVIRONMENT value (QA, Production, ...).

.PARAMETER EnvironmentVariables
    Extra env vars for web.config (secrets etc.).

.PARAMETER SqlPackagePath
    Folder holding the SQL scripts (the "sql" artifact). Optional.

.PARAMETER SqlMigrations
    One "ConnectionStringName=relative/folder" per line (or ';'-separated), folders relative
    to -SqlPackagePath, e.g. "JbsAi=." and "JbsAppsCommon=jbsappscommon".

.PARAMETER SqlExclude
    Script file names never to run (e.g. 03_sample_data.sql).

.PARAMETER PreserveFolders
    Folder names inside the site that are runtime data, not deployed content (logs, uploads).

.PARAMETER SmokeTestUrl
    URL to GET after deploy. Any status below 500 counts as "app is up" (401/403 still prove
    the app started; 500.30/502/503 mean it didn't). Optional.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$SitePath,

    [Parameter(Mandatory = $true)]
    [string]$Environment,

    [hashtable]$EnvironmentVariables = @{},

    [string]$SqlPackagePath,

    [string]$SqlMigrations,

    [string[]]$SqlExclude = @(),

    [string[]]$PreserveFolders = @("logs"),

    [string]$SmokeTestUrl,

    [string]$SiteName,

    [string]$AppPoolName,

    [string]$Binding,

    [string]$CertificateThumbprint,

    [switch]$CertificateAuto,

    [string]$CertificateAuthority,

    [string]$CertificateTemplate,

    [string[]]$CertificateDnsNames = @(),

    [string]$AppPoolUser,

    [string]$AppPoolPassword,

    [int]$SmokeTestTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$PackagePath = (Resolve-Path $PackagePath).Path

function Invoke-Robocopy([string]$source, [string]$destination, [string[]]$extraArgs) {
    $robocopyArgs = @($source, $destination, "/MIR", "/R:3", "/W:5", "/NP", "/NFL", "/NDL") + $extraArgs
    if ($PreserveFolders.Count -gt 0) { $robocopyArgs += @("/XD") + $PreserveFolders }
    & robocopy @robocopyArgs | Out-Host
    # Robocopy exit codes 0-7 are success variants; 8+ means something failed to copy.
    if ($LASTEXITCODE -ge 8) { throw "robocopy $source -> $destination failed with exit code $LASTEXITCODE." }
    $global:LASTEXITCODE = 0
}

function Get-ConnectionString([string]$name) {
    $override = $EnvironmentVariables.Keys | Where-Object { $_ -ieq "ConnectionStrings__$name" } | Select-Object -First 1
    if ($override) { return $EnvironmentVariables[$override] }

    $value = $null
    foreach ($file in @("appsettings.json", "appsettings.$Environment.json")) {
        $path = Join-Path $PackagePath $file
        if (-not (Test-Path $path)) { continue }
        $json = Get-Content $path -Raw | ConvertFrom-Json
        if ($json.ConnectionStrings -and $json.ConnectionStrings.$name) { $value = $json.ConnectionStrings.$name }
    }
    if (-not $value) {
        throw "Connection string '$name' not found in appsettings.json / appsettings.$Environment.json, and no ConnectionStrings__$name env var was supplied."
    }
    return $value
}

Write-Host "==> Configuring web.config for $Environment"
& (Join-Path $PSScriptRoot "Set-WebConfigEnvironment.ps1") `
    -WebConfigPath (Join-Path $PackagePath "web.config") `
    -Environment $Environment `
    -EnvironmentVariables $EnvironmentVariables

if ($SqlMigrations) {
    if (-not $SqlPackagePath) { throw "-SqlMigrations was given but -SqlPackagePath was not." }
    $SqlPackagePath = (Resolve-Path $SqlPackagePath).Path
    foreach ($entry in ($SqlMigrations -split "[`r`n;]+" | Where-Object { $_.Trim() })) {
        $name, $folder = $entry.Split("=", 2) | ForEach-Object { $_.Trim() }
        if (-not $folder) { throw "Invalid -SqlMigrations entry '$entry'; expected Name=folder." }
        & (Join-Path $PSScriptRoot "Invoke-SqlMigrations.ps1") `
            -ConnectionString (Get-ConnectionString $name) `
            -ScriptsPath (Join-Path $SqlPackagePath $folder) `
            -Exclude $SqlExclude
    }
}

if (-not (Test-Path $SitePath)) {
    Write-Host "==> Creating site folder $SitePath"
    New-Item -ItemType Directory -Path $SitePath | Out-Null
}
$SitePath = (Resolve-Path $SitePath).Path
$appOffline = Join-Path $SitePath "app_offline.htm"
$backupPath = "$SitePath.previous"

Write-Host "==> Taking app offline"
Set-Content -Path $appOffline -Value "<html><body><h2>Updating - back in a minute.</h2></body></html>" -Encoding UTF8
# Give the ASP.NET Core Module time to shut the process down and release file locks.
Start-Sleep -Seconds 5

try {
    Write-Host "==> Snapshotting current site to $backupPath"
    Invoke-Robocopy $SitePath $backupPath @("/XF", "app_offline.htm")

    Write-Host "==> Copying new version into $SitePath"
    Invoke-Robocopy $PackagePath $SitePath @("/XF", "app_offline.htm")

    if ($SiteName) {
        & (Join-Path $PSScriptRoot "Initialize-IisSite.ps1") `
            -SiteName $SiteName -AppPoolName $AppPoolName -SitePath $SitePath `
            -Binding $Binding `
            -AppPoolUser $AppPoolUser -AppPoolPassword $AppPoolPassword `
            -WritableFolders $PreserveFolders

        # https sites: make sure the binding has a valid certificate (and renew it when it's close
        # to expiry). Runs every deploy; makes no changes when the certificate is fine.
        $protocol, $ip, $port, $hostName = "$Binding".Split(":", 4)
        if ($protocol -eq "https") {
            $certArgs = @{ SiteName = $SiteName; HostName = $hostName; Port = [int]$port }
            if ($CertificateThumbprint) { $certArgs.Thumbprint = $CertificateThumbprint }
            if ($CertificateAuto) { $certArgs.RequestFromCA = $true }
            if ($CertificateAuthority) { $certArgs.CertificateAuthority = $CertificateAuthority }
            if ($CertificateTemplate) { $certArgs.Template = $CertificateTemplate }
            if ($CertificateDnsNames) { $certArgs.AdditionalDnsNames = $CertificateDnsNames }
            & (Join-Path $PSScriptRoot "Set-IisCertificate.ps1") @certArgs | Out-Null
        }
    }
} finally {
    Write-Host "==> Bringing app back online"
    Remove-Item $appOffline -Force -ErrorAction SilentlyContinue
}

if ($SmokeTestUrl) {
    Write-Host "==> Smoke test: $SmokeTestUrl"
    $deadline = (Get-Date).AddSeconds($SmokeTestTimeoutSeconds)
    $status = $null
    do {
        try {
            $response = Invoke-WebRequest -Uri $SmokeTestUrl -UseDefaultCredentials -UseBasicParsing -TimeoutSec 60
            $status = [int]$response.StatusCode
        } catch [System.Net.WebException] {
            $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { $null }
        }
        if ($null -ne $status -and $status -lt 500) { break }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    if ($null -eq $status -or $status -ge 500) {
        throw "Smoke test failed (last status: $status). Check the site's logs folder; roll back with: robocopy `"$backupPath`" `"$SitePath`" /MIR /XD $($PreserveFolders -join ' ')"
    }
    Write-Host "    HTTP $status - app is up."
}

Write-Host "==> Deploy complete."
