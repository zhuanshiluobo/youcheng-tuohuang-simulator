param(
    [switch]$RunAuthorized,
    [switch]$PrepareManifest,
    [ValidateSet('Function','Performance')][string]$Stage = 'Function',
    [ValidateRange(5,100)][int]$WarmupIterations = 5,
    [ValidateRange(30,1000)][int]$MeasuredIterations = 30,
    [string]$FrozenManifestPath,
    [string]$OutputDirectory,
    [string]$UnityPath = 'D:\2022.3.62f1c1\Editor\Unity.exe'
)

$ErrorActionPreference = 'Stop'
$projectDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../../')).TrimEnd('\','/')
$filter = 'YC.Tests.PlayMode.ExpansionRules.HotseatOpeningUiTests'
$sourcePath = Join-Path $PSScriptRoot 'HotseatOpeningUiTests.cs'
$methods = @([regex]::Matches((Get-Content -LiteralPath $sourcePath -Raw), '\[UnityTest\]\s+public IEnumerator\s+(\w+)') |
    ForEach-Object { $_.Groups[1].Value })
if ($methods.Count -ne 14 -or @($methods | Sort-Object -Unique).Count -ne 14) { throw '源码不再恰好包含14条唯一路径，必须重新审阅过滤器。' }
$markerNames = @('YC.RuleSubmit','YC.GameplaySynchronize','YC.PlayerProjection','YC.GameplayUiRefresh',
    'YC.RoomContentValidation','YC.RoomContentHash','YC.RoomRender')

function Get-InputManifest {
    $paths = @()
    foreach ($folder in @('Assets','Packages','ProjectSettings','tools')) {
        $paths += @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory $folder) -Recurse -File |
            ForEach-Object FullName)
    }
    $paths += Join-Path $projectDirectory 'AGENTS.md'
    @($paths | Sort-Object -Unique | ForEach-Object {
        [pscustomobject]@{
            RelativePath = $_.Substring($projectDirectory.TrimEnd('\','/').Length + 1).Replace('\','/')
            Hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
        }
    })
}

function Assert-InputsEqual($expected, $actual, [string]$label) {
    $left = @($expected | ForEach-Object { $_.RelativePath + '|' + $_.Hash } | Sort-Object)
    $right = @($actual | ForEach-Object { $_.RelativePath + '|' + $_.Hash } | Sort-Object)
    $difference = @(Compare-Object $left $right)
    if ($difference.Count -gt 0) {
        $difference | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'input-differences.json') -Encoding UTF8
        throw ($label + '输入文件集合或SHA256发生变化；已记录差异，不自动回滚。')
    }
}

