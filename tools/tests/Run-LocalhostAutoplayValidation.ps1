param(
    [string]$UnityPath,
    [string]$UnityVersion = "2022.3.62f2c1",
    [string]$ProjectPath,
    [ValidateSet(3, 4)]
    [int]$PlayerCount = 4,
    [string]$OutputDirectory,
    [switch]$NoGraphics,
    [switch]$SkipProjectLockCheck
)

$ErrorActionPreference = "Stop"

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$runner = Join-Path $scriptRoot "Run-EditModeTests.ps1"
$resolvedProjectPath = if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    [IO.Path]::GetFullPath((Join-Path $scriptRoot "../.."))
} else {
    (Resolve-Path -LiteralPath $ProjectPath).Path
}

$testFilter = "YC.Tests.EditMode.LocalhostAutoplayRunnerTests"
if ($PlayerCount -eq 3) {
    # 三人必须命中独立人数断言，不允许把默认四人测试报告当成三人证据。
    $testName = "RunToRound8Settlement_FillsThreeSeatsAndOutputsSnapshot"
    $testSource = Join-Path $resolvedProjectPath "Assets/YC/Tests/EditMode/LocalhostAutoplayRunnerTests.cs"
    if (-not (Test-Path -LiteralPath $testSource -PathType Leaf) -or
        -not ([IO.File]::ReadAllText($testSource) -match ("\b" + $testName + "\s*\("))) {
        throw "缺少三人独立跑局测试 $testName。请集成人员在 $testSource 增加调用 RunToRound8Settlement(3) 的三人断言后重试；尚未启动 Unity。"
    }
    $testFilter += ".$testName"
} else {
    $testFilter += ".RunToRound8Settlement_FillsFourSeatsAndOutputsSnapshot"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $resolvedProjectPath "Logs/Autoplay/$($PlayerCount)p"
}

$params = @{
    UnityVersion = $UnityVersion
    ProjectPath = $resolvedProjectPath
    OutputDirectory = $OutputDirectory
    TestFilter = $testFilter
}

if (-not [string]::IsNullOrWhiteSpace($UnityPath)) {
    $params.UnityPath = $UnityPath
}
if ($NoGraphics) {
    $params.NoGraphics = $true
}
if ($SkipProjectLockCheck) {
    $params.SkipProjectLockCheck = $true
}

Write-Host "执行 $PlayerCount 人进程内权威跑局，地图 map-$(@{3='three';4='four'}[$PlayerCount])-players。"
Write-Host "此入口含验收夹具，不代表正常入口热座、本地三实例或 Steam 验收。"
& $runner @params
exit $LASTEXITCODE
