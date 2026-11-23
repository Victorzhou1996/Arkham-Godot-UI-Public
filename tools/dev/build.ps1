# Shared build (two sessions may build this project): one build at a time, guarded by tools/dev/.build-lock
$lockFile = Join-Path $PSScriptRoot '.build-lock'
$waited = 0
while (Test-Path $lockFile) {
    $age = (Get-Date) - (Get-Item $lockFile).LastWriteTime
    if ($age.TotalMinutes -gt 6) { Remove-Item $lockFile -Force -ErrorAction SilentlyContinue; break }   # a stale lock from a crashed build
    Start-Sleep -Seconds 2; $waited += 2
}
Set-Content -Path $lockFile -Value ("{0} {1}" -f $PID, (Get-Date -Format s))
try {
$ErrorActionPreference = 'Continue'
$tools = 'G:\Python Project\ArkhamHorrorLocal-WinDeploy-20260712\godot-native-20261019\tools'
$env:DOTNET_ROOT = Join-Path $tools 'dotnet'
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
$env:NUGET_PACKAGES = Join-Path $tools 'nuget'
$env:DOTNET_CLI_HOME = Join-Path $tools 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:UseSharedCompilation = 'false'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$proj = 'G:\Python Project\Arkham UI\app\ArkhamApp.csproj'
    # collect all output first: Select-Object -First stopped dotnet early once the EOS sources added 100+ warnings (exit -1)
    $out = & $dotnet build "$proj" -c Debug -nologo 2>&1
    $code = $LASTEXITCODE
    $out | Select-String -Pattern ' error |Build succeeded|个错误|个警告|Error\(s\)|Warning\(s\)' | Select-Object -First 40 | ForEach-Object { $_.Line }
    'build exit=' + $code

} finally { Remove-Item $lockFile -Force -ErrorAction SilentlyContinue }
