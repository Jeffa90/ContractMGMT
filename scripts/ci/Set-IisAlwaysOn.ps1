<#
.SYNOPSIS
    Configures an IIS site/app pool so the Hangfire scheduled sync jobs (see api/Program.cs) fire
    reliably on their daily schedule, instead of only when a real HTTP request happens to wake up
    an idle/recycled app pool.

.DESCRIPTION
    Three settings, all idempotent (safe to re-run):
      1. App pool startMode = AlwaysRunning (starts with IIS/on recycle, not lazily on first request).
      2. App pool processModel.idleTimeout = 0 (never shut down the worker process for inactivity).
      3. Site application preloadEnabled = true, with an Application Initialization warm-up hit
         against "/warmup" (the one anonymous path) so the app actually starts (and Hangfire's recurring jobs get registered) the
         moment the app pool starts, not on the next visitor.

    Requires the "Application Initialization" IIS role feature (Web-AppInit) - the script checks
    for it and warns (does not install it) if missing, since installing Windows features is a
    bigger action than this script should take unattended.

    Run this once per environment (QA/Production), on the IIS box itself, after the site/app pool
    already exist (see Install-ShopFloor.ps1 in SFShared for the JBS convention this follows).
    Uses only the built-in WebAdministration module, so nothing extra needs installing.

.PARAMETER SiteName
    The IIS site name (as shown in IIS Manager / Get-IisWebsite).

.PARAMETER AppPoolName
    The IIS application pool name backing that site.

.EXAMPLE
    ./scripts/ci/Set-IisAlwaysOn.ps1 -SiteName "jbsai-qa" -AppPoolName "jbsai-qa"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SiteName,

    [Parameter(Mandatory = $true)]
    [string]$AppPoolName
)

$ErrorActionPreference = "Stop"

Import-Module WebAdministration

# --- 1 & 2: App pool always running, no idle timeout -----------------------
# WebAdministration only. Don't use Get-IisAppPool: Carbon and Windows' built-in
# IISAdministration module both define it, and they return different object types (only
# Carbon's has CommitChanges), so which one loads decides whether this works.
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "startMode" -Value "AlwaysRunning"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "processModel.idleTimeout" -Value ([TimeSpan]::Zero)
Write-Host "AppPool '$AppPoolName': startMode=AlwaysRunning, idleTimeout=0 (disabled)." -ForegroundColor Green

# --- 3: Preload + Application Initialization warm-up -----------------------
$appInitFeature = Get-WindowsOptionalFeature -Online -FeatureName "IIS-ApplicationInit" -ErrorAction SilentlyContinue
if ($appInitFeature -and $appInitFeature.State -ne "Enabled") {
    Write-Warning "The 'Application Initialization' IIS feature (IIS-ApplicationInit) isn't enabled on this server. Preload will still start the app pool early, but won't auto-warm it - enable the feature via Server Manager / DISM for the full effect."
}

Set-ItemProperty "IIS:\Sites\$SiteName" -Name "applicationDefaults.preloadEnabled" -Value $true
Set-WebConfigurationProperty -PSPath "IIS:\" `
    -Filter "/system.webServer/applicationInitialization" -Name "doAppInitAfterRestart" -Value $true `
    -Location $SiteName
Set-WebConfiguration -PSPath "IIS:\" -Filter "/system.webServer/applicationInitialization" -Location $SiteName `
    -Value @{ remapManagedRequestsTo = ""; skipManagedModulesInitialization = $false }

# Warm-up request: hits /warmup to spin up the ASP.NET Core process and run Program.cs (which is
# where the Hangfire server starts and the recurring jobs get (re-)registered on every app start).
# It has to be a path that allows anonymous access: the warm-up request carries no credentials, so
# with anonymous auth off site-wide IIS answers "/" with a 401 before ANCM ever starts the app.
$warmupPage = "/health/live"   # Ecp has no /warmup; /health/live is AllowAnonymous and starts the app
$existingPages = @(Get-WebConfiguration -PSPath "IIS:\" -Filter "/system.webServer/applicationInitialization/add" -Location $SiteName -ErrorAction SilentlyContinue |
    ForEach-Object { $_.initializationPage })
foreach ($page in $existingPages | Where-Object { $_ -ne $warmupPage }) {
    Remove-WebConfigurationProperty -PSPath "IIS:\" -Filter "/system.webServer/applicationInitialization" -Location $SiteName `
        -Name "." -AtElement @{ initializationPage = $page }
}
if ($existingPages -notcontains $warmupPage) {
    Add-WebConfiguration -PSPath "IIS:\" -Filter "/system.webServer/applicationInitialization" -Location $SiteName `
        -Value @{ initializationPage = $warmupPage }
}

# Anonymous on for /warmup only; the rest of the site stays Windows-auth-only (Initialize-IisSite.ps1).
# The endpoint itself is AllowAnonymous and returns nothing but a 200.
Set-WebConfigurationProperty -PSPath "MACHINE/WEBROOT/APPHOST" -Location "$SiteName$warmupPage" `
    -Filter "/system.webServer/security/authentication/anonymousAuthentication" -Name enabled -Value $true
Write-Host "Site '$SiteName': preloadEnabled=true, Application Initialization warm-up configured for '$warmupPage' (anonymous)." -ForegroundColor Green

Write-Host "Done. Recycle the app pool once for these settings to take effect immediately."
