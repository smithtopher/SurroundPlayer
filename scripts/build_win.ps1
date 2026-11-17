# Configures and builds the Windows engine targets (CMakeLists.txt) with clang-cl + Ninja,
# and with -App also the WinUI 3 app (Windows/SurroundPlayer, needs the .NET 8 SDK).
#   powershell -ExecutionPolicy Bypass -File scripts\build_win.ps1 [-Config Release|Debug] [-App] [-Package]
# Output: .build\win-<config>\{pcdump,pctest}.exe, playercore.dll;
#   app: Windows\SurroundPlayer\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\SurroundPlayer.exe
#   -Package: self-contained app (no .NET or anything else to install) in
#             build\windows\Surround Player\ and build\SurroundPlayer-<ver>-windows-x64.zip
param([string]$Config = 'Release', [switch]$App, [switch]$Package)
if ($Package) { $App = $true; $Config = 'Release' }
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\win_env.ps1"
Enter-PcDevShell
$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path $root ".build\win-$($Config.ToLower())"
cmake -S $root -B $build -G Ninja "-DCMAKE_BUILD_TYPE=$Config" -DCMAKE_C_COMPILER=clang-cl
if ($LASTEXITCODE) { exit $LASTEXITCODE }
cmake --build $build
if ($LASTEXITCODE -or -not $App) { exit $LASTEXITCODE }
# the app always copies the Release engine (see EngineDir in SurroundPlayer.csproj)
$env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$proj = Join-Path $root 'Windows\SurroundPlayer\SurroundPlayer.csproj'
if (-not $Package) {
    dotnet build $proj -c Release -p:Platform=x64
    exit $LASTEXITCODE
}
# self-contained: .NET runtime + Windows App SDK + playercore.dll (static FFmpeg and CRT) in one folder
$out = Join-Path $root 'build\windows\Surround Player'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish $proj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$ver = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$zip = Join-Path $root "build\SurroundPlayer-$ver-windows-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $out -DestinationPath $zip
Write-Host "packaged: $zip"
exit 0
