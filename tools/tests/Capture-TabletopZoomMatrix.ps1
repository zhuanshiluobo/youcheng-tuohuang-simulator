param(
    [string]$ExePath = "",
    [string]$OutputDirectory = "",
    [int]$StartupWaitSeconds = 4,
    [int]$ZoomWaitMilliseconds = 900
)

$ErrorActionPreference = "Stop"

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$projectRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $projectRoot "Builds\LocalhostDevelopment\tuohuang.exe"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot "Artifacts\SecondaryLayoutAcceptance\ZoomMatrix"
}

$resolvedExe = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $resolvedExe -PathType Leaf)) {
    throw "Development Player does not exist: $resolvedExe"
}
if (-not (Test-Path -LiteralPath $resolvedOutput)) {
    New-Item -ItemType Directory -Path $resolvedOutput | Out-Null
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class TabletopZoomMatrixNative
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
'@

[TabletopZoomMatrixNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

function Find-PlayerWindow {
    param([int]$ProcessId)

    $result = $null
    $callback = [TabletopZoomMatrixNative+EnumWindowsProc] {
        param([IntPtr]$Window, [IntPtr]$State)
        $windowProcessId = 0
        [TabletopZoomMatrixNative]::GetWindowThreadProcessId($Window, [ref]$windowProcessId) | Out-Null
        if ($windowProcessId -eq $ProcessId -and [TabletopZoomMatrixNative]::IsWindowVisible($Window)) {
            $client = New-Object TabletopZoomMatrixNative+RECT
            if ([TabletopZoomMatrixNative]::GetClientRect($Window, [ref]$client) -and
                ($client.Right - $client.Left) -gt 500 -and ($client.Bottom - $client.Top) -gt 400) {
                $script:matchedWindow = $Window
                return $false
            }
        }
        return $true
    }
    $script:matchedWindow = [IntPtr]::Zero
    [TabletopZoomMatrixNative]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    if ($script:matchedWindow -ne [IntPtr]::Zero) { $result = $script:matchedWindow }
    return $result
}

function Send-ZoomWheel {
    param([IntPtr]$Window, [int]$Notches)

    if ($Notches -eq 0) { return }
    [TabletopZoomMatrixNative]::SetForegroundWindow($Window) | Out-Null
    $client = New-Object TabletopZoomMatrixNative+RECT
    [TabletopZoomMatrixNative]::GetClientRect($Window, [ref]$client) | Out-Null
    $point = New-Object TabletopZoomMatrixNative+POINT
    $point.X = [int](($client.Right - $client.Left) * 0.5)
    $point.Y = [int](($client.Bottom - $client.Top) * 0.56)
    [TabletopZoomMatrixNative]::ClientToScreen($Window, [ref]$point) | Out-Null
    [TabletopZoomMatrixNative]::SetCursorPos($point.X, $point.Y) | Out-Null
    Start-Sleep -Milliseconds 180
    $direction = if ($Notches -gt 0) { 1 } else { -1 }
    for ($i = 0; $i -lt [Math]::Abs($Notches); $i++) {
        $delta = 120 * $direction
        [TabletopZoomMatrixNative]::mouse_event(0x0800, 0, 0, $delta, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 120
    }
    Start-Sleep -Milliseconds $ZoomWaitMilliseconds
}

function Save-ClientImage {
    param([IntPtr]$Window, [string]$Path, [int]$ExpectedWidth, [int]$ExpectedHeight)

    $windowRect = New-Object TabletopZoomMatrixNative+RECT
    $clientRect = New-Object TabletopZoomMatrixNative+RECT
    if (-not [TabletopZoomMatrixNative]::GetWindowRect($Window, [ref]$windowRect) -or
        -not [TabletopZoomMatrixNative]::GetClientRect($Window, [ref]$clientRect)) {
        throw "Unable to read Player window bounds."
    }
    $windowWidth = $windowRect.Right - $windowRect.Left
    $windowHeight = $windowRect.Bottom - $windowRect.Top
    $clientWidth = $clientRect.Right - $clientRect.Left
    $clientHeight = $clientRect.Bottom - $clientRect.Top
    if ($clientWidth -ne $ExpectedWidth -or $clientHeight -ne $ExpectedHeight) {
        throw "Unexpected Player client size: expected ${ExpectedWidth}x${ExpectedHeight}, actual ${clientWidth}x${clientHeight}."
    }

    $origin = New-Object TabletopZoomMatrixNative+POINT
    $origin.X = 0
    $origin.Y = 0
    [TabletopZoomMatrixNative]::ClientToScreen($Window, [ref]$origin) | Out-Null
    $offsetX = $origin.X - $windowRect.Left
    $offsetY = $origin.Y - $windowRect.Top

    $fullImage = New-Object System.Drawing.Bitmap $windowWidth, $windowHeight
    $graphics = [System.Drawing.Graphics]::FromImage($fullImage)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [TabletopZoomMatrixNative]::PrintWindow($Window, $hdc, 2)) {
                throw "PrintWindow failed."
            }
        }
        finally {
            $graphics.ReleaseHdc($hdc)
        }

        $cropArea = New-Object System.Drawing.Rectangle $offsetX, $offsetY, $clientWidth, $clientHeight
        $clientImage = $fullImage.Clone($cropArea, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $clientImage.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $clientImage.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $fullImage.Dispose()
    }
}

$resolutions = @(
    @{ Width = 1920; Height = 1080 },
    @{ Width = 1600; Height = 900 },
    @{ Width = 1280; Height = 720 }
)
$zoomLevels = @(
    @{ Percent = 90; Notches = -1 },
    @{ Percent = 100; Notches = 0 },
    @{ Percent = 200; Notches = 10 }
)

foreach ($resolution in $resolutions) {
    foreach ($zoomLevel in $zoomLevels) {
        $width = $resolution.Width
        $height = $resolution.Height
        $percent = $zoomLevel.Percent
        $zoomValue = ($percent / 100.0).ToString("0.0", [Globalization.CultureInfo]::InvariantCulture)
        $baseName = "tabletop_${width}x${height}_zoom${percent}"
        $imagePath = Join-Path $resolvedOutput ($baseName + ".png")
        $logPath = Join-Path $resolvedOutput ($baseName + ".log")
        $arguments = @(
            "-screen-width", "$width",
            "-screen-height", "$height",
            "-screen-fullscreen", "0",
            "--yc-dev-start-localhost",
            "--yc-dev-right-card-smoke",
            "--yc-dev-right-card-smoke-no-dialog",
            "--yc-dev-map-zoom=$zoomValue",
            "-logFile", $logPath
        )

        $process = Start-Process -FilePath $resolvedExe -ArgumentList $arguments -PassThru
        try {
            $window = $null
            for ($attempt = 0; $attempt -lt 80; $attempt++) {
                Start-Sleep -Milliseconds 250
                $window = Find-PlayerWindow -ProcessId $process.Id
                if ($null -ne $window) { break }
            }
            if ($null -eq $window) { throw "No Player window found: PID=$($process.Id)" }

            Start-Sleep -Seconds $StartupWaitSeconds
            Save-ClientImage -Window $window -Path $imagePath -ExpectedWidth $width -ExpectedHeight $height
            Write-Output "CAPTURED=$imagePath"
        }
        finally {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id }
        }
    }
}
