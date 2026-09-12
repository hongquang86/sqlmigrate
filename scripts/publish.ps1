<# 
.SYNOPSIS
    Build SQL Management Tools single-file self-contained executable and Inno Setup installer.

.DESCRIPTION
    Publishes the WinForms project as a single-file self-contained executable
    for win-x64, then compiles an Inno Setup installer.

.NOTES
    Requires: .NET 8 SDK, Inno Setup 6 (iscc in PATH)
    Output: D:\Projects\App\Publish\SqlMigrator.exe (single file)
            D:\Projects\App\Installer\SQLMigrator_Setup_<version>.exe
#>

$ErrorActionPreference = 'Stop'

$projectPath = "src\SqlMigrator.UI.WinForms\SqlMigrator.UI.WinForms.csproj"
$publishDir = "..\..\App\Publish"
$rid = "win-x64"
$configuration = "Release"

# Ensure output directories exist
$publishFull = Resolve-Path -Path (Join-Path (Split-Path $projectPath -Parent) $publishDir) -ErrorAction SilentlyContinue
if (-not $publishFull) {
    New-Item -ItemType Directory -Path (Join-Path (Split-Path $projectPath -Parent) $publishDir) | Out-Null
    $publishFull = Resolve-Path -Path (Join-Path (Split-Path $projectPath -Parent) $publishDir)
}

Write-Host "=== Publishing single-file self-contained executable ===" -ForegroundColor Cyan
dotnet publish $projectPath -c $configuration -r $rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishFull

$exePath = Join-Path $publishFull "SqlMigrator.exe"
if (Test-Path $exePath) {
    $sizeMB = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
    Write-Host "✓ Created $exePath ($sizeMB MB)" -ForegroundColor Green
} else {
    Write-Error "Executable not found at $exePath"
    exit 1
}

# Compile Inno Setup installer
$issPath = "..\..\SQLMigrator_Setup.iss"
$issFull = Resolve-Path -Path (Join-Path (Split-Path $projectPath -Parent) $issPath)

Write-Host "`n=== Compiling Inno Setup installer ===" -ForegroundColor Cyan
& iscc /Qp $issFull

$installerDir = Join-Path (Split-Path $projectPath -Parent) "..\..\App\Installer"
$installerExe = Get-ChildItem $installerDir -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($installerExe) {
    $sizeMB = [math]::Round($installerExe.Length / 1MB, 1)
    Write-Host "✓ Created installer: $($installerExe.FullName) ($sizeMB MB)" -ForegroundColor Green
} else {
    Write-Warning "Installer not found in $installerDir"
}

Write-Host "`n=== Build complete ===" -ForegroundColor Cyan
Write-Host "Single-file exe: $exePath"
Write-Host "Installer:       $($installerExe.FullName)"