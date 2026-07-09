param(
    [string]$UnityPath = "F:\2022.3.62f2c1\Editor\Unity.exe",
    [string]$ProjectPath = "",
    [string]$ExePath = "",
    [string]$OutputPath = "",
    [ValidateSet("Build", "Declare", "None")]
    [string]$Dialog = "Build",
    [int]$Width = 1200,
    [int]$Height = 675,
    [int]$WaitSeconds = 6,
    [string]$BuildLogFile = "",
    [int]$BuildTimeoutSeconds = 900,
    [int]$BuildNoLogTimeoutSeconds = 90,
    [switch]$SkipProjectLockCheck,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Split-Path -Parent (Split-Path -Parent $scriptRoot)
}

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $ProjectPath "Builds\Localhost\tuohuang.exe"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $fileName = "right-card-smoke-$($Dialog.ToLowerInvariant()).png"
    $OutputPath = Join-Path $ProjectPath (Join-Path "Logs\Screenshots" $fileName)
}

$resolvedProject = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProjectPath)
$resolvedExe = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

if (-not $SkipBuild) {
    $builder = Join-Path $scriptRoot "BuildLocalhost.ps1"
    if ([string]::IsNullOrWhiteSpace($BuildLogFile)) {
        $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
        $BuildLogFile = Join-Path $resolvedProject "Logs\right-card-smoke-build-$timestamp.log"
    }

    Write-Host "Building Localhost player before capture..."
    & $builder `
        -UnityPath $UnityPath `
        -ProjectPath $resolvedProject `
        -LogFile $BuildLogFile `
        -MaxBuildSeconds $BuildTimeoutSeconds `
        -NoLogTimeoutSeconds $BuildNoLogTimeoutSeconds `
        -SkipProjectLockCheck:$SkipProjectLockCheck
    if ($LASTEXITCODE -ne 0) {
        throw "Localhost build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $resolvedExe -PathType Leaf)) {
    throw "Localhost executable not found: $resolvedExe"
}

$smokeDialogArg = switch ($Dialog) {
    "Declare" { "--yc-dev-right-card-smoke-declare" }
    "None" { "--yc-dev-right-card-smoke-no-dialog" }
    default { "--yc-dev-right-card-smoke-build" }
}

$captureArgs = @(
    "-screen-width", "$Width",
    "-screen-height", "$Height",
    "-screen-fullscreen", "0",
    "--yc-dev-start-localhost",
    "--yc-dev-right-card-smoke",
    $smokeDialogArg
)

$capturer = Join-Path $scriptRoot "CaptureLocalhostWindow.ps1"
Write-Host "Capturing right card smoke screenshot..."
& $capturer `
    -ExePath $resolvedExe `
    -OutputPath $resolvedOutput `
    -ExtraArgs $captureArgs `
    -NoClick `
    -WaitAfterClickSeconds $WaitSeconds

Write-Output "Screenshot: $resolvedOutput"
