param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$AuditToolManifestPath='',[string]$AuditReportPath='')
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith('G:\NoMadCity\Logs\ExpansionValidation\ProductionGameRoutes\', [StringComparison]::OrdinalIgnoreCase)) { throw '审计目录必须位于独立路线日志目录内。' }
$readerPath = Join-Path $PSScriptRoot 'HostArchiveEvidenceReader.cs'
if (-not ([System.Management.Automation.PSTypeName]'YC.Validation.HostArchiveEvidenceReader').Type) {
    $readerReferences = @([Newtonsoft.Json.JsonTextReader].Assembly.Location) +
        @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' -File | ForEach-Object FullName)
    Add-Type -Path $readerPath -ReferencedAssemblies $readerReferences -CompilerOptions '/nowarn:1701'
}
function Assert-EvidencePath([string]$Path) {
    $checkedPath = [IO.Path]::GetFullPath($Path)
    while ($checkedPath -and $checkedPath -ne 'G:\') {
        if ((Test-Path -LiteralPath $checkedPath) -and ((Get-Item -LiteralPath $checkedPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '证据路径含链接或重解析点。' }
        $checkedPath = Split-Path -Parent $checkedPath
    }
    return [IO.Path]::GetFullPath($Path)
}
function Read-Json([string]$Path) {
    [YC.Validation.HostArchiveEvidenceReader]::ReadJson((Assert-EvidencePath $Path))
}
function Read-JsonLines([string]$Path) {
    foreach ($record in [YC.Validation.HostArchiveEvidenceReader]::ReadLines((Assert-EvidencePath $Path))) { $record.Value }
}
function Assert-ProjectionEvidenceFields($View) {
    if ($View -isnot [Collections.IDictionary]) { throw '投影原件必须为对象。' }
    foreach ($field in @('GameId','ContentPackHash','ViewerRole','ViewerPlayerId','StateRevision','RuleConfiguration','Players','ItemZones')) {
        if (-not $View.Contains($field) -or $null -eq $View[$field]) { throw "投影原件缺必要字段：$field" }
    }
    if (@($View.Players).Count -lt 2) { throw '投影原件缺完整玩家隐私字段。' }
    foreach ($player in $View.Players) {
        foreach ($field in @('PlayerId','HandCardIds','CoveredCharacterCardId')) {
            if ($player -isnot [Collections.IDictionary] -or -not $player.Contains($field) -or $null -eq $player[$field]) { throw "玩家投影缺隐私字段：$field" }
        }
    }
    foreach ($field in @('Zones','Items','Orders')) {
        if ($View.ItemZones -isnot [Collections.IDictionary] -or -not $View.ItemZones.Contains($field) -or $null -eq $View.ItemZones[$field]) { throw "区域投影缺隐私字段：$field" }
    }
    foreach ($zone in $View.ItemZones.Zones) {
        if ($zone -isnot [Collections.IDictionary] -or -not $zone.Contains('IdentitiesVisible') -or $null -eq $zone.IdentitiesVisible -or
            $zone.Zone -isnot [Collections.IDictionary] -or -not $zone.Zone.Contains('ZoneId') -or -not $zone.Zone.Contains('TemplateId')) { throw '区域投影缺身份可见性原件。' }
    }
    foreach ($item in $View.ItemZones.Items) {
        if ($item -isnot [Collections.IDictionary] -or -not $item.Contains('ZoneId') -or -not $item.Contains('PhysicalKind') -or
            $item.ControllerSubject -isnot [Collections.IDictionary] -or -not $item.ControllerSubject.Contains('Kind') -or -not $item.ControllerSubject.Contains('PlayerId')) { throw '物品投影缺区域或控制者原件。' }
    }
    foreach ($order in $View.ItemZones.Orders) {
        if ($order -isnot [Collections.IDictionary] -or -not $order.Contains('ZoneId')) { throw '区域顺序投影缺所属区域原件。' }
    }
}
function Get-PlayerDirectoryMap([string]$RunDirectory, [int]$PlayerCount, [string]$AuthorityGameId, [string]$InputMode) {
    if ($PlayerCount -lt 2 -or $PlayerCount -gt 4 -or [string]::IsNullOrEmpty($AuthorityGameId)) { throw '玩家映射缺合法人数或Host局身份。' }
    $launches = @(Read-Json (Join-Path $RunDirectory 'processes.json'))
    $summaries = @(Read-Json (Join-Path $RunDirectory 'summary.json'))
    if ($launches.Count -ne $PlayerCount -or $summaries.Count -ne $PlayerCount -or
        @($launches.ProcessId | Select-Object -Unique).Count -ne $PlayerCount) { throw '玩家映射缺唯一完整的启动/退出进程证据。' }
    $playerDirectoryById = @{}
    foreach ($launchSeat in 1..$PlayerCount) {
        $launch = @($launches | Where-Object { $_.PlayerId -eq $launchSeat })
        $summary = @($summaries | Where-Object { $_.PlayerId -eq $launchSeat })
        $directory = [IO.Path]::GetFullPath((Join-Path $RunDirectory ('player-' + $launchSeat)))
        if ($launch.Count -ne 1 -or $summary.Count -ne 1 -or $launch[0].ProcessId -le 0 -or
            [string]::IsNullOrEmpty($launch[0].Output) -or
            [IO.Path]::GetFullPath($launch[0].Output) -ne $directory -or
            $summary[0].Result.processId -ne $launch[0].ProcessId -or
            (Read-Json (Join-Path $directory 'result.json')).processId -ne $launch[0].ProcessId) { throw "启动序号${launchSeat}的目录与真实进程证据不一致。" }
        if ($launchSeat -eq 1) {
            if ($InputMode -eq 'internal' -and ('--yc-dev-local-mirror-host=' + $PlayerCount) -notin $launch[0].Arguments) { throw '启动目录1缺真实Host启动证据。' }
            $hostViewCount = 0
            Read-JsonLines (Join-Path $directory 'projections.jsonl') | ForEach-Object {
                $hostViewCount++
                if ($_.view.GameId -ne $AuthorityGameId -or [string]$_.view.ViewerRole -notin '1','Player' -or $_.view.ViewerPlayerId -ne 1) {
                    throw '启动目录1未固定为同一Host局的真实玩家1。'
                }
            }
            if ($hostViewCount -eq 0) { throw '启动目录1缺真实玩家1投影原件。' }
            $actualPlayerId = 1
        } else {
            # 新协议接续同一真实已安装Coordinator；旧局仍按旧首同步原件读取。
            $protocolResult = Read-Json (Join-Path $directory 'result.json')
            $deterministic = $protocolResult.ProtocolMode -eq 'deterministic-simulation'
            $synchronizations = @(Read-JsonLines (Join-Path $directory 'initial-state-synchronizations.jsonl'))
            if ($synchronizations.Count -eq 0) { throw "启动序号${launchSeat}缺成功权威初始同步身份。" }
            $actualPlayerId = $synchronizations[0].ViewerPlayerId
            if ($actualPlayerId -notin (1..$PlayerCount) -or @($synchronizations | Where-Object {
                $_.GameId -ne $AuthorityGameId -or [string]$_.ViewerRole -notin '1','Player' -or
                $_.ViewerPlayerId -ne $actualPlayerId -or $_.initialSnapshot -lt 1 -or
                ($deterministic -and ($_.Source -ne 'DeterministicSimulationReady_or_current_installed_coordinator' -or $_.SimulationEpoch -lt 1 -or $_.MembershipVersion -lt 1)) -or
                (-not $deterministic -and $_.NextConfirmedSequence -lt 1)
            }).Count -gt 0) { throw "启动序号${launchSeat}的权威同步错局或身份变化。" }
            foreach ($synchronization in $synchronizations) {
                if ([string]$synchronization.initialSnapshot -notmatch '^[1-9][0-9]*$') { throw '初始同步原件序号无效。' }
                $initialView = Read-Json (Join-Path $directory ('initial-view-' + $synchronization.initialSnapshot + '.json'))
                if ($initialView.GameId -ne $AuthorityGameId -or [string]$initialView.ViewerRole -notin '1','Player' -or
                    $initialView.ViewerPlayerId -ne $actualPlayerId -or $initialView.StateRevision -ne $synchronization.StateRevision) { throw "启动序号${launchSeat}的成功同步缺一致投影原件。" }
            }
        }
        if ($playerDirectoryById.ContainsKey([int]$actualPlayerId)) { throw '真实玩家身份重复，不能建立一一映射。' }
        $playerDirectoryById[[int]$actualPlayerId] = $directory
    }
    return $playerDirectoryById
}
function Read-CommandOutcomes([string]$Path) {
    foreach ($record in [YC.Validation.HostArchiveEvidenceReader]::ReadLines((Assert-EvidencePath $Path))) {
        $value = $record.Value
        @{command=$value.command; rejected=@{Command=$value.rejected.Command}; errorCodeName=$value.errorCodeName;
            observedRevision=$value.observedRevision; view=@{GameId=$value.view.GameId}}
    }
}
function Find-AuthorityTransactions([string]$CommandId) {
    $count = 0; $entry = $null
    if ($transactionIndex.ByCommand.ContainsKey($CommandId)) {
        foreach ($location in $transactionIndex.ByCommand[$CommandId]) {
            $current = [YC.Validation.HostArchiveEvidenceReader]::ReadLineAt($transactionPath, $location.Offset, $location.Length)
            if (Transaction-Proven $current) { $count++; $entry = if ($count -eq 1) { $current } else { $null } }
        }
    }
    @{Count=$count;Entry=$entry}
}
function Canonical($Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [Collections.IDictionary]) {
        return '{' + ((@($Value.Keys | Sort-Object -CaseSensitive) | ForEach-Object { (ConvertTo-Json -InputObject ([string]$_) -Compress) + ':' + (Canonical $Value[$_]) }) -join ',') + '}'
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) { return '[' + ((@($Value | ForEach-Object { Canonical $_ })) -join ',') + ']' }
    return ConvertTo-Json -InputObject $Value -Compress -Depth 100
}
function Public-FinalScoring($Scoring) {
    if ($null -eq $Scoring) { return $null }
    $public = @{}
    foreach ($key in $Scoring.Keys) { if ($key -ne 'PlayerScores') { $public[$key] = $Scoring[$key] } }
    $public.PlayerScores = @($Scoring.PlayerScores | ForEach-Object {
        $score = @{}
        foreach ($key in $_.Keys) { if ($key -ne 'RemainingResources') { $score[$key] = $_[$key] } }
        $score
    })
    return $public
}
function Final-ScoringIssues($Scoring,$Authority,[int]$ViewerPlayerId) {
    $scoreIssues = [Collections.Generic.List[string]]::new()
    if ((Canonical (Public-FinalScoring $Scoring)) -ne (Canonical (Public-FinalScoring $Authority))) {
        $scoreIssues.Add("玩家${ViewerPlayerId}公开终局计分与Host不一致")
    }
    foreach ($score in $Scoring.PlayerScores) {
        if ($score.PlayerId -ne $ViewerPlayerId) {
            if ($null -ne $score.RemainingResources) { $scoreIssues.Add("玩家${ViewerPlayerId}终局暴露他人剩余资源") }
        } else {
            $own = @($Authority.PlayerScores | Where-Object PlayerId -EQ $ViewerPlayerId)
            if ($own.Count -ne 1 -or (Canonical $score.RemainingResources) -ne (Canonical $own[0].RemainingResources)) {
                $scoreIssues.Add("玩家${ViewerPlayerId}本人终局剩余资源与Host不一致")
            }
        }
    }
    return $scoreIssues.ToArray()
}
function Field($Value, [string]$Path) {
    # 仅点分字段与显式数字下标；不执行JSONPath表达式或用户脚本。
    if ($Path -notmatch '^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*|\.[0-9]+)*$') { throw "证据字段路径无效：$Path" }
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $Value) { return $null }
        if ($Value -is [Collections.IDictionary] -and $Value.Contains($part)) { $Value = $Value[$part] }
        elseif ($part -match '^\d+$' -and $Value -is [Collections.IList] -and [int]$part -lt $Value.Count) { $Value = $Value[[int]$part] }
        else { return $null }
    }
    return ,$Value
}
function Unwrap($Value) {
    if ($null -eq $Value) { return $null }
    switch ([string]$Value.Kind) {
        { $_ -in '0','Null' } { return $null }
        { $_ -in '1','Boolean' } { return $Value.BooleanValue }
        { $_ -in '2','Integer' } { return $Value.IntegerValue }
        { $_ -in '3','String' } { return $Value.StringValue }
        { $_ -in '4','StableReference' } { return $Value.ReferenceId }
        { $_ -in '5','Array' } { return ,@($Value.Items | ForEach-Object { Unwrap $_ }) }
        { $_ -in '6','Object' } { $result = @{}; foreach ($property in $Value.Properties) { $result[$property.Name] = Unwrap $property.Value }; return $result }
        default { throw '未知NormalizedValue类型。' }
    }
}
$issues = [Collections.Generic.List[string]]::new()
$coverage = [Collections.Generic.List[object]]::new()
$runInput = Read-Json (Join-Path $OutputDirectory 'run-input.json')
$plan = Read-Json (Join-Path $OutputDirectory 'route-plan.json')
$manifest = Read-Json (Join-Path $OutputDirectory 'frozen-manifest.json')
if ($plan.EvaluationMode -eq 'whole-game-health') {
    $gameIssues = [Collections.Generic.List[string]]::new()
    $driverIssues = [Collections.Generic.List[string]]::new()
    $observed = [Collections.Generic.List[object]]::new()
    $seenCoverage = @{}
    $recoveryStatus='未运行'
    $driverFailure = $false
    $snapshotRoot = [IO.Path]::GetFullPath($manifest.SnapshotRoot)
    if ($manifest.SchemaVersion -ne 3 -or -not $manifest.Frozen -or -not $manifest.ReadbackIndependent -or
        $snapshotRoot -notmatch '^G:\\NoMadCity\\Builds\\ValidationSnapshots\\[A-Za-z0-9][A-Za-z0-9_-]{7,80}$') { throw '整局审计缺固定Player快照。' }
    $auditRevision = $null
    if ($AuditToolManifestPath -or $AuditReportPath) {
        if (-not $AuditToolManifestPath -or -not $AuditReportPath) { throw '补验必须同时指定工具清单和独立报告路径' }
        $auditRevision = Read-Json $AuditToolManifestPath
        $toolRoot = [IO.Path]::GetFullPath($auditRevision.SnapshotRoot)
        if (-not $auditRevision.Frozen -or -not $auditRevision.ReadbackIndependent -or
            $auditRevision.SourceSnapshotRoot -ne $snapshotRoot -or
            $toolRoot -notmatch '^G:\\NoMadCity\\Builds\\ValidationSnapshots\\\d{8}-\d{6}$' -or
            $PSCommandPath -ne (Join-Path $toolRoot 'Tools\Test-ProductionGameRouteEvidence.ps1') -or
            (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'frozen-manifest.json')).Hash -ne $auditRevision.SourceManifestSha256 -or
            $auditRevision.SourceAuditPath -ne (Join-Path $OutputDirectory 'audit.json') -or
            (Get-FileHash -LiteralPath $auditRevision.SourceAuditPath).Hash -ne $auditRevision.SourceAuditSha256) { throw '补验工具或原局版本来源不匹配' }
        foreach ($file in $auditRevision.ToolFiles) {
            $path = Assert-EvidencePath $file.Path
            if (-not $path.StartsWith($toolRoot+'\Tools\',[StringComparison]::OrdinalIgnoreCase) -or
                (Get-FileHash -LiteralPath $path).Hash -ne $file.Sha256) { throw '补验固定工具指纹不匹配' }
        }
        foreach ($name in @('Test-ProductionGameRouteEvidence.ps1','HostArchiveEvidenceReader.cs','Update-ProductionGameCoveragePriorities.ps1')) {
            if (@($auditRevision.ToolFiles | Where-Object Path -EQ (Join-Path $toolRoot ('Tools\'+$name))).Count -ne 1) { throw '补验清单缺当前实际读取工具' }
        }
        $AuditReportPath = Assert-EvidencePath $AuditReportPath
        if ((Split-Path -Parent $AuditReportPath) -ne $OutputDirectory -or
            [IO.Path]::GetFileName($AuditReportPath) -notmatch '^audit-[a-z0-9-]+\.json$' -or (Test-Path -LiteralPath $AuditReportPath)) { throw '补验报告须为原局目录内新的明确文件' }
    } elseif ($PSCommandPath -ne (Join-Path $snapshotRoot 'Tools\Test-ProductionGameRouteEvidence.ps1')) { throw '整局审计须由快照工具执行。' }
    foreach ($file in @($manifest.Files) + @(@{Path=$manifest.BuildReceiptPath;Sha256=$manifest.BuildReceiptSha256})) {
        if (-not [IO.Path]::IsPathRooted($file.Path)) { throw '快照输入必须为绝对路径。' }
        $path = [IO.Path]::GetFullPath($file.Path)
        if (-not $path.StartsWith($snapshotRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '快照清单不得回读活动源码或受限档案。' }
        $path = Assert-EvidencePath $path
        if ($file.Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path).Hash -ne $file.Sha256) { $driverIssues.Add("快照输入缺失或漂移：$path") }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'route-plan.json')).Hash -ne $runInput.RoutePlanSha256 -or
        (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'frozen-manifest.json')).Hash -ne $runInput.FrozenManifestSha256) { $driverIssues.Add('本局固定计划或清单副本改变') }
    $driverFailure = $driverIssues.Count -gt 0
    $archivePath = Join-Path $OutputDirectory 'player-1\final-host-archive.json'
    $catalogPath = Join-Path $OutputDirectory 'player-1\active-catalog.json'
    $archiveAvailable = Test-Path -LiteralPath $archivePath -PathType Leaf
    $catalogAvailable = Test-Path -LiteralPath $catalogPath -PathType Leaf
    $state = $null; $catalog = $null; $playerDirectoryById = @{}; $completed = $false
    if ($archiveAvailable -and $catalogAvailable) {
        $archive = [YC.Validation.HostArchiveEvidenceReader]::ReadArchive((Assert-EvidencePath $archivePath))
        $state = $archive.State; $catalog = Read-Json $catalogPath; $runtime = $state.EffectRuntime
        if (-not $state.GameId -or $state.ContentPackHash -ne $plan.ContentPackHash -or $catalog.ContentPackHash -ne $state.ContentPackHash -or
            $state.MapId -ne $plan.MapId -or @($state.Players).Count -ne $plan.PlayerCount -or
            (@($state.RuleConfiguration.EnabledExpansionIds|Sort-Object) -join ',') -ne (@($plan.EnabledExpansionIds|Sort-Object) -join ',')) { $gameIssues.Add('终局配置、局身份或正式活动目录不一致') }
        $playerDirectoryById = Get-PlayerDirectoryMap $OutputDirectory $plan.PlayerCount $state.GameId $runInput.InputMode
        $summary=Read-Json (Join-Path $OutputDirectory 'summary.json')
        if (@($summary).Count -ne $plan.PlayerCount -or @($summary|Where-Object { $_.ExitCode -ne 0 -or -not $_.Result.GameplayRunPassed }).Count -gt 0) { $driverIssues.Add('实际Player未全部成功自然退出') }
        $completed = $state.Round -eq 8 -and [string]$state.Phase -in '8','FinalScoring','9','GameOver' -and $state.FinalScoring.IsResolved
        if ([string]$runtime.Status -in '1','PausedFault' -or $runtime.LastFaultCode -or @($runtime.EffectNodes|Where-Object { [string]$_.Status -in '6','Faulted' }).Count -gt 0) { $gameIssues.Add('正式Effect运行态存在异常') }
        $nodes = @{}; $completedEvents = @{}
        foreach ($node in $runtime.EffectNodes) {
            if (-not $node.EffectId -or $nodes.ContainsKey($node.EffectId)) { $gameIssues.Add('Effect身份缺失或重复'); continue }
            $nodes[$node.EffectId] = $node
            if ($node.CompletedEventId) {
                if ($completedEvents.ContainsKey($node.CompletedEventId)) { $gameIssues.Add('Effect完成事件重复') }
                $completedEvents[$node.CompletedEventId]=$true
            }
        }
        foreach ($node in $runtime.EffectNodes) {
            if ($node.ParentEffectId -and (-not $nodes.ContainsKey($node.ParentEffectId) -or $node.EffectId -notin @($nodes[$node.ParentEffectId].ChildEffectIds))) { $gameIssues.Add("Effect父链孤立：$($node.EffectId)") }
            if (@($node.ChildEffectIds|Select-Object -Unique).Count -ne @($node.ChildEffectIds).Count) { $gameIssues.Add("Effect子节点重复：$($node.EffectId)") }
            foreach ($child in $node.ChildEffectIds) { if (-not $nodes.ContainsKey($child) -or $nodes[$child].ParentEffectId -ne $node.EffectId) { $gameIssues.Add("Effect子链不一致：$($node.EffectId)") } }
            $visited=@{}; $ancestor=$node
            while ($ancestor.ParentEffectId -and $nodes.ContainsKey($ancestor.ParentEffectId)) {
                if ($visited.ContainsKey($ancestor.EffectId)) { $gameIssues.Add("Effect父链成环：$($node.EffectId)"); break }
                $visited[$ancestor.EffectId]=$true; $ancestor=$nodes[$ancestor.ParentEffectId]
            }
            if ($completed -and [string]$node.Status -notin '4','Completed','5','Failed') { $gameIssues.Add("终局仍有待结Effect：$($node.EffectId)") }
        }
        if ($completed -and @($runtime.InteractionRequests|Where-Object Status -EQ 'open').Count -gt 0) { $gameIssues.Add('终局遗漏未完成交互义务') }
        $lastSequence=0; $commitIds=@{}; $revisionCommits=@{}
        foreach ($entry in $runtime.Journal) {
            if ($entry.CommitSequence -ne $lastSequence + 1) { $gameIssues.Add('正式日志序号重复、回退或缺失') }; $lastSequence=[long]$entry.CommitSequence
            if (-not $entry.CommitId -or ($commitIds.ContainsKey($entry.CommitId) -and $commitIds[$entry.CommitId] -ne $entry.StateRevision) -or
                ($revisionCommits.ContainsKey([long]$entry.StateRevision) -and $revisionCommits[[long]$entry.StateRevision] -ne $entry.CommitId)) { $gameIssues.Add('正式提交身份或版本分组不一致') }
            $commitIds[$entry.CommitId]=$entry.StateRevision;$revisionCommits[[long]$entry.StateRevision]=$entry.CommitId
        }
        if ($lastSequence + 1 -ne $runtime.NextCommitSequence) { $gameIssues.Add('正式提交尾序号不一致') }
        $definitionById=@{}; $definitionByRuntime=@{}; $itemDefinitions=@{}
        foreach ($definition in $catalog.definitions) { $definitionById[$definition.DefinitionId]=$definition; $definitionByRuntime[$definition.RuntimeId]=$definition }
        foreach ($item in $state.ItemZones.Items) { $itemDefinitions[$item.ItemId]=$item.DefinitionId }
        function Normalize-CoverageDefinition([string]$Id) {
            if ($itemDefinitions.ContainsKey($Id)) { return [string]$itemDefinitions[$Id] }
            if ($definitionById.ContainsKey($Id)) { return $Id }
            if ($definitionByRuntime.ContainsKey($Id)) { return [string]$definitionByRuntime[$Id].DefinitionId }
            return ''
        }
        function Add-ObservedCoverage([string]$Category,[string]$Id,[string]$Mode,[string]$EvidenceId) {
            if (-not $Id) { return }; $key=$Category+'|'+$Id+'|'+$Mode
            if (-not $seenCoverage.ContainsKey($key)) { $seenCoverage[$key]=$true; $observed.Add(@{Category=$Category;DefinitionId=$Id;Mode=$Mode;EvidenceId=$EvidenceId}) }
        }
        foreach ($node in $runtime.EffectNodes|Where-Object { [string]$_.Status -in '4','Completed' }) {
            $args=Unwrap $node.NormalizedArguments; $result=Unwrap $node.NormalizedResult; $continuation=Unwrap $node.ContinuationData
            $content=Normalize-CoverageDefinition $node.ContinuationContentId
            if ($content) { Add-ObservedCoverage 'definition_effect' $content '' $node.EffectId }
            switch ($node.EffectTypeId) {
                'setup.enterprise.select' { foreach ($id in $result.selectedEnterpriseItemIds) { Add-ObservedCoverage 'enterprise_selected' (Normalize-CoverageDefinition $id) '' $node.EffectId } }
                'effect.operator.dispatch' { if ($result.accepted) { Add-ObservedCoverage 'dispatch_character' (Normalize-CoverageDefinition $result.selectedOperatorItemId) '' $node.EffectId } }
                'effect.facility.build' { if ($result -is [string] -and $result) { Add-ObservedCoverage 'facility_built' (Normalize-CoverageDefinition $result) '' $node.EffectId } }
                'effect.event_card.resolve' { Add-ObservedCoverage 'event_resolved' (Normalize-CoverageDefinition $result.cardId) '' $node.EffectId
                    if ($null -ne $result.selectedOptionIndex) { Add-ObservedCoverage 'event_option' (Normalize-CoverageDefinition $result.cardId) ([string]$result.selectedOptionIndex) $node.EffectId } }
                'effect.city_style.special_action.activate' { Add-ObservedCoverage 'special_action_used' $args.specialActionId '' $node.EffectId }
            }
        }
        foreach ($use in $state.CharacterUses) { foreach ($mode in $use.CompletedModes) { Add-ObservedCoverage 'character_mode' (Normalize-CoverageDefinition $use.CardInstanceId) $mode $use.UseId } }
        $coverageLines=Join-Path $OutputDirectory 'player-1\observed-coverage.jsonl'
        if (Test-Path -LiteralPath $coverageLines) {
            foreach ($line in Read-JsonLines $coverageLines) {
                if ($line.GameId -ne $state.GameId -or $line.ContentPackHash -ne $state.ContentPackHash) { $driverFailure=$true;$driverIssues.Add('实际执行覆盖记录错局或错版本'); continue }
                foreach ($mode in $line.CharacterModes) {
                    if ($mode.Mode -in 'strategy','tactic' -and $definitionById.ContainsKey($mode.DefinitionId)) { Add-ObservedCoverage 'character_mode' $mode.DefinitionId $mode.Mode $mode.UseId }
                }
            }
        }
        foreach ($player in $state.Players) { foreach ($style in $player.DeclaredCityStyles) { Add-ObservedCoverage 'city_style_declared' (Normalize-CoverageDefinition $style.CityStyleId) '' $style.DeclarationId } }
        $publicFinal=$null; $protocolFinal=$null; $recoveryStatus='未运行'
        foreach ($playerId in 1..$plan.PlayerCount) {
            $directory=$playerDirectoryById[[int]$playerId]; $resultPath=Join-Path $directory 'result.json';$finalPath=Join-Path $directory 'final-view.json'
            if (-not (Test-Path -LiteralPath $resultPath) -or -not (Test-Path -LiteralPath $finalPath)) { $driverIssues.Add("玩家${playerId}缺结果或终局投影"); continue }
            $result=Read-Json $resultPath
            $deterministic = $result.ProtocolMode -eq 'deterministic-simulation'
            if ($runInput.NetworkProtocol -eq 'deterministic-simulation' -and -not $deterministic) {$driverIssues.Add("玩家${playerId}未消费本局声明的新协议")}
            if ($deterministic) {
                $protocolPath = Join-Path $directory 'protocol-summary.json'
                if (-not (Test-Path -LiteralPath $protocolPath)) { $driverIssues.Add("玩家${playerId}缺新协议终局原件") }
                else {
                    $protocol = Read-Json $protocolPath
                    if ($protocol.GameId -ne $state.GameId -or $protocol.LocalPlayerId -ne $playerId -or
                        $protocol.SimulationEpoch -lt 1 -or $protocol.MembershipVersion -lt 1 -or
                        $protocol.LocalAppliedSequence -le 0 -or $protocol.HostValidatedSequence -ne $protocol.LocalAppliedSequence -or
                        $protocol.HasPendingLocalApplication -or $protocol.IsRecoveringConfirmedPrefix -or $protocol.IsStopped -or $protocol.RequiresRecovery -or
                        $result.HasUnresolvedLocalCommand -or $result.RecoveryInProgress -or
                        $protocol.PrefixDigest -notmatch '^[A-Fa-f0-9]{64}$' -or $protocol.RuleStateDigest -notmatch '^[A-Fa-f0-9]{64}$') {
                        $gameIssues.Add("玩家${playerId}新协议终局仍有未执行/未核验/故障或摘要缺失")
                    }
                    $sameOrder = Canonical @{GameId=$protocol.GameId;SimulationEpoch=$protocol.SimulationEpoch;MembershipVersion=$protocol.MembershipVersion;
                        G=$protocol.LocalAppliedSequence;R=$protocol.HostValidatedSequence;PrefixDigest=$protocol.PrefixDigest;
                        RuleDigestVersion=$protocol.RuleDigestVersion;RuleStateDigest=$protocol.RuleStateDigest}
                    if ($null -eq $protocolFinal) {$protocolFinal=$sameOrder} elseif ($protocolFinal -ne $sameOrder) {$gameIssues.Add('新协议多端同序或完整模拟摘要不一致')}
                }
                $recoveryPath = Join-Path $directory 'simulation-recovery-events.jsonl'
                if (Test-Path -LiteralPath $recoveryPath) {
                    $completedRecovery = @(Read-JsonLines $recoveryPath | Where-Object Phase -EQ 'completed')
                    if ($completedRecovery.Count -gt 0) {$recoveryStatus='确认前缀恢复已运行'}
                    if ($plan.RepresentativeRecovery -and $completedRecovery.Count -eq 0) {$driverIssues.Add("玩家${playerId}缺确认前缀恢复完成原件")}
                } elseif ($plan.RepresentativeRecovery) {$driverIssues.Add("玩家${playerId}代表性确认前缀恢复未运行")}
            }
            if ($result.RunOutcome -eq 'game_failure') { $gameIssues.Add("正式运行故障：$($result.failure)") }
            if ($result.RunOutcome -eq 'driver_failure') { $driverFailure=$true }
            if (-not $result.GameplayRunPassed -or $result.RunOutcome -ne 'completed' -or (@($result.rounds|Sort-Object) -join ',') -ne '1,2,3,4,5,6,7,8') { $driverIssues.Add("玩家${playerId}未完成八回合真实运行") }
            $final=Read-Json $finalPath
            if ($final.GameId -ne $state.GameId -or [string]$final.ViewerRole -notin '1','Player' -or $final.ViewerPlayerId -ne $playerId -or -not $final.FinalScoring.IsResolved -or $final.Round -ne 8 -or
                (Canonical $final.RuleConfiguration) -ne (Canonical $state.RuleConfiguration)) { $gameIssues.Add("玩家${playerId}终局身份、配置或阶段不一致") }
            foreach ($scoreIssue in @(Final-ScoringIssues $final.FinalScoring $state.FinalScoring $playerId)) { $gameIssues.Add($scoreIssue) }
            $score=Canonical (Public-FinalScoring $final.FinalScoring)
            if ($null -eq $publicFinal) { $publicFinal=$score } elseif ($score -ne $publicFinal) { $gameIssues.Add('多端公开终局计分不一致') }
            foreach ($projectionFile in $(if ($deterministic) { @('projections.jsonl','simulation-applied.jsonl') } else { @('projections.jsonl','confirmed.jsonl') })) {
            $previousRevision=-1
            Read-JsonLines (Join-Path $directory $projectionFile) | ForEach-Object {
                $line=$_
                $view=$line.view
                if ($deterministic) {
                    try { Assert-ProjectionEvidenceFields $view }
                    catch { $gameIssues.Add("玩家${playerId}投影原件字段不完整：$($_.Exception.Message)"); return }
                }
                if ($view.GameId -ne $state.GameId -or [string]$view.ViewerRole -notin '1','Player' -or $view.ViewerPlayerId -ne $playerId -or $view.ContentPackHash -ne $state.ContentPackHash -or
                    @($view.Players|Where-Object { $_.PlayerId -ne $playerId -and (@($_.HandCardIds).Count -gt 0 -or $_.CoveredCharacterCardId) }).Count -gt 0) { $gameIssues.Add("玩家${playerId}投影身份或手牌隐私不一致") }
                if ($view.StateRevision -lt $previousRevision) { $gameIssues.Add("玩家${playerId}权威投影版本回退") };$previousRevision=[long]$view.StateRevision
                if ((Canonical $view.RuleConfiguration) -ne (Canonical $final.RuleConfiguration)) { $gameIssues.Add("玩家${playerId}局内配置不一致") }
                $hidden=@($view.ItemZones.Zones|Where-Object { -not $_.IdentitiesVisible }|ForEach-Object {$_.Zone.ZoneId})
                $secret=@($view.ItemZones.Zones|Where-Object {$_.Zone.TemplateId -eq 'core.zone.box_secret_retired'}|ForEach-Object {$_.Zone.ZoneId})
                if (@($view.ItemZones.Items|Where-Object { $_.ZoneId -in $hidden -and -not ($_.ZoneId -in $secret -and [string]$_.PhysicalKind -in '1','Card' -and [string]$_.ControllerSubject.Kind -in '1','Player' -and $_.ControllerSubject.PlayerId -eq $playerId) }).Count -gt 0 -or
                    @($view.ItemZones.Orders|Where-Object {$_.ZoneId -in $hidden}).Count -gt 0) { $gameIssues.Add("玩家${playerId}隐藏区域泄漏") }
            }
            }
        }
    } else {
        $driverIssues.Add('缺正式终局归档或活动内容目录，无法确认整局健康')
        foreach ($directory in Get-ChildItem -LiteralPath $OutputDirectory -Directory | Where-Object Name -Match '^player-[1-4]$') {
            $resultPath=Join-Path $directory.FullName 'result.json'
            if (Test-Path -LiteralPath $resultPath) {
                $result=Read-Json $resultPath
                if ($result.RunOutcome -eq 'game_failure') { $gameIssues.Add("正式运行故障：$($result.failure)") }
                elseif ($result.RunOutcome -eq 'driver_failure') { $driverFailure=$true;$driverIssues.Add("驱动故障：$($result.failure)") }
            }
        }
    }
    $outcome=if ($gameIssues.Count -gt 0) {'game_failure'} elseif ($driverFailure) {'driver_failure'} elseif ($completed -and $driverIssues.Count -eq 0) {'completed'} else {'incomplete'}
    $report=[ordered]@{SchemaVersion=3;EvaluationMode='whole-game-health';GameplayRunPassed=($outcome -eq 'completed');RunOutcome=$outcome;
        EffectChainHealthy=$(if ($gameIssues.Count -gt 0) {$false} elseif ($completed) {$true} else {$null});GameId=$state.GameId;
        ContentPackHash=$plan.ContentPackHash;EnabledExpansionIds=@($plan.EnabledExpansionIds);MapId=$plan.MapId;PlayerCount=$plan.PlayerCount;
        SnapshotRoot=$snapshotRoot;GameIssues=@($gameIssues|Select-Object -Unique);DriverIssues=@($driverIssues|Select-Object -Unique);
        ObservedCoverage=@($observed);MissingCoverage=@();NextSelectionPriorities=$null;ConfirmedRecoveryStatus=$recoveryStatus;ManualUiAcceptance='待用户人工验收'}
    $auditPath=if ($AuditReportPath) {$AuditReportPath} else {Join-Path $OutputDirectory 'audit.json'}
    if ($auditRevision) {
        $report.SourceAuditPath=$auditRevision.SourceAuditPath;$report.SourceAuditSha256=$auditRevision.SourceAuditSha256
        $report.AuditToolManifestPath=$AuditToolManifestPath;$report.AuditToolManifestSha256=(Get-FileHash -LiteralPath $AuditToolManifestPath).Hash
        $report.AuditSnapshotRoot=$auditRevision.SnapshotRoot;$report.GameWasRerun=$false
    }
    $report|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $auditPath -Encoding utf8
    if ($catalogAvailable) {
        $prioritiesPath=if ($AuditReportPath) {Join-Path $OutputDirectory ([IO.Path]::GetFileNameWithoutExtension($AuditReportPath)+'-coverage-priorities.json')} else {Join-Path $OutputDirectory 'coverage-priorities.json'}
        if ($AuditReportPath -and (Test-Path -LiteralPath $prioritiesPath)) { throw '保留已有补验覆盖优先级原件' }
        & (Join-Path $PSScriptRoot 'Update-ProductionGameCoveragePriorities.ps1') -ReportPaths @($auditPath) -RoutePlanPath (Join-Path $OutputDirectory 'route-plan.json') -CatalogPath $catalogPath -OutputPath $prioritiesPath
        $priorities=Read-Json $prioritiesPath
        $report.MissingCoverage=@($priorities.MissingCoverage);$report.NextSelectionPriorities=$priorities.NextSelectionPriorities
        $report|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $auditPath -Encoding utf8
    }
    Write-Host ("整局结果：{0}；实际覆盖{1}项，缺口{2}项。" -f $outcome,$observed.Count,@($report.MissingCoverage).Count)
    exit $(if ($report.GameplayRunPassed) {0} else {1})
}
if ((Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'route-plan.json') -Algorithm SHA256).Hash -ne $runInput.RoutePlanSha256 -or
    (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'frozen-manifest.json') -Algorithm SHA256).Hash -ne $runInput.FrozenManifestSha256) { $issues.Add('冻结计划或清单副本已改变') }
