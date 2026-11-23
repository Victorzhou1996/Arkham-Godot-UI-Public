# Run the game for a QA script in the background (no focus, off-screen). Log: evidence/p0/<Tag>.log
param([string]$Tag, [string]$Size, [string]$Script, [string]$DataRoot = "qa-data/p0", [int]$TimeoutSec = 120, [string]$Extra = "")
$ErrorActionPreference = 'Stop'
$ws = 'G:\Python Project\Arkham UI'
$app = Join-Path $ws 'app'
$godot = 'G:\Python Project\ArkhamHorrorLocal-WinDeploy-20260712\godot-native-20261019\tools\godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64.exe'
$log = Join-Path $ws "evidence\p0\$Tag.log"
$argv = @('--path', "`"$app`"", '--rendering-driver', 'opengl3_angle', '--position', '30000,0', '--log-file', "`"$log`"", '--', "--data-root=$DataRoot", "--window=$Size", '--background', "`"--script=$Script`"") + $(if ($Extra) { @($Extra) } else { @() })
$p = Start-Process -FilePath $godot -ArgumentList $argv -WorkingDirectory $app -PassThru -WindowStyle Minimized
if (-not $p.WaitForExit($TimeoutSec * 1000)) { Stop-Process -Id $p.Id -Force; "TIMEOUT pid=$($p.Id)" } else { "exit=$($p.ExitCode)" }
