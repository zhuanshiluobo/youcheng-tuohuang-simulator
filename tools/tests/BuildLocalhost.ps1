param(
    [string]$UnityPath = "F:\2022.3.62f2c1\Editor\Unity.exe",
    [string]$ProjectPath = "",
    [string]$LogFile = "",
    [int]$StatusIntervalSeconds = 10,
    [int]$MaxBuildSeconds = 900,
    [int]$NoLogTimeoutSeconds = 90,
    [switch]$Development,
    [switch]$Release,
    [switch]$SkipProjectLockCheck
)

$ErrorActionPreference = "Stop"

if ($Development -and $Release) {
    throw "-Development and -Release cannot be used together."
}

function Normalize-ProcessPathEnvironment {
    $processPath = [Environment]::GetEnvironmentVariable(
        "Path",
        [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($processPath)) {
        return
    }

    # Codex/CI 可能同时提供 Path 与 PATH；PowerShell 5 构造大小写不敏感
    # 的进程环境字典时会把它们判为重复键。
    [Environment]::SetEnvironmentVariable("PATH", $null, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("Path", $null, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("Path", $processPath, [EnvironmentVariableTarget]::Process)
}

function Test-CurrentRunLog {
    param(
        [string]$Path,
        [DateTime]$NotBeforeUtc
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    return (Get-Item -LiteralPath $Path).LastWriteTimeUtc -ge $NotBeforeUtc
}

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $ProjectPath = Split-Path -Parent (Split-Path -Parent $scriptRoot)
}

if ([string]::IsNullOrWhiteSpace($LogFile)) {
    $defaultLogName = if ($Release) {
        "release-build.log"
    }
    elseif ($Development) {
        "localhost-development-build.log"
    }
    else {
        "localhost-build.log"
    }
    $LogFile = Join-Path $ProjectPath ("Logs\" + $defaultLogName)
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

$batchBuildArgument = if ($Release) {
    "-ycBuildRelease"
}
elseif ($Development) {
    "-ycBuildLocalhostDevelopment"
}
else {
    "-ycBuildLocalhost"
}
$successPattern = if ($Release) {
    "Release build succeeded"
}
elseif ($Development) {
    "Localhost development simulator build succeeded"
}
else {
    "Localhost simulator build succeeded"
}

$unityArgs = @(
    "-batchmode",
    "-projectPath", $ProjectPath,
    $batchBuildArgument,
    "-logFile", $LogFile
)

Write-Host "Starting Unity build..."
Write-Host "Unity: $UnityPath"
Write-Host "Project: $ProjectPath"
Write-Host "Log: $LogFile"

Normalize-ProcessPathEnvironment
$launchStartedAtUtc = [DateTime]::UtcNow.AddSeconds(-1)
$process = Start-Process -FilePath $UnityPath -WorkingDirectory $ProjectPath -ArgumentList $unityArgs -PassThru -WindowStyle Hidden
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
        -not (Test-CurrentRunLog -Path $LogFile -NotBeforeUtc $launchStartedAtUtc)) {
        Stop-StartedUnityProcess -Process $process
        throw "Unity did not create a build log within ${elapsed}s. Log: $LogFile"
    }

    if (($now - $lastStatusAt).TotalSeconds -lt $StatusIntervalSeconds) {
        continue
    }

    $lastStatusAt = $now
    Write-Host "Unity build still running... PID=$($process.Id), elapsed=${elapsed}s"

    if (Test-CurrentRunLog -Path $LogFile -NotBeforeUtc $launchStartedAtUtc) {
        $progressLine = Select-String -LiteralPath $LogFile `
            -Pattern @(
                "DisplayProgressbar:",
                "Build Finished, Result",
                $successPattern,
                "error CS",
                "BuildFailedException") `
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

if (-not (Test-CurrentRunLog -Path $LogFile -NotBeforeUtc $launchStartedAtUtc)) {
    throw "Unity did not create a log file: $LogFile"
}

$success = Select-String -LiteralPath $LogFile `
    -Pattern $successPattern `
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
