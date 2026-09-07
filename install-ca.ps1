#!/usr/bin/env pwsh
#
# Installeert de root CA van EasyIntercept in de Windows Trusted Root store.
#
#   .\install-ca.ps1                     # CA van de draaiende app (of de default data-root)
#   .\install-ca.ps1 pad\naar\ca.crt     # expliciet certificaat
#   $env:UI_PORT = 8080; .\install-ca.ps1
#
# Zonder argument wordt eerst de draaiende app gevraagd (http://localhost:<UI_PORT>/ca), zodat
# je gegarandeerd de CA vertrouwt die op dit moment de host-certs ondertekent. Daarna vallen we
# terug op $env:DataRoot, de default data-root en tenslotte de dev-map in deze repo.
param([string]$CertPath)

$ErrorActionPreference = "Stop"

$CN = "EasyIntercept Root CA"
$CRT = "easyntercept-ca.crt"
$UiPort = if ($env:UI_PORT) { $env:UI_PORT } else { 1337 }
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$DefaultDataRoot = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "EasyIntercept" } else { $null }

$temp = $null
if ($CertPath) {
    if (-not (Test-Path $CertPath)) {
        Write-Host "CA cert niet gevonden: $CertPath" -ForegroundColor Red
        exit 1
    }
    Write-Host "Certificaat: $CertPath"
}
else {
    # 1. De draaiende app - dit is de enige bron die zeker klopt.
    try {
        $temp = Join-Path ([System.IO.Path]::GetTempPath()) "easyntercept-ca-$PID.crt"
        Invoke-WebRequest -Uri "http://localhost:$UiPort/ca" -OutFile $temp -TimeoutSec 5 -UseBasicParsing | Out-Null
        $CertPath = $temp
        Write-Host "Certificaat opgehaald bij de draaiende app (poort $UiPort)"
    }
    catch {
        if ($temp -and (Test-Path $temp)) { Remove-Item $temp -Force }
        $temp = $null
        # 2. $env:DataRoot, 3. default data-root, 4. dev-map in de repo.
        foreach ($dir in @($env:DataRoot, $DefaultDataRoot, (Join-Path $Root "EasyIntercept"))) {
            if ($dir) {
                $candidate = Join-Path $dir "certs\$CRT"
                if (Test-Path $candidate) { $CertPath = $candidate; break }
            }
        }
        if (-not $CertPath) {
            Write-Host "Geen CA cert gevonden." -ForegroundColor Red
            Write-Host "Start EasyIntercept eerst (dan wordt het certificaat gegenereerd), of geef het pad mee:"
            Write-Host "  .\install-ca.ps1 pad\naar\$CRT"
            exit 1
        }
        Write-Host "EasyIntercept draait niet op poort $UiPort - certificaat van schijf gebruikt:" -ForegroundColor Yellow
        Write-Host "  $CertPath"
        Write-Host "  Draait de app met een andere DataRoot, dan is dit niet de CA die hij gebruikt."
    }
}

try {
    $cert = Get-PfxCertificate -FilePath (Resolve-Path $CertPath).Path
    Write-Host "  SHA-256: $($cert.GetCertHashString('SHA256'))"
    Write-Host ""

    # Al een (andere) EasyIntercept CA vertrouwd? Dat is precies wat certificaatfouten in de
    # proxied browser veroorzaakt, dus meld het.
    $stale = Get-ChildItem Cert:\LocalMachine\Root |
        Where-Object { $_.Subject -like "*$CN*" -and $_.Thumbprint -ne $cert.Thumbprint }
    if ($stale) {
        Write-Host "Er staat al een andere EasyIntercept CA in Trusted Root:" -ForegroundColor Yellow
        $stale | ForEach-Object { Write-Host "  $($_.Thumbprint)" }
        Write-Host "  Verwijder die na afloop met:"
        Write-Host '    Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -like "*EasyIntercept Root CA*" } | Remove-Item'
        Write-Host ""
    }

    Write-Host "EasyIntercept Root CA installeren in Windows Trusted Root..." -ForegroundColor Cyan
    Write-Host "(Administrator rechten kunnen gevraagd worden)"
    Write-Host ""

    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host "Dit script heeft administrator rechten nodig." -ForegroundColor Yellow
        Write-Host "Start PowerShell als administrator en probeer opnieuw."
        exit 1
    }

    Import-Certificate -FilePath (Resolve-Path $CertPath).Path -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
}
finally {
    if ($temp -and (Test-Path $temp)) { Remove-Item $temp -Force }
}

Write-Host ""
Write-Host "CA geinstalleerd en vertrouwd!" -ForegroundColor Green
Write-Host ""
Write-Host "Test met:"
Write-Host "  curl -x http://localhost:9999 https://httpbin.org/get"
Write-Host ""
Write-Host "Sluit een al geopende proxied browser volledig af en start hem opnieuw."
Write-Host ""
Write-Host "Verwijderen:"
Write-Host '  Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -like "*EasyIntercept Root CA*" } | Remove-Item'
