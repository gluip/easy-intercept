#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Regenerates the app/installer icon from the web UI's favicon (the ⚡ emoji in frontend/public/favicon.svg).

  Renders the glyph with headless Chrome (so it matches what the browser tab shows), then writes:
    EasyIntercept\icon.ico            multi-size icon (exe + Setup.exe)
    frontend\public\favicon.ico       same icon for browsers that request /favicon.ico
    installer\wizard-*.bmp            Inno Setup wizard images (100/150/200 % DPI)
  Only needed when the favicon changes; the results are committed.
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path ([IO.Path]::GetTempPath()) "easyintercept-icons"
New-Item -ItemType Directory -Force $work | Out-Null

$chrome = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw "Google Chrome not found (needed to render the emoji)." }

# Same glyph/font as favicon.svg, 256 px, transparent background
$html = '<!doctype html><html><head><meta charset="utf-8"><style>html,body{margin:0;background:transparent}body{width:256px;height:256px;display:flex;align-items:center;justify-content:center}span{font-family:system-ui;font-size:232px;line-height:1}</style></head><body><span>&#x26A1;</span></body></html>'
[IO.File]::WriteAllText("$work\icon.html", $html, [Text.UTF8Encoding]::new($false))
& $chrome --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 --window-size=256,256 --screenshot="$work\icon-256.png" "file:///$($work -replace '\\','/')/icon.html" 2>&1 | Out-Null
if (-not (Test-Path "$work\icon-256.png")) { throw "Chrome did not produce icon-256.png" }
$src = [System.Drawing.Bitmap]::FromFile("$work\icon-256.png")

function New-Graphics([System.Drawing.Bitmap]$bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    return $g
}

# ---- multi-size .ico (PNG entries) ----
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$entries = @()
foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $sz, $sz, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics $bmp
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $sz, $sz)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries += ,@{ Size = $sz; Bytes = $ms.ToArray() }
    $bmp.Dispose(); $ms.Dispose()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$e.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.Bytes) }
$bw.Flush()
$ico = $out.ToArray()
[IO.File]::WriteAllBytes("$repo\EasyIntercept\icon.ico", $ico)
[IO.File]::WriteAllBytes("$repo\frontend\public\favicon.ico", $ico)
Write-Host "icon.ico written ($($ico.Length) bytes; $($sizes -join ', ') px)"

# ---- Inno Setup wizard images: icon centered on white, 24-bit BMP ----
function Write-WizardBmp([int]$w, [int]$h, [int]$iconSize, [string]$name) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = New-Graphics $bmp
    $g.Clear([System.Drawing.Color]::White)
    $g.DrawImage($src, [int](($w - $iconSize) / 2), [int](($h - $iconSize) / 2), $iconSize, $iconSize)
    $g.Dispose()
    $bmp.Save("$PSScriptRoot\$name", [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
    Write-Host "$name written ($w x $h)"
}
Write-WizardBmp 55  58  48  "wizard-small.bmp"
Write-WizardBmp 83  80  72  "wizard-small-150.bmp"
Write-WizardBmp 110 116 96  "wizard-small-200.bmp"
Write-WizardBmp 164 314 128 "wizard-large.bmp"
Write-WizardBmp 246 471 192 "wizard-large-150.bmp"
Write-WizardBmp 328 628 256 "wizard-large-200.bmp"

$src.Dispose()
