#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Dev loop: stop any running instance, rebuild the frontend and backend, then run.
.EXAMPLE
  .\restart.ps1
  .\restart.ps1 -UiPort 8080     # if you run the UI on a non-default port
#>
param(
    [int]$UiPort = 1337,
    [int]$ProxyPort = 9999
)

$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$project = Join-Path $root "EasyIntercept\EasyIntercept.csproj"

Write-Host "Killing existing processes..." -ForegroundColor Cyan
# The Windows build is a WinExe, so `dotnet run` spawns EasyIntercept.exe rather than staying in dotnet.exe.
Get-Process -Name "EasyIntercept" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*EasyIntercept*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Get-NetTCPConnection -LocalPort $UiPort, $ProxyPort -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1

Write-Host "Building frontend..." -ForegroundColor Cyan
Push-Location (Join-Path $root "frontend")
$env:HTTP_PROXY = $null
$env:HTTPS_PROXY = $null
$env:NO_PROXY = "*"
npx vite build --emptyOutDir 2>&1 | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
Pop-Location

Write-Host "Building backend..." -ForegroundColor Cyan
dotnet build $project -c Debug --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Host "Backend build failed." -ForegroundColor Red; exit 1 }

# Keep dev data (sessions, certs, mock rules) in the project folder instead of %LOCALAPPDATA%\EasyIntercept
$env:DataRoot = Join-Path $root "EasyIntercept"
$env:UiPort = $UiPort

Write-Host "Starting... (UI http://localhost:$UiPort, proxy $ProxyPort)" -ForegroundColor Cyan
dotnet run --project $project --no-build
