param(
    [Parameter(Mandatory = $true)]
    [int] $TargetProcessId,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [int] $TimeoutMilliseconds = 15000
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class SoFreshCaptureNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr handle, out RECT rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr handle, int command);
}
'@

$deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
$windowHandle = [IntPtr]::Zero
do {
    $targetProcess = Get-Process -Id $TargetProcessId -ErrorAction Stop
    $targetProcess.Refresh()
    $windowHandle = $targetProcess.MainWindowHandle
    if ($windowHandle -eq [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 200
    }
} while ($windowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)

if ($windowHandle -eq [IntPtr]::Zero) {
    throw "The window for process $TargetProcessId did not become available."
}

[void][SoFreshCaptureNative]::ShowWindow($windowHandle, 9)
[void][SoFreshCaptureNative]::SetForegroundWindow($windowHandle)
Start-Sleep -Milliseconds 700

$rectangle = New-Object SoFreshCaptureNative+RECT
if (-not [SoFreshCaptureNative]::GetWindowRect($windowHandle, [ref]$rectangle)) {
    throw 'Could not read the window bounds.'
}

$width = $rectangle.Right - $rectangle.Left
$height = $rectangle.Bottom - $rectangle.Top
if ($width -le 0 -or $height -le 0) {
    throw "Invalid window dimensions: ${width}x${height}."
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput))
$bitmap = New-Object Drawing.Bitmap($width, $height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen(
        $rectangle.Left,
        $rectangle.Top,
        0,
        0,
        $bitmap.Size,
        [Drawing.CopyPixelOperation]::SourceCopy)
    $bitmap.Save($resolvedOutput, [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output $resolvedOutput
