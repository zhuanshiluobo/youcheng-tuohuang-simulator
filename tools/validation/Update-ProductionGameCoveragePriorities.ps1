param(
    [Parameter(Mandatory=$true)][string[]]$ReportPaths,
    [Parameter(Mandatory=$true)][string]$RoutePlanPath,
    [Parameter(Mandatory=$true)][string]$CatalogPath,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$NextPlanPath
)
$ErrorActionPreference='Stop'
function Resolve-CoveragePath([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path)) { throw '覆盖输入输出须用绝对路径。' }
    $full=[IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith('G:\NoMadCity\Logs\ExpansionValidation\ProductionGameRoutes\',[StringComparison]::OrdinalIgnoreCase) -and
        -not $full.StartsWith('G:\NoMadCity\Builds\ValidationSnapshots\',[StringComparison]::OrdinalIgnoreCase) -and
        -not $full.StartsWith('G:\NoMadCity\Logs\ExpansionValidation\HotseatWholeGames\',[StringComparison]::OrdinalIgnoreCase)) { throw '覆盖工具仅处理明确路线、热座证据或固定Player快照。' }
    $current=$full
    while ($current -and $current -ne 'G:\') {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '覆盖路径包含重解析点。' }
        $current=Split-Path -Parent $current
    }
    return $full
}
function Read-CoverageJson([string]$Path) { Get-Content -LiteralPath (Resolve-CoveragePath $Path) -Raw -Encoding utf8|ConvertFrom-Json -Depth 100 }
$plan=Read-CoverageJson $RoutePlanPath; $catalog=Read-CoverageJson $CatalogPath
if ($plan.EvaluationMode -ne 'whole-game-health' -or $catalog.ContentPackHash -ne $plan.ContentPackHash -or
    (@($catalog.EnabledExpansionIds|Sort-Object) -join ',') -ne (@($plan.EnabledExpansionIds|Sort-Object) -join ',')) { throw '覆盖累计必须使用同模式及同内容版本的正式目录。' }
$seen=@{}; $observed=[Collections.Generic.List[object]]::new();$runRows=[Collections.Generic.List[object]]::new();$seenRuns=@{}
foreach ($path in $ReportPaths) {
    $full=Resolve-CoveragePath $path; $report=Read-CoverageJson $full
    if ($report.EvaluationMode -ne 'whole-game-health' -or $report.ContentPackHash -ne $plan.ContentPackHash -or
        (@($report.EnabledExpansionIds|Sort-Object) -join ',') -ne (@($plan.EnabledExpansionIds|Sort-Object) -join ',')) { throw '不能跨模式、跨内容哈希累计覆盖。' }
    if ($report.GameId -and $seenRuns.ContainsKey($report.GameId)) { throw '同一正式对局不能重复累计。' }
    if ($report.GameId) {$seenRuns[$report.GameId]=$true}
    $before=$seen.Count
    foreach ($item in $report.ObservedCoverage) {
        if (-not $item.Category -or -not $item.DefinitionId -or -not $item.EvidenceId) { throw '实际覆盖缺类别、定义或执行证据身份。' }
        if ($item.Category -eq 'character_mode' -and $item.Mode -notin 'strategy','tactic') { throw '策略与计谋必须分别记录实际完成模式。' }
        $key=$item.Category+'|'+$item.DefinitionId+'|'+$item.Mode
        if (-not $seen.ContainsKey($key)) { $seen[$key]=$true;$observed.Add($item) }
    }
    $runRows.Add([ordered]@{ReportPath=$full;ReportSha256=(Get-FileHash -LiteralPath $full).Hash;GameId=$report.GameId;RunOutcome=$report.RunOutcome;NewCoverage=$seen.Count-$before})
}
$expected=[Collections.Generic.List[object]]::new();$expectedKeys=@{}
function Add-Expected([string]$Category,[string]$Id,[string]$Mode='') {
    $key=$Category+'|'+$Id+'|'+$Mode
    if ($Id -and -not $expectedKeys.ContainsKey($key)) {$expectedKeys[$key]=$true;$expected.Add([ordered]@{Category=$Category;DefinitionId=$Id;Mode=$Mode})}
}
foreach ($definition in $catalog.definitions) {
    switch ($definition.ContentType) {
        'enterprise' {Add-Expected 'enterprise_selected' $definition.DefinitionId}
        'character' {
            Add-Expected 'dispatch_character' $definition.DefinitionId
            foreach ($mode in @($definition.Abilities.Mode|Where-Object {$_ -in 'strategy','tactic'}|Select-Object -Unique)) {Add-Expected 'character_mode' $definition.DefinitionId $mode}
        }
        'facility' {if (-not $definition.Data.reserveOnly) {Add-Expected 'facility_built' $definition.DefinitionId}}
        'city_style' {Add-Expected 'city_style_declared' $definition.DefinitionId}
        'event' {Add-Expected 'event_resolved' $definition.DefinitionId
            for ($option=0;$option -lt @($definition.Data.choiceDescriptions).Count;$option++) {Add-Expected 'event_option' $definition.DefinitionId ([string]$option)}
        }
        'department' {Add-Expected 'definition_effect' $definition.DefinitionId}
    }
}
foreach ($action in $catalog.nativeDefinitions.SpecialActions) {Add-Expected 'special_action_used' $action.SpecialActionId}
$missing=@($expected|Where-Object { -not $seen.ContainsKey($_.Category+'|'+$_.DefinitionId+'|'+$_.Mode) })
function Missing-Ids([string]$Category) {return @($missing|Where-Object Category -EQ $Category|ForEach-Object DefinitionId|Select-Object -Unique)}
$priorities=[ordered]@{
    EnterpriseDefinitionIds=@(Missing-Ids 'enterprise_selected');DispatchCharacterDefinitionIds=@(Missing-Ids 'dispatch_character');
    CharacterUseModes=@($missing|Where-Object Category -EQ 'character_mode'|ForEach-Object {[ordered]@{DefinitionId=$_.DefinitionId;Mode=$_.Mode}});
    CityStyleDefinitionIds=@(Missing-Ids 'city_style_declared');SpecialActionIds=@(Missing-Ids 'special_action_used');FacilityDefinitionIds=@(Missing-Ids 'facility_built')
}
$eventPriorities=@($missing|Where-Object Category -EQ 'event_option'|ForEach-Object {
    $definition=$catalog.definitions|Where-Object DefinitionId -EQ $_.DefinitionId|Select-Object -First 1
    # 正式EventCardOptionIdFactory.Create(cardId, optionIndex)的稳定合同；只生成目录内真实选项。
    [string]$definition.RuntimeId+':option:'+([int]$_.Mode).ToString([Globalization.CultureInfo]::InvariantCulture)
})
$candidatePriorities=@($eventPriorities + @($missing|Where-Object {$_.Category -in 'dispatch_character','facility_built','definition_effect'}|ForEach-Object DefinitionId)|Select-Object -Unique)
$noProgress=@($runRows|Select-Object -Last 2|Where-Object NewCoverage -EQ 0).Count -eq 2
$result=[ordered]@{SchemaVersion=1;EvaluationMode='whole-game-health';ContentPackHash=$plan.ContentPackHash;EnabledExpansionIds=@($plan.EnabledExpansionIds);
    Runs=@($runRows);ObservedCoverage=@($observed);MissingCoverage=$missing;NextSelectionPriorities=$priorities;NextCandidatePriorityDefinitionIds=$candidatePriorities;
    ConsecutiveNoProgress=$noProgress;ContinuationReason=$(if ($noProgress) {'连续两局无新增覆盖，需核随机、前置、模式或实现限制并调整合法路线后再跑'} elseif ($missing.Count) {'按实际缺口继续合法选择'} else {'本模式目录登记的覆盖目标已累计完成'});
    CoverageIsIndependentOfGameplayPass=$true}
$outputFull=Resolve-CoveragePath $OutputPath
if (Test-Path -LiteralPath $outputFull) {throw '覆盖输出已存在，保留原件并使用新路径。'}
$result|ConvertTo-Json -Depth 40|Set-Content -LiteralPath $outputFull -Encoding utf8
if ($NextPlanPath) {
    $nextFull=Resolve-CoveragePath $NextPlanPath
    if (Test-Path -LiteralPath $nextFull) {throw '后继计划已存在，不能覆盖已运行输入。'}
    $plan.SelectionPriorities=$priorities
    $plan.CandidatePriorityDefinitionIds=$candidatePriorities
    $plan.RepresentativeRecovery=$false;$plan.RecoveryCheckpoints=@()
    $plan|ConvertTo-Json -Depth 40|Set-Content -LiteralPath $nextFull -Encoding utf8
}
Write-Host ("累计{0}局，实际覆盖{1}项，缺口{2}项。" -f $runRows.Count,$observed.Count,$missing.Count)
