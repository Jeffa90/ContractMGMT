<#
.SYNOPSIS
    Checks that a downloaded build package is exactly the one the build recorded (one immutable build for every environment).

.DESCRIPTION
    The build writes build-manifest.json next to the published files: the commit, the run, and a SHA-256 for every file except web.config
    (web.config is stamped per environment at deploy time). This script recomputes the hashes and fails on any missing, changed or extra file.
    It prints the commit and run so the deploy log says which build is being deployed. It never prints file contents.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath
)

$ErrorActionPreference = "Stop"
$PackagePath = (Resolve-Path $PackagePath).Path
$manifestPath = Join-Path $PackagePath "build-manifest.json"
if (-not (Test-Path $manifestPath)) {
    throw "build-manifest.json is missing from the package. Deploy only a package produced by the build workflow."
}
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if (-not $manifest.commit -or -not $manifest.files) { throw "build-manifest.json is not in the expected format." }

$expected = @{}
foreach ($entry in $manifest.files) { $expected[$entry.path] = $entry.sha256 }

$problems = New-Object System.Collections.Generic.List[string]
$actualPaths = @{}
foreach ($file in Get-ChildItem -Path $PackagePath -Recurse -File) {
    $relative = $file.FullName.Substring($PackagePath.Length + 1).Replace("\", "/")
    if ($relative -eq "build-manifest.json" -or $relative -eq "web.config") { continue }
    $actualPaths[$relative] = $true
    if (-not $expected.ContainsKey($relative)) { $problems.Add("unexpected file: $relative"); continue }
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $expected[$relative]) { $problems.Add("changed file: $relative") }
}
foreach ($path in $expected.Keys) {
    if (-not $actualPaths.ContainsKey($path)) { $problems.Add("missing file: $path") }
}
if ($problems.Count -gt 0) {
    throw "The package does not match its build manifest ($($problems.Count) problem(s)): $(($problems | Select-Object -First 5) -join '; ')"
}
Write-Host "==> Package verified: commit $($manifest.commit), run $($manifest.runId), $($expected.Count) files, built $($manifest.builtUtc)"
