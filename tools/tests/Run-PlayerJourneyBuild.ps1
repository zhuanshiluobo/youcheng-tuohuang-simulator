param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [string]$Scenario = 'PJ-003',
    [string]$Seed = '2026091901',
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [int]$TimeoutSeconds = 360
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
if (Test-Path -LiteralPath (Join-Path $output 'result.json')) { throw '结果目录已有记录，请使用新的目录。' }
$arguments = @('-screen-width','1920','-screen-height','1080','-screen-fullscreen','0',
    ('--yc-player-journey-scenario=' + $Scenario), ('--yc-player-journey-seed=' + $Seed),
    ('"--yc-player-journey-output=' + $output + '"'), '-logFile', ('"' + (Join-Path $output 'unity.log') + '"'))
$process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $process.Id; throw '玩家测试进程超时。' }
$result = Get-Content -LiteralPath (Join-Path $output 'result.json') -Raw | ConvertFrom-Json
$result | Format-List
if (-not ($result.success -and $result.finalScoringVisible -and $result.returnedToStart -and $result.observedRounds -eq 8 -and $result.errors -eq 0)) { exit 1 }
