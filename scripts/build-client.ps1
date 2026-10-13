<#
  Az Invoice Sorter kliens fordítása: önálló (self-contained) single-file exe, és ha az Inno Setup
  elérhető, a telepítő is.

  Használat:  pwsh scripts/build-client.ps1 -Version 1.2.3
  Kimenet:    publish/client/InvoiceSorter.exe
              installer/client/Output/InvoiceSorter-Setup-<verzió>.exe
#>
param(
  [string]$Version = "1.0.0",
  [switch]$NoInstaller
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "publish/client"

Write-Host "==> Invoice Sorter $Version fordítása" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "src/InvoiceSorter.App") -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:Version=$Version -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish sikertelen" }

$exe = Join-Path $publish "InvoiceSorter.exe"
Write-Host "    Exe: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)" -ForegroundColor Green
if ($NoInstaller) { return }

. (Join-Path $PSScriptRoot "find-iscc.ps1")
$iscc = Find-Iscc
if ($iscc) {
  Write-Host "==> Telepítő fordítása" -ForegroundColor Cyan
  & $iscc "/Q" "/DAppVersion=$Version" (Join-Path $root "installer/client/InvoiceSorter.iss")
  if ($LASTEXITCODE -ne 0) { throw "iscc sikertelen" }
  Write-Host "    Telepítő: installer/client/Output/InvoiceSorter-Setup-$Version.exe" -ForegroundColor Green
} else {
  Write-Warning "Az Inno Setup (iscc) nem található – a telepítő nem készült el. Letöltés: https://jrsoftware.org/isdl.php"
}
