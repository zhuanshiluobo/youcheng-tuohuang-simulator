param(
    [Parameter(Mandatory=$true)][string]$RoutePlanPath,
    [Parameter(Mandatory=$true)][string]$FrozenManifestPath,
    [ValidateSet('internal','external')][string]$InputMode = 'internal',
    [string]$ExecutablePath = 'G:\NoMadCity\Builds\LocalhostDevelopment\tuohuang.exe',
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(0,14400)][int]$TimeoutSeconds = 0,
    [switch]$ObserveExternalWindows
)
$ErrorActionPreference = 'Stop'
function Assert-AllowedPath([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path)) { throw "必须使用绝对路径：$Path" }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -eq 'G:\NoMadCity\游城拓荒\UI素材' -or $full.StartsWith('G:\NoMadCity\游城拓荒\UI素材\', [StringComparison]::OrdinalIgnoreCase)) {
        throw '冻结输入不得访问受限UI原档案。'
    }
    if ($full.Contains('"') -or $full.Contains("`n") -or $full.Contains("`r")) { throw '路径包含不支持的命令行字符。' }
    $checkedPath = $full
    while ($checkedPath -and $checkedPath -ne 'G:\') {
        if ((Test-Path -LiteralPath $checkedPath) -and ((Get-Item -LiteralPath $checkedPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '路径含链接或重解析点，不能间接访问受限档案。' }
        $checkedPath = Split-Path -Parent $checkedPath
    }
    return $full
}
function Read-Json([string]$Path) { Get-Content -LiteralPath (Assert-AllowedPath $Path) -Raw -Encoding utf8 | ConvertFrom-Json -Depth 100 }
function Assert-FrozenFile([string]$Path) {
    $full = Assert-AllowedPath $Path
    if ($wholeGameHealth) {
        if (-not $full.StartsWith($snapshotRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '整局运行输入必须全部位于本次Player快照。' }
        return $full
    }
    $roots = @('G:\NoMadCity\Assets\YC\','G:\NoMadCity\Assets\StreamingAssets\Content\core\','G:\NoMadCity\Builds\',
        'G:\NoMadCity\ProjectSettings\','G:\NoMadCity\Packages\','G:\NoMadCity\tools\validation\')
    $scenes=@('StartScene','LoadingScene','SampleScene','CollectionRoom','ThreePlayerScene')|ForEach-Object { 'G:\NoMadCity\Assets\Scenes\'+$_+'.unity' }
    $explicitScene=$full -in $scenes -or $full -in @($scenes|ForEach-Object {$_+'.meta'})
    $explicitDependency=$full.StartsWith('G:\NoMadCity\Assets\',[StringComparison]::OrdinalIgnoreCase) -and $full -in @($manifest.AssetDependencies)
    $explicitBuildAuxiliary=$full -eq 'G:\NoMadCity\steam_appid.txt'
    if (@($roots | Where-Object { $full.StartsWith($_,[StringComparison]::OrdinalIgnoreCase) }).Count -eq 0 -and
        -not $explicitScene -and -not $explicitDependency -and -not $explicitBuildAuxiliary -and $full -notin @($manifest.RoutePlanPaths)) { throw "冻结文件超出明确运行输入范围：$full" }
    $current = $full
    while ($current -and $current -ne 'G:\') {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "冻结路径含链接或重解析点：$current" }
        }
        $current = Split-Path -Parent $current
    }
    return $full
}
$RoutePlanPath = Assert-AllowedPath $RoutePlanPath
$FrozenManifestPath = Assert-AllowedPath $FrozenManifestPath
$OutputDirectory = Assert-AllowedPath $OutputDirectory
if (-not $OutputDirectory.StartsWith('G:\NoMadCity\Logs\ExpansionValidation\ProductionGameRoutes\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '本启动器仅写入Logs/ExpansionValidation/ProductionGameRoutes下的独立运行目录。'
}
if (Test-Path -LiteralPath $OutputDirectory) { throw '输出目录已存在；每次运行必须使用全新目录。' }
$plan = Read-Json $RoutePlanPath
$manifest = Read-Json $FrozenManifestPath
$wholeGameHealth = $plan.EvaluationMode -eq 'whole-game-health'
$snapshotRoot = $null
if ($wholeGameHealth) {
    $snapshotRoot = Assert-AllowedPath $manifest.SnapshotRoot
    if ($snapshotRoot -notmatch '^G:\\NoMadCity\\Builds\\ValidationSnapshots\\[A-Za-z0-9][A-Za-z0-9_-]{7,80}$' -or
        $manifest.SchemaVersion -ne 3 -or -not $manifest.ReadbackIndependent -or -not $manifest.BuildReceiptPath -or
        $manifest.BuildReceiptSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw '整局模式缺独立Player快照及构建点回执。' }
    $receipt = Assert-FrozenFile $manifest.BuildReceiptPath
    if ((Get-FileHash -LiteralPath $receipt).Hash -ne $manifest.BuildReceiptSha256) { throw '构建点回执已改变。' }
    foreach ($tool in 'Run-ProductionGameRoutes.ps1','Test-ProductionGameRouteEvidence.ps1','HostArchiveEvidenceReader.cs','Update-ProductionGameCoveragePriorities.ps1') {
        $toolPath = Join-Path $snapshotRoot ('Tools\' + $tool)
        if (@($manifest.Files | Where-Object Path -EQ $toolPath).Count -ne 1) { throw "快照缺实际工具：$tool" }
    }
    if ($PSCommandPath -ne (Join-Path $snapshotRoot 'Tools\Run-ProductionGameRoutes.ps1')) { throw '整局必须从快照工具启动。' }
    if (@($manifest.RoutePlanPaths | Where-Object { $_ -eq $RoutePlanPath }).Count -ne 1) { throw '本局路线须唯一登记为快照运行输入。' }
}
if ($plan.SchemaVersion -ne 1 -or -not $plan.Frozen -or -not $plan.Executable -or $plan.PlayerCount -notin 2,3,4 -or $plan.MapId -notin 'map-three-players','map-four-players' -or
    ($plan.PlayerCount -eq 4 -and $plan.MapId -ne 'map-four-players') -or [string]::IsNullOrWhiteSpace($plan.ContentPackHash)) { throw '路线配置无效。' }
if (($wholeGameHealth -and $manifest.SchemaVersion -ne 3) -or (-not $wholeGameHealth -and ($manifest.SchemaVersion -ne 2 -or -not $manifest.PaymentClosureReady)) -or -not $manifest.Frozen -or @($manifest.Files).Count -eq 0 -or
    @($manifest.ActiveCatalogs|Where-Object { $_.ContentPackHash -eq $plan.ContentPackHash -and
        (@($_.EnabledExpansionIds|Sort-Object) -join ',') -eq (@($plan.EnabledExpansionIds|Sort-Object) -join ',') }).Count -ne 1) { throw '缺正式付款收口冻结清单或活动内容哈希不一致。' }
foreach ($path in $manifest.RoutePlanPaths) {
    if ($wholeGameHealth) {
        $resolvedPlan = Assert-FrozenFile $path
        if (-not $resolvedPlan.StartsWith((Join-Path $snapshotRoot 'InputPlans') + '\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($resolvedPlan) -ne '.json') { throw '整局路线须为快照InputPlans内的明确JSON。' }
        continue
    }
    $allowedPlans=@(
        foreach ($allowedPlanDirectory in @('G:\NoMadCity\Logs\ExpansionValidation\ProductionGameRoutes\Preparation\PaymentClosure\','G:\NoMadCity\Logs\ExpansionValidation\ProductionGameRoutes\Preparation\CoverageClosure\')) {
            foreach ($allowedPlanName in @('core-internal','core-external','enterprise-internal','enterprise-external','infected-internal','infected-external')) {
                $allowedPlanDirectory+$allowedPlanName+'.json'
            }
        }
    )
    if ($path -notin $allowedPlans) { throw '冻结路线只允许本轮六个明确的可执行计划，报告/日志不纳入输入冻结。' }
}
if ($wholeGameHealth -and ($InputMode -ne 'internal' -or -not $plan.AllowAdaptiveMainActions -or $plan.MaximumCommands -ne 0 -or $TimeoutSeconds -ne 0)) { throw '正式整局须使用内部合法自适应路线，时间与命令上限均为0（关闭）。' }
if (-not $wholeGameHealth -and $InputMode -eq 'internal' -and (@($plan.Steps).Count -eq 0 -or @($plan.Coverage).Count -eq 0)) { throw '内部路线必须有有限行动步骤和业务结果覆盖合同。' }
$verified = foreach ($entry in $manifest.Files) {
    $file = Assert-FrozenFile $entry.Path
    if ($entry.Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "冻结文件已改变：$file" }
    [pscustomobject]@{ Path=$file; Sha256=$entry.Sha256 }
}
foreach ($scene in $(if ($wholeGameHealth) { @() } else { @('G:\NoMadCity\Assets\Scenes\SampleScene.unity','G:\NoMadCity\Assets\Scenes\ThreePlayerScene.unity') })) {
    if (@($verified|Where-Object Path -EQ $scene).Count -ne 1) { throw "冻结清单缺正式场景及场景覆盖：$scene" }
}
if (@($verified|Where-Object Path -EQ $RoutePlanPath).Count -ne 1) { throw '当前可执行路线必须唯一纳入统一冻结输入。' }
$projectUnity=@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'"|Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf('G:\NoMadCity',[StringComparison]::OrdinalIgnoreCase) -ge 0 })
if ($projectUnity.Count -gt 0) { throw '当前项目仍有Unity进程占用；保留进程，不启动对局。' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$planCopy = Join-Path $OutputDirectory 'route-plan.json'
Copy-Item -LiteralPath $RoutePlanPath -Destination $planCopy
Copy-Item -LiteralPath $FrozenManifestPath -Destination (Join-Path $OutputDirectory 'frozen-manifest.json')
[pscustomobject]@{ SchemaVersion=1; InputMode=$InputMode; NetworkProtocol='deterministic-simulation'; RouteName=$plan.RouteName; PlayerCount=$plan.PlayerCount; MapId=$plan.MapId;
    ContentPackHash=$plan.ContentPackHash; EnabledExpansionIds=$plan.EnabledExpansionIds; StartedUtc=[DateTime]::UtcNow;
    RoutePlanSha256=(Get-FileHash -LiteralPath $planCopy -Algorithm SHA256).Hash;
    FrozenManifestSha256=(Get-FileHash -LiteralPath $FrozenManifestPath -Algorithm SHA256).Hash;
    LimitsVersion='whole-game-unbounded-v1'; EffectiveTimeoutSeconds=$TimeoutSeconds; MaximumCommands=$plan.MaximumCommands;
    NoProgressHardStopSeconds=$(if($wholeGameHealth){0}else{180}); NoProgressWarningSeconds=180; Files=@($verified) } |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'run-input.json') -Encoding utf8
if ($InputMode -eq 'external' -and -not $ObserveExternalWindows) {
    [pscustomobject]@{ Status='未开始'; Reason='外部模式需显式启动观察窗口后，由真实原生工具逐步操作；本启动器不执行点击'; InputMode='external'; WindowsStarted=0; AutomaticActions=0; Success=$false } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'input-unavailable.json') -Encoding utf8
    Write-Host '外部路线未开始：需要显式观察启动及真实原生输入证据。已保留固定输入记录。'
    exit 2
}
$ExecutablePath = Assert-AllowedPath $ExecutablePath
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf) -or @($verified | Where-Object { $_.Path -eq $ExecutablePath }).Count -ne 1) {
    throw '可执行文件必须存在并且在冻结清单中唯一登记。'
}
$managed = Join-Path (Split-Path -Parent $ExecutablePath) (([IO.Path]::GetFileNameWithoutExtension($ExecutablePath)) + '_Data\Managed')
foreach ($dll in Get-ChildItem -LiteralPath $managed -Filter '*.dll' -File) {
    if (@($verified | Where-Object { $_.Path -eq $dll.FullName }).Count -ne 1) { throw "冻结清单缺少Managed程序集：$($dll.FullName)" }
}
if ($wholeGameHealth) {
    if (-not $ExecutablePath.StartsWith((Join-Path $snapshotRoot 'Player') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Player必须来自快照Player目录。' }
    foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $snapshotRoot 'Player') -Directory -Recurse -Force) {
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Player快照目录包含重解析点。' }
    }
    foreach ($runtimeFile in Get-ChildItem -LiteralPath (Join-Path $snapshotRoot 'Player') -File -Recurse) {
        $runtimePath = Assert-FrozenFile $runtimeFile.FullName
        if (@($verified | Where-Object Path -EQ $runtimePath).Count -ne 1) { throw "Player文件未唯一固定：$runtimePath" }
    }
}
$roomFile = Join-Path $OutputDirectory 'room.txt'
$players = [Collections.Generic.List[object]]::new()
function Write-RouteLaunchFailure([string]$Reason, [string]$ExceptionText) {
    $observations = [Collections.Generic.List[object]]::new()
    $failedSummary = foreach ($entry in $players) {
        $hasExited = $null
        $exitCode = $null
        $processId = $null
        $processObservationError = $null
        try {
            $processId = $entry.Process.Id
            $entry.Process.Refresh()
            $hasExited = $entry.Process.HasExited
            if ($hasExited) { $exitCode = $entry.Process.ExitCode }
        } catch { $processObservationError = $_.Exception.ToString() }
        $path = Join-Path $entry.Output 'result.json'
        $result = $null
        $resultReadError = $null
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            try { $result = Read-Json $path } catch { $resultReadError = $_.Exception.ToString() }
        }
        $observations.Add([pscustomobject]@{PlayerId=$entry.PlayerId; ProcessId=$processId; HasExited=$hasExited;
            ExitCode=$exitCode; ProcessObservationError=$processObservationError; ResultPath=$path;
            ResultExists=(Test-Path -LiteralPath $path -PathType Leaf); ResultReadError=$resultReadError})
        [pscustomobject]@{PlayerId=$entry.PlayerId; ExitCode=$exitCode; Result=$result}
    }
    ConvertTo-Json -InputObject @($failedSummary) -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
    [pscustomobject]@{SchemaVersion=1; Success=$false; Reason=$Reason; ExceptionText=$ExceptionText;
        GameplayRunPassed=$false;RunOutcome=$(if ($Reason -like '*超时*') {'incomplete'} else {'driver_failure'});EffectChainHealthy=$null;
        RecordedUtc=[DateTime]::UtcNow; DeadlineUtc=$deadline; Processes=@($observations);
        ForcedTermination=$false; SuccessAuditRun=$false} |
        ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'launcher-failure.json') -Encoding utf8
}
function Start-RoutePlayer([int]$PlayerId, [string]$Address) {
    $playerOutput = Join-Path $OutputDirectory ('player-' + $PlayerId)
    New-Item -ItemType Directory -Path $playerOutput | Out-Null
    $arguments = @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720', '-logFile', ('"' + (Join-Path $playerOutput 'player.log') + '"'),
        '--yc-local-multiplayer-test', ('--yc-local-player-name=验收玩家' + $PlayerId), ('"--yc-network-validation-output=' + $playerOutput + '"'),
        ('--yc-network-validation-timeout=' + $TimeoutSeconds), ('--yc-production-input-mode=' + $InputMode), ('"--yc-production-route-plan=' + $planCopy + '"'))
    if ($InputMode -eq 'internal') {
        if ($PlayerId -eq 1) { $arguments += ('--yc-dev-local-mirror-host=' + $plan.PlayerCount), ('"--yc-dev-local-mirror-room-file=' + $roomFile + '"') }
        else { $arguments += ('--yc-dev-local-mirror-join=' + $Address) }
        $process = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path -Parent $ExecutablePath) -ArgumentList $arguments -WindowStyle Hidden -PassThru
    } else {
        # 显式观察选项才显示独立窗口；不传自动建房、加入、准备、开始或断连参数。
        $process = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path -Parent $ExecutablePath) -ArgumentList $arguments -PassThru
    }
    $players.Add([pscustomobject]@{PlayerId=$PlayerId; Process=$process; Output=$playerOutput; Arguments=$arguments})
    $players | ForEach-Object { [pscustomobject]@{PlayerId=$_.PlayerId; ProcessId=$_.Process.Id; Output=$_.Output; Arguments=$_.Arguments} } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'processes.json') -Encoding utf8
}
Start-RoutePlayer 1 ''
if ($InputMode -eq 'external') {
    foreach ($id in 2..$plan.PlayerCount) { Start-RoutePlayer $id '' }
    [pscustomobject]@{ Status='仅观察'; NativeInputVerified=$false; AutomaticActions=0; Success=$false } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'observation-only.json') -Encoding utf8
    Write-Host '独立窗口已启动供观察；真实原生输入尚未验证，仅启动不能计入外部验收。'
    exit 2
}
$deadline = if ($TimeoutSeconds -gt 0) { [DateTime]::UtcNow.AddSeconds($TimeoutSeconds) } else { $null }
try {
    while ($true) {
        if ($players[0].Process.HasExited -or ($null -ne $deadline -and [DateTime]::UtcNow -gt $deadline)) { throw '房主未建立正式房间；保留当前日志与进程。' }
        if (Test-Path -LiteralPath $roomFile -PathType Leaf) {
            $address = ([string](Get-Content -LiteralPath $roomFile -Raw)).Trim()
            if ($address) { break }
        }
        Start-Sleep -Milliseconds 500
    }
    if ($address -notmatch ':(\d+)$') { throw '正式房间地址格式无效。' }
    $joinAddress = '127.0.0.1:' + $Matches[1]
    foreach ($id in 2..$plan.PlayerCount) { Start-RoutePlayer $id $joinAddress }
    while (@($players | Where-Object { -not $_.Process.HasExited }).Count -gt 0) {
        if ($null -ne $deadline -and [DateTime]::UtcNow -gt $deadline) { throw '路线超时；保留本次进程和原始证据，未执行强制关闭。' }
        Start-Sleep -Seconds 1
    }
} catch {
    $launchError = $_
    try { Write-RouteLaunchFailure $launchError.Exception.Message $launchError.Exception.ToString() }
    catch { Write-Warning ('失败收证未完整落盘：' + $_.Exception.ToString()) }
    throw $launchError
}
$summary = foreach ($entry in $players) {
    $path = Join-Path $entry.Output 'result.json'
    [pscustomobject]@{PlayerId=$entry.PlayerId; ExitCode=$entry.Process.ExitCode; Result=$(if (Test-Path -LiteralPath $path) { Read-Json $path } else { $null })}
}
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
try { & (Join-Path $PSScriptRoot 'Test-ProductionGameRouteEvidence.ps1') -OutputDirectory $OutputDirectory }
catch {
    Write-RouteLaunchFailure '整局报告工具未完成' $_.Exception.ToString()
    throw
}
exit $LASTEXITCODE
