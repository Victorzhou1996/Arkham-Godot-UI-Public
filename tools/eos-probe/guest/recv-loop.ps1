# Run in the windowsguest session. Repeats the 90s receiver until a packet arrives
# or the rounds run out, so there is time to switch to the victo session and send.
param([int]$Rounds = 8)
$dir = $PSScriptRoot
for ($i = 1; $i -le $Rounds; $i++) {
    Write-Host "=== receiver round $i / $Rounds ==="
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dir 'run-guest.ps1') recv
    if ($LASTEXITCODE -eq 0) { Write-Host 'PACKET RECEIVED'; exit 0 }
    if ($LASTEXITCODE -ne 8) { Write-Host "receiver failed with exit $LASTEXITCODE"; exit $LASTEXITCODE }
}
Write-Host 'No packet in any round'
exit 8
