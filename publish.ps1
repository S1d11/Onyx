# Build + publish Onyx as a self-contained single-file Windows .exe,
# then build the .exe installer with Inno Setup (if installed).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File publish.ps1            # publish app only
#   powershell -ExecutionPolicy Bypass -File publish.ps1 -MakeInstaller  # also build installer

param(
  [switch]$MakeInstaller,
  [string]$Configuration = "Release",
  [string]$AppVersion = "2.23.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\Onyx.Windows\Onyx.Windows.csproj"
$publishDir = Join-Path $root "publish"
$exe = Join-Path $publishDir "Onyx.exe"

Write-Host "==> Restoring packages" -ForegroundColor Cyan
dotnet restore $proj | Out-Null

Write-Host "==> Publishing self-contained single-file .exe -> $publishDir" -ForegroundColor Cyan
dotnet publish $proj -c $Configuration -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:Version=$AppVersion `
  -o $publishDir

if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "==> Built $exe ($size MB)" -ForegroundColor Green

if ($MakeInstaller) {
  $iscc = Get-ChildItem "C:\Program Files (x86)\Inno Setup*\ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $iscc) {
    Write-Warning "Inno Setup not found. Install it from https://jrsoftware.org/isdl.php to build the installer."
    Write-Warning "The setup script is at: installer\setup.iss"
    return
  }
  $iss = Join-Path $root "installer\setup.iss"
  Write-Host "==> Building installer with Inno Setup" -ForegroundColor Cyan
  & $iscc.FullName "/DAppVersion=$AppVersion" $iss
  Write-Host "==> Installer written to installer\Output\" -ForegroundColor Green
}
