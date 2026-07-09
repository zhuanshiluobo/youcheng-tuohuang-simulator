param(
    [string]$UnityPath = "F:\2022.3.62f2c1\Editor\Unity.exe",
    [string]$ProjectPath = "",
    [string]$LogFile = "",
    [int]$StatusIntervalSeconds = 10,
    [int]$MaxBuildSeconds = 900,
    [int]$NoLogTimeoutSeconds = 90,
    [switch]$SkipProjectLockCheck
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $ProjectPath = Split-Path -Parent (Split-Path -Parent $scriptRoot)
}

if ([string]::IsNullOrWhiteSpace($LogFile)) {
    $LogFile = Join-Path $ProjectPath "Logs\localhost-build.log"
}

$ProjectPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProjectPath)
$LogFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogFile)

if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity.exe not found: $UnityPath"
}

if (-not (Test-Path -LiteralPath $ProjectPath -PathType Container)) {
    throw "Project path not found: $ProjectPath"
}

function Stop-StartedUnityProcess {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    $Process.Refresh()
    if ($Process.HasExited) {
        return
    }

    Write-Host "Stopping Unity build process PID=$($Process.Id)..." -ForegroundColor Yellow
    Stop-Process -Id $Process.Id -Force
}

function Get-UnityProcessesUsingProject {
    param([string]$TargetProjectPath)

    try {
        $allUnityProcesses = Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Unity*" }
    }
    catch {
        try {
            $allUnityProcesses = Get-WmiObject Win32_Process | Where-Object { $_.Name -like "Unity*" }
        }
        catch {
            throw "Unable to inspect Unity process command lines while Temp\UnityLockfile exists. Close Unity for this project or rerun after the lock is gone."
        }
    }

    return @(
        $allUnityProcesses | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and
            $_.CommandLine.IndexOf($TargetProjectPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        }
    )
}

$lockFile = Join-Path $ProjectPath "Temp\UnityLockfile"
if (-not $SkipProjectLockCheck -and (Test-Path -LiteralPath $lockFile)) {
    $matchingUnityProcesses = @(Get-UnityProcessesUsingProject -TargetProjectPath $ProjectPath)
    if ($matchingUnityProcesses.Count -gt 0) {
        $summary = ($matchingUnityProcesses | ForEach-Object { "PID=$($_.ProcessId)" }) -join ", "
        throw "Unity project is already open for this project ($summary). Close that Unity instance before running the Localhost build."
    }

    throw "Temp\UnityLockfile exists, but no matching Unity process was found. This script will not delete lock files automatically; clear the stale lock manually before running the Localhost build."
}

$logDirectory = Split-Path -Parent $LogFile
if (-not [string]::IsNullOrWhiteSpace($logDirectory) -and -not (Test-Path -LiteralPath $logDirectory)) {
    New-Item -ItemType Directory -Path $logDirectory | Out-Null
}

$unityArgs = @(
    "-batchmode",
    "-projectPath", $ProjectPath,
    "-ycBuildLocalhost",
    "-logFile", $LogFile
)

Write-Host "Starting Unity build..."
Write-Host "Unity: $UnityPath"
Write-Host "Project: $ProjectPath"
Write-Host "Log: $LogFile"

$process = Start-Process -FilePath $UnityPath -WorkingDirectory $ProjectPath -ArgumentList $unityArgs -PassThru
$startedAt = Get-Date
$lastStatusAt = $startedAt.AddSeconds(-$StatusIntervalSeconds)

while (-not $process.HasExited) {
    Start-Sleep -Seconds 1
    $process.Refresh()

    $now = Get-Date
    $elapsed = [int]($now - $startedAt).TotalSeconds
    if ($MaxBuildSeconds -gt 0 -and $elapsed -ge $MaxBuildSeconds) {
        Stop-StartedUnityProcess -Process $process
        throw "Unity build timed out after ${elapsed}s. Log: $LogFile"
    }

    if ($NoLogTimeoutSeconds -gt 0 -and
        $elapsed -ge $NoLogTimeoutSeconds -and
        -not (Test-Path -LiteralPath $LogFile)) {
        Stop-StartedUnityProcess -Process $process
        throw "Unity did not create a build log within ${elapsed}s. Log: $LogFile"
    }

    if (($now - $lastStatusAt).TotalSeconds -lt $StatusIntervalSeconds) {
        continue
    }

    $lastStatusAt = $now
    Write-Host "Unity build still running... PID=$($process.Id), elapsed=${elapsed}s"

    if (Test-Path -LiteralPath $LogFile) {
        $progressLine = Select-String -LiteralPath $LogFile `
            -Pattern "DisplayProgressbar:|Build Finished, Result|Localhost simulator build succeeded|error CS|BuildFailedException" `
            -CaseSensitive:$false |
            Select-Object -Last 1
        if ($null -ne $progressLine) {
            Write-Host $progressLine.Line
        }
    }
    else {
        Write-Host "Waiting for Unity to create build log..."
    }
}

$process.Refresh()
$exitCode = $process.ExitCode
Write-Host "Unity exited with code $exitCode"

if (-not (Test-Path -LiteralPath $LogFile)) {
    throw "Unity did not create a log file: $LogFile"
}

$success = Select-String -LiteralPath $LogFile `
    -Pattern "Localhost simulator build succeeded" `
    -CaseSensitive:$false |
    Select-Object -Last 1

if ($null -ne $success) {
    Write-Host $success.Line
    exit 0
}

$failure = Select-String -LiteralPath $LogFile `
    -Pattern "BuildFailedException|error CS|Exception" `
    -CaseSensitive:$false |
    Select-Object -Last 20

if ($failure) {
    Write-Host "Unity build failed. Recent error lines:" -ForegroundColor Red
    $failure | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
}
else {
    Write-Host "Unity build ended without the success marker. Last log lines:" -ForegroundColor Red
    Get-Content -LiteralPath $LogFile -Tail 40
}

exit 1
