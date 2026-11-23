# Run in the victo session after the test. Removes the staged copy,
# including the config file that holds the client secret.
$dst = 'C:\Users\Public\eos-probe-test'
if (Test-Path $dst) { Remove-Item -Recurse -Force $dst; Write-Host "Removed $dst" }
else { Write-Host "Nothing to remove" }
