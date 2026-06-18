param(
    [string]$UnityPath,
    [string]$UnityVersion = "2022.3.62f2c1",
    [string]$ProjectPath,
    [string]$TestFilter,
    [string]$OutputDirectory,
    [string]$LogFile,
    [string]$ResultsFile,
    [switch]$NoGraphics,
    [string[]]$ExtraUnityArgs = @()
)

$ErrorActionPreference = "Stop"

trap {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

function Resolve-ScriptRoot {
    if ($PSScriptRoot) {
        return $PSScriptRoot
    }

    return Split-Path -Parent $MyInvocation.MyCommand.Path
}

function Add-UnityCandidate {
    param(
        [System.Collections.Generic.List[string]]$Candidates,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    if ((Split-Path -Leaf $Path) -ieq "Unity.exe") {
        $Candidates.Add($Path)
        return
    }

    $Candidates.Add((Join-Path $Path "Unity.exe"))
    $Candidates.Add((Join-Path $Path "Editor\Unity.exe"))
}

function Get-UnityEditorCandidates {
    param([string]$Version)

    $candidates = [System.Collections.Generic.List[string]]::new()

    Add-UnityCandidate $candidates ([Environment]::GetEnvironmentVariable("UNITY_2022_3_62F2C1"))

    $programRoots = @(
        ${env:ProgramFiles},
        ${env:ProgramFiles(x86)}
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($root in $programRoots) {
        $candidates.Add((Join-Path $root "Unity\Hub\Editor\$Version\Editor\Unity.exe"))
    }

    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        if ([string]::IsNullOrWhiteSpace($drive.Root)) {
            continue
        }

        $candidates.Add((Join-Path $drive.Root "$Version\Editor\Unity.exe"))
        $candidates.Add((Join-Path $drive.Root "Unity\Hub\Editor\$Version\Editor\Unity.exe"))
        $candidates.Add((Join-Path $drive.Root "Program Files\Unity\Hub\Editor\$Version\Editor\Unity.exe"))
        $candidates.Add((Join-Path $drive.Root "Program Files (x86)\Unity\Hub\Editor\$Version\Editor\Unity.exe"))
    }

    if ($IsMacOS) {
        $candidates.Add("/Applications/Unity/Hub/Editor/$Version/Unity.app/Contents/MacOS/Unity")
    }

    if ($IsLinux) {
        foreach ($root in @($env:HOME, "/opt", "/usr/local")) {
            if ([string]::IsNullOrWhiteSpace($root)) {
                continue
            }

            $candidates.Add((Join-Path $root "Unity/Hub/Editor/$Version/Editor/Unity"))
        }
    }

    foreach ($envName in @("UNITY_EDITOR_PATH", "UNITY_PATH")) {
        Add-UnityCandidate $candidates ([Environment]::GetEnvironmentVariable($envName))
    }

    return $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
}

function Resolve-UnityEditorPath {
    param(
        [string]$RequestedPath,
        [string]$Version
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $expandedPath = [Environment]::ExpandEnvironmentVariables($RequestedPath)
        if (Test-Path -LiteralPath $expandedPath -PathType Leaf) {
            return (Resolve-Path -LiteralPath $expandedPath).Path
        }

        throw "Unity Editor was not found at -UnityPath '$RequestedPath'."
    }

    foreach ($candidate in Get-UnityEditorCandidates $Version) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $defaultHubPath = "C:\Program Files\Unity\Hub\Editor\$Version\Editor\Unity.exe"
    throw "Unity Editor $Version was not found. Install it with Unity Hub, set UNITY_EDITOR_PATH, or pass -UnityPath. Preferred Hub path: $defaultHubPath"
}

function Get-PathForUnityArgument {
    param([string]$Path)

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-OutputFilePath {
    param([string]$Path)

    $expandedPath = [Environment]::ExpandEnvironmentVariables($Path)
    if (-not [System.IO.Path]::IsPathRooted($expandedPath)) {
        $expandedPath = Join-Path (Get-Location).Path $expandedPath
    }

    $directory = Split-Path -Parent $expandedPath
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    return $expandedPath
}

$scriptRoot = Resolve-ScriptRoot
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $scriptRoot "..")).Path

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = $repoRoot
}

$ProjectPath = Get-PathForUnityArgument $ProjectPath

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $ProjectPath "Logs\EditModeTests"
}

if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

if ([string]::IsNullOrWhiteSpace($LogFile)) {
    $LogFile = Join-Path $OutputDirectory "editmode-$timestamp.log"
}

if ([string]::IsNullOrWhiteSpace($ResultsFile)) {
    $ResultsFile = Join-Path $OutputDirectory "editmode-$timestamp.xml"
}

$LogFile = Resolve-OutputFilePath $LogFile
$ResultsFile = Resolve-OutputFilePath $ResultsFile

$resolvedUnityPath = Resolve-UnityEditorPath $UnityPath $UnityVersion

$unityArgs = @(
    "-batchmode",
    "-projectPath", $ProjectPath,
    "-runTests",
    "-testPlatform", "EditMode",
    "-testResults", $ResultsFile,
    "-logFile", $LogFile
)

if ($NoGraphics) {
    $unityArgs += "-nographics"
}

if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
    $unityArgs += @("-testFilter", $TestFilter)
}

if ($ExtraUnityArgs.Count -gt 0) {
    $unityArgs += $ExtraUnityArgs
}

Write-Host "Unity Editor: $resolvedUnityPath"
Write-Host "Project Path: $ProjectPath"
Write-Host "Test Platform: EditMode"
if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
    Write-Host "Test Filter: $TestFilter"
}
Write-Host "Log File: $LogFile"
Write-Host "Results File: $ResultsFile"

& $resolvedUnityPath @unityArgs
$exitCode = $LASTEXITCODE

Write-Host "Unity exited with code $exitCode"
Write-Host "Log File: $LogFile"
Write-Host "Results File: $ResultsFile"

if (-not (Test-Path -LiteralPath $ResultsFile -PathType Leaf)) {
    Write-Host "ERROR: Unity did not create a test results file. Check the log for startup failures before tests ran." -ForegroundColor Red
    exit 1
}

exit $exitCode
