param(
    [Parameter(Mandatory = $true)]
    [int] $TargetProcessId,

    [Parameter(Mandatory = $true)]
    [string] $FolderPath
)

$ErrorActionPreference = 'Stop'
$resolvedFolder = [IO.Path]::GetFullPath($FolderPath)
if (-not [IO.Directory]::Exists($resolvedFolder)) {
    throw "The fixture folder does not exist: $resolvedFolder"
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SoFreshDialogNative
{
    public delegate bool EnumWindowsProc(IntPtr handle, IntPtr state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr handle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDlgItem(IntPtr dialog, int controlId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowText(IntPtr handle, string text);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wordParameter, IntPtr longParameter);

    public static IntPtr FindDialog(uint expectedProcessId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((handle, state) =>
        {
            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            if (processId != expectedProcessId)
            {
                return true;
            }

            var className = new StringBuilder(64);
            GetClassName(handle, className, className.Capacity);
            if (className.ToString() == "#32770")
            {
                result = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

$dialog = [SoFreshDialogNative]::FindDialog([uint32]$TargetProcessId)
if ($dialog -eq [IntPtr]::Zero) {
    throw 'Windows folder picker not found.'
}

$selectButton = [SoFreshDialogNative]::GetDlgItem($dialog, 1)
if ($selectButton -eq [IntPtr]::Zero) {
    throw 'Folder picker controls not found.'
}

[void][SoFreshDialogNative]::SetForegroundWindow($dialog)
Start-Sleep -Milliseconds 200
[Windows.Forms.SendKeys]::SendWait('^l')
Start-Sleep -Milliseconds 150
[Windows.Forms.SendKeys]::SendWait($resolvedFolder)
[Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Milliseconds 900
[void][SoFreshDialogNative]::SendMessage($selectButton, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Output $resolvedFolder
