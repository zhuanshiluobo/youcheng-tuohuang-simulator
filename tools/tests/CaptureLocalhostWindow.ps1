param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [int]$ClickX = 755,
    [int]$ClickY = 480,
    [int]$WaitAfterClickSeconds = 8,
    [string[]]$ExtraArgs = @(),
    [switch]$NoClick
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class CaptureLocalhostWindowNative
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
}
'@

# Per-monitor-v2 DPI awareness keeps window rectangles and PrintWindow bitmaps in
# the same physical-pixel coordinate space on displays using 125%/150% scaling.
[CaptureLocalhostWindowNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

function Find-WindowForProcess {
    param([int]$TargetProcessId)

    $matches = New-Object System.Collections.Generic.List[object]
    $callback = [CaptureLocalhostWindowNative+EnumWindowsProc] {
        param([IntPtr]$Hwnd, [IntPtr]$LParam)

        $windowProcessId = 0
        [CaptureLocalhostWindowNative]::GetWindowThreadProcessId($Hwnd, [ref]$windowProcessId) | Out-Null
        if ($windowProcessId -eq $TargetProcessId -and [CaptureLocalhostWindowNative]::IsWindowVisible($Hwnd)) {
            $rect = New-Object CaptureLocalhostWindowNative+RECT
            if ([CaptureLocalhostWindowNative]::GetWindowRect($Hwnd, [ref]$rect)) {
                $width = $rect.Right - $rect.Left
                $height = $rect.Bottom - $rect.Top
                if ($width -gt 300 -and $height -gt 200) {
                    $titleBuilder = New-Object System.Text.StringBuilder 256
                    [CaptureLocalhostWindowNative]::GetWindowText($Hwnd, $titleBuilder, $titleBuilder.Capacity) | Out-Null
                    $matches.Add([pscustomobject]@{
                        Handle = $Hwnd
                        Width = $width
                        Height = $height
                        Title = $titleBuilder.ToString()
                    })
                }
            }
        }

        return $true
    }

    [CaptureLocalhostWindowNative]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    return $matches | Sort-Object Width, Height -Descending | Select-Object -First 1
}

function Invoke-ClientClick {
    param(
        [IntPtr]$Hwnd,
        [int]$X,
        [int]$Y
    )

    $lParamValue = ($Y -shl 16) -bor ($X -band 0xffff)
    [CaptureLocalhostWindowNative]::PostMessage($Hwnd, 0x0201, [IntPtr]1, [IntPtr]$lParamValue) | Out-Null
    Start-Sleep -Milliseconds 80
    [CaptureLocalhostWindowNative]::PostMessage($Hwnd, 0x0202, [IntPtr]0, [IntPtr]$lParamValue) | Out-Null
}

function Save-WindowImage {
    param(
        [IntPtr]$Hwnd,
        [string]$Path
    )

    $rect = New-Object CaptureLocalhostWindowNative+RECT
    if (-not [CaptureLocalhostWindowNative]::GetWindowRect($Hwnd, [ref]$rect)) {
        throw "GetWindowRect failed."
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 300 -or $height -le 200) {
        throw "Unexpected window size: $width x $height."
    }

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [CaptureLocalhostWindowNative]::PrintWindow($Hwnd, $hdc, 2)) {
                throw "PrintWindow failed."
            }
        }
        finally {
            $graphics.ReleaseHdc($hdc)
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$process = Start-Process -FilePath $ExePath -ArgumentList $ExtraArgs -PassThru
try {
    $window = $null
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 250
        $window = Find-WindowForProcess -TargetProcessId $process.Id
        if ($null -ne $window) {
            break
        }
    }

    if ($null -eq $window) {
        throw "No visible window found for PID $($process.Id)."
    }

    $hwnd = [IntPtr]$window.Handle
    [CaptureLocalhostWindowNative]::SetWindowPos($hwnd, [IntPtr]::Zero, 40, 40, 1186, 687, 0x0040) | Out-Null
    Start-Sleep -Seconds 2
    if (-not $NoClick) {
        Invoke-ClientClick -Hwnd $hwnd -X $ClickX -Y $ClickY
    }

    Start-Sleep -Seconds $WaitAfterClickSeconds
    Save-WindowImage -Hwnd $hwnd -Path $OutputPath
    Write-Output "PID=$($process.Id) HWND=$hwnd TITLE=$($window.Title) SIZE=$($window.Width)x$($window.Height)"
    Write-Output $OutputPath
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id
    }
}