if (-not $PrepareManifest -and -not $RunAuthorized) {
    [pscustomobject]@{ State='仅显示准备信息，没有启动Unity'; Stage=$Stage; Project=$projectDirectory; Platform='PlayMode';
        TestFilter=$filter; ExpectedTests=$methods; Markers=$markerNames;
        Usage='总调度冻结输入后先-PrepareManifest保存摘要；明确放行后传-RunAuthorized、-FrozenManifestPath及新的-OutputDirectory。' } |
        ConvertTo-Json -Depth 4
    exit 0
}
if ($PrepareManifest -and $RunAuthorized) { throw '生成输入摘要与执行测试须分开调用。' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { throw '必须指定新的独立证据目录。' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $projectDirectory 'Logs/ExpansionValidation/GameplayIssueAcceptance'))
if (-not $OutputDirectory.StartsWith($evidenceRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw ('输出目录须位于独立证据根内：' + $evidenceRoot)
}
if (Test-Path -LiteralPath $OutputDirectory) { throw '输出目录已存在；为避免旧结果混入，请指定新目录。' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

if ($PrepareManifest) {
    [pscustomobject]@{ Utc=[DateTime]::UtcNow.ToString('O'); Project=$projectDirectory; Files=@(Get-InputManifest);
        Note='只读输入快照；不等价于总调度冻结或运行授权。包含Assets全文件、Packages、ProjectSettings、tools、AGENTS。' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'inputs.json') -Encoding UTF8
    exit 0
}

if (-not (Test-Path -LiteralPath $FrozenManifestPath -PathType Leaf)) { throw '缺少总调度确认的冻结输入摘要。' }
$manifest = Get-Content -LiteralPath $FrozenManifestPath -Raw | ConvertFrom-Json
if ($manifest.Project -ne $projectDirectory -or @($manifest.Files).Count -eq 0) { throw '冻结摘要项目不符或文件集合为空。' }
$before = @(Get-InputManifest)
Assert-InputsEqual $manifest.Files $before '运行前'
$projectComparable = $projectDirectory.Replace('/','\').TrimEnd('\')
$occupants = @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | Where-Object {
    $_.CommandLine -and $_.CommandLine.Replace('/','\').IndexOf($projectComparable, [StringComparison]::OrdinalIgnoreCase) -ge 0
})
if ($occupants.Count -gt 0) { throw ('当前项目有匹配的Unity进程：' + (($occupants | ForEach-Object ProcessId) -join ',') + '；不终止进程或删除锁。') }
$runner = Join-Path $projectDirectory 'tools/Run-EditModeTests.ps1'
$results = Join-Path $OutputDirectory 'results.xml'
$log = Join-Path $OutputDirectory 'unity.log'
$traceDirectory = Join-Path $OutputDirectory 'pointer-traces'
$previousEvidence = [Environment]::GetEnvironmentVariable('YC_HOTSEAT_OPENING_EVIDENCE', 'Process')
$previousStage = [Environment]::GetEnvironmentVariable('YC_HOTSEAT_STAGE', 'Process')
$previousWarmup = [Environment]::GetEnvironmentVariable('YC_HOTSEAT_WARMUP', 'Process')
$previousSamples = [Environment]::GetEnvironmentVariable('YC_HOTSEAT_SAMPLES', 'Process')
$iterations = if ($Stage -eq 'Performance') { 1 + $WarmupIterations + $MeasuredIterations } else { 1 }
$maxSeconds = if ($Stage -eq 'Performance') { 21600 } else { 3600 }
$shellPath = (Get-Process -Id $PID).Path
try {
    [Environment]::SetEnvironmentVariable('YC_HOTSEAT_OPENING_EVIDENCE', $traceDirectory, 'Process')
    [Environment]::SetEnvironmentVariable('YC_HOTSEAT_STAGE', $Stage, 'Process')
    [Environment]::SetEnvironmentVariable('YC_HOTSEAT_WARMUP', $WarmupIterations.ToString(), 'Process')
    [Environment]::SetEnvironmentVariable('YC_HOTSEAT_SAMPLES', $MeasuredIterations.ToString(), 'Process')
    # 使用子PowerShell隔离旧runner中的exit；不用NoGraphics、SkipProjectLockCheck或executeMethod。
    & $shellPath -NoProfile -NonInteractive -File $runner -UnityPath $UnityPath -ProjectPath $projectDirectory `
        -TestPlatform PlayMode -TestFilter $filter -OutputDirectory $OutputDirectory -ResultsFile $results `
        -LogFile $log -MaxTestSeconds $maxSeconds -NoLogTimeoutSeconds 1800
    $runnerExit = $LASTEXITCODE
}
finally {
    [Environment]::SetEnvironmentVariable('YC_HOTSEAT_OPENING_EVIDENCE', $previousEvidence, 'Process')
    [Environment]::SetEnvironmentVariable('YC_HOTSEAT_STAGE', $previousStage, 'Process')
    [Environment]::SetEnvironmentVariable('YC_HOTSEAT_WARMUP', $previousWarmup, 'Process')
    [Environment]::SetEnvironmentVariable('YC_HOTSEAT_SAMPLES', $previousSamples, 'Process')
    Assert-InputsEqual $before @(Get-InputManifest) '运行后'
    & git -C $projectDirectory status --short | Set-Content -LiteralPath (Join-Path $OutputDirectory 'git-status-after.txt') -Encoding UTF8
}
if ($runnerExit -ne 0) { throw ('Unity测试入口退出非0：' + $runnerExit) }
if (-not (Test-Path -LiteralPath $results)) { throw '没有XML，不计通过。' }
[xml]$xml = Get-Content -LiteralPath $results -Raw
$cases = @($xml.SelectNodes('//test-case'))
if ($cases.Count -ne 14 -or @($cases | Where-Object { $_.result -ne 'Passed' }).Count -gt 0) { throw 'XML必须恰好14项Passed；失败/跳过/零匹配均不可接受。' }
foreach ($method in $methods) {
    if (@($cases | Where-Object { $_.fullname -eq ($filter + '.' + $method) }).Count -ne 1) { throw ('XML方法缺失或重复：' + $method) }
}
$reports = @(Get-ChildItem -LiteralPath $traceDirectory -Recurse -File -Filter 'profiler-summary.json')
if ($reports.Count -ne 14 * $iterations) { throw ('预期证据份数不符：应为' + (14 * $iterations)) }
foreach ($method in $methods) {
    if (@($reports | Where-Object { $_.Directory.Name.StartsWith($method + '-', [StringComparison]::Ordinal) }).Count -ne $iterations) {
        throw ('Profiler证据目录缺失或重复：' + $method)
    }
}
foreach ($report in $reports) {
    $data = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
    if ($data.stage -ne $Stage) { throw '输出阶段与请求不符。' }
    if (-not $data.completed -or @($data.faults).Count -gt 0) { throw ('Profiler未完成或有采样错误：' + $report.FullName) }
    if ($Stage -eq 'Function') {
        if (-not (Test-Path -LiteralPath (Join-Path $report.DirectoryName 'private-host-observation.jsonl'))) { throw '缺少功能输入证据。' }
        continue # 功能阶段不依赖计时marker或RawFrameDataView；未测不算计时通过。
    }
    if ($data.nativeSettings.enabledBefore -ne $data.nativeSettings.enabledAfter -or
        $data.nativeSettings.binaryBefore -ne $data.nativeSettings.binaryAfter -or
        $data.nativeSettings.logFileBefore -ne $data.nativeSettings.logFileAfter) { throw 'Profiler运行设置未恢复。' }
    foreach ($marker in $markerNames) {
        $found = @($data.markers | Where-Object { $_.name -eq $marker })
        if ($found.Count -ne 1 -or -not $found[0].registered -or $found[0].invocationCount -le 0 -or $found[0].totalNanoseconds -le 0) {
            throw ('Profiler标记缺失/没有正样本：' + $marker + ' / ' + $report.FullName)
        }
    }
    foreach ($file in @('profiler-samples.jsonl','profiler-checkpoints.jsonl','private-host-observation.jsonl',
        'readiness-samples.jsonl','native-profiler-tree.jsonl','operation-metrics.jsonl')) {
        if (-not (Test-Path -LiteralPath (Join-Path $report.DirectoryName $file) -PathType Leaf)) { throw ('缺少原始证据：' + $file) }
    }
    $raw = @(Get-Content -LiteralPath (Join-Path $report.DirectoryName 'profiler-samples.jsonl') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
    if (@($raw | Where-Object { $_.Count -ne 1 -or $_.Unit -ne 'TimeNanoseconds' -or -not $_.Inclusive -or $_.Value -lt 0 }).Count -gt 0) {
        throw ('原始样本单位、逐调用计数或inclusive标记不符：' + $report.FullName)
    }
    foreach ($marker in $markerNames) {
        $metric = @($data.markers | Where-Object { $_.name -eq $marker })[0]
        $values = @($raw | Where-Object { $_.Marker -eq $marker })
        if ($values.Count -ne $metric.rawSampleRecords -or ($values | Measure-Object Count -Sum).Sum -ne $metric.invocationCount -or
            ($values | Measure-Object Value -Sum).Sum -ne $metric.totalNanoseconds) {
            throw ('摘要与原始样本不一致：' + $marker + ' / ' + $report.FullName)
        }
    }
}
[pscustomobject]@{ Stage=$Stage; FunctionalTests='14/14'; IterationsPerPath=$iterations;
    ProfilerAvailability= $(if ($Stage -eq 'Performance') {'各轮7标记正样本已核对'} else {'功能阶段未采样'});
    PerformanceAcceptance='功能与性能分阶段；性能分组判定另见performance-groups.json，UI全部本体与视觉须审阅完整边界。';
    TestFilter=$filter; Results=$results; Evidence=$traceDirectory } |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'acceptance-scope.json') -Encoding UTF8
if ($Stage -eq 'Performance') {
    $operations = @($reports | ForEach-Object { Get-Content -LiteralPath (Join-Path $_.DirectoryName 'operation-metrics.jsonl') } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
    function Get-P95($values) {
        $sorted = @($values | Where-Object { $null -ne $_ } | Sort-Object)
        if ($sorted.Count -lt 30) { return $null }
        return $sorted[[int][Math]::Ceiling($sorted.Count * .95) - 1]
    }
    $groups = @($operations | Group-Object { $_.Scenario + '|' + $_.RequestType + '|' + $_.Operation } | ForEach-Object {
        $warm = @($_.Group | Where-Object { $_.Valid -and $_.SampleClass -eq '预热' })
        $hot = @($_.Group | Where-Object { $_.Valid -and $_.SampleClass -eq '热候选' })
        $cold = @($_.Group | Where-Object { $_.SampleClass.StartsWith('冷态') })
        $operation = $_.Group[0].Operation
        $nonSubmit = $operation.Contains('非提交')
        $coldOnly = $operation.Contains('场景加载')
        $roomOperation = [string]::IsNullOrEmpty($_.Group[0].RequestType)
        $p95 = Get-P95 @($hot | ForEach-Object ElapsedMs)
        $ruleP95 = Get-P95 @($hot | Where-Object { $_.ruleCalls -eq 1 } | ForEach-Object ruleInclusiveMs)
        $roomP95 = Get-P95 @($hot | ForEach-Object roomValidationAndHashMarkedUnionMs)
        $limit = if ($roomOperation) { 200 } else { 300 }
        $status = if ($nonSubmit -or $coldOnly) { '单列，不套提交热门槛' }
            elseif ($warm.Count -lt 5 -or $hot.Count -lt 30 -or $null -eq $p95) { '同类预热或有效热样本不足' }
            elseif ($p95 -gt $limit -or ($null -ne $ruleP95 -and $ruleP95 -gt 200) -or ($null -ne $roomP95 -and $roomP95 -gt 20)) { '已测指标超过固定门槛' }
            else { '已测指标在门槛内；未测分段不算通过' }
        [pscustomobject]@{ Group=$_.Name; WarmupCount=$warm.Count; HotCount=$hot.Count; ColdCount=$cold.Count;
            EndToEndP95Ms=$p95; EndToEndLimitMs=$limit; RuleInclusiveP95Ms=$ruleP95; RuleLimitMs=200;
            RoomMarkedUnionP95Ms=$roomP95; RoomLimitMs=20; UiBodyLimitMs=100;
            UiBodyVerdict='已标记块及同父子树扣除数据另存；完整UI本体门槛未自动关闭'; Status=$status }
    })
    $groups | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'performance-groups.json') -Encoding UTF8
    if (@($groups | Where-Object { $_.Status -in @('同类预热或有效热样本不足','已测指标超过固定门槛') }).Count -gt 0) {
        throw '性能存在样本不足或超过固定门槛；原始数据已保留，不临时放宽。'
    }
}
Write-Output ('阶段' + $Stage + '的14条功能链与对应证据核对完成；完整性能、视觉及九项关闭仍按记录范围验收。')
