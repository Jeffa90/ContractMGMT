<#
.SYNOPSIS
    Creates the IIS app pool and site for an ASP.NET Core app if they don't exist yet.
    Does nothing when both already exist, so every deploy can call it safely.

.DESCRIPTION
    Called by Deploy-IisSite.ps1. When something is missing it:
      1. Checks prerequisites: running elevated, the ASP.NET Core Hosting Bundle
         (AspNetCoreModuleV2) and the IIS Windows Authentication feature.
      2. Creates the app pool: No Managed Code (ASP.NET Core runs out of the CLR), integrated
         pipeline, and either ApplicationPoolIdentity or the given domain account.
      3. Creates the site on the physical path with the given binding. For https,
         Set-IisCertificate.ps1 attaches the certificate afterwards.
      4. Turns on Windows Authentication and turns off Anonymous for the site.
      5. Grants the app pool identity Read on the site folder and Modify on the runtime
         folders (logs, uploads).
      6. Applies the always-on settings (Set-IisAlwaysOn.ps1) so scheduled jobs keep firing.

    It never changes an existing site or app pool. If you change bindings or identity later,
    do it in IIS Manager.

    Only the first run needs the caller to be a local administrator. Once the site exists,
    deploys need nothing more than write access to the site folder.

.PARAMETER SiteName
    IIS site name.

.PARAMETER AppPoolName
    App pool name. Defaults to the site name.

.PARAMETER SitePath
    Physical path of the site.

.PARAMETER Binding
    "protocol:ip:port:hostname", e.g. "https:*:443:aiinitiatives.jbssa.com.au" or
    "http:*:8080:". Required when the site has to be created.

.PARAMETER AppPoolUser
    Domain account for the app pool (DOMAIN\user). Leave empty for ApplicationPoolIdentity,
    which reaches SQL Server as the machine account (DOMAIN\SERVERNAME$).

.PARAMETER AppPoolPassword
    Password for -AppPoolUser.

.PARAMETER WritableFolders
    Folder names under the site that the app writes to at runtime.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SiteName,

    [string]$AppPoolName,

    [Parameter(Mandatory = $true)]
    [string]$SitePath,

    [string]$Binding,

    [string]$AppPoolUser,

    [string]$AppPoolPassword,

    [string[]]$WritableFolders = @("logs"),

    [string]$DataPath
)

$ErrorActionPreference = "Stop"
if (-not $AppPoolName) { $AppPoolName = $SiteName }

Import-Module WebAdministration

$poolExists = Test-Path "IIS:\AppPools\$AppPoolName"
$siteExists = Test-Path "IIS:\Sites\$SiteName"
if ($poolExists -and $siteExists) {
    # The always-on settings are the last setup step, so a first run that stopped part-way can leave
    # them unset. Re-apply them only when they're missing; otherwise change nothing.
    if ((Get-Item "IIS:\AppPools\$AppPoolName").startMode -ne "AlwaysRunning") {
        Write-Host "==> App pool '$AppPoolName' isn't set to AlwaysRunning; applying the always-on settings"
        & (Join-Path $PSScriptRoot "Set-IisAlwaysOn.ps1") -SiteName $SiteName -AppPoolName $AppPoolName
    } else {
        Write-Host "==> IIS site '$SiteName' and app pool '$AppPoolName' already exist; leaving them as they are."
    }
    return
}

Write-Host "==> Setting up IIS (site exists: $siteExists, app pool exists: $poolExists)"

# --- Prerequisites ------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Creating the IIS site needs local administrator rights. Add the runner's service account ($env:USERDOMAIN\$env:USERNAME) to the local Administrators group for the first deploy, or create the site by hand."
}
$ancm = Get-WebConfiguration -PSPath "MACHINE/WEBROOT/APPHOST" -Filter "/system.webServer/globalModules/add[@name='AspNetCoreModuleV2']"
if (-not $ancm) {
    throw "The ASP.NET Core Hosting Bundle isn't installed (no AspNetCoreModuleV2 in IIS). Install the .NET 10 Hosting Bundle on this server, run 'iisreset', then re-run the deploy."
}
$winAuth = Get-WindowsOptionalFeature -Online -FeatureName "IIS-WindowsAuthentication" -ErrorAction SilentlyContinue
if ($winAuth -and $winAuth.State -ne "Enabled") {
    throw "The IIS Windows Authentication feature isn't enabled. Enable it (Server Manager > Web Server > Security > Windows Authentication), then re-run the deploy."
}
if (-not $siteExists) {
    if (-not $Binding) { throw "The site '$SiteName' doesn't exist and no binding was given. Set IIS_BINDING on the GitHub environment, e.g. https:*:443:aiinitiatives.jbssa.com.au" }
    $protocol, $ip, $port, $hostName = $Binding.Split(":", 4)
    if (-not $port) { throw "Invalid binding '$Binding'; expected protocol:ip:port:hostname." }
}

