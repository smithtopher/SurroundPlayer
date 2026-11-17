# Configures and builds the Windows engine targets (CMakeLists.txt) with clang-cl + Ninja,
# and with -App also the WinUI 3 app (Windows/SurroundPlayer, needs the .NET 8 SDK).
#   powershell -ExecutionPolicy Bypass -File scripts\build_win.ps1 [-Config Release|Debug] [-App] [-Package]
# Output: .build\win-<config>\{pcdump,pctest}.exe, playercore.dll;
#   app: Windows\SurroundPlayer\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\SurroundPlayer.exe
#   -Package: one self-contained, compressed exe (no .NET or anything else to install):
#             build\SurroundPlayer-<ver>-windows-x64.exe (version from SurroundPlayer.csproj).
#             It unpacks itself to %TEMP%\.net on first run (slower first start).
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
# self-contained single file: .NET runtime + Windows App SDK + playercore.dll (static FFmpeg
# and CRT) and the XAML resources, all inside one compressed exe
$out = Join-Path $root 'build\windows\publish'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
# The single-file build must not share obj\ or bin\ with the normal build: the Windows App
# SDK generates a different reg-free WinRT manifest for it (DLLs loaded from the unpack
# folder), and its incremental targets would otherwise reuse the other build's manifest
# ("ClassFactory cannot supply requested class") or leave a single-file exe in bin\.
$sfObj = Join-Path $root 'build\windows\obj-singlefile\'
$sfBin = Join-Path $root 'build\windows\bin-singlefile\'
dotnet publish $proj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $out `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none "-p:IntermediateOutputPath=$sfObj" "-p:OutDir=$sfBin"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$ver = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$exe = Join-Path $root "build\SurroundPlayer-$ver-windows-x64.exe"
Copy-Item (Join-Path $out 'SurroundPlayer.exe') $exe -Force
$extra = Get-ChildItem $out -File | Where-Object { $_.Name -ne 'SurroundPlayer.exe' }
if ($extra) { Write-Warning "files left beside the exe (not inside it): $($extra.Name -join ', ')" }
Write-Host "packaged: $exe"
exit 0
