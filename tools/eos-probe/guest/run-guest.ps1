# Run inside the windowsguest session.
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\Public\eos-probe-test\run-guest.ps1" recv
#   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Users\Public\eos-probe-test\run-guest.ps1" send <PUID>
param(
    [Parameter(Mandatory = $true)][ValidateSet('recv', 'send')][string]$Role,
    [string]$PeerPuid
)
$ErrorActionPreference = 'Continue'
$dir  = $PSScriptRoot
$exe  = Join-Path $dir 'EosProbe.exe'
$cfg  = Join-Path $dir 'eos-config.local.json'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log  = Join-Path $dir ("guest-$Role-$stamp.log")

if (-not (Test-Path $exe)) { Write-Host "Missing $exe"; exit 2 }
if (-not (Test-Path $cfg)) { Write-Host "Missing $cfg"; exit 2 }
if ($Role -eq 'send' -and -not $PeerPuid) { Write-Host 'send needs the receiver PUID'; exit 2 }

$probeArgs = @('--config', $cfg, "p2p-$Role")
if ($Role -eq 'send') { $probeArgs += $PeerPuid }

"user=$env:USERNAME role=$Role time=$stamp" | Out-File -FilePath $log -Encoding utf8
& $exe @probeArgs 2>&1 | Tee-Object -FilePath $log -Append
$code = $LASTEXITCODE
"EXIT=$code" | Out-File -FilePath $log -Append -Encoding utf8
Write-Host ''
Write-Host "Log: $log"
Write-Host "Exit code: $code"
exit $code
