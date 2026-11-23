# EOS online self-test: runs the room flow in the current Windows account and writes evidence/eos/selftest-<role>-<user>-<time>.log.
# Host (creates the room, prints its code in the log):   powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev\online-selftest.ps1 -Role host
# Join (uses the code the host printed):                 powershell -NoProfile -ExecutionPolicy Bypass -File tools\dev\online-selftest.ps1 -Role join -Code XXXXXX
# Needs the developer EOS config in this account's profile (%USERPROFILE%\.arkham\eos-config.local.json). The config is never printed.
param(
    [Parameter(Mandatory = $true)][ValidateSet('host', 'join')][string]$Role,
    [string]$Code = '',
    [int]$TimeoutSec = 900
)
$ErrorActionPreference = 'Stop'
if ($Role -eq 'join' -and $Code -notmatch '^[A-Za-z0-9]{6}$') { Write-Host 'join needs -Code with the 6-character room code'; exit 2 }
$ws = 'G:\Python Project\Arkham UI'
$app = Join-Path $ws 'app'
$godot = 'G:\Python Project\ArkhamHorrorLocal-WinDeploy-20260712\godot-native-20261019\tools\godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $ws ("evidence\eos\selftest-{0}-{1}-{2}.log" -f $Role, $env:USERNAME, $stamp)
$mode = if ($Role -eq 'host') { 'host' } else { "join:$($Code.ToUpperInvariant())" }
$argv = @('--path', "`"$app`"", '--rendering-driver', 'opengl3_angle', '--position', '30000,0', '--log-file', "`"$log`"", '--', '--background', "--online-selftest=$mode")
Write-Host "user=$env:USERNAME role=$Role log=$log"
$p = Start-Process -FilePath $godot -ArgumentList $argv -WorkingDirectory $app -PassThru -WindowStyle Minimized
if (-not $p.WaitForExit($TimeoutSec * 1000)) { Stop-Process -Id $p.Id -Force; Write-Host "TIMEOUT (this self-test process only) pid=$($p.Id)" } else { Write-Host "exit=$($p.ExitCode)" }
Write-Host "log: $log"
