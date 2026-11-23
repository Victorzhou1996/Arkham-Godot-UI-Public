# Build the rules probe (tools/rules-probe). Shares the project's build lock (it also builds engine/), like tools/dev/build.ps1.
$lockFile = Join-Path $PSScriptRoot '..\dev\.build-lock'
while (Test-Path $lockFile) {
    $age = (Get-Date) - (Get-Item $lockFile).LastWriteTime
    if ($age.TotalMinutes -gt 6) { Remove-Item $lockFile -Force -ErrorAction SilentlyContinue; break }
    Start-Sleep -Seconds 2
}
Set-Content -Path $lockFile -Value ("{0} {1} sim" -f $PID, (Get-Date -Format s))
try {
    $tools = 'G:\Python Project\ArkhamHorrorLocal-WinDeploy-20260712\godot-native-20261019\tools'
    $env:DOTNET_ROOT = Join-Path $tools 'dotnet'; $env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
    $env:NUGET_PACKAGES = Join-Path $tools 'nuget'; $env:DOTNET_CLI_HOME = Join-Path $tools 'dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:MSBUILDDISABLENODEREUSE = '1'; $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'; $env:UseSharedCompilation = 'false'
    & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') build (Join-Path $PSScriptRoot 'RulesProbe.csproj') -c Release -nologo 2>&1 | Select-String -Pattern 'error|Build succeeded' | Select-Object -First 40 | ForEach-Object { $_.Line }
    'build exit=' + $LASTEXITCODE
} finally { Remove-Item $lockFile -Force -ErrorAction SilentlyContinue }
