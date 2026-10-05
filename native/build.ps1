<#
.SYNOPSIS
    Build native/mkpfs_zlib (zlib 1.3.1 + lz4 1.9.4 + shims) for Windows into artifacts/native/<rid>/.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File native/build.ps1 -Rid win-x64
#>
param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $repo "artifacts\native-build\$Rid"
$outDir = Join-Path $repo "artifacts\native\$Rid"

$vcArch = @{ "win-x64" = "x64"; "win-x86" = "x64_x86"; "win-arm64" = "x64_arm64" }[$Rid]
if (-not $vcArch) { throw "Unsupported Windows RID: $Rid" }

# vcvarsall.bat calls vswhere.exe by name, so the installer folder must be on PATH.
$installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
$vswhere = Join-Path $installer "vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found; install Visual Studio with C++ tools" }
$vs = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "No Visual Studio installation with C++ tools found" }

$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvarsall.bat"
$cmakeBin = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin"
$ninjaBin = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja"

# A temporary .cmd avoids PowerShell-to-cmd quoting issues with spaces and '#'.
$script = Join-Path ([IO.Path]::GetTempPath()) "mkpfs-native-$Rid.cmd"
@"
@echo off
set "PATH=$installer;$cmakeBin;$ninjaBin;%PATH%"
call "$vcvars" $vcArch >nul || exit /b 1
cmake -S "$PSScriptRoot" -B "$buildDir" -G Ninja -DCMAKE_BUILD_TYPE=$Configuration || exit /b 1
cmake --build "$buildDir" || exit /b 1
"@ | Set-Content -Encoding ascii $script

& cmd /c $script
$code = $LASTEXITCODE
Remove-Item $script -ErrorAction SilentlyContinue
if ($code -ne 0) { throw "native build failed with exit code $code" }

New-Item -ItemType Directory -Force $outDir | Out-Null
Copy-Item (Join-Path $buildDir "mkpfs_zlib.dll") $outDir -Force
Write-Host "Built $outDir\mkpfs_zlib.dll"
