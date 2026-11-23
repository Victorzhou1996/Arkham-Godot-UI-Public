# The update helper (docs/HOT-UPDATE-DESIGN.md section 5.3). Shipped as game\updater\apply-update.ps1; the game copies it to
# game\updates\helper-run.ps1 and starts it when an update replaces files the running game holds open (the program, its
# code, the rules core), then exits. This waits for the game to be gone, makes the planned moves in order (undoing them all
# if one fails), writes game\updates\helper-result.json and starts the game again.
param([string]$Plan, [string]$Result, [int]$WaitPid = 0, [string]$Exe = '', [string]$ArgsLine = '')
$ErrorActionPreference = 'Stop'
$p = $null; $ok = $true; $err = ''; $done = New-Object System.Collections.ArrayList
try {
    if ($WaitPid -gt 0) { try { Wait-Process -Id $WaitPid -Timeout 30 -ErrorAction SilentlyContinue } catch {} }
    Start-Sleep -Milliseconds 400
    $p = [System.IO.File]::ReadAllText($Plan, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($op in $p.ops) {
        $from = [System.IO.Path]::Combine($p.root, $op.from); $to = [System.IO.Path]::Combine($p.root, $op.to)
        if ([System.IO.File]::Exists($to)) { throw ('exists: ' + $op.to) }
        $dir = [System.IO.Path]::GetDirectoryName($to)
        if (-not [System.IO.Directory]::Exists($dir)) { [void][System.IO.Directory]::CreateDirectory($dir) }
        # the exiting game may hold a file a moment longer: try for up to 10 s
        $n = 0
        while ($true) {
            try { [System.IO.File]::Move($from, $to); break }
            catch { $n++; if ($n -ge 40) { throw }; Start-Sleep -Milliseconds 250 }
        }
        [void]$done.Add($op)
    }
} catch {
    $ok = $false; $err = $_.Exception.Message
    if ($p -ne $null) {
        for ($i = $done.Count - 1; $i -ge 0; $i--) {
            $op = $done[$i]
            try { [System.IO.File]::Move([System.IO.Path]::Combine($p.root, $op.to), [System.IO.Path]::Combine($p.root, $op.from)) } catch {}
        }
    }
}
$mode = ''; $release = ''
if ($p -ne $null) { $mode = $p.mode; $release = $p.release }
$json = @{ ok = $ok; error = $err; mode = $mode; release = $release; moves = $done.Count } | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText($Result, $json, (New-Object System.Text.UTF8Encoding $false))
if ($Exe -ne '') { Start-Process -FilePath $Exe -ArgumentList $ArgsLine }
