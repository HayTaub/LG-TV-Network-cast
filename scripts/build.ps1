# Builds LG Network Cast and its installer.
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1
# Requires: .NET SDK 8+ (builds a .NET Framework 4.8 app), Inno Setup 6 (for the installer).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot 'get-ffmpeg.ps1')

dotnet build (Join-Path $root 'src\LGNetworkCast\LGNetworkCast.csproj') -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Warning 'Inno Setup not found - skipping installer (winget install JRSoftware.InnoSetup)'; exit 0 }

& $iscc (Join-Path $root 'installer\LGNetworkCast.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
Write-Host "Installer written to $(Join-Path $root 'dist')"
