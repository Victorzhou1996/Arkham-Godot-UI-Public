# Real-mouse check of the table. Starts the game, then drives the OS cursor (SetCursorPos + mouse_event) over the
# game window, the same input path a player's mouse uses. Screenshots are taken from the screen itself.
# Design coordinates are 1920x1080; they are scaled to the real client size (Windows display scaling).
# SAFETY: before every click or press, the point is checked with WindowFromPoint. If the topmost window there is not
# the game, the run aborts without clicking, so nothing in another application is ever pressed.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/real_mouse_check.ps1 "<Arkham UI root>"
param([string]$Root = "G:\Python Project\Arkham UI")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Native {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@

$godot = 'G:\Python Project\ArkhamHorrorLocal-WinDeploy-20260712\godot-native-20261019\tools\godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64.exe'
$app = Join-Path $Root 'app'
$logDir = Join-Path $Root 'evidence\logs'
$shotDir = Join-Path $Root 'evidence\round2\real-mouse'
New-Item -ItemType Directory -Force $logDir, $shotDir | Out-Null
$stepsFile = Join-Path $logDir 'real-mouse-steps.txt'
function Log([string]$s) { Add-Content -Path $stepsFile -Value ((Get-Date -Format 'HH:mm:ss') + ' ' + $s) }

$proc = Start-Process -FilePath $godot -ArgumentList @('--path', "`"$app`"", '--rendering-driver', 'opengl3_angle', '--', '--data-root=qa-data/final', '--window=1920x1080') -WorkingDirectory $app -PassThru
Start-Sleep -Seconds 5
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $hwnd -eq [IntPtr]::Zero; $i++) {
  Start-Sleep -Milliseconds 500
  $p = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
  if ($p -and $p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle }
}
if ($hwnd -eq [IntPtr]::Zero) { throw 'game window not found' }
[void][Native]::ShowWindow($hwnd, 9)                                      # restore if minimised
[void][Native]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)   # HWND_TOP, no move/size
[void][Native]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 700

$cr = New-Object Native+RECT
[void][Native]::GetClientRect($hwnd, [ref]$cr)
$cw = $cr.Right - $cr.Left; $ch = $cr.Bottom - $cr.Top
$scale = $cw / 1920.0
Log ("client " + $cw + "x" + $ch + " scale " + $scale)

function ScreenPoint([double]$x, [double]$y) {
  $pt = New-Object Native+POINT
  $pt.X = [int][math]::Round($x * $scale); $pt.Y = [int][math]::Round($y * $scale)
  [void][Native]::ClientToScreen($hwnd, [ref]$pt)
  return $pt
}
# abort unless the topmost window at the point belongs to the game
function Guard([double]$x, [double]$y) {
  $s = ScreenPoint $x $y
  $at = [Native]::WindowFromPoint($s.X, $s.Y)
  $root = [Native]::GetAncestor($at, 2)
  if ($root -ne $hwnd) { Log ("ABORT: point " + $x + "," + $y + " is covered by window " + $root); throw "point covered by another window; nothing clicked" }
}
function MoveTo([double]$x, [double]$y) { Guard $x $y; $s = ScreenPoint $x $y; [void][Native]::SetCursorPos($s.X, $s.Y) }
function Down { [Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) }
function Up { [Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) }
function Click([double]$x, [double]$y) { MoveTo $x $y; Start-Sleep -Milliseconds 150; Down; Start-Sleep -Milliseconds 70; Up }

$script:shot = 0
function Snap {
  Start-Sleep -Milliseconds 900
  $o = ScreenPoint 0 0
  $bmp = New-Object System.Drawing.Bitmap $cw, $ch
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($o.X, $o.Y, 0, 0, $bmp.Size)
  $g.Dispose()
  $script:shot++
  $name = Join-Path $shotDir ('{0:00}.png' -f $script:shot)
  $bmp.Save($name); $bmp.Dispose()
  Log ('snap ' + $name)
}

try {
  Start-Sleep -Seconds 3
  Log 'click Continue on the title'; Click 176 527; Start-Sleep -Milliseconds 1200
  Log 'click Continue in the save dialog'; Click 1403 338; Start-Sleep -Seconds 9
  Snap
  Log 'hover playable hand card 3 for 1.6 s'; MoveTo 1092 962; Start-Sleep -Milliseconds 1600
  Snap
  Log 'drag hand card 3 to the middle of the table'
  MoveTo 1092 962; Start-Sleep -Milliseconds 150; Down; Start-Sleep -Milliseconds 120
  foreach ($step in 1..14) { MoveTo (1092 + (960 - 1092) * $step / 14) (962 + (420 - 962) * $step / 14); Start-Sleep -Milliseconds 25 }
  Start-Sleep -Milliseconds 300
  Snap
  Log 'release'; Up; Start-Sleep -Milliseconds 1800
  Snap
  Log 'click ? in the top bar'; Click 1864 44; Start-Sleep -Milliseconds 800
  Snap
  Log 'done'
} catch {
  Log ('stopped: ' + $_.Exception.Message)
  try { [Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) } catch {}
} finally {
  Start-Sleep -Seconds 1
  try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
}
'real-mouse check finished'
