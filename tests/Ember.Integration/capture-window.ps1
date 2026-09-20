param([string]$Output = 'current-game.png')
$ErrorActionPreference = 'Stop'
$run = Join-Path $PSScriptRoot '../../test-runs/client'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class EmberWindowCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
'@
$launch = Get-Content (Join-Path $run 'launch.json') -Raw | ConvertFrom-Json
$client = Get-Process -Id $launch.pid
$rect = [EmberWindowCapture+Rect]::new()
if (-not [EmberWindowCapture]::GetWindowRect($client.MainWindowHandle, [ref]$rect)) { throw 'Missing game window.' }
$bitmap = [System.Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $dc = $graphics.GetHdc()
    try { if (-not [EmberWindowCapture]::PrintWindow($client.MainWindowHandle, $dc, 2)) { throw 'Window capture failed.' } }
    finally { $graphics.ReleaseHdc($dc) }
    $bitmap.Save((Join-Path $run $Output))
} finally { $graphics.Dispose(); $bitmap.Dispose() }
