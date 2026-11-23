# Diagnostic only: starts the game, reports where its window is and whether it is foreground, then closes it.
# It sends NO mouse input, so it cannot click anything in another application.
param([string]$Root = "G:\Python Project\Arkham UI")
$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Diag {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
}
"@
$godot = 'G:\Python Project\ArkhamHorrorLocal-WinDeploy-20260712\godot-native-20261019\tools\godot\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64.exe'
$app = Join-Path $Root 'app'
$proc = Start-Process -FilePath $godot -ArgumentList @('--path', "`"$app`"", '--rendering-driver', 'opengl3_angle', '--', '--data-root=qa-data/final', '--window=1920x1080') -WorkingDirectory $app -PassThru
Start-Sleep -Seconds 8
$p = Get-Process -Id $proc.Id
$h = $p.MainWindowHandle
$r = New-Object Diag+RECT
[void][Diag]::GetWindowRect($h, [ref]$r)
"game window handle: $h  title: '$($p.MainWindowTitle)'"
"rect: L=$($r.Left) T=$($r.Top) R=$($r.Right) B=$($r.Bottom)"
"iconic: $([Diag]::IsIconic($h))  visible: $([Diag]::IsWindowVisible($h))"
$fg = [Diag]::GetForegroundWindow()
"foreground handle: $fg  is game: $($fg -eq $h)"
$at = [Diag]::WindowFromPoint($r.Left + 960, $r.Top + 540)
"window at game centre: $at  root is game: $(([Diag]::GetAncestor($at, 2)) -eq $h)"
try { Stop-Process -Id $proc.Id -Force } catch {}
