param(
    [ValidateSet(2, 3, 4)][int]$PlayerCount = 3,
    [ValidateSet('core', 'enterprise')][string]$Expansion = 'enterprise',
    [string]$ExecutablePath = 'G:\NoMadCity\Builds\LocalhostDevelopment\tuohuang.exe',
    [string]$OutputDirectory,
    [int]$TimeoutSeconds = 1800
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw "未找到构建：$ExecutablePath" }
if ([string]::IsNullOrEmpty($OutputDirectory)) {
    $OutputDirectory = Join-Path 'G:\NoMadCity\Logs\ExpansionValidation\EnterpriseExpansionIntegration\network' ((Get-Date -Format 'yyyyMMdd_HHmmss') + '-' + $Expansion + '-' + $PlayerCount)
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$roomFile = Join-Path $OutputDirectory 'room.txt'
if (Test-Path -LiteralPath $roomFile) { throw '输出目录已有房间证据，请为新运行使用新目录。' }
$managedDirectory = Join-Path (Split-Path -Parent $ExecutablePath) 'tuohuang_Data\Managed'
$managedHashes = Get-ChildItem -LiteralPath $managedDirectory -Filter '*.dll' | ForEach-Object { [pscustomobject]@{Name=$_.Name;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} }
[pscustomobject]@{ ExecutablePath=$ExecutablePath; Sha256=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash; ManagedDlls=$managedHashes; PlayerCount=$PlayerCount; Expansion=$Expansion; StartedUtc=[DateTime]::UtcNow } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-and-input.json') -Encoding utf8
$startedPlayers = [System.Collections.Generic.List[object]]::new()
function Start-ValidationPlayer([int]$PlayerId, [string]$RoomId) {
    $playerOutput = Join-Path $OutputDirectory ('player-' + $PlayerId)
    New-Item -ItemType Directory -Path $playerOutput -Force | Out-Null
    $arguments = @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720',
        '-logFile', ('"' + (Join-Path $playerOutput 'player.log') + '"'),
        '--yc-local-multiplayer-test', ('--yc-local-player-name=验收玩家' + $PlayerId),
        ('"--yc-network-validation-output=' + $playerOutput + '"'),
        ('--yc-network-validation-timeout=' + $TimeoutSeconds),
        ('--yc-network-validation-expansion=' + $Expansion))
    if ($PlayerId -eq 1) {
        $arguments += ('--yc-dev-local-mirror-host=' + $PlayerCount)
        $arguments += ('"--yc-dev-local-mirror-room-file=' + $roomFile + '"')
    } else {
        $arguments += ('--yc-dev-local-mirror-join=' + $RoomId)
        if ($PlayerId -eq 2) { $arguments += '--yc-network-validation-reconnect=true' }
    }
    $process = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path -Parent $ExecutablePath) -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $startedPlayers.Add([pscustomobject]@{ PlayerId=$PlayerId; Process=$process; Output=$playerOutput })
    Write-Host "已启动玩家 $PlayerId，PID=$($process.Id)"
}
Start-ValidationPlayer 1 ''
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while (-not (Test-Path -LiteralPath $roomFile -PathType Leaf)) {
    if ($startedPlayers[0].Process.HasExited -or (Get-Date) -gt $deadline) { throw '房主未建立房间，请读取本次日志。' }
    Start-Sleep -Milliseconds 500
}
$roomId = (Get-Content -LiteralPath $roomFile -Raw).Trim()
if ($roomId -notmatch ':(\d+)$') { throw "房间号格式错误：$roomId" }
$joinAddress = '127.0.0.1:' + $Matches[1]
for ($playerId=2; $playerId -le $PlayerCount; $playerId++) {
    Start-ValidationPlayer $playerId $joinAddress
    Start-Sleep -Milliseconds 750
}
$nextStatus = Get-Date
while (@($startedPlayers | Where-Object { -not $_.Process.HasExited }).Count -gt 0) {
    $failedPlayer = $startedPlayers | Where-Object {
        $resultFile = Join-Path $_.Output 'result.json'
        (Test-Path -LiteralPath $resultFile) -and -not (Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json).success
    } | Select-Object -First 1
    if ($null -ne $failedPlayer) {
        Write-Host "玩家 $($failedPlayer.PlayerId) 已报告失败，结束本次其余验证进程并保留证据。"
        foreach ($entry in $startedPlayers) { if (-not $entry.Process.HasExited) { Stop-Process -Id $entry.Process.Id -Force } }
        break
    }
    if ((Get-Date) -gt $deadline) {
        foreach ($entry in $startedPlayers) { if (-not $entry.Process.HasExited) { Stop-Process -Id $entry.Process.Id -Force } }
        throw '本次验证超时，已结束本脚本启动的进程；失败证据保留。'
    }
    if ((Get-Date) -ge $nextStatus) {
        Write-Host ('验证进行中：' + $OutputDirectory)
        $nextStatus = (Get-Date).AddSeconds(30)
    }
    Start-Sleep -Seconds 1
}
$results = foreach ($entry in $startedPlayers) {
    $path = Join-Path $entry.Output 'result.json'
    $result = if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{ success=$false; failure='同局失败后终止，未生成终局结果' } }
    [pscustomobject]@{PlayerId=$entry.PlayerId; ExitCode=$entry.Process.ExitCode; Result=$result; Path=$path}
}
$results | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
$results | Select-Object PlayerId,ExitCode,@{n='Success';e={$_.Result.success}},@{n='Failure';e={$_.Result.failure}} | Format-Table
if (@($results | Where-Object { -not $_.Result.success -or $_.ExitCode -ne 0 }).Count -gt 0) { exit 1 }
& (Join-Path $PSScriptRoot 'Test-EnterpriseNetworkEvidence.ps1') -OutputDirectory $OutputDirectory -PlayerCount $PlayerCount -Expansion $Expansion
exit 0
