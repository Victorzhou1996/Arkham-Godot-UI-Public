# Real-mouse check for round 3 (11-18 feedback items 5, 6, 10, 13): drives the OS cursor (SetCursorPos + mouse_event) over
# the game window, the same input path a player's mouse uses; screenshots come from the screen itself.
# SAFETY: before every move/click the point is checked with WindowFromPoint; if the topmost window there is not the game,
# the run stops without clicking. Design coordinates are 1920x1080, scaled to the real client size.
# Usage: -Phase start (launch the game at the table, leaves it open, prints its pid) | -Phase map -GamePid N | -Phase fight -GamePid N | -Phase stop -GamePid N
param([string]$Phase = "start", [int]$GamePid = 0, [double]$X = 0, [double]$Y = 0, [string]$Name = "shot", [double]$Hold = 0, [string]$Root = "G:\Python Project\Arkham UI", [string]$DataRoot = "qa-data/rm1")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Native3 {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool alt);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
}
"@
[void][Native3]::SetProcessDPIAware()   # physical pixels everywhere (the display may be scaled)
$godot = 'G:\Python Project\ArkhamHorrorLocal-WinDeploy-20260712\godot-native-20261019\tools\godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64.exe'
$app = Join-Path $Root 'app'
$shotDir = Join-Path $Root 'evidence\round3\real-mouse'
New-Item -ItemType Directory -Force $shotDir | Out-Null
$stepsFile = Join-Path $shotDir 'steps.txt'
function Log([string]$s) { Add-Content -Path $stepsFile -Value ((Get-Date -Format 'HH:mm:ss') + ' [' + $Phase + '] ' + $s); Write-Output $s }

