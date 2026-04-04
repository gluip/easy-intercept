#!/usr/bin/env pwsh

$ErrorActionPreference = "Continue"
$PROJECT = "d:\git\easy-intercept\EasyIntercept\EasyIntercept.csproj"

Write-Host "Killing existing processes..." -ForegroundColor Cyan
Get-Process | Where-Object { $_.ProcessName -eq "dotnet" -and $_.Path -like "*EasyIntercept*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Get-NetTCPConnection -LocalPort 8080,8888 -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1

Write-Host "Building frontend..." -ForegroundColor Cyan
Push-Location "d:\git\easy-intercept\frontend"
$env:HTTP_PROXY = $null
$env:HTTPS_PROXY = $null
$env:NO_PROXY = "*"
npx vite build --emptyOutDir 2>&1 | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
Pop-Location

Write-Host "Building backend..." -ForegroundColor Cyan
dotnet build $PROJECT -c Debug --nologo -v quiet

Write-Host "Starting..." -ForegroundColor Cyan
dotnet run --project $PROJECT --no-build
