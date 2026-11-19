param([Parameter(Mandatory=$true)][ValidateSet('default','selected')][string]$Case)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# 这些路径对应已列明并经用户授权的本次独立夹具，不接受调用方替换目标。
$base = 'G:\NoMadCity\TestResults\DIY-uninstall-prep2-20261111'
$caseRoot = Join-Path $base $Case
$game = Join-Path $caseRoot 'installed-game'
$data = Join-Path $caseRoot 'data'
$legacy = Join-Path $caseRoot 'legacy-data'
$manifest = Join-Path $base '逐项目标清单.json'
$run = Join-Path $caseRoot 'uninstall-run'
$tempRoot = Join-Path $run 'temp'
$uninstaller = Join-Path $game '卸载游城拓荒.exe'
if (Test-Path -LiteralPath $run) { throw '卸载测试记录目录已存在，不覆盖。' }
& python -X utf8 'G:\NoMadCity\tools\tests\inventory_diy_uninstall.py' --base $base --output $manifest --verify-case $Case
if ($LASTEXITCODE -ne 0) { throw '逐项目标复核失败，未运行卸载。' }
$inventory = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
$entry = $inventory.cases.$Case
$expected = if ($Case -eq 'default') { @($game) } else { @($game, $data, $legacy) }
if (($entry.deleteRoots -join "`n") -ne ($expected -join "`n") -or $entry.uninstaller -ne $uninstaller) {
    throw '清单中的删除范围与本次授权不一致。'
}
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $uninstaller
$start.WorkingDirectory = $caseRoot
$start.UseShellExecute = $false
$start.EnvironmentVariables['TEMP'] = $tempRoot
$start.EnvironmentVariables['TMP'] = $tempRoot
$process = [Diagnostics.Process]::Start($start)
@{ ProcessId=$process.Id; Uninstaller=$uninstaller; AuthorizedRoots=$expected; TemporaryRoot=$tempRoot } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run '进程身份.json') -Encoding UTF8

function Get-Windows {
    $owned = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ProcessId -eq $process.Id -or
        ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($tempRoot + '\',[StringComparison]::OrdinalIgnoreCase) -and
         $_.CommandLine -and $_.CommandLine.Contains($game))
    } | ForEach-Object { [int]$_.ProcessId })
    $all = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
    return @($all | Where-Object { $owned -contains $_.Current.ProcessId })
}
function Get-Nodes($window) {
    return @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition))
}
function Save-Stage([string]$name) {
    $snapshot = @(Get-Windows | ForEach-Object {
        $window = $_
        @{ Name=$window.Current.Name; ProcessId=$window.Current.ProcessId; Nodes=@(Get-Nodes $window | ForEach-Object {
            @{ Type=$_.Current.ControlType.ProgrammaticName; Name=$_.Current.Name; Enabled=$_.Current.IsEnabled;
               Patterns=@($_.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) }
        }) }
    })
    $snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run ($name + '.json')) -Encoding UTF8
}
function Wait-Node([string]$pattern,[string]$type,[int]$seconds=45) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $seconds) {
        foreach ($window in @(Get-Windows)) {
            foreach ($node in @(Get-Nodes $window)) {
                if ($node.Current.Name -like $pattern -and $node.Current.ControlType.ProgrammaticName -eq $type -and $node.Current.IsEnabled) { return $node }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "等待卸载器控件超时：$pattern / $type"
}
function Invoke-Button([string]$name) {
    $node = Wait-Node $name 'ControlType.Button'
    $node.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 600
}
try {
    $null = Wait-Node '下一步*' 'ControlType.Button'
    Save-Stage '01-卸载确认'
    Invoke-Button '下一步*'
    $checkbox = Wait-Node '删除此应用数据*' 'ControlType.CheckBox'
    $toggle = $checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw '删除数据默认状态不是未勾选。' }
    Save-Stage '02-删除数据默认状态'
    if ($Case -eq 'selected') { $toggle.Toggle() }
    $expectedState = if ($Case -eq 'selected') { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
    if ($toggle.Current.ToggleState -ne $expectedState) { throw '删除数据勾选状态与本次测试不一致。' }
    Save-Stage '03-删除数据确认状态'
    # 在真正点击卸载前再次逐项核验：有新增、变化或联接均中止。
    & python -X utf8 'G:\NoMadCity\tools\tests\inventory_diy_uninstall.py' --base $base --output $manifest --verify-case $Case
    if ($LASTEXITCODE -ne 0) { throw '点击卸载前复核失败，保留页面。' }
    Invoke-Button '卸载*'
    $finish = Wait-Node '完成*' 'ControlType.Button' 120
    Save-Stage '04-卸载完成页'
    $finish.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    foreach ($root in $expected) {
        if (Test-Path -LiteralPath $root) { throw "卸载后授权目标仍存在：$root" }
    }
    if ($Case -eq 'default') {
        & python -X utf8 'G:\NoMadCity\tools\tests\inventory_diy_uninstall.py' --base $base --output $manifest --verify-preserved
        if ($LASTEXITCODE -ne 0) { throw '默认卸载未完整保留测试数据。' }
    }
    @{ Result='通过'; Case=$Case; DeletedRoots=$expected; PreservedRoots=@($entry.preserveRoots); Inventory=$manifest } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run '结果.json') -Encoding UTF8
    Write-Host "隔离卸载测试通过：$Case；证据：$run"
} catch {
    Save-Stage '失败时窗口'
    @{ Result='失败'; Case=$Case; Error=$_.Exception.ToString(); ProcessId=$process.Id } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run '结果.json') -Encoding UTF8
    throw
}
