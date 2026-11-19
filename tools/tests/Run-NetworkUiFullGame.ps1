param([Parameter(Mandatory=$true)][string]$EvidenceRoot, [string]$BuildDirectory='build', [string]$ExecutablePath='',
    [switch]$NetworkObservation, [string]$CampaignRequestedPriorities='',
    [string]$RuleMode='standard', [string]$FrozenPackageManifest='', [switch]$FixtureIsolation,
    [string]$CampaignExpansions='enterprise,infected', [int[]]$AllowedIsolatedLocalProcessIds=@(),
    [string[]]$FixtureDiyReleaseManifests=@(), [string]$FixtureDiyTool='', [string]$FixtureHostContentRoot='',
    [string]$FixtureResumeMetadata='', [ValidateSet(0,2,3)][int]$NetworkStopAfterRound=0,
    [switch]$CrossNavigationAudit,
    [ValidateSet('','client2','client3','client4')][string]$ManualReconnectRole='')
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
if ($RuleMode.Length -gt 192 -or $RuleMode -cnotmatch '^[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)*$') { throw '规则模式 ID 无效。' }
if ($ManualReconnectRole -and !$FixtureIsolation) { throw '主动重连测试必须隔离内容和存档。' }
if ($CrossNavigationAudit -and !$FixtureIsolation) { throw '互查页面验收必须隔离内容和存档。' }
if ($RuleMode -cnotin @('standard','entrepreneur') -and !$FixtureIsolation) { throw 'DIY 模式验收必须使用隔离内容目录。' }
$resume = $null
if ($FixtureResumeMetadata) {
    if (!$FixtureIsolation) { throw '续局夹具必须使用隔离内容和存档。' }
    $metadataPath = (Resolve-Path -LiteralPath $FixtureResumeMetadata).Path
    if (!$metadataPath.StartsWith($evidence+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '续局元数据必须位于本次独立证据目录。' }
    $resume = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $resumeSave = (Resolve-Path -LiteralPath $resume.saveCopy).Path
    $resumePolicy = (Resolve-Path -LiteralPath $resume.policy).Path
    if (!$resumeSave.StartsWith($evidence+'\',[StringComparison]::OrdinalIgnoreCase) -or
        !$resumePolicy.StartsWith($evidence+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $resume.gameId -cnotmatch '^[a-f0-9]{32}$' -or $resume.contentHash -cnotmatch '^[a-f0-9]{64}$' -or
        $resume.ruleMode -cne $RuleMode -or $resume.saveSha256 -cnotmatch '^[a-f0-9]{64}$' -or
        (Get-FileHash -LiteralPath $resumeSave -Algorithm SHA256).Hash -ne $resume.saveSha256) {
        throw '续局原档路径、身份、模式或哈希不符。'
    }
    $policy = Get-Content -LiteralPath $resumePolicy -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($policy.GameId -cne $resume.gameId -or $policy.ContentHash -cne $resume.contentHash -or @($policy.Players).Count -ne 4) {
        throw '续局行动策略不属于该原档。'
    }
    foreach ($role in @('host','client2','client3','client4')) {
        $identity = $resume.roles.$role
        if (!$identity -or $identity.operatorId -cnotmatch '^[a-f0-9]{32}$' -or [string]::IsNullOrWhiteSpace($identity.name)) {
            throw "续局角色身份无效：$role"
        }
    }
}
$existing = @(Get-Process -Name tuohuang -ErrorAction SilentlyContinue)
$isolatedExisting = @()
foreach ($process in $existing) {
    if (!$FixtureIsolation -or $AllowedIsolatedLocalProcessIds -notcontains $process.Id) {
        throw '已有未明确列出的游戏实例，保留其现场；不得启动联机夹具。'
    }
    $identity = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)"
    $commandLine = $identity.CommandLine
    $saveMatch = [regex]::Match($commandLine, '--yc-performance-save-root=([^"\s]+)')
    $outputMatch = [regex]::Match($commandLine, '--yc-hotseat-ui-validation-output=([^"\s]+)')
    $testRoot = (Join-Path $project 'TestResults') + '\'
    if (!$identity.ExecutablePath -or !$identity.ExecutablePath.StartsWith((Join-Path $project 'Builds')+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $commandLine -notmatch '--yc-hotseat-ui-validation-case=full-game(?:\s|$)' -or
        $commandLine -match '--yc-local-multiplayer-test|--yc-network-ui-role=' -or !$saveMatch.Success -or !$outputMatch.Success) {
        throw "已有实例 $($process.Id) 不是可核验的本地隔离整局夹具。"
    }
    $savePath = [IO.Path]::GetFullPath($saveMatch.Groups[1].Value)
    $outputPath = [IO.Path]::GetFullPath($outputMatch.Groups[1].Value)
    if (!$savePath.StartsWith($testRoot,[StringComparison]::OrdinalIgnoreCase) -or
        !$outputPath.StartsWith($testRoot,[StringComparison]::OrdinalIgnoreCase) -or
        $savePath.StartsWith($evidence+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $outputPath.StartsWith($evidence+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw "已有实例 $($process.Id) 的存档或证据路径不在独立测试目录。"
    }
    $isolatedExisting += @{ ProcessId=$process.Id; ExecutablePath=$identity.ExecutablePath; SaveRoot=$savePath; Output=$outputPath; LocalOnly=$true }
}
foreach ($id in $CampaignExpansions.Split(',')) {
    if (!$id) { continue }
    if ($id.Length -gt 192 -or $id -cnotmatch '^[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)*$' -or
        ($id -cnotin @('enterprise','infected') -and !$FixtureIsolation)) { throw "联机夹具扩展 ID 无效或缺少隔离目录：$id" }
}
if ($FixtureDiyReleaseManifests.Count -gt 0 -and (!$FixtureIsolation -or !$FixtureDiyTool)) {
    throw '安装测试包需要隔离目录和测试版 DIY 工具。'
}
if ($FixtureHostContentRoot) {
    if (!$FixtureIsolation -or !$FixtureDiyReleaseManifests.Count -or ![IO.Path]::IsPathRooted($FixtureHostContentRoot)) {
        throw '复用首次安装夹具需要隔离模式、包清单和绝对内容目录。'
    }
    $FixtureHostContentRoot = (Resolve-Path -LiteralPath $FixtureHostContentRoot).Path
    $testResultsRoot = (Join-Path $project 'TestResults') + '\'
    if (!$FixtureHostContentRoot.StartsWith($testResultsRoot,[StringComparison]::OrdinalIgnoreCase)) { throw '安装内容根必须位于项目 TestResults。' }
    $cursor = $FixtureHostContentRoot
    while ($cursor.StartsWith($testResultsRoot,[StringComparison]::OrdinalIgnoreCase)) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '安装内容根不能包含重解析点。' }
        $cursor = Split-Path -Parent $cursor
    }
}
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
$fixtureSaves = $null
if ($FixtureIsolation) {
    $current = $evidence
    while ($current -and $current.StartsWith($project,[StringComparison]::OrdinalIgnoreCase)) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '隔离测试目录不能包含重解析点。' }
        if ($current -eq $project) { break }
        $current = Split-Path -Parent $current
    }
    $fixtureSaves = Join-Path $evidence '联机隔离存档'
    if (Test-Path -LiteralPath $fixtureSaves) { throw '隔离存档目录已存在，禁止复用。' }
    New-Item -ItemType Directory -Path $fixtureSaves | Out-Null
} else {
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
}
$cohort = Join-Path $evidence '四端正常联机'
New-Item -ItemType Directory -Path $cohort | Out-Null
$isolatedExisting | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $cohort '并存本地夹具身份.json') -Encoding UTF8
$diyFixtures = @()
if ($FixtureDiyReleaseManifests.Count -gt 0) {
    $toolPath = (Resolve-Path -LiteralPath $FixtureDiyTool).Path
    if ([IO.Path]::GetExtension($toolPath) -cne '.dll') { throw '隔离安装仅接受测试版 .NET 工具程序集。' }
    foreach ($manifestName in $FixtureDiyReleaseManifests) {
        $manifestPath = (Resolve-Path -LiteralPath $manifestName).Path
        $release = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($release.packId -cnotmatch '^[a-z0-9]+(?:[._-][a-z0-9]+)*$' -or
            $release.version -cnotmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$' -or
            $release.zipFile -cne "$($release.packId)-$($release.version).zip" -or
            $release.zipSha256 -cnotmatch '^[0-9a-f]{64}$' -or $release.contentHash -cnotmatch '^[0-9a-f]{64}$') {
            throw "隔离包发布清单身份无效：$manifestPath"
        }
        $archive = (Resolve-Path -LiteralPath (Join-Path (Split-Path -Parent $manifestPath) $release.zipFile)).Path
        $diyFixtures += @{ Manifest=$manifestPath; Archive=$archive; Data=$release }
    }
}
foreach ($role in @('host','client2','client3','client4')) {
    $output = Join-Path $cohort $role
    New-Item -ItemType Directory -Path $output | Out-Null
    if ($FixtureIsolation) {
        $roleSave = Join-Path $fixtureSaves $role
        New-Item -ItemType Directory -Path $roleSave | Out-Null
        if ($resume -and $role -eq 'host') {
            $copiedSave = Join-Path $roleSave 'auto-1.json'
            Copy-Item -LiteralPath $resumeSave -Destination $copiedSave
            if ((Get-FileHash -LiteralPath $copiedSave -Algorithm SHA256).Hash -ne $resume.saveSha256) { throw '续局存档副本哈希不符。' }
        }
        New-Item -ItemType Directory -Path (Join-Path $output '隔离临时目录') | Out-Null
        $reuseInstalled = $role -eq 'host' -and $FixtureHostContentRoot
        $roleContent = if ($reuseInstalled) { $FixtureHostContentRoot } else { Join-Path $output '隔离用户内容' }
        if (!$reuseInstalled) { New-Item -ItemType Directory -Path $roleContent | Out-Null }
        foreach ($fixture in $diyFixtures) {
            $entry = $fixture.Data
            if ($reuseInstalled) {
                $installed = Join-Path (Join-Path (Join-Path $roleContent 'Mods') $entry.packId) $entry.version
                $checkRoot = Join-Path $output ("安装内容身份核验-" + $entry.packId)
                $installOutput = & dotnet $toolPath publish --game (Split-Path -Parent $executable) --source $installed --output $checkRoot 2>&1
            } else {
                $installOutput = & dotnet $toolPath install --game (Split-Path -Parent $executable) --product 'NoMadCity' `
                    --zip $fixture.Archive --sha256 $entry.zipSha256 --pack-id $entry.packId --version $entry.version `
                    --content-hash $entry.contentHash --fixture-data-root $roleContent --keep-stage true 2>&1
            }
            $code = $LASTEXITCODE
            $installOutput | Out-String | Set-Content -LiteralPath (Join-Path $output ("DIY夹具安装-" + $entry.packId + '.txt')) -Encoding UTF8
            if ($code -ne 0) { throw "隔离内容包安装失败：$role / $($entry.packId)，退出码 $code" }
            if ($reuseInstalled) {
                $actual = Get-Content -LiteralPath (Join-Path $checkRoot ($entry.packId + '-' + $entry.version + '.release.json')) -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($actual.contentHash -cne $entry.contentHash -or $actual.zipSha256 -cne $entry.zipSha256) {
                    throw "首次安装的内容身份与发布清单不符：$($entry.packId)"
                }
            }
        }
    }
}
$players = @()
$launches = @()
foreach ($role in @('host','client2','client3','client4')) {
    $output = Join-Path $cohort $role
    $playerName = if ($resume) { $resume.roles.$role.name } else { "本地验收-$role-$(Get-Date -Format 'MMddHHmmss')" }
    $playerArguments = @(
        '--yc-local-multiplayer-test', "--yc-local-player-name=$playerName",
        '--yc-hotseat-ui-validation-case=network-full-game', "--yc-hotseat-ui-validation-output=$output",
        "--yc-ui-rule-mode=$RuleMode", '--yc-hotseat-player-count=4', '--yc-ui-defer-content-differences=true',
        "--yc-network-ui-root=$cohort", "--yc-network-ui-role=$role", "--yc-ui-campaign-path=$output\coverage.json",
        "--yc-campaign-expansions=$CampaignExpansions", '--yc-amiya-original-viewer=true',
        '-logFile', "$output\Player.log", '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720'
    )
    if ($FixtureIsolation) {
        $roleSave = Join-Path $fixtureSaves $role
        $roleContent = if ($role -eq 'host' -and $FixtureHostContentRoot) { $FixtureHostContentRoot } else { Join-Path $output '隔离用户内容' }
        $operatorId = if ($resume) { $resume.roles.$role.operatorId } else { [guid]::NewGuid().ToString('N') }
        $playerArguments += @("--yc-performance-save-root=$roleSave", "--yc-test-content-root=$roleContent", "--yc-test-operator-id=$operatorId")
        if ($resume) { $playerArguments += @("--yc-network-resume-game-id=$($resume.gameId)", "--yc-network-resume-play-policy=$resumePolicy") }
    }
    if ($NetworkObservation) {
        $playerArguments += @('--yc-network-observation', "--yc-network-observation-output=$(Join-Path $output '网络分段观测.jsonl')")
    }
    if ($NetworkStopAfterRound -gt 0) { $playerArguments += "--yc-network-stop-after-round=$NetworkStopAfterRound" }
    if ($CrossNavigationAudit) { $playerArguments += '--yc-cross-navigation-audit=true' }
    if ($ManualReconnectRole) { $playerArguments += "--yc-network-manual-reconnect-role=$ManualReconnectRole" }
    if (![string]::IsNullOrWhiteSpace($CampaignRequestedPriorities)) {
        $playerArguments += "--yc-campaign-requested-priorities=$CampaignRequestedPriorities"
    }
    $line = ($playerArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
    $startInfo.WorkingDirectory = Split-Path -Parent $executable
    $startInfo.Arguments = $line
    $startInfo.UseShellExecute = $false
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
    if ($FixtureIsolation) {
        $roleTemp = Join-Path $output '隔离临时目录'
        $startInfo.EnvironmentVariables['TEMP'] = $roleTemp
        $startInfo.EnvironmentVariables['TMP'] = $roleTemp
    }
    $player = [Diagnostics.Process]::Start($startInfo)
    $players += $player
    $identity = Get-CimInstance Win32_Process -Filter "ProcessId=$($player.Id)"
    $launches += @{ Role=$role; ProcessId=$player.Id; StartedUtc=[DateTime]::UtcNow.ToString('o');
        CreationDate=$identity.CreationDate; ExecutablePath=$identity.ExecutablePath; CommandLine=$identity.CommandLine;
        Arguments=$playerArguments; VisibleWindow=$true; RequestedRuleMode=$RuleMode; NormalMenuFlow=$true;
        FixtureTemporaryRoot=$(if ($FixtureIsolation) { $roleTemp } else { $null }) }
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
