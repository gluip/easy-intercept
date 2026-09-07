#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Builds the Windows installer: frontend (Vite) -> self-contained single-file publish -> Inno Setup.
.EXAMPLE
  .\build-installer.ps1 -Version 0.1.0
  Output: dist\EasyIntercept-Setup-0.1.0.exe
#>
param(
    [string]$Version = "0.1.0",
    [switch]$SkipFrontend,
    [switch]$SkipInstaller
)

# "Continue" on purpose: native tools (vite, dotnet) write warnings to stderr, which Windows PowerShell 5.1
# would otherwise turn into terminating errors. Every native call checks $LASTEXITCODE explicitly instead.
$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$publishDir = Join-Path $root "dist\publish"

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like x.y.z (got '$Version')"
}

if (-not $SkipFrontend) {
    Write-Host "==> Building frontend" -ForegroundColor Cyan
    Push-Location (Join-Path $root "frontend")
    try {
        $env:HTTP_PROXY = $null
        $env:HTTPS_PROXY = $null
        $env:NO_PROXY = "*"
        if (-not (Test-Path "node_modules")) {
            npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
        }
        npx vite build --emptyOutDir
        if ($LASTEXITCODE -ne 0) { throw "vite build failed" }
    }
    finally {
        Pop-Location
    }
}

Write-Host "==> Publishing EasyIntercept $Version (win-x64, self-contained, single file)" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish (Join-Path $root "EasyIntercept\EasyIntercept.csproj") `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if ($SkipInstaller) {
    Write-Host "Published to $publishDir (installer skipped)" -ForegroundColor Green
    exit 0
}

Write-Host "==> Building installer" -ForegroundColor Cyan
$iscc = $null
$cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($cmd) { $iscc = $cmd.Source }
if (-not $iscc) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    $iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install it with:  winget install JRSoftware.InnoSetup"
}

& $iscc "/DAppVersion=$Version" (Join-Path $root "installer\EasyIntercept.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $root "dist\EasyIntercept-Setup-$Version.exe"
Write-Host "Installer ready: $setup" -ForegroundColor Green
