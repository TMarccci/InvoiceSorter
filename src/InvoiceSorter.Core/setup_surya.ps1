# Telepíti a Surya OCR-t az Invoice Sorter saját Python-környezetébe:
#   alapértelmezés: %LOCALAPPDATA%\InvoiceSorter\surya  (kliens)
#   -HomeDir:       pl. C:\ProgramData\TMarccci\InvoiceSorter\Server\surya  (szerver)
# Rendszerszinten semmit nem telepít. Python 3.10+ (python.org) és kb. 6 GB szabad hely kell;
# NVIDIA videokártya esetén a PyTorch CUDA-s változata települ (sokkal gyorsabb).
#
# Surya: https://github.com/datalab-to/surya – a kód GPL-3.0, a modellsúlyok saját licenc alatt
# (személyes / kutatási célra és kisebb cégeknek ingyenes – üzleti használat előtt ellenőrizd).
param([string]$HomeDir = (Join-Path $env:LOCALAPPDATA "InvoiceSorter\surya"))

$ErrorActionPreference = "Stop"
$venv = Join-Path $HomeDir "venv"
$models = Join-Path $HomeDir "models"
New-Item -ItemType Directory -Force $HomeDir, $models | Out-Null

function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host $text -ForegroundColor Red; Read-Host "Nyomj Entert a bezáráshoz"; exit 1 }

Step "Python 3.10+ keresése"
$python = $null
foreach ($candidate in @("py -3", "python")) {
    try {
        $version = & cmd /c "$candidate -c ""import sys;print(sys.version_info[:2] >= (3,10))"" 2>nul"
        if ($version -eq "True") { $python = $candidate; break }
    } catch { }
}
if (-not $python) { Fail "Nem található Python 3.10 vagy újabb. Telepítsd innen: https://www.python.org/downloads/ ('Add python.exe to PATH' bepipálva), majd futtasd újra." }
Write-Host "Használt Python: $python"

Step "Környezet létrehozása ($venv)"
if (-not (Test-Path (Join-Path $venv "Scripts\python.exe"))) { & cmd /c "$python -m venv ""$venv""" }
$py = Join-Path $venv "Scripts\python.exe"
& $py -m pip install --upgrade pip

$gpu = $false
try { if ((& nvidia-smi -L 2>$null) -match "GPU") { $gpu = $true } } catch { }
if ($gpu) {
    Step "PyTorch telepítése CUDA-val (NVIDIA videokártya található) – nagy letöltés"
    & $py -m pip install torch torchvision --index-url https://download.pytorch.org/whl/cu128
} else {
    Step "PyTorch telepítése (CPU – nincs NVIDIA videokártya, a Surya lassú lesz)"
    & $py -m pip install torch torchvision
}
if ($LASTEXITCODE -ne 0) { Fail "A PyTorch telepítése nem sikerült." }

Step "Surya OCR 0.17.1 és könyvtárai telepítése"
# A surya-ocr 0.17.1 pillow<11-et kér, amihez nincs csomag az új Python-verziókhoz – a mostani Pillow működik.
& $py -m pip install "transformers>=4.56.1,<5" "einops>=0.8.1,<0.9" "opencv-python-headless==4.11.0.86" "pypdfium2==4.30.0" `
    pillow "pydantic>=2.5.3,<3" "pydantic-settings>=2.1.0,<3" "platformdirs>=4.3.6,<5" "filetype>=1.2.0,<2" "click>=8.1.8,<9" "python-dotenv>=1.0.0,<2"
& $py -m pip install "surya-ocr==0.17.1" --no-deps
if ($LASTEXITCODE -ne 0) { Fail "A Surya telepítése nem sikerült." }

Step "A Surya modellek letöltése (kb. 1,3 GB, csak egyszer)"
$env:MODEL_CACHE_DIR = $models
& $py -c "from surya.foundation import FoundationPredictor; from surya.detection import DetectionPredictor; FoundationPredictor(); DetectionPredictor(); import torch; print('Eszköz:', 'cuda' if torch.cuda.is_available() else 'cpu')"
if ($LASTEXITCODE -ne 0) { Fail "A modellek letöltése nem sikerült." }

Write-Host ""
Write-Host "A Surya OCR telepítve. Az Invoice Sorterben / szerverkezelőben válaszd ki a módot, majd nyomd meg a 'Surya tesztelése' gombot." -ForegroundColor Green
Read-Host "Nyomj Entert a bezáráshoz"
