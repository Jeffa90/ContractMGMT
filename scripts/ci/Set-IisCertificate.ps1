<#
.SYNOPSIS
    Makes sure an IIS site's https binding has a valid certificate, requesting one from the
    JBS internal certificate authority when needed. Safe to run on every deploy.

.DESCRIPTION
    Built from the team's Azure DevOps scripts (scripts/CertificateScripts/CheckCreateCert.ps1
    and "NewCertReq Function.psm1"), using the same subject, but ordered so the site is never
    left without a certificate:

      1. Look in LocalMachine\My for a certificate whose friendly name is the host name (the
         team convention), with a private key, valid for more than -RenewWithinDays.
      2. If there's none and -RequestFromCA is set, request one from the CA (certreq) and wait
         for it to be issued into the store.
      3. Make sure the site has an https binding for the host name (SNI), bound to that
         certificate. The binding is only touched if it points at a different certificate.
      4. Once the binding has moved, delete older certificates with the same friendly name.

    When everything is already in place, nothing changes and no admin rights are needed. Any
    change (request, bind, delete) needs a local administrator.

    Without -RequestFromCA, pass -Thumbprint for a certificate that IT installed by hand.

    CA status (Oct 2026): the CA those scripts used, AUQLDDC01CER1BI-ISSUING, expired on
    2026-10-01 and denies every request (CERT_E_EXPIRED). Its replacement, "JBS Australia
    Issuing CA" (AUQLDDC01CER1ei), publishes no web server template, and its web certificates
    are issued by hand. So -Thumbprint is the working option for now. -RequestFromCA needs
    -CertificateAuthority and -Template to be given explicitly; there is no default to fall
    back to.

.PARAMETER SiteName
    IIS site name.

.PARAMETER HostName
    Host name the site answers on, e.g. jbsai.jbssa.com.au. Used for the binding, the
    certificate subject and its friendly name.

.PARAMETER Port
    https port. Default 443.

.PARAMETER Thumbprint
    Use this certificate instead of finding or requesting one.

.PARAMETER RequestFromCA
    Request a new certificate from the CA when no valid one is found, or when the current one
    expires within -RenewWithinDays.

.PARAMETER AdditionalDnsNames
    Extra names for the certificate, e.g. the server's own FQDN.

.PARAMETER CertificateAuthority
    CA config string, "server\CA name". Required with -RequestFromCA.

.PARAMETER Template
    Certificate template short name on the CA. Required with -RequestFromCA.

.PARAMETER RenewWithinDays
    Replace the certificate once it's within this many days of expiry. The original scripts used 40.

.EXAMPLE
    ./scripts/ci/Set-IisCertificate.ps1 -SiteName JbsAiInitiatives -HostName jbsai-qa.jbssa.com.au -Thumbprint E5FE4C4CB1D912DB07AC70EE8673F80DF441E2B6
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SiteName,

    [string]$HostName,

    [int]$Port = 443,

    [string]$Thumbprint,

    [switch]$RequestFromCA,

    [string[]]$AdditionalDnsNames = @(),

    [string]$CertificateAuthority,

    [string]$Template,

    [string]$SubjectSuffix = "C=AU,S=QLD,L=Riverview,O=JBS AUSTRALIA PTY LTD,OU=IT",

    [int]$RenewWithinDays = 40
)

$ErrorActionPreference = "Stop"
Import-Module WebAdministration

if ($RequestFromCA -and -not $Thumbprint -and (-not $CertificateAuthority -or -not $Template)) {
    throw "Requesting a certificate needs IIS_CERT_CA and IIS_CERT_TEMPLATE (the old AUQLDDC01CER1BI-ISSUING CA expired on 2026-10-01). While web certificates are issued by hand, set IIS_CERT_THUMBPRINT instead and remove IIS_CERT_AUTO."
}

function Assert-Admin([string]$action) {
    $principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "$action needs local administrator rights. Add the runner's service account ($env:USERDOMAIN\$env:USERNAME) to the local Administrators group on this server."
    }
}

