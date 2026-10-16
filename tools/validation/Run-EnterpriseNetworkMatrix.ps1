param(
    [string]$OutputDirectory,
    [string]$ExecutablePath = 'G:\NoMadCity\Builds\LocalhostDevelopment\tuohuang.exe'
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($OutputDirectory)) {
    $OutputDirectory = Join-Path 'G:\NoMadCity\Logs\ExpansionValidation\EnterpriseExpansionIntegration\network' ('matrix-' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$matrix = [System.Collections.Generic.List[object]]::new()
# 先检查含秘密选牌与断线恢复的企业双人局，尽早发现真实网络链路问题。
$cases = @(
    @{ Expansion='enterprise'; Players=2 }, @{ Expansion='core'; Players=2 },
    @{ Expansion='core'; Players=3 }, @{ Expansion='enterprise'; Players=3 },
    @{ Expansion='core'; Players=4 }, @{ Expansion='enterprise'; Players=4 }
)
foreach ($case in $cases) {
        $expansion = $case.Expansion
        $players = $case.Players
        $caseOutput = Join-Path $OutputDirectory ($expansion + '-' + $players)
        & (Join-Path $PSScriptRoot 'Run-EnterpriseNetworkValidation.ps1') -PlayerCount $players -Expansion $expansion -ExecutablePath $ExecutablePath -OutputDirectory $caseOutput -TimeoutSeconds 3600
        $caseExit = $LASTEXITCODE
        $matrix.Add([pscustomobject]@{ Expansion=$expansion; PlayerCount=$players; ExitCode=$caseExit; Evidence=$caseOutput; CompletedUtc=[DateTime]::UtcNow })
        $matrix | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'matrix.json') -Encoding utf8
        if ($caseExit -ne 0) { Write-Error "本组失败，保留证据并停止后续矩阵：$caseOutput"; exit 1 }
}
Write-Host ('六组实际网络矩阵已完成：' + $OutputDirectory)
exit 0
