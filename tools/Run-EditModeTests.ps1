param(
    [string]$UnityPath,
    [string]$UnityVersion = "2022.3.62f2c1",
    [string]$ProjectPath,
    [string]$TestFilter,
    [ValidateSet("EditMode", "PlayMode")]
    [string]$TestPlatform = "EditMode",
    [string]$OutputDirectory,
    [string]$LogFile,
    [string]$ResultsFile,
    [int]$ResultsWaitTimeoutSeconds = 120,
    [int]$ResultsStableMilliseconds = 1000,
    [int]$StatusIntervalSeconds = 10,
    [int]$MaxTestSeconds = 900,
    [int]$NoLogTimeoutSeconds = 90,
    [switch]$NoGraphics,
    [switch]$SkipProjectLockCheck,
    [string[]]$ExtraUnityArgs = @()
)

$ErrorActionPreference = "Stop"

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$runner = Join-Path $scriptRoot "tests\Run-EditModeTests.ps1"

if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "EditMode test runner not found: $runner"
}

& $runner @PSBoundParameters
exit $LASTEXITCODE
