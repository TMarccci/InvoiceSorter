<#
  Az Invoice Sorter szerver (Windows-szolgáltatás) és a szerverkezelő fordítása single-file exe-ként, és ha
  az Inno Setup elérhető, a szerver telepítője is.

  Használat:  pwsh scripts/build-server.ps1 -Version 1.2.3
  Kimenet:    publish/server/InvoiceSorterServer.exe, publish/server/InvoiceSorterServerManager.exe
              installer/server/Output/InvoiceSorter-Server-Setup-<verzió>.exe
#>
param(
  [string]$Version = "1.0.0",
  [switch]$NoInstaller
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "publish/server"
$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
            "-p:DebugType=none", "-p:Version=$Version", "-o", $publish, "--nologo")

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Write-Host "==> Invoice Sorter Server $Version fordítása" -ForegroundColor Cyan
dotnet publish (Join-Path $root "src/InvoiceSorter.Server") @common
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (szerver) sikertelen" }

Write-Host "==> Szerverkezelő fordítása" -ForegroundColor Cyan
dotnet publish (Join-Path $root "src/InvoiceSorter.ServerManager") @common
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (szerverkezelő) sikertelen" }

# Only the two exes go into the installer (no stray config / pdb files).
Get-ChildItem $publish -File | Where-Object { $_.Name -notin @("InvoiceSorterServer.exe", "InvoiceSorterServerManager.exe") } | Remove-Item -Force
Get-ChildItem $publish -File | ForEach-Object { Write-Host ("    {0} ({1} MB)" -f $_.Name, [math]::Round($_.Length / 1MB, 1)) -ForegroundColor Green }
if ($NoInstaller) { return }

. (Join-Path $PSScriptRoot "find-iscc.ps1")
$iscc = Find-Iscc
if ($iscc) {
  Write-Host "==> Telepítő fordítása" -ForegroundColor Cyan
  & $iscc "/Q" "/DAppVersion=$Version" (Join-Path $root "installer/server/InvoiceSorterServer.iss")
  if ($LASTEXITCODE -ne 0) { throw "iscc sikertelen" }
  Write-Host "    Telepítő: installer/server/Output/InvoiceSorter-Server-Setup-$Version.exe" -ForegroundColor Green
} else {
  Write-Warning "Az Inno Setup (iscc) nem található – a telepítő nem készült el. Letöltés: https://jrsoftware.org/isdl.php"
}
