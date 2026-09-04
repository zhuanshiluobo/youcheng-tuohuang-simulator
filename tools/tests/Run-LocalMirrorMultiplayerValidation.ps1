param(
    [string]$ProjectPath = "",
    [string]$ExecutablePath = "",
    [ValidateSet(3, 4)]
    [int]$PlayerCount = 3,
    [int]$StartupTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = (Resolve-Path (Join-Path $scriptRoot "..\..")).Path
}
else {
    $ProjectPath = (Resolve-Path $ProjectPath).Path
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $ProjectPath "Builds\LocalhostDevelopment\tuohuang.exe"
}
$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)

if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "未找到 Development Build：$ExecutablePath`n请先运行：powershell -ExecutionPolicy Bypass -File .\tools\tests\BuildLocalhost.ps1 -Development"
}

function ConvertTo-ProcessArgument {
    param([string]$Value)

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + ($Value -replace '"', '\"') + '"'
}

function Start-ValidationPlayer {
    param(
        [string]$Name,
        [string[]]$Arguments,
        [string]$LogFile
    )

    $allArguments = @(
        "-screen-fullscreen", "0",
        "-screen-width", "960",
        "-screen-height", "540",
        "-logFile", $LogFile
    ) + $Arguments
    $argumentLine = ($allArguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join " "
    $process = Start-Process -FilePath $ExecutablePath `
        -WorkingDirectory (Split-Path -Parent $ExecutablePath) `
        -ArgumentList $argumentLine `
        -PassThru

    Write-Host ("已启动 {0}：PID={1}，日志={2}" -f $Name, $process.Id, $LogFile)
    return $process
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$runDirectory = Join-Path $ProjectPath "Logs\LocalMirrorValidation\$timestamp"
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

$roomFile = Join-Path $runDirectory "room.txt"
$hostLog = Join-Path $runDirectory "host.log"
$hostProcess = Start-ValidationPlayer -Name "Host" -LogFile $hostLog -Arguments @(
    "--yc-local-multiplayer-test",
    "--yc-local-player-name=Host",
    "--yc-dev-local-mirror-host=$PlayerCount",
    "--yc-dev-local-mirror-room-file=$roomFile"
)

$deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
$roomId = ""
while ((Get-Date) -lt $deadline) {
    if ($hostProcess.HasExited) {
        throw "Host 在创建房间前退出，退出码=$($hostProcess.ExitCode)。请查看：$hostLog"
    }

    if (Test-Path -LiteralPath $roomFile -PathType Leaf) {
        $roomId = (Get-Content -LiteralPath $roomFile -Raw).Trim()
        if ($roomId -match ':(?<port>\d+)$') {
            break
        }
    }

    Start-Sleep -Milliseconds 500
    $hostProcess.Refresh()
}

if ([string]::IsNullOrWhiteSpace($roomId) -or $roomId -notmatch ':(?<port>\d+)$') {
    throw "等待本地房间号超时。进程仍保留供排查；请查看：$hostLog"
}

# 本脚本只做同机验收，统一走 loopback，避免局域网地址和防火墙差异。
$joinRoomId = "127.0.0.1:$($Matches['port'])"
Write-Host "房间已创建：$roomId；同机 Client 使用：$joinRoomId"

$clients = @()
for ($playerId = 2; $playerId -le $PlayerCount; $playerId++) {
    $clientLog = Join-Path $runDirectory "client$playerId.log"
    $clients += Start-ValidationPlayer -Name "Client$playerId" -LogFile $clientLog -Arguments @(
        "--yc-local-multiplayer-test",
        "--yc-local-player-name=Client$playerId",
        "--yc-dev-local-mirror-join=$joinRoomId"
    )
    Start-Sleep -Milliseconds 700
}

$startMarker = "[LocalMirrorAutomation] All seats are ready; requesting game start."
$started = $false
while ((Get-Date) -lt $deadline) {
    $hostProcess.Refresh()
    if ($hostProcess.HasExited) {
        throw "Host 在联机开局前退出，退出码=$($hostProcess.ExitCode)。请查看：$hostLog"
    }

    if (Test-Path -LiteralPath $hostLog -PathType Leaf) {
        $match = Select-String -LiteralPath $hostLog -SimpleMatch $startMarker -Quiet
        if ($match) {
            $started = $true
            break
        }
    }

    Start-Sleep -Seconds 1
}

Write-Host ""
Write-Host "本次日志目录：$runDirectory"
Write-Host ("进程：Host={0}；Clients={1}" -f $hostProcess.Id, (($clients | ForEach-Object { $_.Id }) -join ","))
if (-not $started) {
    throw "等待全部席位通过连接与身份校验超时。进程仍保留供人工查看；请检查 host/client 日志。"
}

Write-Host "本地联机开局门禁已通过，三/四个窗口应已自动进入对局。" -ForegroundColor Green
Write-Host "人工最小验收：在任一 Client 执行一次主要行动，确认 Host 和其他 Client 同步更新；随后手动关闭所有窗口。"
