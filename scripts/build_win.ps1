# Configures and builds the Windows engine targets (CMakeLists.txt) with clang-cl + Ninja,
# and with -App also the WinUI 3 app (Windows/SurroundPlayer, needs the .NET 8 SDK).
#   powershell -ExecutionPolicy Bypass -File scripts\build_win.ps1 [-Config Release|Debug] [-App]
# Output: .build\win-<config>\{pcdump,pctest}.exe, playercore.dll;
#   app: Windows\SurroundPlayer\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\SurroundPlayer.exe
param([string]$Config = 'Release', [switch]$App)
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
dotnet build (Join-Path $root 'Windows\SurroundPlayer\SurroundPlayer.csproj') -c Release -p:Platform=x64
exit $LASTEXITCODE