if ($Phase -eq 'start') {
  $log = Join-Path $Root 'evidence\p0\real-mouse-round3.log'
  $p = Start-Process -FilePath $godot -ArgumentList @('--path', "`"$app`"", '--rendering-driver', 'opengl3_angle', '--log-file', "`"$log`"", '--', "--data-root=$DataRoot", '--window=1920x1080', '"--script=wait:4;menu:continue;wait:1.5;continue"') -WorkingDirectory $app -PassThru
  Log ("game pid " + $p.Id); return
}

$proc = Get-Process -Id $GamePid
if ($Phase -eq 'stop') { Stop-Process -Id $GamePid -Force; Log 'game closed'; return }
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { throw 'game window not found' }
[void][Native3]::ShowWindow($hwnd, 9)
[void][Native3]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)
# Windows only lets the foreground process hand over the foreground; a tap of Alt first lets this script bring the game up
[Native3]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [Native3]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
[void][Native3]::SetForegroundWindow($hwnd)
[void][Native3]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)
Start-Sleep -Milliseconds 700
# Yunke handed the computer over for this check (11-18): a window still lying over the game's middle (the Claude app) is
# minimised for the check and restored afterwards; nothing else about it is touched
$script:hidden = @()
# keep the game above the other apps for the check without touching them: topmost now, normal again at the end
[void][Native3]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)
Start-Sleep -Milliseconds 800
$cr = New-Object Native3+RECT; [void][Native3]::GetClientRect($hwnd, [ref]$cr)
$cw = $cr.Right - $cr.Left; $ch = $cr.Bottom - $cr.Top; $scale = $cw / 1920.0
$wr = New-Object Native3+RECT; [void][Native3]::GetWindowRect($hwnd, [ref]$wr)
Log ("client " + $cw + "x" + $ch + " scale " + $scale + " window " + $wr.Left + "," + $wr.Top + "," + $wr.Right + "," + $wr.Bottom)
function ScreenPoint([double]$x, [double]$y) { $pt = New-Object Native3+POINT; $pt.X = [int][math]::Round($x * $scale); $pt.Y = [int][math]::Round($y * $scale); [void][Native3]::ClientToScreen($hwnd, [ref]$pt); return $pt }
function Guard([double]$x, [double]$y) { $s = ScreenPoint $x $y; $root = [Native3]::GetAncestor([Native3]::WindowFromPoint($s.X, $s.Y), 2); if ($root -ne $hwnd) { Log ("ABORT: " + $x + "," + $y + " covered by window " + $root); throw "point covered by another window; nothing clicked" } }
function MoveTo([double]$x, [double]$y) { Guard $x $y; $s = ScreenPoint $x $y; [void][Native3]::SetCursorPos($s.X, $s.Y) }
function Down { [Native3]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) }
function Up { [Native3]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) }
function Click([double]$x, [double]$y) { MoveTo $x $y; Start-Sleep -Milliseconds 150; Down; Start-Sleep -Milliseconds 70; Up }
function Drag([double]$x1, [double]$y1, [double]$x2, [double]$y2) {
  MoveTo $x1 $y1; Start-Sleep -Milliseconds 150; Down; Start-Sleep -Milliseconds 120
  foreach ($i in 1..16) { MoveTo ($x1 + ($x2 - $x1) * $i / 16) ($y1 + ($y2 - $y1) * $i / 16); Start-Sleep -Milliseconds 30 }
  Start-Sleep -Milliseconds 200; Up
}
function Key([byte]$vk) { [Native3]::keybd_event($vk, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60; [Native3]::keybd_event($vk, 0, 2, [UIntPtr]::Zero) }
function Snap([string]$name) {
  Start-Sleep -Milliseconds 900
  $o = ScreenPoint 0 0; $bmp = New-Object System.Drawing.Bitmap $cw, $ch; $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($o.X, $o.Y, 0, 0, $bmp.Size); $g.Dispose()
  $file = Join-Path $shotDir ($Phase + '-' + $name + '.png'); $bmp.Save($file); $bmp.Dispose(); Log ('snap ' + $file)
}

try {
  if ($Phase -eq 'map') {
    Snap '01-start'
    Log 'assign 1 damage, then 1 horror (one portrait, two buttons)'; Click 960 609; Start-Sleep -Milliseconds 1800; Snap '02-after-damage'
    if (Test-Path variable:\null) {}
    Click 960 609; Start-Sleep -Milliseconds 2200; Snap '03-after-horror'
    Log 'hover the attic card 1.6 s (glow, hover text)'; MoveTo 350 305; Start-Sleep -Milliseconds 1600; Snap '04-hover-attic'
    Log 'press on the attic card and drag: the map pans, nothing is acted on'; Drag 350 305 470 420; Start-Sleep -Milliseconds 900; Snap '05-drag-pans'
    Click 1200 200; Start-Sleep -Milliseconds 300
    Log 'Tab: hide the map'; Key 0x09; Start-Sleep -Milliseconds 900; Snap '06-tab-hidden'
    Log 'Tab: show the map again (fitted)'; Key 0x09; Start-Sleep -Milliseconds 1200; Snap '07-tab-shown'
    Log 'unlock locations, drag the hallway card'; Click 285 115; Start-Sleep -Milliseconds 900; Drag 343 405 343 470; Start-Sleep -Milliseconds 900; Snap '08-unlocked-dragged'
    Log 'lock again'; Click 285 115; Start-Sleep -Milliseconds 900; Snap '09-locked'
  }
  elseif ($Phase -eq 'fight') {
    Snap '01-start'
    Log 'click the engaged ghoul card'; Click 1550 330; Start-Sleep -Milliseconds 1200; Snap '02-enemy-menu'
  }
  elseif ($Phase -eq 'click') { Log ("click " + $X + "," + $Y + " -> " + $Name); Click $X $Y; Start-Sleep -Milliseconds 2500; Snap $Name }
  elseif ($Phase -eq 'hover') { Log ("hover " + $X + "," + $Y + " -> " + $Name); MoveTo $X $Y; Start-Sleep -Milliseconds ([int](1000 * [math]::Max(1.2, $Hold))); Snap $Name }
  elseif ($Phase -eq 'snap') { Snap $Name }
  elseif ($Phase -eq 'fight2') {
    Log 'click the first menu entry'; Click $X $Y; Start-Sleep -Milliseconds 2200; Snap '03-test-panel'
  }
  Log 'phase done'
} catch { Log ('stopped: ' + $_.Exception.Message); try { Up } catch {} }
finally {
  foreach ($w in $script:hidden) { [void][Native3]::ShowWindow($w, 9); Log ("restored window " + $w) }
  [void][Native3]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010); Log 'game window back to normal (not topmost)'
}
