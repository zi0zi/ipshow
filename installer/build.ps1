#requires -Version 5
<#
  Builds the IpShow MSI installer.

  - Framework-dependent .NET 8 build (small; target machine needs the .NET 8
    Desktop Runtime, otherwise Windows prompts to download it on first launch).
  - The GeoLite2 city database is NOT bundled; it is downloaded on demand from
    the app's right-click menu, so it is stripped from the publish output here.

  Usage:  powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-Version 1.1.0.0]
#>
param(
  [string]$Version = "1.1.0.0"
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$root = Split-Path -Parent $here
$proj = Join-Path $root "IpShow\IpShow.csproj"
$publishDir = Join-Path $here "dist\publish"
$msiName = "IpShow-{0}-x64.msi" -f ($Version -replace '\.0$','')
$msi = Join-Path $here "dist\$msiName"

Write-Host "==> Publishing framework-dependent build..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# City database is downloaded on demand at runtime; never ship it in the installer.
$geo = Join-Path $publishDir "GeoIP"
if (Test-Path $geo) { Remove-Item $geo -Recurse -Force }
Remove-Item (Join-Path $publishDir "*.pdb") -Force -ErrorAction SilentlyContinue

Write-Host "==> Building MSI ($Version)..."
$wix = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"
if (-not (Test-Path $wix)) { $wix = "wix" }
& $wix build (Join-Path $here "Package.wxs") `
  -arch x64 `
  -ext WixToolset.UI.wixext `
  -bindpath $here `
  -d PublishDir=$publishDir `
  -d ProductVersion=$Version `
  -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

Write-Host "==> Done."
Get-Item $msi | Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,2)}}