foreach ($file in $manifest.Files) {
    $path = [IO.Path]::GetFullPath($file.Path)
    if (-not [IO.Path]::IsPathRooted($file.Path) -or $path -eq 'G:\NoMadCity\游城拓荒\UI素材' -or $path.StartsWith('G:\NoMadCity\游城拓荒\UI素材\', [StringComparison]::OrdinalIgnoreCase)) { throw '冻结清单包含禁止访问路径。' }
    $allowedRoots = @('G:\NoMadCity\Assets\YC\','G:\NoMadCity\Assets\StreamingAssets\Content\core\','G:\NoMadCity\Builds\',
        'G:\NoMadCity\ProjectSettings\','G:\NoMadCity\Packages\','G:\NoMadCity\tools\validation\')
    $scenePaths=@('StartScene','LoadingScene','SampleScene','CollectionRoom','ThreePlayerScene')|ForEach-Object { 'G:\NoMadCity\Assets\Scenes\'+$_+'.unity' }
    $explicitScene=$path -in $scenePaths -or $path -in @($scenePaths|ForEach-Object {$_+'.meta'})
    $explicitDependency=$path.StartsWith('G:\NoMadCity\Assets\',[StringComparison]::OrdinalIgnoreCase) -and $path -in @($manifest.AssetDependencies)
    $explicitPlan=$path -in @($manifest.RoutePlanPaths)
    $explicitBuildAuxiliary=$path -eq 'G:\NoMadCity\steam_appid.txt'
    $allowedPlans = @(
        foreach ($allowedPlanDirectory in @('G:\NoMadCity\Logs\ExpansionValidation\ProductionGameRoutes\Preparation\PaymentClosure\','G:\NoMadCity\Logs\ExpansionValidation\ProductionGameRoutes\Preparation\CoverageClosure\')) {
            foreach ($allowedPlanName in @('core-internal','core-external','enterprise-internal','enterprise-external','infected-internal','infected-external')) {
                $allowedPlanDirectory+$allowedPlanName+'.json'
            }
        }
    )
    if ($explicitPlan -and $path -notin $allowedPlans) { throw '报告和日志不能作为可执行路线冻结。' }
    if (@($allowedRoots | Where-Object { $path.StartsWith($_,[StringComparison]::OrdinalIgnoreCase) }).Count -eq 0 -and
        -not $explicitScene -and -not $explicitDependency -and -not $explicitPlan -and -not $explicitBuildAuxiliary) { throw '冻结文件不在明确运行输入范围。' }
    $currentPath = $path
    while ($currentPath -and $currentPath -ne 'G:\') {
        if ((Test-Path -LiteralPath $currentPath) -and ((Get-Item -LiteralPath $currentPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '冻结路径含链接或重解析点。' }
        $currentPath = Split-Path -Parent $currentPath
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Sha256) { $issues.Add("冻结文件缺失或已改变：$path") }
}
$nativeInputEvents = @()
if ($runInput.InputMode -eq 'external') {
    $nativeTracePath = Join-Path $OutputDirectory 'native-input-events.jsonl'
    if (-not (Test-Path -LiteralPath $nativeTracePath)) { $issues.Add('缺真实原生输入轨迹；外部观察不能计入验收') }
    else {
        $nativeInputEvents = @(Read-JsonLines $nativeTracePath)
        if ($nativeInputEvents.Count -eq 0 -or @($nativeInputEvents | Where-Object { $_.Tool -ne 'node_repl/@oai/sky' -or $_.Action -notin 'click','type_text','press_key','scroll','drag' -or
            -not $_.InputTraceId -or -not $_.WindowId -or -not $_.ObservationBeforeId -or -not $_.ObservationAfterId }).Count -gt 0) { $issues.Add('原生输入记录缺准确工具、窗口、动作或前后观察身份') }
    }
}
$archivePath = Join-Path $OutputDirectory 'player-1/final-host-archive.json'
$catalogPath = Join-Path $OutputDirectory 'player-1/active-catalog.json'
if (-not (Test-Path -LiteralPath $archivePath) -or -not (Test-Path -LiteralPath $catalogPath)) {
    $issues.Add('缺少真实终局归档或正式活动目录，不能计算覆盖通过')
    [pscustomobject]@{ Success=$false; Status='缺证据'; Issues=@($issues); Coverage=@(); GameplayEvidence='缺少正式归档，无法确认完整运行' } |
        ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'audit.json') -Encoding utf8
    exit 1
}
$archive = [YC.Validation.HostArchiveEvidenceReader]::ReadArchive((Assert-EvidencePath $archivePath))
$state = $archive.State
$playerDirectoryById = Get-PlayerDirectoryMap $OutputDirectory $plan.PlayerCount $state.GameId $runInput.InputMode
$catalog = Read-Json $catalogPath
$runtime = $state.EffectRuntime
$transactionPath = Join-Path $OutputDirectory 'player-1/authoritative-transactions.jsonl'
$transactionIndex = if (Test-Path -LiteralPath $transactionPath) { [YC.Validation.HostArchiveEvidenceReader]::IndexTransactions((Assert-EvidencePath $transactionPath)) } else { [YC.Validation.TransactionEvidenceIndex]::new() }
if ($transactionIndex.Count -eq 0) { $issues.Add('缺实际Host连续观察的命令前后快照，不能从Journal反造业务前态') }
$summaryPath = Join-Path $OutputDirectory 'summary.json'
if ($runInput.InputMode -eq 'internal') {
    if (-not (Test-Path -LiteralPath $summaryPath)) { $issues.Add('缺独立Player进程退出结果') }
    else {
        $processResults = @(Read-Json $summaryPath)
        if ($processResults.Count -ne $plan.PlayerCount -or @($processResults.PlayerId | Select-Object -Unique).Count -ne $plan.PlayerCount -or
            @($processResults | Where-Object { $_.ExitCode -ne 0 -or -not $_.Result.success }).Count -gt 0) { $issues.Add('独立Player进程未全部正常完成') }
    }
}
if ($catalog.ContentPackHash -ne $plan.ContentPackHash -or $archive.Snapshot.ContentHash -ne $plan.ContentPackHash -or $runInput.ContentPackHash -ne $plan.ContentPackHash) { $issues.Add('活动目录、归档和固定输入内容哈希不一致') }
if ($runInput.InputMode -eq 'external') {
    if (@($nativeInputEvents | Group-Object InputTraceId | Where-Object { $_.Count -gt 1 }).Count -gt 0 -or
        @($nativeInputEvents | Where-Object { $_.GameId -ne $state.GameId -or -not $_.PlayerId -or $_.PlayerId -gt $plan.PlayerCount }).Count -gt 0) { $issues.Add('原生输入身份重复或不属于同一局/玩家') }
    foreach ($event in $nativeInputEvents | Where-Object { $_.CommandSubmitted }) {
        $outcomeFile = if ($event.Outcome -eq 'rejected') { 'rejected.jsonl' } else { 'confirmed.jsonl' }
        $outcomePath = Join-Path $playerDirectoryById[[int]$event.PlayerId] $outcomeFile
        $matching = @(if (Test-Path -LiteralPath $outcomePath) { Read-CommandOutcomes $outcomePath | Where-Object {
            $(if ($event.Outcome -eq 'rejected') { $_.rejected.Command.CommandId } else { $_.command.CommandId }) -eq $event.CommandId -and $_.view.GameId -eq $event.GameId } })
        if (-not $event.CommandId -or $matching.Count -ne 1) { $issues.Add('原生提交缺同一正式命令确认/拒绝关联') }
        elseif ($event.Outcome -eq 'rejected') {
            $expected = @($plan.Steps | Where-Object { $_.StepId -eq $event.StepId -and $_.PlayerId -eq $event.PlayerId -and $_.ExpectRejection })
            if ($expected.Count -ne 1 -or [string]$matching[0].errorCodeName -ne [string]$event.ErrorCode -or [string]$expected[0].ExpectedErrorCode -ne [string]$event.ErrorCode) { $issues.Add('外部拒绝不属于明确反例或错误码不一致') }
        }
    }
}
if ($state.RuleConfiguration.MapId -ne $plan.MapId -or $state.RuleConfiguration.PlayerCount -ne $plan.PlayerCount -or
    (Canonical @($state.RuleConfiguration.EnabledExpansionIds | Sort-Object)) -ne (Canonical @($plan.EnabledExpansionIds | Sort-Object))) { $issues.Add('权威配置与路线不一致') }
if (@($runtime.DispatchReceipts | Group-Object InvocationKey | Where-Object { $_.Count -gt 1 }).Count -gt 0) { $issues.Add('派发InvocationKey重复，恢复去重未成立') }
if (@($runtime.EffectNodes | Group-Object EffectId | Where-Object { $_.Count -gt 1 }).Count -gt 0) { $issues.Add('EffectId重复') }
$nodes = @{}; foreach ($node in $runtime.EffectNodes) { $nodes[$node.EffectId] = $node }
function Completed-Tree([string]$Id, $Visited) {
    if (-not $nodes.ContainsKey($Id) -or -not $Visited.Add($Id)) { return $false }
    $node = $nodes[$Id]
    if ([string]$node.Status -notin '4','Completed') { return $false }
    foreach ($child in $node.ChildEffectIds) { if (-not (Completed-Tree $child $Visited)) { return $false } }
    return $true
}
function Effect-TreeNodes([string]$Id, $Visited) {
    if (-not $nodes.ContainsKey($Id) -or -not $Visited.Add($Id)) { return }
    $node = $nodes[$Id]
    $node
    foreach ($child in $node.ChildEffectIds) { Effect-TreeNodes $child $Visited }
}
function Scope-Value($Context, [string]$Scope, [string]$Path) {
    if (-not $Context.ContainsKey($Scope)) { throw "未登记业务证据域：$Scope" }
    if ([string]::IsNullOrEmpty($Path)) { return ,$Context[$Scope] }
    return ,(Field $Context[$Scope] $Path)
}
function Transaction-Proven($Transaction) {
    $before=$Transaction.Before; $after=$Transaction.After
    if ($null -eq $before -or $null -eq $after -or -not $Transaction.CommandId -or
        $before.State.GameId -ne $state.GameId -or $after.State.GameId -ne $state.GameId -or
        $before.ContentHash -ne $plan.ContentPackHash -or $after.ContentHash -ne $plan.ContentPackHash -or
        $after.LastCommitSequence -le $before.LastCommitSequence) { return $false }
    $span=@($after.State.EffectRuntime.Journal|Where-Object { $_.CommitSequence -gt $before.LastCommitSequence -and $_.CommitSequence -le $after.LastCommitSequence })
    return $span.Count -eq ($after.LastCommitSequence - $before.LastCommitSequence) -and
        @($span|Where-Object { $_.CommandId -and $_.CommandId -ne $Transaction.CommandId }).Count -eq 0 -and
        @($span|Where-Object CommandId -EQ $Transaction.CommandId).Count -gt 0
}
function Joint-Clause($Clause, $Context) {
    $left = Scope-Value $Context $Clause.LeftScope $Clause.LeftPath
    $right = if ($Clause.RightScope -eq 'literal') {
        if ([string]::IsNullOrEmpty($Clause.RightJson)) { return $false }
        ConvertFrom-Json -InputObject $Clause.RightJson -AsHashtable -Depth 100
    } else { Scope-Value $Context $Clause.RightScope $Clause.RightPath }
    if ($null -eq $left -or $null -eq $right) { return $false }
    switch ($Clause.Operator) {
        'equals' { return (Canonical $left) -eq (Canonical $right) }
        'not-equals' { return (Canonical $left) -ne (Canonical $right) }
        'contains' { return $left -is [Collections.IEnumerable] -and $left -isnot [string] -and @($left | Where-Object { (Canonical $_) -eq (Canonical $right) }).Count -gt 0 }
        'delta-equals' {
            $previous = Scope-Value $Context $Clause.BeforeScope $Clause.BeforePath
            if ($null -eq $previous -or $left -isnot [ValueType] -or $previous -isnot [ValueType] -or $right -isnot [ValueType] -or $left -is [bool] -or $previous -is [bool] -or $right -is [bool]) { return $false }
            return ([decimal]$left - [decimal]$previous) -eq [decimal]$right
        }
        default { throw "未知业务审计算子：$($Clause.Operator)" }
    }
}
function Payment-Proven($Context, [string]$Kind) {
    $payment=$Context.payment_after; $previous=$Context.payment_before
    if ($null -eq $payment -or $null -eq $previous -or -not $payment.PaymentId -or
        $payment.PaymentId -ne $payment.OwnerEffectId -or $payment.OwnerEffectId -ne $Context.node.EffectId -or
        $payment.PlayerId -ne $Context.node.PlayerId -or $previous.PaymentId -ne $payment.PaymentId -or
        $previous.OwnerEffectId -ne $payment.OwnerEffectId -or $previous.PlayerId -ne $payment.PlayerId -or
        $previous.IsCommitted -or -not $payment.IsCommitted -or $payment.IsCommitRequested -or
        (Canonical $payment.PaymentCost) -ne (Canonical $previous.PaymentCost)) { return $false }
    $transitioned=@($Context.after.EffectRuntime.EffectNodes|Where-Object {
        $_.Payment.PlayerId -eq $payment.PlayerId -and $_.Payment.IsCommitted -and
        @($Context.before.EffectRuntime.EffectNodes|Where-Object { $_.Payment.PaymentId -eq $payment.PaymentId -and $_.Payment.IsCommitted }).Count -eq 0 -and
        $_.Payment.PaymentId -eq $payment.PaymentId
    })
    if ($transitioned.Count -ne 1) { return $false }
    $otherPayments=@($Context.after.EffectRuntime.EffectNodes|Where-Object {
        $candidate=$_; $candidate.Payment -and $candidate.Payment.PlayerId -eq $payment.PlayerId -and $candidate.Payment.IsCommitted -and
        $candidate.Payment.PaymentId -ne $payment.PaymentId -and
        @($Context.before.EffectRuntime.EffectNodes|Where-Object { $_.Payment.PaymentId -eq $candidate.Payment.PaymentId -and $_.Payment.IsCommitted }).Count -eq 0
    })
    if ($otherPayments.Count -gt 0 -or $null -eq $Context.player_before -or $null -eq $Context.player_after) { return $false }
    $total=0
    foreach ($resource in 'Originium','OriginiumShard','Iron','PureOriginium','GoldVoucher') {
        $allocation=if ($payment.SelectedSubstitution) { $payment.SelectedSubstitution.Allocation[$resource] } else { 0 }
        $cost=$payment.PaymentCost[$resource]
        if ($null -eq $cost -or $null -eq $allocation -or $cost -lt 0 -or $allocation -lt 0 -or $allocation -gt $cost -or
            ($resource -in 'PureOriginium','GoldVoucher' -and $allocation -ne 0) -or
            $Context.player_before.Resources[$resource]-$Context.player_after.Resources[$resource] -ne $cost-$allocation) { return $false }
        $total+=$allocation
    }
    if ($total -gt $payment.SubstitutionBudget) { return $false }
    $windows=@($Context.after.EffectRuntime.EffectNodes.SettlementWindow|Where-Object { $_.Context.PaymentId -eq $payment.PaymentId })
    if ($Kind -eq 'payment-substitution') {
        $draft=$payment.SelectedSubstitution
        if ($total -le 0 -or $windows.Count -ne 1 -or (Canonical $draft) -ne (Canonical $previous.SelectedSubstitution)) { return $false }
        $source=@($Context.before.ItemZones.Items|Where-Object { $_.ItemId -eq $draft.SourceItemId -and $_.ControllerSubject.PlayerId -eq $payment.PlayerId })
        $entry=@($windows[0].Entries|Where-Object { $_.SourceItemId -eq $draft.SourceItemId -and $_.SourceDefinitionId -eq $source[0].DefinitionId -and $_.AcceptedUseCount -eq 1 -and $_.MaxUses -eq 1 -and $_.TargetEffectId })
        if ($source.Count -ne 1 -or $entry.Count -ne 1) { return $false }
        if ($Context.receipt.ContentId -and ($entry[0].SourceDefinitionId -ne $Context.receipt.ContentId -or $entry[0].AbilityId -ne $Context.receipt.AbilityId)) { return $false }
        $moved=@($Context.before.ItemZones.Items|Where-Object {
            $item=$_; $item.ZoneId -eq $draft.SourceZoneId -and $item.TypeId -eq 'core.player_marker' -and $item.OwnerSubject.PlayerId -eq $payment.PlayerId -and
            @($Context.after.ItemZones.Items|Where-Object { $_.ItemId -eq $item.ItemId -and $_.ZoneId -ne $draft.SourceZoneId -and
                $_.ZoneId -in @($Context.after.ItemZones.Zones|Where-Object { $_.LocalZoneId -eq 'player_markers' -and $_.OwnerPlayerId -eq $payment.PlayerId }|ForEach-Object ZoneId) }).Count -eq 1
        })
        if ($moved.Count -ne $total) { return $false }
    } elseif ($Kind -eq 'payment-original') {
        if ($total -ne 0 -or $payment.SelectedSubstitution -or @($windows.Entries|Where-Object AcceptedUseCount -GT 0).Count -gt 0) { return $false }
    } else { return $false }
    return $true
}
function Business-Proven($Requirement, $Node, $Receipt) {
    # 旧单字段合同只保留供迁移，绝不独立给出成功。三个角色必须在同一节点/同一命令边界全部成立。
    $clauses=@($Requirement.Clauses)
    if (($Requirement.EffectTypeId -and $Requirement.EffectTypeId -ne $Node.EffectTypeId) -or
        ($Receipt.ContentId -and $Receipt.ContentId -ne $Requirement.DefinitionId)) { return $false }
    if ($clauses.Count -lt 3 -or @('identity','result','state-change'|Where-Object { $_ -notin $clauses.Role }).Count -gt 0 -or
        @($clauses|Where-Object { $_.Role -eq 'result' -and $_.LeftScope -eq 'result' }).Count -eq 0 -or
        @($clauses|Where-Object { $_.Role -eq 'state-change' -and $_.LeftScope.EndsWith('_after') -and
            (($_.Operator -eq 'delta-equals' -and $_.BeforeScope.EndsWith('_before')) -or ($_.Operator -eq 'not-equals' -and $_.RightScope.EndsWith('_before'))) }).Count -eq 0) { return $false }
    if (-not $transactionIndex.ByNode.ContainsKey([string]$Node.EffectId)) { return $false }
    foreach ($location in $transactionIndex.ByNode[[string]$Node.EffectId]) {
        $transaction = [YC.Validation.HostArchiveEvidenceReader]::ReadLineAt($transactionPath, $location.Offset, $location.Length)
        if (-not (Transaction-Proven $transaction)) { continue }
        $before=$transaction.Before; $after=$transaction.After
        if ($null -eq $before -or $null -eq $after -or -not $transaction.CommandId -or
            $before.State.GameId -ne $state.GameId -or $after.State.GameId -ne $state.GameId -or
            $before.ContentHash -ne $plan.ContentPackHash -or $after.ContentHash -ne $plan.ContentPackHash -or
            $after.LastCommitSequence -le $before.LastCommitSequence) { continue }
        $span=@($after.State.EffectRuntime.Journal|Where-Object { $_.CommitSequence -gt $before.LastCommitSequence -and $_.CommitSequence -le $after.LastCommitSequence })
        if ($span.Count -ne $after.LastCommitSequence-$before.LastCommitSequence -or @($span|Where-Object { $_.CommandId -and $_.CommandId -ne $transaction.CommandId }).Count -gt 0 -or
            @($span|Where-Object CommandId -EQ $transaction.CommandId).Count -eq 0) { continue }
        $afterNode=@($after.State.EffectRuntime.EffectNodes|Where-Object { $_.EffectId -eq $Node.EffectId -and [string]$_.Status -in '4','Completed' })
        if ($afterNode.Count -ne 1 -or $afterNode[0].LastCommitSequence -le $before.LastCommitSequence -or $afterNode[0].LastCommitSequence -gt $after.LastCommitSequence -or
            (Canonical $afterNode[0].NormalizedResult) -ne (Canonical $Node.NormalizedResult) -or
            @($before.State.EffectRuntime.EffectNodes|Where-Object { $_.EffectId -eq $Node.EffectId -and [string]$_.Status -in '4','Completed' }).Count -gt 0) { continue }
        $result=Unwrap $afterNode[0].NormalizedResult
        $sourceId=if ($Receipt.ContentInstanceId) { $Receipt.ContentInstanceId } else { $Node.SourceId }
        $targetId=if ($Requirement.TargetResultPath) { Field $result $Requirement.TargetResultPath } else { '' }
        $context=@{node=$afterNode[0];receipt=$Receipt;result=$result;input=(Unwrap $afterNode[0].NormalizedArguments);before=$before.State;after=$after.State;
            player_before=@($before.State.Players|Where-Object PlayerId -EQ $Node.PlayerId)[0];player_after=@($after.State.Players|Where-Object PlayerId -EQ $Node.PlayerId)[0];
            source_before=@($before.State.ItemZones.Items|Where-Object ItemId -EQ $sourceId)[0];source_after=@($after.State.ItemZones.Items|Where-Object ItemId -EQ $sourceId)[0];
            target_before=@($before.State.ItemZones.Items|Where-Object ItemId -EQ $targetId)[0];target_after=@($after.State.ItemZones.Items|Where-Object ItemId -EQ $targetId)[0];
            payment_before=@($before.State.EffectRuntime.EffectNodes|Where-Object EffectId -EQ $Node.EffectId)[0].Payment;payment_after=$afterNode[0].Payment}
        if ($result -is [Collections.IDictionary] -and $result.resourceType -in 'Originium','OriginiumShard','Iron','PureOriginium','GoldVoucher') {
            $context.resource_before=$context.player_before.Resources[$result.resourceType];$context.resource_after=$context.player_after.Resources[$result.resourceType]
        }
        $all=$true
        foreach ($clause in $clauses) { if (-not (Joint-Clause $clause $context)) { $all=$false; break } }
        if ($all -and $Requirement.EvidenceKind -in 'payment-original','payment-substitution') { $all=Payment-Proven $context $Requirement.EvidenceKind }
        if ($all) { $script:businessProof=@{CommandId=$transaction.CommandId;BeforeCommit=$before.LastCommitSequence;AfterCommit=$after.LastCommitSequence;ClausesPassed=$clauses.Count}; return $true }
    }
    return $false
}
foreach ($definition in $catalog.definitions) {
    $requirements = @($plan.Coverage | Where-Object { $_.DefinitionId -eq $definition.DefinitionId })
    $rows = @(@{CoverageId='definition:' + $definition.DefinitionId; DefinitionId=$definition.DefinitionId; AbilityId=''; Branch='定义';Category='definition'})
    foreach ($ability in $definition.Abilities) { $rows += @{CoverageId='ability:' + $definition.DefinitionId + ':' + $ability.AbilityId; DefinitionId=$definition.DefinitionId; AbilityId=$ability.AbilityId; Branch='显式能力';Category='ability'} }
    if ($definition.ContentType -in 'character','facility','event','city_style','special_action') {
        $rows += @{CoverageId='native:' + $definition.DefinitionId; DefinitionId=$definition.DefinitionId; AbilityId=''; Branch='原生模板能力'; Native=$true;Category='template'}
    }
    foreach ($action in $catalog.nativeDefinitions.SpecialActions | Where-Object { $_.CityStyleId -eq $definition.RuntimeId -or $_.CityStyleId -eq $definition.DefinitionId }) {
        $rows += @{CoverageId='native-special-action:' + $action.SpecialActionId; DefinitionId=$definition.DefinitionId; AbilityId=''; Branch='原生样式特殊行动'; Native=$true;Category='special-action'}
    }
    # 关键分支由已冻结语义合同列入计划；未提供合同不能悄悄缩小分母。
    foreach ($requirement in $requirements) { if ($requirement.CoverageId -notin $rows.CoverageId) { $rows += $requirement } }
    foreach ($row in $rows) {
        $category=if ($row.Category) { $row.Category } else { 'key-branch' }
        $validators = @($requirements | Where-Object { (-not $row.AbilityId -or $_.AbilityId -eq $row.AbilityId) -and
            (-not $row.Native -or [string]::IsNullOrEmpty($_.AbilityId)) -and
            ($category -eq 'definition' -or $category -eq 'ability' -or $_.CoverageId -eq $row.CoverageId) })
        $matchingReceipts = @($runtime.DispatchReceipts | Where-Object { $_.ContentId -eq $definition.DefinitionId -and (-not $row.AbilityId -or $_.AbilityId -eq $row.AbilityId) })
        $matchedNodes = @($runtime.EffectNodes | Where-Object {
            $candidateNode = $_
            ($candidateNode.ContinuationContentId -eq $definition.DefinitionId -or
            @($state.ItemZones.Items | Where-Object { $_.ItemId -eq $candidateNode.SourceId -and $_.DefinitionId -eq $definition.DefinitionId }).Count -gt 0) -and
            (-not $row.AbilityId -or $candidateNode.ContinuationAbilityId -eq $row.AbilityId)
        })
        $passed = $false; $failed = $false; $linked = $false; $evidence = [Collections.Generic.List[object]]::new()
        foreach ($receipt in $matchingReceipts) {
            if (-not $receipt.Succeeded) { $failed = $true; continue }
            $linked = $true
            if (@($receipt.AttachedEffectIds).Count -eq 0) { continue }
            $allCompleted = $true
            foreach ($id in $receipt.AttachedEffectIds) { if (-not (Completed-Tree $id ([Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)))) { $allCompleted = $false } }
            if (-not $allCompleted) { continue }
            $businessNodes = @($receipt.AttachedEffectIds | ForEach-Object { Effect-TreeNodes $_ ([Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)) })
            # 替代来源正文与原付款宿主是不同节点；只有真实窗口/目标/来源合同共同关联才纳入原付款结果。
            foreach ($paymentOwner in $runtime.EffectNodes|Where-Object { $_.Payment -and $_.Payment.IsCommitted }) {
                $related=@($runtime.EffectNodes.SettlementWindow|Where-Object { $_.Context.PaymentId -eq $paymentOwner.Payment.PaymentId }|ForEach-Object Entries|Where-Object {
                    $_.SourceDefinitionId -eq $receipt.ContentId -and $_.AbilityId -eq $receipt.AbilityId -and $_.AcceptedUseCount -eq 1 -and $_.TargetEffectId -in $businessNodes.EffectId })
                if ($related.Count -eq 1 -and $paymentOwner.EffectId -notin $businessNodes.EffectId) { $businessNodes += $paymentOwner }
            }
            foreach ($node in $businessNodes) {
                foreach ($validator in $validators) {
                    if ($validator.EffectTypeId -and $node.EffectTypeId -ne $validator.EffectTypeId) { continue }
                    if (Business-Proven $validator $node $receipt) { $passed = $true; $evidence.Add(@{ReceiptId=$receipt.ReceiptId; EffectId=$node.EffectId; CommitSequence=$receipt.CommitSequence; CoverageId=$validator.CoverageId;JointProof=$script:businessProof}) }
                }
            }
        }
        # 原生模板无派发回执时仍须真实完成节点和同一业务变化；不能只看目录data。
        foreach ($node in $matchedNodes) {
            if ([string]$node.Status -in '5','6','Failed','Faulted') { $failed = $true }
            foreach ($validator in $validators) {
                if ($row.AbilityId -or $validator.AbilityId -or -not $validator.EffectTypeId -or $node.EffectTypeId -ne $validator.EffectTypeId -or
                    -not (Completed-Tree $node.EffectId ([Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)))) { continue }
                if (Business-Proven $validator $node @{CommitSequence=$node.LastCommitSequence}) { $passed = $true; $evidence.Add(@{EffectId=$node.EffectId; CoverageId=$validator.CoverageId; Native=$true;JointProof=$script:businessProof}) }
            }
        }
        $status = if ($passed) { '成功' } elseif ($failed) { '失败' } elseif ($linked -or $matchedNodes.Count -gt 0) { '缺业务结果证据' } else { '未发生' }
        if ($validators.Count -eq 0) { $status = '缺覆盖合同' }
        $coverage.Add(@{CoverageId=$row.CoverageId; DefinitionId=$definition.DefinitionId; AbilityId=$row.AbilityId; Branch=$row.Branch;Category=$category;
            Required=(@($requirements|Where-Object { $_.CoverageId -eq $row.CoverageId -and $_.Required }).Count -gt 0); Status=$status; Evidence=@($evidence)})
    }
}
if (@($plan.Coverage | Where-Object { $_.DefinitionId -notin $catalog.definitions.DefinitionId }).Count -gt 0) { $issues.Add('覆盖计划包含非活动定义') }
foreach ($action in $catalog.nativeDefinitions.SpecialActions) {
    if (@($catalog.definitions | Where-Object { $_.RuntimeId -eq $action.CityStyleId -or $_.DefinitionId -eq $action.CityStyleId }).Count -ne 1) { $issues.Add("原生特殊行动缺唯一活动来源定义：$($action.SpecialActionId)") }
}
function Test-PlayerProjection($view, [int]$playerId) {
    $script:projectionCount++
    if ($null -eq $view -or $view.GameId -ne $state.GameId -or [string]$view.ViewerRole -notin '1','Player' -or $view.ViewerPlayerId -ne ${playerId} -or
        @($view.Players | Where-Object { $_.PlayerId -ne ${playerId} -and (@($_.HandCardIds).Count -gt 0 -or $_.CoveredCharacterCardId) }).Count -gt 0) { $issues.Add("玩家${playerId}投影身份或私密手牌泄漏"); return }
    $secretZones = @($view.ItemZones.Zones | Where-Object { $_.Zone.TemplateId -eq 'core.zone.box_secret_retired' } | ForEach-Object { $_.Zone.ZoneId })
    $hidden = @($view.ItemZones.Zones | Where-Object { -not $_.IdentitiesVisible } | ForEach-Object { $_.Zone.ZoneId })
    if (@($view.ItemZones.Items | Where-Object { $_.ZoneId -in $hidden -and -not ($_.ZoneId -in $secretZones -and
        [string]$_.PhysicalKind -in '1','Card' -and [string]$_.ControllerSubject.Kind -in '1','Player' -and $_.ControllerSubject.PlayerId -eq ${playerId}) }).Count -gt 0 -or
        @($view.ItemZones.Orders | Where-Object { $_.ZoneId -in $hidden }).Count -gt 0) { $issues.Add("玩家${playerId}隐藏区域泄漏") }
    foreach ($item in $view.ItemZones.Items | Where-Object { $_.ZoneId -in $secretZones }) {
        if ([string]$item.ControllerSubject.Kind -notin '1','Player' -or $item.ControllerSubject.PlayerId -ne ${playerId}) { $issues.Add("玩家${playerId}看到他人秘密裁牌") }
        else { $ownerSecretEvidence[[string]${playerId}] = $true }
    }
}
$projectionCount = 0; $ownerSecretEvidence = @{}; $publicScoring = @()
foreach (${playerId} in 1..$plan.PlayerCount) {
    $directory = $playerDirectoryById[[int]${playerId}]
    $resultPath = Join-Path $directory 'result.json'; $finalPath = Join-Path $directory 'final-view.json'
    if (-not (Test-Path -LiteralPath $resultPath) -or -not (Test-Path -LiteralPath $finalPath)) { $issues.Add("玩家${playerId}缺少结果或终局投影"); continue }
    $result = Read-Json $resultPath; $final = Read-Json $finalPath
    if (-not $result.success -or (Canonical @($result.rounds)) -ne '[1,2,3,4,5,6,7,8]') { $issues.Add("玩家${playerId}未完成八回合") }
    if ($final.GameId -ne $state.GameId -or $final.StateRevision -ne $runtime.StateRevision -or $final.ContentPackHash -ne $plan.ContentPackHash -or
        (Canonical $final.RuleConfiguration) -ne (Canonical $state.RuleConfiguration) -or -not $final.FinalScoring.IsResolved) { $issues.Add("玩家${playerId}终局与Host不一致") }
    foreach ($score in $final.FinalScoring.PlayerScores) {
        $authority = @($state.FinalScoring.PlayerScores | Where-Object { $_.PlayerId -eq $score.PlayerId })
        if ($authority.Count -ne 1 -or ($score.PlayerId -eq ${playerId} -and (Canonical $score.RemainingResources) -ne (Canonical $authority[0].RemainingResources)) -or
            ($score.PlayerId -ne ${playerId} -and $null -ne $score.RemainingResources)) { $issues.Add("玩家${playerId}终局资源可见性错误") }
        $score.RemainingResources = $null
    }
    $publicScoring += Canonical $final.FinalScoring
    Test-PlayerProjection $final ${playerId}
    foreach ($name in 'projections.jsonl','confirmed.jsonl','route-command-trace.jsonl','rejected.jsonl') {
        $path = Join-Path $directory $name
        if (-not (Test-Path -LiteralPath $path)) { if ($name -in 'projections.jsonl','confirmed.jsonl') { $issues.Add("玩家${playerId}缺少$name") }; continue }
        foreach ($record in [YC.Validation.HostArchiveEvidenceReader]::ReadLines((Assert-EvidencePath $path))) { Test-PlayerProjection $record.Value.view ${playerId} }
    }
    foreach ($file in Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -like 'initial-view-*.json' -or $_.Name -like 'view-*.json' -or $_.Name -in 'before-reconnect-view.json','after-reconnect-view.json','failure-view.json' }) { Test-PlayerProjection (Read-Json $file.FullName) ${playerId} }
    if ($plan.EnabledExpansionIds -contains 'enterprise' -and -not $ownerSecretEvidence.ContainsKey([string]${playerId})) { $issues.Add("玩家${playerId}缺少本人秘密裁牌正向显示证据") }
    if ($runInput.InputMode -eq 'internal') {
        foreach ($step in $plan.Steps | Where-Object { $_.PlayerId -eq ${playerId} }) { if ($step.StepId -notin $result.completedSteps) { $issues.Add("路线步骤未确认：$($step.StepId)") } }
    }
}
foreach ($score in $state.FinalScoring.PlayerScores) { $score.RemainingResources = $null }
if (@($publicScoring | Select-Object -Unique).Count -ne 1 -or $publicScoring[0] -ne (Canonical $state.FinalScoring)) { $issues.Add('公开计分、排名或胜者不一致') }
# 断连参数由启动器赋给启动序号2，真实玩家映射不能改变这个取证对象。
$reconnectPath = Join-Path $OutputDirectory 'player-2/result.json'
if ($runInput.InputMode -eq 'internal' -and (Test-Path -LiteralPath $reconnectPath)) {
    $reconnect = Read-Json $reconnectPath
    if (-not $reconnect.disconnected -or -not $reconnect.reconnected) { $issues.Add('没有客户端真实断连恢复证据') }
}
$recoveryEvidence = [Collections.Generic.List[object]]::new()
if ($runInput.InputMode -eq 'internal' -and (-not $plan.RecoveryCheckpoints -or @($plan.RecoveryCheckpoints | Where-Object { $null -ne $_ }).Count -eq 0)) { $issues.Add('缺冻结的Host恢复检查点合同') }
foreach ($checkpoint in $plan.RecoveryCheckpoints) {
    if ($checkpoint.CheckpointId -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,63}$' -or $checkpoint.PlayerId -ne 1) { $issues.Add('恢复检查点必须指向本启动器真实Host玩家1及明确目录身份'); continue }
    $recoveryDirectory = Join-Path $OutputDirectory ('player-1/host-recovery/' + $checkpoint.CheckpointId)
    $recoveryBeforePath = Join-Path $recoveryDirectory 'before-host-recovery.json'
    $recoveryRestoredPath = Join-Path $recoveryDirectory 'restored-host-archive.json'
    $recoveryResultPath = Join-Path $recoveryDirectory 'host-recovery-result.json'
    if (-not (Test-Path -LiteralPath $recoveryBeforePath) -or -not (Test-Path -LiteralPath $recoveryRestoredPath) -or -not (Test-Path -LiteralPath $recoveryResultPath)) {
        if ($checkpoint.OptionalIfUnavailable) { $recoveryEvidence.Add(@{CheckpointId=$checkpoint.CheckpointId;Success=$false;Status='自然路线未出现，按实际缺口安排必要补局';RecoveryKind='same_process_checkpoint'}); continue }
        $issues.Add("尚缺Host暂停窗口保存、恢复后继续结算与去重的运行证据：$($checkpoint.CheckpointId)"); continue
    }
    $beforeRecovery = Read-Json $recoveryBeforePath; $restoredRecovery = Read-Json $recoveryRestoredPath; $recoveryResult = Read-Json $recoveryResultPath
    $beforeState = $beforeRecovery.Snapshot.State; $restoredState = $restoredRecovery.Snapshot.State
    $pausedRequests = @($beforeState.EffectRuntime.InteractionRequests | Where-Object { $_.Status -eq 'open' -and $_.AnsweringPlayerId -eq $checkpoint.PlayerId -and
        $_.InteractionTypeId -eq $checkpoint.RequestKind -and (-not $checkpoint.PromptKey -or $_.PromptKey -eq $checkpoint.PromptKey) -and
        ($_.InteractionId -eq $recoveryResult.InteractionId -or (-not $_.InteractionId -and $_.RequestId -eq $recoveryResult.InteractionId)) })
    $valid = $true
    $hostResultPath = Join-Path $OutputDirectory 'player-1/result.json'
    if (-not (Test-Path -LiteralPath $hostResultPath)) { $issues.Add('Host恢复缺实际驱动结果及进程身份'); continue }
    $hostResult = Read-Json $hostResultPath
    $continuedRequest = @($runtime.InteractionRequests | Where-Object { $_.InteractionId -eq $recoveryResult.InteractionId -or (-not $_.InteractionId -and $_.RequestId -eq $recoveryResult.InteractionId) })
    $completedOwner = @(if ($pausedRequests.Count -eq 1) { $runtime.EffectNodes | Where-Object { $_.EffectId -eq $pausedRequests[0].OwnerEffectId -and [string]$_.Status -in '4','Completed' } })
    if (-not $recoveryResult.Succeeded -or $recoveryResult.RuntimeMethod -ne 'GameSession.RecoverHost' -or
        $recoveryResult.InputSource -ne 'GameSession.CreateHostArchive' -or $recoveryResult.RecoveryKind -ne 'same_process_checkpoint' -or -not $recoveryResult.PersistedBeforeRecover -or
        $recoveryResult.CheckpointId -ne $checkpoint.CheckpointId -or $recoveryResult.GameId -ne $state.GameId -or $recoveryResult.ContentPackHash -ne $plan.ContentPackHash -or
        $recoveryResult.PlayerId -ne $checkpoint.PlayerId -or $pausedRequests.Count -ne 1 -or $beforeState.Round -ne $checkpoint.Round -or
        $recoveryResult.processId -ne $hostResult.processId -or $checkpoint.CheckpointId -notin $hostResult.recoveredCheckpoints -or
        $continuedRequest.Count -ne 1 -or $continuedRequest[0].Status -eq 'open' -or $completedOwner.Count -ne 1 -or
        $recoveryResult.BeforeArchiveSha256 -ne (Get-FileHash -LiteralPath $recoveryBeforePath -Algorithm SHA256).Hash -or
        $recoveryResult.RestoredArchiveSha256 -ne (Get-FileHash -LiteralPath $recoveryRestoredPath -Algorithm SHA256).Hash -or
        $beforeRecovery.Snapshot.ContentHash -ne $plan.ContentPackHash -or $restoredRecovery.Snapshot.ContentHash -ne $plan.ContentPackHash -or
        $beforeState.GameId -ne $state.GameId -or (Canonical $beforeState) -ne (Canonical $restoredState) -or
        $runtime.StateRevision -le $restoredState.EffectRuntime.StateRevision) { $valid = $false; $issues.Add("Host恢复证据没有保留同一暂停状态或没有继续推进：$($checkpoint.CheckpointId)") }
    $phases = @('Setup','Entrance','RoundStart','CharacterCover','ActionRound1','ActionRound2','ResourceCollection','Cleanup','FinalScoring','GameOver')
    if ([string]$beforeState.Phase -ne $checkpoint.Phase -and [string]$beforeState.Phase -ne [string][Array]::IndexOf($phases, [string]$checkpoint.Phase)) { $valid = $false; $issues.Add('Host恢复的实际阶段与检查点不一致') }
    if ($checkpoint.RequireSettlementWindow -or $checkpoint.SettlementLocalId) {
        $windows = @($beforeState.EffectRuntime.EffectNodes | ForEach-Object { $_.SettlementWindow } | Where-Object { $_ -and -not $_.IsClosed -and $_.Context.Id -eq $recoveryResult.WindowId -and $_.Context.PlayerId -eq $checkpoint.PlayerId })
        if ($windows.Count -ne 1 -or ($checkpoint.SettlementLocalId -and @($windows[0].Entries | Where-Object { $_.LocalId -eq $checkpoint.SettlementLocalId }).Count -ne 1)) { $valid = $false; $issues.Add('Host恢复缺同一真实窗口/结算项身份') }
    }
    foreach ($paymentId in $recoveryResult.PaymentIds) {
        $payments = @($beforeState.EffectRuntime.EffectNodes | Where-Object { $_.Payment.PaymentId -eq $paymentId -and $_.Payment.PlayerId -eq $checkpoint.PlayerId -and -not $_.Payment.IsCommitted })
        $finalPayments = @($runtime.EffectNodes | Where-Object { $_.Payment.PaymentId -eq $paymentId })
        if ($payments.Count -ne 1 -or $finalPayments.Count -ne 1 -or ($checkpoint.RequirePayment -and -not $finalPayments[0].Payment.IsCommitted)) { $valid = $false; $issues.Add('Host恢复付款身份丢失、重复或未继续提交'); continue }
        $cursor = $pausedRequests[0].OwnerEffectId; $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        while ($cursor -and $cursor -ne $payments[0].EffectId -and $visited.Add($cursor)) {
            $ancestor = @($beforeState.EffectRuntime.EffectNodes | Where-Object { $_.EffectId -eq $cursor })
            $cursor = if ($ancestor.Count -eq 1) { $ancestor[0].ParentEffectId } else { '' }
        }
        if ($cursor -ne $payments[0].EffectId) { $valid = $false; $issues.Add('Host恢复付款与暂停请求不属于同一付款树') }
    }
    if ($checkpoint.RequirePayment -and @($recoveryResult.PaymentIds).Count -eq 0) { $valid = $false; $issues.Add('Host恢复缺要求的实际Payment身份') }
    foreach ($receipt in $beforeRecovery.Snapshot.State.EffectRuntime.DispatchReceipts) {
        $preserved = @($runtime.DispatchReceipts | Where-Object { $_.ReceiptId -eq $receipt.ReceiptId })
        if ($preserved.Count -ne 1 -or (Canonical $preserved[0]) -ne (Canonical $receipt)) { $valid = $false; $issues.Add('Host恢复后原派发回执丢失、改变或重复') }
    }
    $recoveryEvidence.Add(@{CheckpointId=$checkpoint.CheckpointId; InteractionId=$recoveryResult.InteractionId; WindowId=$recoveryResult.WindowId; PaymentIds=@($recoveryResult.PaymentIds); Success=$valid; RecoveryKind=$recoveryResult.RecoveryKind})
}
$endActionEvidence = [Collections.Generic.List[object]]::new()
foreach ($case in $plan.EndActionCases) {
    $tracePath = Join-Path $playerDirectoryById[[int]$case.PlayerId] 'route-command-trace.jsonl'
    $traces = @()
    if ($runInput.InputMode -eq 'external') {
        foreach ($event in $nativeInputEvents | Where-Object { $_.StepId -eq $case.StepId -and $_.PlayerId -eq $case.PlayerId -and $_.CommandSubmitted }) {
            $outcomeFile = if ($event.Outcome -eq 'rejected') { 'rejected.jsonl' } else { 'confirmed.jsonl' }
            $outcomePath = Join-Path $playerDirectoryById[[int]$case.PlayerId] $outcomeFile
            if (-not (Test-Path -LiteralPath $outcomePath)) { continue }
            foreach ($outcome in Read-CommandOutcomes $outcomePath) {
                $command = if ($event.Outcome -eq 'rejected') { $outcome.rejected.Command } else { $outcome.command }
                if ($command.CommandId -eq $event.CommandId) { $traces += @{stepId=$case.StepId;command=$command;StateRevision=$event.StateRevisionBefore} }
            }
        }
    } elseif (Test-Path -LiteralPath $tracePath) {
        $traces = @(Read-JsonLines $tracePath | Where-Object { $_.stepId -eq $case.StepId } | ForEach-Object { @{stepId=$_.stepId;command=$_.command;StateRevision=$_.StateRevision} })
    }
    if ($traces.Count -eq 0 -and $case.OptionalIfUnavailable) { $endActionEvidence.Add(@{CaseId=$case.CaseId;Kind=$case.Kind;Success=$false;Status='自然路线未出现，需必要补局'}); continue }
    if ($traces.Count -ne 1) { $issues.Add("结束语义命令身份不唯一：$($case.CaseId)"); continue }
    $trace = $traces[0]
    $authority=Find-AuthorityTransactions ([string]$trace.command.CommandId)
    if ($case.Kind -eq 'mandatory-idle-rejected') {
        $authorityPath=Join-Path $OutputDirectory 'player-1/authoritative-rejections.jsonl'
        $count=0; $entry=$null
        if(Test-Path -LiteralPath $authorityPath) {
            foreach($record in [YC.Validation.HostArchiveEvidenceReader]::ReadLines((Assert-EvidencePath $authorityPath))) {
                $value=$record.Value
                if($value.CommandId -eq $trace.command.CommandId -and $value.Before -and $value.After -and $value.Before.State.GameId -eq $state.GameId -and
                    $value.Before.ContentHash -eq $plan.ContentPackHash -and $value.After.ContentHash -eq $plan.ContentPackHash -and
                    (Canonical $value.Before.State) -eq (Canonical $value.After.State) -and $value.errorCodeName -eq 'SettlementRequiredPending') {
                    $count++; $entry=if($count -eq 1){$value}else{$null}
                }
            }
        }
        $authority=@{Count=$count;Entry=$entry}
    }
    $beforeEntry=@(if($authority.Count -eq 1 -and $authority.Entry.Before.State.EffectRuntime.StateRevision -eq $trace.StateRevision) { @{StateAfter=$authority.Entry.Before.State} })
    $afterEntry=@(if($authority.Count -eq 1 -and $case.Kind -ne 'mandatory-idle-rejected') { @{StateAfter=$authority.Entry.After.State} })
    if ($beforeEntry.Count -ne 1) { $issues.Add("结束语义缺前态：$($case.CaseId)"); continue }
    $beforeState = $beforeEntry[0].StateAfter
    $windows = @($beforeState.EffectRuntime.EffectNodes | ForEach-Object { $_.SettlementWindow } | Where-Object { $_.Context.PlayerId -eq $case.PlayerId -and $_.Context.Kind -eq 'player_cleanup' -and -not $_.IsClosed })
    if ($windows.Count -ne 1) { $issues.Add("结束语义不是同一真实收尾窗口：$($case.CaseId)"); continue }
    $window = $windows[0]; $passed = $false
    if ($case.Kind -eq 'mandatory-idle-rejected') {
        $rejectedPath = Join-Path $playerDirectoryById[[int]$case.PlayerId] 'rejected.jsonl'
        $rejected = @(if (Test-Path -LiteralPath $rejectedPath) { Read-CommandOutcomes $rejectedPath | Where-Object { $_.rejected.Command.CommandId -eq $trace.command.CommandId } })
        $passed = -not $window.ActiveTargetEffectId -and @($window.Entries | Where-Object { $_.SettlementState -eq 'available' -and [string]$_.WholeEffectPolicy -in '2','Required' }).Count -gt 0 -and
            $rejected.Count -eq 1 -and $rejected[0].errorCodeName -eq 'SettlementRequiredPending' -and $rejected[0].observedRevision -eq $trace.StateRevision -and $afterEntry.Count -eq 0
    } elseif ($afterEntry.Count -eq 1) {
        $afterState = $afterEntry[0].StateAfter
        $afterWindow = @($afterState.EffectRuntime.EffectNodes | ForEach-Object { $_.SettlementWindow } | Where-Object { $_.Context.Id -eq $window.Context.Id })
        if ($afterWindow.Count -eq 1 -and $case.Kind -eq 'active-node-only') {
            $active = @($window.Entries | Where-Object { $_.TargetEffectId -eq $window.ActiveTargetEffectId -and $_.SettlementState -eq 'executing' })
            $others = @($window.Entries | Where-Object { $_.TargetEffectId -ne $window.ActiveTargetEffectId })
            $sameOthers = $others.Count -gt 0
            foreach ($other in $others) {
                $remaining = @($afterWindow[0].Entries | Where-Object { $_.OptionId -eq $other.OptionId })
                if ($remaining.Count -ne 1 -or (Canonical $other) -ne (Canonical $remaining[0])) { $sameOthers = $false }
            }
            $interactionId = $trace.command.Parameters.interactionId
            $currentRequest = @($beforeState.EffectRuntime.InteractionRequests | Where-Object {
                ($_.InteractionId -eq $interactionId -or $_.RequestId -eq $interactionId) -and $_.Status -eq 'open' -and $_.AnsweringPlayerId -eq $case.PlayerId -and $_.AllowDecline })
            $stoppedNode = @(if ($currentRequest.Count -eq 1) { $afterState.EffectRuntime.EffectNodes | Where-Object { $_.EffectId -eq $currentRequest[0].OwnerEffectId -and [string]$_.Status -in '4','Completed' } })
            $withinActive = $false
            if ($currentRequest.Count -eq 1) {
                $cursor = $currentRequest[0].OwnerEffectId; $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                while ($cursor -and $visited.Add($cursor)) {
                    if ($cursor -eq $window.ActiveTargetEffectId) { $withinActive = $true; break }
                    $ancestor = @($beforeState.EffectRuntime.EffectNodes | Where-Object { $_.EffectId -eq $cursor })
                    $cursor = if ($ancestor.Count -eq 1) { $ancestor[0].ParentEffectId } else { '' }
                }
            }
            $passed = $active.Count -eq 1 -and $stoppedNode.Count -eq 1 -and $withinActive -and $sameOthers -and -not $afterWindow[0].IsClosed -and
                $trace.command.Parameters.currentNodeEffectId -eq $currentRequest[0].OwnerEffectId -and $trace.command.Parameters.windowId -eq $window.Context.Id
        } elseif ($afterWindow.Count -eq 1 -and $case.Kind -eq 'optional-window-declined') {
            $optional = @($window.Entries | Where-Object { $_.SettlementState -eq 'available' -and [string]$_.WholeEffectPolicy -in '1','Optional' })
            $required = @($window.Entries | Where-Object { [string]$_.WholeEffectPolicy -in '2','Required' -and $_.SettlementState -notin 'resolved','declined' })
            $allDeclined = $true
            foreach ($entry in $optional) { if (@($afterWindow[0].Entries | Where-Object { $_.OptionId -eq $entry.OptionId -and $_.TargetEffectId -eq $entry.TargetEffectId -and $_.SettlementState -eq 'declined' }).Count -ne 1) { $allDeclined = $false } }
            $passed = -not $window.ActiveTargetEffectId -and $required.Count -eq 0 -and $optional.Count -gt 0 -and $afterWindow[0].IsClosed -and $allDeclined
        }
        # 已提交节点结果不可因结束行动回滚。
        foreach ($completedNode in $beforeState.EffectRuntime.EffectNodes | Where-Object { [string]$_.Status -in '4','Completed' }) {
            $preserved = @($afterState.EffectRuntime.EffectNodes | Where-Object { $_.EffectId -eq $completedNode.EffectId -and [string]$_.Status -in '4','Completed' })
            if ($preserved.Count -ne 1 -or (Canonical $completedNode.NormalizedResult) -ne (Canonical $preserved[0].NormalizedResult)) { $passed = $false }
        }
    }
    $endActionEvidence.Add(@{CaseId=$case.CaseId; Kind=$case.Kind; CommandId=$trace.command.CommandId; WindowId=$window.Context.Id; Success=$passed})
    if (-not $passed) { $issues.Add("结束语义未满足：$($case.CaseId)") }
}
$layoutEvidence = @()
if ($runInput.InputMode -eq 'external' -and $plan.EnabledExpansionIds -contains 'enterprise') {
    $layoutPath = Join-Path $OutputDirectory 'ui-layout-evidence.jsonl'
    if (-not (Test-Path -LiteralPath $layoutPath)) { $issues.Add('缺按钮/动态提示布局互斥的真实屏幕与原生输入证据') }
    else {
        $layoutEvidence = @(Read-JsonLines $layoutPath)
        foreach ($requiredState in 'enabled','pending','disabled','no-buttons') {
            $observations = @($layoutEvidence | Where-Object { $_.State -eq $requiredState -and $_.GameId -eq $state.GameId -and $_.InputMethod -eq 'native' })
            if ($observations.Count -eq 0) { $issues.Add("缺布局状态证据：$requiredState"); continue }
            foreach ($observation in $observations) {
                $relative = [string]$observation.Screenshot
                $screenshot = [IO.Path]::GetFullPath((Join-Path $OutputDirectory $relative))
                if ([IO.Path]::IsPathRooted($relative) -or -not $screenshot.StartsWith($OutputDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '布局证据截图只能引用本次运行目录内的相对路径。' }
                $valid = if ($requiredState -eq 'no-buttons') { $observation.VisibleButtonCount -eq 0 -and -not $observation.ButtonLayoutActive -and $observation.PromptLayoutActive -and $observation.PromptIsCurrent }
                    else { $observation.VisibleButtonCount -gt 0 -and $observation.ButtonLayoutActive -and -not $observation.PromptLayoutActive }
                $linkedInput = @($nativeInputEvents | Where-Object { $_.InputTraceId -eq $observation.InputTraceId -and $_.GameId -eq $state.GameId -and $_.WindowId -eq $observation.WindowId })
                if (-not $valid -or -not (Test-Path -LiteralPath $screenshot -PathType Leaf) -or $linkedInput.Count -ne 1) { $issues.Add("布局互斥或截图/输入链缺证据：$requiredState") }
            }
        }
    }
}
$gaps = @($coverage | Where-Object { $_.Status -ne '成功' })
$requiredGaps=@($gaps|Where-Object Required)
$categoryCoverage=@(foreach ($category in 'definition','ability','template','special-action','key-branch') {
    $rows=@($coverage|Where-Object Category -EQ $category)
    @{Category=$category;Denominator=$rows.Count;Succeeded=@($rows|Where-Object Status -EQ '成功').Count;Uncovered=@($rows|Where-Object Status -NE '成功')}
})
$report = @{SchemaVersion=2; Success=($issues.Count -eq 0 -and $requiredGaps.Count -eq 0); CoverageComplete=($gaps.Count -eq 0); Status='审计完成'; Issues=@($issues | Select-Object -Unique);
    ContentPackHash=$plan.ContentPackHash; InputMode=$runInput.InputMode; MapId=$plan.MapId; PlayerCount=$plan.PlayerCount;
    ActiveDefinitionCount=@($catalog.definitions).Count; ExplicitAbilityCount=@($catalog.definitions | ForEach-Object { $_.Abilities }).Count;
    EvidenceRowCount=$coverage.Count; UniqueOverallCoverageRate=$null;CategoryCoverage=$categoryCoverage;
    UniqueActiveDefinitionCount=@($catalog.definitions.DefinitionId|Select-Object -Unique).Count;
    ProvenDefinitionIds=@($coverage|Where-Object { $_.Category -eq 'definition' -and $_.Status -eq '成功' }|ForEach-Object DefinitionId|Select-Object -Unique);
    SuccessfulEvidenceRows=@($coverage | Where-Object { $_.Status -eq '成功' }).Count;RequiredGaps=$requiredGaps; ProjectionCount=$projectionCount;
    OwnerSecretEvidence=$ownerSecretEvidence; Coverage=@($coverage); Gaps=$gaps; EndActionEvidence=@($endActionEvidence); RecoveryEvidence=@($recoveryEvidence); LayoutEvidence=$layoutEvidence; NativeInputEvents=$nativeInputEvents; NativeDefinitions=$catalog.nativeDefinitions}
$report | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'audit.json') -Encoding utf8
Write-Host "审计完成：定义、能力、模板、特殊行动、关键分支分项记录；$($report.SuccessfulEvidenceRows)条联合业务证明，$($report.Issues.Count)项运行证据缺口，$($requiredGaps.Count)项必测缺口。"
if (-not $report.Success) { exit 1 }
exit 0


