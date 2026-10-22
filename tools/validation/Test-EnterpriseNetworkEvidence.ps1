param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateSet(2, 3, 4)][int]$PlayerCount,
    [ValidateSet('core', 'enterprise')][string]$Expansion,
    [string]$ReportFileName = 'consistency.json'
)
$ErrorActionPreference = 'Stop'
$results = @(Get-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Raw | ConvertFrom-Json)
$finalViews = @(1..$PlayerCount | ForEach-Object {
    Get-Content -LiteralPath (Join-Path $OutputDirectory ('player-' + $_ + '/final-view.json')) -Raw | ConvertFrom-Json
})
$archive = Get-Content -LiteralPath (Join-Path $OutputDirectory 'player-1/final-host-archive.json') -Raw | ConvertFrom-Json -Depth 100
$authoritativeScores = $archive.Snapshot.State.FinalScoring
$configuration = $archive.Snapshot.State.RuleConfiguration | ConvertTo-Json -Depth 20 -Compress
$privacyValid = $true
$scores = @($finalViews | ForEach-Object {
    $view = $_
    foreach ($score in $view.FinalScoring.PlayerScores) {
        $authority = @($authoritativeScores.PlayerScores | Where-Object { $_.PlayerId -eq $score.PlayerId })
        if ($authority.Count -ne 1) { $privacyValid = $false; continue }
        if ($score.PlayerId -eq $view.ViewerPlayerId) {
            if (($score.RemainingResources | ConvertTo-Json -Compress) -ne ($authority[0].RemainingResources | ConvertTo-Json -Compress)) { $privacyValid = $false }
        } elseif ($null -ne $score.RemainingResources) { $privacyValid = $false }
        # 每位玩家只收到本人的资源明细；公开分项、排名和胜者必须与权威终局一致。
        $score.RemainingResources = $null
    }
    $view.FinalScoring | ConvertTo-Json -Depth 30 -Compress
} | Select-Object -Unique)
foreach ($score in $authoritativeScores.PlayerScores) { $score.RemainingResources = $null }
$publicAuthority = $authoritativeScores | ConvertTo-Json -Depth 30 -Compress
$expectedExpansions = if ($Expansion -eq 'enterprise') { 'core,enterprise' } else { 'core' }
$expectedMap = if ($PlayerCount -eq 4) { 'map-four-players' } else { 'map-three-players' }
$inputMismatch = @($finalViews | Where-Object {
    $_.Players.Count -ne $PlayerCount -or $_.RuleConfiguration.PlayerCount -ne $PlayerCount -or
    $_.RuleConfiguration.MapId -ne $expectedMap -or $_.RuleConfiguration.RuleModeId -ne 'standard' -or
    (@($_.RuleConfiguration.EnabledExpansionIds | Sort-Object) -join ',') -ne $expectedExpansions -or
    $_.Round -ne 8 -or -not $_.FinalScoring.IsResolved -or
    ($_.RuleConfiguration | ConvertTo-Json -Depth 20 -Compress) -ne $configuration -or
    $_.GameId -ne $archive.Snapshot.State.GameId -or $_.StateRevision -ne $archive.Snapshot.State.EffectRuntime.StateRevision
})
$processesSucceeded = $results.Count -eq $PlayerCount -and @($results | Where-Object { -not $_.Result.success -or $_.ExitCode -ne 0 }).Count -eq 0
$publicScoresMatch = $scores.Count -eq 1 -and $scores[0] -eq $publicAuthority
$projectionCount = 0
$privacyViolations = [System.Collections.Generic.List[string]]::new()
foreach ($playerId in 1..$PlayerCount) {
    $playerDirectory = Join-Path $OutputDirectory ('player-' + $playerId)
    $projections = Get-ChildItem -LiteralPath $playerDirectory -File | Where-Object {
        $_.Name -like 'initial-view-*.json' -or $_.Name -like 'view-*.json' -or
        $_.Name -in @('before-reconnect-view.json', 'after-reconnect-view.json', 'final-view.json')
    }
    foreach ($file in $projections) {
        $projection = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -Depth 100
        $projectionCount++
        $opponentCards = @($projection.Players | Where-Object {
            $_.PlayerId -ne $playerId -and ($_.HandCardIds.Count -ne 0 -or -not [string]::IsNullOrEmpty($_.CoveredCharacterCardId))
        })
        $hiddenZones = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($zone in $projection.ItemZones.Zones) {
            if (-not $zone.IdentitiesVisible) { [void]$hiddenZones.Add($zone.Zone.ZoneId) }
        }
        $hiddenItems = @($projection.ItemZones.Items | Where-Object { $hiddenZones.Contains($_.ZoneId) })
        $hiddenOrders = @($projection.ItemZones.Orders | Where-Object { $hiddenZones.Contains($_.ZoneId) })
        if ($projection.ViewerPlayerId -ne $playerId -or $opponentCards.Count -gt 0 -or $hiddenItems.Count -gt 0 -or $hiddenOrders.Count -gt 0) {
            $privacyViolations.Add($file.FullName)
        }
    }
}
$consistency = [pscustomobject]@{
    Success = $processesSucceeded -and $inputMismatch.Count -eq 0 -and $privacyValid -and $publicScoresMatch -and $privacyViolations.Count -eq 0 -and @($finalViews.ContentPackHash | Select-Object -Unique).Count -eq 1
    PlayerCount = $PlayerCount
    Expansion = $Expansion
    ProcessesSucceeded = $processesSucceeded
    ConfigurationMatchesInputAndArchive = ($inputMismatch.Count -eq 0)
    ContentHashes = @($finalViews.ContentPackHash | Select-Object -Unique)
    PrivateResourcesMatchOwnerOnly = $privacyValid
    PublicFinalScoringMatchesArchive = $publicScoresMatch
    PublicFinalScoringVariants = $scores.Count
    SavedProjectionCount = $projectionCount
    SavedProjectionsPreservePrivacy = ($privacyViolations.Count -eq 0)
    PrivacyViolations = @($privacyViolations)
}
$consistency | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory $ReportFileName) -Encoding utf8
$consistency
if (-not $consistency.Success) { throw '真实网络证据的进程结果、固定配置、公开计分或私密资源投影未通过核验。' }
