param(
    [string]$UnityPath = 'D:\2022.3.62f1c1\Editor\Unity.exe',
    [string]$ProjectPath = 'G:\NoMadCity',
    [ValidateSet('PJ-001','PJ-002')][string]$Scenario = 'PJ-001',
    [string]$Seed = '2026091901',
    [string]$EntranceLocation = '',
    [ValidateSet('liskarm-strategy','cannot-tactic','elysium-strategy')][string]$CharacterScenario = 'liskarm-strategy',
    [string]$OutputDirectory,
    [int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $project ('Logs\PlayerJourney\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $Scenario) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
if (Test-Path -LiteralPath (Join-Path $output 'result.json')) { throw '结果目录已有记录，请使用新的目录。' }
$owners = @(Get-CimInstance Win32_Process -Filter "name = 'Unity.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Replace('/','\').Contains($project) })
if ($owners.Count -gt 0) { throw '该项目已有 Unity 进程，请等待其完成。' }
$arguments = @('-projectPath', ('"' + $project + '"'), '-executeMethod', 'YC.Editor.PlayerJourneyEditorLauncher.Run',
    '-screen-width', '1920', '-screen-height', '1080', ('--yc-player-journey-scenario=' + $Scenario),
    ('--yc-player-journey-seed=' + $Seed), ('"--yc-player-journey-output=' + $output + '"'), '-logFile', ('"' + (Join-Path $output 'unity.log') + '"'))
if ($EntranceLocation) { $arguments += ('--yc-player-journey-entrance=' + $EntranceLocation) }
$arguments += ('--yc-player-journey-character=' + $CharacterScenario)
$process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $process.Id; throw '玩家测试进程超时；保留日志和轨迹。' }
$resultFile = Join-Path $output 'result.json'
if (-not (Test-Path -LiteralPath $resultFile)) { throw ('测试未产出结果，请检查 ' + (Join-Path $output 'unity.log')) }
$result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
$result | Format-List
if (-not ($result.success -and $result.finalScoringVisible -and $result.returnedToStart -and $result.observedRounds -eq 8 -and $result.errors -eq 0)) { exit 1 }
exit 0
