# Run in the victo session (admin not required). Copies the probe and the
# config into a folder that the windowsguest account can read.
# WARNING: the config contains the client secret. Delete the copy afterwards
# with cleanup-public.ps1.
$ErrorActionPreference = 'Stop'
$src    = 'G:\Python Project\Arkham UI\tools\eos-probe\bin\Release\net8.0'
$cfgSrc = 'C:\Users\victo\.arkham\eos-config.local.json'
$dst    = 'C:\Users\Public\eos-probe-test'

New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item -Path (Join-Path $src '*') -Destination $dst -Force
Copy-Item -Path $cfgSrc -Destination (Join-Path $dst 'eos-config.local.json') -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'run-guest.ps1') -Destination $dst -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'recv-loop.ps1') -Destination $dst -Force
Write-Host "Staged to $dst"
Write-Host "Next: log in as windowsguest and run run-guest.ps1 (see the guide)."