# --- App pool -----------------------------------------------------------------
if (-not $poolExists) {
    Write-Host "    creating app pool '$AppPoolName'"
    New-WebAppPool -Name $AppPoolName | Out-Null
    $pool = "IIS:\AppPools\$AppPoolName"
    Set-ItemProperty $pool -Name managedRuntimeVersion -Value ""
    Set-ItemProperty $pool -Name managedPipelineMode -Value "Integrated"
    if ($AppPoolUser) {
        if (-not $AppPoolPassword) { throw "IIS_APP_POOL_USER is set but the IIS_APP_POOL_PASSWORD secret is missing." }
        Set-ItemProperty $pool -Name processModel -Value @{ userName = $AppPoolUser; password = $AppPoolPassword; identityType = 3 }
        Write-Host "    app pool runs as $AppPoolUser"
    } else {
        Set-ItemProperty $pool -Name processModel.identityType -Value "ApplicationPoolIdentity"
        Write-Host "    app pool runs as ApplicationPoolIdentity (SQL sees $env:USERDOMAIN\$env:COMPUTERNAME`$)"
    }
}

# The data protection key ring is protected per user unless the app is told otherwise, and a user's keys need the account profile to be loaded.
# Set on every run (new and existing pools): it is cheap and it is what keeps sign-in sessions valid after a pool recycle.
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "processModel.loadUserProfile" -Value $true
Write-Host "    app pool loads the user profile"

# --- Folder permissions -------------------------------------------------------
if (-not (Test-Path $SitePath)) { New-Item -ItemType Directory -Path $SitePath | Out-Null }
$identity = if ($AppPoolUser) { $AppPoolUser } else { "IIS AppPool\$AppPoolName" }
& icacls $SitePath /grant "$($identity):(OI)(CI)RX" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed granting Read on $SitePath to $identity." }
foreach ($folder in $WritableFolders) {
    $path = Join-Path $SitePath $folder
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path | Out-Null }
    & icacls $path /grant "$($identity):(OI)(CI)M" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed granting Modify on $path to $identity." }
}
Write-Host "    granted $identity Read on the site and Modify on: $($WritableFolders -join ', ')"

# The data root (documents, key ring, logs) lives OUTSIDE the site so a deploy never touches it. Only a grant is made here; nothing is removed, because other accounts
# (for example the backup agent) may legitimately hold access that Infrastructure owns. Tightening it to the pool account alone is an Infrastructure decision.
if ($DataPath) {
    if (-not (Test-Path $DataPath)) { New-Item -ItemType Directory -Path $DataPath | Out-Null }
    & icacls $DataPath /grant "$($identity):(OI)(CI)M" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed granting Modify on $DataPath to $identity." }
    Write-Host "    granted $identity Modify on the data root $DataPath"
}

# --- Site ---------------------------------------------------------------------
if (-not $siteExists) {
    Write-Host "    creating site '$SiteName' ($Binding) at $SitePath"
    $hostHeader = if ($hostName) { $hostName } else { "" }
    $ipAddress = if ($ip) { $ip } else { "*" }
    New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $AppPoolName `
        -Port ([int]$port) -IPAddress $ipAddress -HostHeader $hostHeader `
        -Ssl:($protocol -eq "https") -SslFlags $(if ($hostHeader -and $protocol -eq "https") { 1 } else { 0 }) | Out-Null
    # The https binding's certificate is attached by Set-IisCertificate.ps1, which Deploy-IisSite.ps1
    # runs straight after this script.

    # Windows auth on, anonymous off. Written as <location> entries in applicationHost.config,
    # so the sections don't need unlocking at server level.
    Set-WebConfigurationProperty -PSPath "MACHINE/WEBROOT/APPHOST" -Location $SiteName `
        -Filter "/system.webServer/security/authentication/windowsAuthentication" -Name enabled -Value $true
    Set-WebConfigurationProperty -PSPath "MACHINE/WEBROOT/APPHOST" -Location $SiteName `
        -Filter "/system.webServer/security/authentication/anonymousAuthentication" -Name enabled -Value $false
    Write-Host "    Windows Authentication on, Anonymous off"
}

# --- Always on (Hangfire jobs fire on schedule) -------------------------------
& (Join-Path $PSScriptRoot "Set-IisAlwaysOn.ps1") -SiteName $SiteName -AppPoolName $AppPoolName

Write-Host "==> IIS setup complete."
