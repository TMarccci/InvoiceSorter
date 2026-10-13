# The Inno Setup compiler: on PATH, or in its default install folder (local machine / GitHub Actions via choco).
function Find-Iscc {
  $cmd = Get-Command iscc -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  foreach ($dir in @("${env:ProgramFiles(x86)}\Inno Setup 6", "$env:ProgramFiles\Inno Setup 6", "$env:LOCALAPPDATA\Programs\Inno Setup 6")) {
    $exe = Join-Path $dir "ISCC.exe"
    if (Test-Path $exe) { return $exe }
  }
  return $null
}
