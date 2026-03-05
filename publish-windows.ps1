# Publish PictureEditor as a Windows 11 executable
# Usage: .\publish-windows.ps1 [-Arch x64|arm64]
#   x64   = 64-bit Intel/AMD (default)
#   arm64 = ARM-based Windows

param(
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64"
)

$ErrorActionPreference = "Stop"

$RID = "win-$Arch"
$ProjectDir = Join-Path $PSScriptRoot "PictureEditor"
$PublishDir = Join-Path $ProjectDir "bin\publish\windows-$Arch"

Write-Host "=== Publishing PictureEditor for Windows 11 ($RID) ===" -ForegroundColor Cyan

# Clean previous publish
if (Test-Path $PublishDir) {
    Remove-Item -Recurse -Force $PublishDir
}

# Publish self-contained single-file executable
dotnet publish "$ProjectDir\PictureEditor.csproj" `
    -c Release `
    -r $RID `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -o $PublishDir

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "Output directory: $PublishDir"
Write-Host "Executable: $PublishDir\PictureEditor.exe"
