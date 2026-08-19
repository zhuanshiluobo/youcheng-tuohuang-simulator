param(
    [string]$ExePath = "Builds\Localhost\tuohuang.exe",
    [string]$OutputPath = "Logs\Screenshots\info-panel-smoke.png",
    [string]$LogPath = "",
    [int]$Width = 1200,
    [int]$Height = 675
)

$ErrorActionPreference = "Stop"

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $scriptRoot "..\..")).Path

if (-not [System.IO.Path]::IsPathRooted($ExePath)) {
    $ExePath = Join-Path $repoRoot $ExePath
}

if (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repoRoot $OutputPath
}

Add-Type -AssemblyName System.Drawing
$typeDefinition = @(
    'using System;',
    'using System.Runtime.InteropServices;',
    '',
    'public static class YcSmokeWin32',
    '{',
    '    [StructLayout(LayoutKind.Sequential)]',
    '    public struct RECT',
    '    {',
    '        public int Left;',
    '        public int Top;',
    '        public int Right;',
    '        public int Bottom;',
    '    }',
    '',
    '    [StructLayout(LayoutKind.Sequential)]',
    '    public struct POINT',
    '    {',
    '        public int X;',
    '        public int Y;',
    '    }',
    '',
    '    [DllImport("user32.dll")]',
    '    public static extern bool SetForegroundWindow(IntPtr hWnd);',
    '',
    '    [DllImport("user32.dll")]',
    '    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);',
    '',
    '    [DllImport("user32.dll")]',
    '    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);',
    '',
    '    [DllImport("user32.dll")]',
    '    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);',
    '',
    '    [DllImport("user32.dll")]',
    '    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);',
    '',
    '    [DllImport("user32.dll")]',
    '    public static extern bool SetCursorPos(int x, int y);',
    '',
    '    [DllImport("user32.dll")]',
    '    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);',
    '}'
) -join [Environment]::NewLine

Add-Type -TypeDefinition $typeDefinition

$script:TopMostWindow = [IntPtr](-1)
$script:NoTopMostWindow = [IntPtr](-2)
$script:SwRestore = 9
$script:SwpShowWindow = 0x0040

function Get-WindowHandle {
    param([System.Diagnostics.Process]$Process)

    for ($i = 0; $i -lt 80; $i++) {
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $Process.MainWindowHandle
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Game window was not found."
}

function Click-ClientPoint {
    param(
        [IntPtr]$Handle,
        [int]$X,
        [int]$Y
    )

    $point = New-Object YcSmokeWin32+POINT
    $point.X = $X
    $point.Y = $Y
    [YcSmokeWin32]::ClientToScreen($Handle, [ref]$point) | Out-Null
    Activate-GameWindow -Handle $Handle
    Start-Sleep -Milliseconds 150
    [YcSmokeWin32]::SetCursorPos($point.X, $point.Y) | Out-Null
    [YcSmokeWin32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [YcSmokeWin32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Activate-GameWindow {
    param([IntPtr]$Handle)

    [YcSmokeWin32]::ShowWindow($Handle, $script:SwRestore) | Out-Null
    [YcSmokeWin32]::SetWindowPos($Handle, $script:TopMostWindow, 40, 40, $Width, $Height, $script:SwpShowWindow) | Out-Null
    Start-Sleep -Milliseconds 100
    [YcSmokeWin32]::SetForegroundWindow($Handle) | Out-Null
    Start-Sleep -Milliseconds 100
    [YcSmokeWin32]::SetWindowPos($Handle, $script:NoTopMostWindow, 40, 40, $Width, $Height, $script:SwpShowWindow) | Out-Null
}

function Save-ClientScreenshot {
    param(
        [IntPtr]$Handle,
        [string]$Path
    )

    $rect = New-Object YcSmokeWin32+RECT
    [YcSmokeWin32]::GetClientRect($Handle, [ref]$rect) | Out-Null

    $origin = New-Object YcSmokeWin32+POINT
    $origin.X = 0
    $origin.Y = 0
    [YcSmokeWin32]::ClientToScreen($Handle, [ref]$origin) | Out-Null

    $captureWidth = [Math]::Max(1, $rect.Right - $rect.Left)
    $captureHeight = [Math]::Max(1, $rect.Bottom - $rect.Top)
    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $bitmap = New-Object System.Drawing.Bitmap($captureWidth, $captureHeight)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($origin.X, $origin.Y, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$resolvedExe = Resolve-Path $ExePath
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$resolvedLog = if ([string]::IsNullOrWhiteSpace($LogPath)) {
    [System.IO.Path]::ChangeExtension($resolvedOutput, ".log")
}
else {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
}
$arguments = @(
    "-screen-width", $Width,
    "-screen-height", $Height,
    "-screen-fullscreen", "0",
    "--yc-dev-start-localhost",
    "-logFile", $resolvedLog
)

$process = Start-Process -FilePath $resolvedExe -WorkingDirectory (Split-Path -Parent $resolvedExe) -ArgumentList $arguments -PassThru
$handle = [IntPtr]::Zero

try {
    $handle = Get-WindowHandle -Process $process
    Activate-GameWindow -Handle $handle
    Start-Sleep -Seconds 5

    # Loading SampleScene may recreate the native Player window. Resolve the
    # handle again before sending input or capturing the client area.
    $handle = Get-WindowHandle -Process $process
    Activate-GameWindow -Handle $handle
    Click-ClientPoint -Handle $handle -X 30 -Y ([int]($Height * 0.5))
    Start-Sleep -Seconds 2

    Save-ClientScreenshot -Handle $handle -Path $resolvedOutput
    Write-Output "Screenshot: $resolvedOutput"
}
finally {
    if ($process -ne $null -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(3000)) {
            $process.Kill()
            $process.WaitForExit()
        }
    }
}
