# Stop the learning loop at a deadline (only this AI work's own processes: loop_mc.py and ArkhamSim), then run the final evaluation.
#   powershell -File tools/ai/deadline.ps1 -StopAt "21:40"
param([string]$StopAt = "21:40")
$ws = 'G:\Python Project\Arkham UI'
$deadline = [datetime]::ParseExact((Get-Date -Format 'yyyy-MM-dd') + ' ' + $StopAt, 'yyyy-MM-dd HH:mm', $null)
function LoopProcs { Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'python*' -and $_.CommandLine -like '*loop_mc.py*' } }
while ((Get-Date) -lt $deadline -and (LoopProcs)) { Start-Sleep -Seconds 30 }
LoopProcs | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; "stopped loop $($_.ProcessId)" }
Start-Sleep -Seconds 2
Get-Process ArkhamSim -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force; "stopped sim $($_.Id)" }
"final eval start " + (Get-Date -Format 'HH:mm')
$p = Start-Process python -ArgumentList 'tools/ai/final_eval.py final1 20' -WorkingDirectory $ws -PassThru -NoNewWindow
$p.PriorityClass = 'BelowNormal'
$end = [datetime]::ParseExact((Get-Date -Format 'yyyy-MM-dd') + ' 21:57', 'yyyy-MM-dd HH:mm', $null)
while (-not $p.HasExited -and (Get-Date) -lt $end) { Start-Sleep -Seconds 10 }
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Get-Process ArkhamSim -ErrorAction SilentlyContinue | Stop-Process -Force; "final eval cut at 21:57" }
"done " + (Get-Date -Format 'HH:mm')
