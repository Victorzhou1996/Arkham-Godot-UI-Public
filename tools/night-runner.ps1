# Night runner: executes a fixed set of actions requested via jobs\inbox\*.json
# Allowed actions: build, import, run, stop, probe, reference, ping, restart. Nothing else.
$ErrorActionPreference = 'Continue'
$ws = Split-Path -Parent $PSScriptRoot
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
$godotDir = Join-Path $tools 'godot\Godot_v4.5.1-stable_mono_win64'
$godotGui = Join-Path $godotDir 'Godot_v4.5.1-stable_mono_win64.exe'
$godotCon = Join-Path $godotDir 'Godot_v4.5.1-stable_mono_win64_console.exe'
$reference = 'C:\Users\victo\Downloads\ArkhamHorror-0.1.1-preview-windows\' + [char]0x8BE1 + [char]0x9547 + [char]0x5947 + [char]0x8C08 + '.exe'
$inbox = Join-Path $ws 'jobs\inbox'; $done = Join-Path $ws 'jobs\done'
$pidFile = Join-Path $ws 'jobs\runner-pids.txt'
$app = Join-Path $ws 'app'
function Log($id, $text) { Add-Content -Path (Join-Path $done "$id.log") -Value $text -Encoding UTF8 }
function RunWait($id, $exe, [string[]]$argv, $cwd) {
  $out = Join-Path $done "$id.out.txt"; $err = Join-Path $done "$id.err.txt"
  $p = Start-Process -FilePath $exe -ArgumentList $argv -WorkingDirectory $cwd -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
  $null = $p.Handle
  if (-not $p.WaitForExit(900000)) { try { $p.Kill() } catch {} ; return -9 }
  $p.WaitForExit()
  return $p.ExitCode
}
Write-Host "Night runner started. Workspace: $ws"
Set-Content -Path (Join-Path $ws 'jobs\runner-alive.txt') -Value (Get-Date -Format o)
while ($true) {
  Set-Content -Path (Join-Path $ws 'jobs\runner-alive.txt') -Value (Get-Date -Format o)
  $jobs = Get-ChildItem -Path $inbox -Filter *.json -ErrorAction SilentlyContinue | Sort-Object Name
  foreach ($f in $jobs) {
    $id = [IO.Path]::GetFileNameWithoutExtension($f.Name)
    $job = $null
    try { $job = Get-Content -Raw -Encoding UTF8 $f.FullName | ConvertFrom-Json } catch { }
    Move-Item -Force $f.FullName (Join-Path $done $f.Name)
    $started = Get-Date -Format o; $code = -1; $note = ''
    if ($null -eq $job) { $note = 'bad json' }
    else {
      $extra = @(); if ($job.args) { $extra = @($job.args | ForEach-Object { $a = [string]$_; if ($a -match '\s') { '"' + $a + '"' } else { $a } }) }
      Write-Host "[$started] job $id action=$($job.action)"
      try {
        switch ($job.action) {
          'ping'  { $code = 0; $note = 'pong' }
          'closepm' {
            Get-Process | Where-Object { $_.ProcessName -like 'Godot*' -and $_.MainWindowTitle -like 'Godot Engine -*' } | ForEach-Object { Stop-Process -Id $_.Id }
            $code = 0; $note = 'closed Godot project manager windows' }
          'ps' {
            $rows = Get-Process | Where-Object { $_.ProcessName -like 'Godot*' -or $_.ProcessName -like '*诡镇*' } | Select-Object Id, ProcessName, MainWindowTitle, StartTime
            Set-Content -Path (Join-Path $done "$id.out.txt") -Value ($rows | Format-Table -AutoSize | Out-String -Width 300) -Encoding UTF8
            Add-Type @'
using System; using System.Runtime.InteropServices;
public static class DpiInfo { [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i); [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@ -ErrorAction SilentlyContinue
            Add-Content -Path (Join-Path $done "$id.out.txt") -Value ("screen " + [DpiInfo]::GetSystemMetrics(0) + "x" + [DpiInfo]::GetSystemMetrics(1))
            $code = 0; $note = 'listed' }
          'build' {
            $proj = if ($job.project) { Join-Path $ws $job.project } else { Join-Path $app 'Arkham.App.csproj' }
            $code = RunWait $id $dotnet (@('build', "`"$proj`"", '-c', 'Debug', '-nologo') + $extra) $ws }
          'import' { $code = RunWait $id $godotCon (@('--headless', '--path', "`"$app`"", '--editor', '--import', '--quit') + $extra) $ws }
          'probe' {
            $proj = if ($job.project) { Join-Path $ws $job.project } else { Join-Path $ws 'probe\Arkham.Probe.csproj' }
            $code = RunWait $id $dotnet (@('run', '--project', "`"$proj`"", '-c', 'Debug', '--') + $extra) $ws }
          'run' {
            $log = Join-Path $done "$id.godot.log"
            $engine = @(); if ($job.engineArgs) { $engine = @($job.engineArgs | ForEach-Object { [string]$_ }) }
            $argv = @('--path', "`"$app`"", '--log-file', "`"$log`"") + $engine + $(if ($extra.Count) { @('--') + $extra } else { @() })
            $p = Start-Process -FilePath $godotGui -ArgumentList $argv -WorkingDirectory $app -PassThru
            Add-Content -Path $pidFile -Value $p.Id; $code = 0; $note = "pid $($p.Id)" }
          'reference' {
            $engine = @(); if ($job.engineArgs) { $engine = @($job.engineArgs | ForEach-Object { [string]$_ }) }
            $p = if ($engine.Count) { Start-Process -FilePath $reference -ArgumentList $engine -WorkingDirectory (Split-Path $reference) -PassThru } else { Start-Process -FilePath $reference -WorkingDirectory (Split-Path $reference) -PassThru }
            Add-Content -Path $pidFile -Value $p.Id; $code = 0; $note = "pid $($p.Id)" }
          'stop' {
            if (Test-Path $pidFile) {
              foreach ($line in Get-Content $pidFile) { $n = 0; if ([int]::TryParse($line, [ref]$n)) { Stop-Process -Id $n -ErrorAction SilentlyContinue } }
              Clear-Content $pidFile }
            $code = 0; $note = 'stopped runner-started processes only' }
          'restart' {
            $launcher = Join-Path $ws ([char]0x542F + [char]0x52A8 + [char]0x591C + [char]0x95F4 + [char]0x6784 + [char]0x5EFA + [char]0x52A9 + [char]0x624B + '.cmd')
            Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', 'start', '""', "`"$launcher`"") -WorkingDirectory $ws
            $code = 0; $note = 'restarting'
            $result = @{ id = $id; started = $started; ended = (Get-Date -Format o); exitCode = $code; note = $note } | ConvertTo-Json
            Set-Content -Path (Join-Path $done "$id.result.json") -Value $result -Encoding UTF8
            exit 0 }
          default { $note = "action not allowed: $($job.action)" }
        }
      } catch { $note = $_.Exception.Message }
    }
    $result = @{ id = $id; started = $started; ended = (Get-Date -Format o); exitCode = $code; note = $note } | ConvertTo-Json
    Set-Content -Path (Join-Path $done "$id.result.json") -Value $result -Encoding UTF8
    Write-Host "  -> exit $code $note"
  }
  Start-Sleep -Seconds 2
}
