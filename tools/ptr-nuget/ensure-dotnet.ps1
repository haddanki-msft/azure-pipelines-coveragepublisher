#!/usr/bin/env pwsh
# Windows counterpart of ensure-dotnet.sh: uses an existing .NET runtime of the required major
# version or installs the runtime only into a user-writable directory (no admin rights).
[CmdletBinding()]
param(
    [int] $Major = $(if ($env:PTR_DOTNET_MAJOR) { [int]$env:PTR_DOTNET_MAJOR } else { 10 }),
    [string] $InstallDir = $(if ($env:PTR_DOTNET_DIR) { $env:PTR_DOTNET_DIR } else { Join-Path ($env:RUNNER_TOOL_CACHE ?? $env:LOCALAPPDATA) 'ptr-dotnet' })
)
$ErrorActionPreference = 'Stop'

function Write-Log([string] $Message) { Write-Host "[ensure-dotnet] $Message" }

function Test-Runtime([string] $Dotnet) {
    if (-not (Get-Command $Dotnet -ErrorAction SilentlyContinue)) { return $false }
    return [bool]((& $Dotnet --list-runtimes 2>$null) -match "^Microsoft\.NETCore\.App $Major\.")
}

function Export-Dotnet([string] $Root, [string] $Source) {
    if ($env:GITHUB_ENV) {
        "DOTNET_ROOT=$Root" >> $env:GITHUB_ENV
        "PTR_DOTNET=$(Join-Path $Root 'dotnet.exe')" >> $env:GITHUB_ENV
        "PTR_DOTNET_SOURCE=$Source" >> $env:GITHUB_ENV
    }
    if ($env:GITHUB_PATH) { $Root >> $env:GITHUB_PATH }
    Write-Host "PTR_DOTNET=$(Join-Path $Root 'dotnet.exe')"
}

if (Test-Runtime 'dotnet') {
    $root = Split-Path -Parent (Get-Command dotnet).Source
    Write-Log "Found preinstalled .NET $Major runtime in $root."
    Export-Dotnet $root 'preinstalled'
    return
}

$cached = Join-Path $InstallDir 'dotnet.exe'
if (Test-Runtime $cached) {
    Write-Log "Found cached .NET $Major runtime in $InstallDir."
    Export-Dotnet $InstallDir 'cached'
    return
}

Write-Log "No .NET $Major runtime found; installing the runtime into $InstallDir."
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$script = Join-Path ([IO.Path]::GetTempPath()) ("dotnet-install-{0}.ps1" -f [guid]::NewGuid())
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing -TimeoutSec 60
    & $script -Channel "$Major.0" -Runtime dotnet -InstallDir $InstallDir -NoPath
}
catch {
    Write-Log "ERROR: Could not download .NET $Major. The runner cannot reach the .NET download servers."
    Write-Log "Fix: install the .NET $Major runtime on the runner once, or use the self-contained (bundled .NET) package."
    throw
}
finally {
    Remove-Item -LiteralPath $script -Force -ErrorAction SilentlyContinue
}
Write-Log ("Installed in {0:N0}s." -f $sw.Elapsed.TotalSeconds)
if (-not (Test-Runtime $cached)) { throw ".NET $Major runtime not usable after install." }
Export-Dotnet $InstallDir 'installed'
