param(
    [string]$UnityPath,
    [string]$UnityVersion = "2022.3.62f2c1",
    [string]$ProjectPath,
    [switch]$NoGraphics,
    [switch]$SkipProjectLockCheck
)

$ErrorActionPreference = "Stop"

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$runner = Join-Path $scriptRoot "Run-EditModeTests.ps1"

$params = @{
    UnityVersion = $UnityVersion
    TestFilter = "YC.Tests.EditMode.FontUtilityTests"
}

if (-not [string]::IsNullOrWhiteSpace($UnityPath)) {
    $params.UnityPath = $UnityPath
}

if (-not [string]::IsNullOrWhiteSpace($ProjectPath)) {
    $params.ProjectPath = $ProjectPath
}

if ($NoGraphics) {
    $params.NoGraphics = $true
}

if ($SkipProjectLockCheck) {
    $params.SkipProjectLockCheck = $true
}

& $runner @params