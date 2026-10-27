param([Parameter(Mandatory=$true)][string]$EvidenceRoot, [string]$BuildDirectory='build', [string]$ExecutablePath='',
    [switch]$NetworkObservation, [string]$CampaignRequestedPriorities='',
    [ValidateSet('standard','entrepreneur')][string]$RuleMode='standard', [string]$FrozenPackageManifest='')
$ErrorActionPreference = 'Stop'
$processPathValue = [Environment]::GetEnvironmentVariable('Path', [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable('PATH', $null, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable('Path', $null, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable('Path', $processPathValue, [EnvironmentVariableTarget]::Process)
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$evidence = (Resolve-Path -LiteralPath $EvidenceRoot).Path
if (!$evidence.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase) -or
    $evidence.StartsWith((Join-Path $project '游城拓荒\UI素材')+'\',[StringComparison]::OrdinalIgnoreCase) -or
    $evidence -eq (Join-Path $project '游城拓荒\UI素材')) { throw '运行证据必须是本项目独立子目录，禁止写入受限原档案。' }
$RuleMode = $RuleMode.ToLowerInvariant()
if (Get-Process -Name tuohuang -ErrorAction SilentlyContinue) { throw '已有游戏实例，保留其现场；先按交接准确核对并正常关闭本次旧测试进程。' }
foreach ($name in @('四端正常联机','启动前存档备份','本轮冻结运行清单原件.json')) {
    if (Test-Path -LiteralPath (Join-Path $evidence $name)) { throw "运行证据已存在，禁止覆盖：$name" }
}
$fixedPlayer = ![string]::IsNullOrWhiteSpace($ExecutablePath)
$executable = if ($fixedPlayer) { (Resolve-Path -LiteralPath $ExecutablePath).Path } else { Join-Path $project 'Builds\LocalhostDevelopment\tuohuang.exe' }
$buildEvidence = Join-Path $evidence $BuildDirectory
if ([string]::IsNullOrWhiteSpace($FrozenPackageManifest)) {
$receiptPath = Join-Path $buildEvidence 'build-receipt.json'
$receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$receipt.succeeded) { throw '构建输入存在漂移，不能运行。' }
foreach ($item in $receipt.files) {
    $artifactPath = if ($fixedPlayer) {
        $prefix = 'Builds/LocalhostDevelopment/'
        if (!$item.path.StartsWith($prefix)) { throw "产物清单路径不属于构建目录：$($item.path)" }
        Join-Path (Split-Path -Parent $executable) $item.path.Substring($prefix.Length)
    } else { Join-Path $project $item.path }
    $actual = Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256
    if ($actual.Hash -ne $item.sha256) { throw "构建产物已变化：$($item.path)" }
}
$inputs = Get-Content -LiteralPath (Join-Path $buildEvidence 'build-input.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$fixedPlayer) { foreach ($item in $inputs.files) {
    $actual = Get-FileHash -LiteralPath (Join-Path $project $item.path) -Algorithm SHA256
    if ($actual.Hash -ne $item.sha256) { throw "构建后源文件已变化：$($item.path)" }
} }
} else {
    if (!$fixedPlayer) { throw '冻结运行清单必须配合准确ExecutablePath，不猜构建目录。' }
    $frozen = Get-Content -LiteralPath $FrozenPackageManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $packageRoot = [IO.Path]::GetFullPath((Split-Path -Parent $executable))
    if ([IO.Path]::GetFullPath($frozen.root) -ne $packageRoot -or @($frozen.files).Count -eq 0 -or
        $frozen.fileCount -ne @($frozen.files).Count) { throw '冻结清单根目录或数量不符。' }
    foreach ($item in $frozen.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $packageRoot $item.path))
        if (!$path.StartsWith($packageRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.sha256) { throw "冻结产物路径或SHA不符：$path" }
    }
    Copy-Item -LiteralPath $FrozenPackageManifest -Destination (Join-Path $evidence '本轮冻结运行清单原件.json')
}
$saveRoot = Join-Path $env:USERPROFILE 'AppData\LocalLow\zhuanshiluobo\游城拓荒模拟器\MatchSaves'
$backup = Join-Path $evidence '启动前存档备份'
New-Item -ItemType Directory -Path $backup | Out-Null
$saveInventory = @()
foreach ($kind in @('manual','auto')) {
    foreach ($slot in 1..3) {
        $path = Join-Path $saveRoot "$kind-$slot.json"
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Copy-Item -LiteralPath $path -Destination $backup
            $saveInventory += @{ Path=$path; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        }
    }
}
$saveInventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backup '六槽原件清单.json') -Encoding UTF8
$cohort = Join-Path $evidence '四端正常联机'
New-Item -ItemType Directory -Path $cohort | Out-Null
$players = @()
$launches = @()
foreach ($role in @('host','client2','client3','client4')) {
    $output = Join-Path $cohort $role
    New-Item -ItemType Directory -Path $output | Out-Null
    $playerArguments = @(
        '--yc-local-multiplayer-test', "--yc-local-player-name=本地验收-$role-$(Get-Date -Format 'MMddHHmmss')",
        '--yc-hotseat-ui-validation-case=network-full-game', "--yc-hotseat-ui-validation-output=$output",
        "--yc-ui-rule-mode=$RuleMode", '--yc-hotseat-player-count=4', '--yc-ui-defer-content-differences=true',
        "--yc-network-ui-root=$cohort", "--yc-network-ui-role=$role", "--yc-ui-campaign-path=$output\coverage.json",
        '--yc-campaign-expansions=enterprise,infected', '--yc-amiya-original-viewer=true',
        '-logFile', "$output\Player.log", '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720'
    )
    if ($NetworkObservation) {
        $playerArguments += @('--yc-network-observation', "--yc-network-observation-output=$(Join-Path $output '网络分段观测.jsonl')")
    }
    if (![string]::IsNullOrWhiteSpace($CampaignRequestedPriorities)) {
        $playerArguments += "--yc-campaign-requested-priorities=$CampaignRequestedPriorities"
    }
    $line = ($playerArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $player = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -ArgumentList $line -WindowStyle Normal -PassThru
    $players += $player
    $identity = Get-CimInstance Win32_Process -Filter "ProcessId=$($player.Id)"
    $launches += @{ Role=$role; ProcessId=$player.Id; StartedUtc=[DateTime]::UtcNow.ToString('o');
        CreationDate=$identity.CreationDate; ExecutablePath=$identity.ExecutablePath; CommandLine=$identity.CommandLine;
        Arguments=$playerArguments; VisibleWindow=$true; RequestedRuleMode=$RuleMode; NormalMenuFlow=$true }
    $launches | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $cohort '四实例启动记录.json') -Encoding UTF8
    Write-Host "已启动可见独立程序 $role，PID=$($player.Id)"
}
$exits = @{}
while ($exits.Count -lt 4) {
    for ($index=0; $index -lt $players.Count; $index++) {
        $player = $players[$index]; $role = $launches[$index].Role
        if ($exits.ContainsKey($role)) { continue }
        $player.Refresh()
        if (!$player.HasExited) { continue }
        $forcedReceipt = Join-Path $cohort '异常实例终止收尾记录.json'
        $naturalExit = $true
        if (Test-Path -LiteralPath $forcedReceipt) {
            $termination = Get-Content -LiteralPath $forcedReceipt -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($termination.ProcessId -eq $player.Id) { $naturalExit = $false }
        }
        $exits[$role] = @{ ProcessId=$player.Id; ExitCode=$player.ExitCode; NaturalExit=$naturalExit; EndedUtc=[DateTime]::UtcNow.ToString('o') }
        $report = Join-Path $cohort "$role\验证结果.json"
        if (!(Test-Path -LiteralPath $report)) {
            $failurePath = Join-Path $cohort '首个实例失败.json'
            if (!(Test-Path -LiteralPath $failurePath)) {
                @{ Role=$role; Error='实例退出但没有验证报告'; ExitCode=$player.ExitCode } | ConvertTo-Json | Set-Content -LiteralPath $failurePath -Encoding UTF8
            }
        }
        $exits | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $cohort '实际退出记录.json') -Encoding UTF8
        Write-Host "$role 已退出，ExitCode=$($player.ExitCode)，NaturalExit=$naturalExit"
    }
    Start-Sleep -Seconds 2
}
Write-Host "四端原件：$cohort"
