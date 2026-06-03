#!/usr/bin/env pwsh

$CERT = "EasyIntercept\certs\easyntercept-ca.crt"
$CN = "EasyIntercept Root CA"

if (-not (Test-Path $CERT)) {
    Write-Host "CA cert niet gevonden: $CERT" -ForegroundColor Red
    Write-Host "Start eerst de proxy zodat het certificaat wordt gegenereerd."
    exit 1
}

Write-Host "EasyIntercept Root CA installeren in Windows Trusted Root..." -ForegroundColor Cyan
Write-Host "(Administrator rechten kunnen gevraagd worden)"
Write-Host ""

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Dit script heeft administrator rechten nodig." -ForegroundColor Yellow
    Write-Host "Start PowerShell als administrator en probeer opnieuw."
    exit 1
}

# Install certificate to Trusted Root Certification Authorities
Import-Certificate -FilePath $CERT -CertStoreLocation Cert:\LocalMachine\Root

Write-Host ""
Write-Host "CA geinstalleerd en vertrouwd!" -ForegroundColor Green
Write-Host ""
Write-Host "Test met:"
Write-Host "  curl -x http://localhost:9999 https://httpbin.org/get"
Write-Host ""
Write-Host "Verwijderen:"
Write-Host '  Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -like "*EasyIntercept Root CA*" } | Remove-Item'

