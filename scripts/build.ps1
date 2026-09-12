# ============================================================
#  SQL Management Tools — Script build toàn bộ solution
#  Cho phép chỉ định đường dẫn SDK .NET 8 (bản cài user-scope).
#  Cách dùng:        .\build.ps1
#  Build + test:     .\build.ps1 -RunTests
# ============================================================

param(
    [switch]$RunTests,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Resolve-DotNet {
    # Ưu tiên SDK 8 trong hồ sơ người dùng (môi trường cài user-scope).
    $userDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    $systemDotnet = "dotnet"

    if (Test-Path $userDotnet) {
        return $userDotnet
    }
    return $systemDotnet
}

$dotnet = Resolve-DotNet
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root "SqlMigrator.sln"

Write-Host "[1/2] Build $Configuration..." -ForegroundColor Cyan
& $dotnet build $sln -c $Configuration --nologo -m
if ($LASTEXITCODE -ne 0) { throw "Build thất bại (mã thoát $LASTEXITCODE)." }

if ($RunTests) {
    Write-Host "[2/2] Chạy kiểm thử..." -ForegroundColor Cyan
    & $dotnet test $sln -c $Configuration --nologo --no-build
    if ($LASTEXITCODE -ne 0) { throw "Kiểm thử thất bại (mã thoát $LASTEXITCODE)." }
}

Write-Host "Hoàn tất." -ForegroundColor Green