function Get-UsableCertificate([string]$friendlyName) {
    Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.FriendlyName -eq $friendlyName -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays($RenewWithinDays) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function Request-Certificate([string]$friendlyName) {
    Assert-Admin "Requesting a certificate"
    $dnsNames = @($HostName) + $AdditionalDnsNames | Where-Object { $_ } | Select-Object -Unique
    $subject = "CN=$HostName,$SubjectSuffix"
    Write-Host "    requesting certificate for $($dnsNames -join ', ') from $CertificateAuthority (template '$Template')"

    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("certreq-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $ini = Join-Path $work "request.inf"
        $req = Join-Path $work "request.req"
        $cer = Join-Path $work "request.cer"

        $lines = @(
            '[Version]'
            'Signature="$Windows NT$"'
            ''
            '[NewRequest]'
            "Subject=`"$subject`""
            "FriendlyName=`"$friendlyName`""
            'Exportable=TRUE'
            'KeyLength=2048'
            'KeySpec=1'
            'KeyUsage=0xA0'
            'MachineKeySet=True'
            'ProviderName="Microsoft RSA SChannel Cryptographic Provider"'
            'ProviderType=12'
            'SMIME=FALSE'
            'RequestType=PKCS10'
            ''
            '[Extensions]'
            '2.5.29.17 = "{text}"'
        )
        $lines += $dnsNames | ForEach-Object { "_continue_ = `"dns=$_&`"" }
        Set-Content -Path $ini -Value $lines -Encoding ASCII

        # -q: never show a dialog. A service has no desktop, so a dialog would hang the deploy.
        & certreq.exe -q -new $ini $req | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "certreq -new failed ($LASTEXITCODE)." }

        & certreq.exe -q -submit -attrib "CertificateTemplate:$Template" -config $CertificateAuthority $req $cer | Out-Host
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $cer)) {
            throw "The CA didn't issue the certificate (certreq -submit exit $LASTEXITCODE). If the request is pending approval, ask IT to approve it, or check that this account has Enroll permission on '$Template'."
        }

        & certreq.exe -q -accept $cer | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "certreq -accept failed ($LASTEXITCODE)." }
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }

    $issued = Get-UsableCertificate $friendlyName
    if (-not $issued) { throw "The certificate was issued but isn't in LocalMachine\My with friendly name '$friendlyName'." }
    Write-Host "    issued $($issued.Thumbprint), expires $($issued.NotAfter.ToString('yyyy-MM-dd'))"
    return $issued
}

# --- Pick the certificate -----------------------------------------------------
$friendlyName = $HostName
if ($Thumbprint) {
    $Thumbprint = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    $cert = Get-Item "Cert:\LocalMachine\My\$Thumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "No certificate with thumbprint $Thumbprint in LocalMachine\My." }
    if ($cert.NotAfter -lt (Get-Date).AddDays($RenewWithinDays)) {
        Write-Warning "Certificate $Thumbprint expires $($cert.NotAfter.ToString('yyyy-MM-dd')). Replace it, or switch to IIS_CERT_AUTO."
    }
} else {
    if (-not $HostName) { throw "A host name is needed to find or request a certificate. Put it in the binding, e.g. https:*:443:jbsai.jbssa.com.au" }
    $cert = Get-UsableCertificate $friendlyName
    if ($cert) {
        Write-Host "==> Certificate '$friendlyName' is valid until $($cert.NotAfter.ToString('yyyy-MM-dd')) ($($cert.Thumbprint))"
    } elseif ($RequestFromCA) {
        Write-Host "==> No certificate '$friendlyName' valid for more than $RenewWithinDays days; requesting one"
        $cert = Request-Certificate $friendlyName
    } else {
        throw "No valid certificate with friendly name '$friendlyName' in LocalMachine\My. Set IIS_CERT_AUTO=true to request one, or IIS_CERT_THUMBPRINT for one installed by hand."
    }
}

# --- Bind it ------------------------------------------------------------------
$hostHeader = if ($HostName) { $HostName } else { "" }
$binding = Get-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $hostHeader
if (-not $binding) {
    Assert-Admin "Adding the https binding"
    Write-Host "    adding https binding *:$($Port):$hostHeader to '$SiteName'"
    New-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $hostHeader -SslFlags $(if ($hostHeader) { 1 } else { 0 })
    $binding = Get-WebBinding -Name $SiteName -Protocol https -Port $Port -HostHeader $hostHeader
}

$current = [string]$binding.certificateHash
if ($current -ne $cert.Thumbprint) {
    Assert-Admin "Changing the site's certificate"
    if ($current) { $binding.RemoveSslCertificate() }
    $binding.AddSslCertificate($cert.Thumbprint, "My")
    Write-Host "    bound $($cert.Thumbprint) to https *:$($Port):$hostHeader"
} else {
    Write-Host "    https binding already uses this certificate"
}

# --- Tidy up replaced certificates ---------------------------------------------
if (-not $Thumbprint) {
    $old = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq $friendlyName -and $_.Thumbprint -ne $cert.Thumbprint }
    foreach ($o in $old) {
        $inUse = Get-ChildItem IIS:\SslBindings -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $o.Thumbprint }
        if ($inUse) { Write-Host "    keeping old $($o.Thumbprint): still bound elsewhere"; continue }
        Assert-Admin "Removing the replaced certificate"
        $o | Remove-Item -DeleteKey
        Write-Host "    removed old certificate $($o.Thumbprint) (expires $($o.NotAfter.ToString('yyyy-MM-dd')))"
    }
}

Write-Output $cert.Thumbprint
