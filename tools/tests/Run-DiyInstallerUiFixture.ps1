param(
    [Parameter(Mandatory=$true)][ValidateSet('export','export-all','supplement','cancel-supplement','incompatible-export','game-no-diy','game-empty','game-with-diy','game-with-basic-diy','game-with-mixed-and-events','game-with-completion')][string]$Operation,
    [Parameter(Mandatory=$true)][string]$FixtureRoot,
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$TargetDirectory
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DiyInstallerFixtureNative {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
'@
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$allowed = [IO.Path]::GetFullPath((Join-Path $project 'TestResults')) + '\'
$fixture = [IO.Path]::GetFullPath($FixtureRoot)
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$target = [IO.Path]::GetFullPath($TargetDirectory)
foreach ($path in @($fixture, $target)) {
    if (!$path.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw '安装器夹具所有路径必须位于 TestResults。' }
    $cursor = $path
    while ($cursor.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw '安装器夹具路径不能包含重解析点。'
        }
        $cursor = Split-Path -Parent $cursor
    }
}
if (!$installerPath.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -and
    !($Operation -in @('export','export-all') -and $installerPath.StartsWith((Join-Path $project 'Builds') + '\',[StringComparison]::OrdinalIgnoreCase))) {
    throw '仅导出测试可读取 Builds 中的发行安装器，其余操作必须使用隔离测试安装器。'
}
if (Test-Path -LiteralPath $fixture) { throw '安装器界面夹具目录已存在，禁止覆盖。' }
if ($Operation -in @('supplement','cancel-supplement','incompatible-export')) {
    $receiptPath = Join-Path (Split-Path -Parent $installerPath) '安装包构建回执.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$receipt.fixtureDataRoot -or !([IO.Path]::GetFullPath($receipt.fixtureDataRoot)).StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash -ne $receipt.sha256) {
        throw '补装测试仅允许带项目内隔离内容根的准确安装器。'
    }
}
if ($Operation -like 'game-*') {
    $receiptPath = Join-Path (Split-Path -Parent $installerPath) '安装包构建回执.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$receipt.fixtureRegistryWritesDisabled -or !$receipt.fixtureDataRoot -or $receipt.fixtureGameTarget -ne $target -or
        (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash -ne $receipt.sha256 -or (Test-Path -LiteralPath $target)) {
        throw '首次安装只允许使用关闭注册表写入、锁定目标且未使用的测试安装器。'
    }
    if ($Operation -eq 'game-empty' -and (@($receipt.diyReleases).Count -ne 0 -or $receipt.diyToolSha256 -or $receipt.diySupportsSupplementAndExport)) {
        throw '无随附内容向导测试要求安装器没有内置 DIY 包或工具。'
    }
}
New-Item -ItemType Directory -Path $fixture | Out-Null
$tempRoot = Join-Path $fixture 'temp'
New-Item -ItemType Directory -Path $tempRoot | Out-Null
if ($Operation -notin @('supplement','cancel-supplement') -and !(Test-Path -LiteralPath $target)) { New-Item -ItemType Directory -Path $target | Out-Null }
if (!(Test-Path -LiteralPath $target -PathType Container)) { throw '目标目录不存在。' }
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = $installerPath
$start.WorkingDirectory = $fixture
$start.UseShellExecute = $false
$start.EnvironmentVariables['TEMP'] = $tempRoot
$start.EnvironmentVariables['TMP'] = $tempRoot
$installerProcess = [System.Diagnostics.Process]::Start($start)
@{ ProcessId=$installerProcess.Id; Operation=$Operation; Installer=$installerPath; Target=$target; Temp=$tempRoot } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture '进程身份.json') -Encoding UTF8

function Get-Windows {
    $all = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
    return @($all | Where-Object { $_.Current.ProcessId -eq $installerProcess.Id })
}
function Get-Nodes($window) {
    return @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition))
}
function Save-Stage([string]$name) {
    $windows = @(Get-Windows)
    $snapshot = @($windows | ForEach-Object {
        $window = $_
        @{ Name=$window.Current.Name; Nodes=@(Get-Nodes $window | ForEach-Object {
            @{ Type=$_.Current.ControlType.ProgrammaticName; Name=$_.Current.Name; Id=$_.Current.AutomationId;
               Class=$_.Current.ClassName; Enabled=$_.Current.IsEnabled; Bounds=$_.Current.BoundingRectangle.ToString();
               Patterns=@($_.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) }
        }) }
    })
    $snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fixture ($name + '.json')) -Encoding UTF8
    if ($name -like '05-*' -or $name -like '06-*' -or $name -eq '失败时窗口') {
        foreach ($window in $windows) {
            $area = $window.Current.BoundingRectangle
            $handle = [IntPtr]$window.Current.NativeWindowHandle
            if ($handle -eq [IntPtr]::Zero -or $area.Width -lt 1 -or $area.Height -lt 1) { continue }
            $bitmap = [Drawing.Bitmap]::new([int][Math]::Ceiling($area.Width),[int][Math]::Ceiling($area.Height))
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $dc = $graphics.GetHdc()
                try { $printed = [DiyInstallerFixtureNative]::PrintWindow($handle,$dc,0) }
                finally { $graphics.ReleaseHdc($dc) }
                if ($printed) { $bitmap.Save((Join-Path $fixture ($name + '.png')),[Drawing.Imaging.ImageFormat]::Png) }
            } finally { $graphics.Dispose(); $bitmap.Dispose() }
            break
        }
    }
}
function Wait-Node([string]$namePattern,[string]$type,[int]$seconds=45) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $seconds) {
        foreach ($window in @(Get-Windows)) {
            foreach ($node in @(Get-Nodes $window)) {
                if ($node.Current.Name -like $namePattern -and (!$type -or $node.Current.ControlType.ProgrammaticName -eq $type) -and $node.Current.IsEnabled) { return $node }
            }
        }
        if ($installerProcess.HasExited) { throw '安装程序提前退出。' }
        Start-Sleep -Milliseconds 250
    }
    throw "等待安装器控件超时：$namePattern / $type"
}
function Invoke-Button([string]$pattern) {
    $node = Wait-Node $pattern 'ControlType.Button'
    $node.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 600
}
function Set-TreeCheck($window,[string]$itemName,[bool]$selected) {
    $item = Wait-Node $itemName 'ControlType.TreeItem'
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $tree = @(Get-Nodes $window | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Tree' -and $_.Current.ClassName -eq 'SysTreeView32' })
    if ($tree.Count -ne 1 -or !$tree[0].Current.NativeWindowHandle) { throw '组件树控件不存在或不唯一。' }
    $treeHandle = [IntPtr]$tree[0].Current.NativeWindowHandle
    $treeItem = [DiyInstallerFixtureNative]::SendMessage($treeHandle,0x110a,[IntPtr]9,[IntPtr]::Zero)
    if ($treeItem -eq [IntPtr]::Zero) { throw '组件树没有实际选中项。' }
    $beforeState = [DiyInstallerFixtureNative]::SendMessage($treeHandle,0x1127,$treeItem,[IntPtr]0xf000).ToInt64()
    $beforeChecked = ($beforeState -band 0xf000) -eq 0x2000
    if ($beforeChecked -ne $selected) {
        # NSIS 的组件树没有 UIA TogglePattern；向取得焦点的原控件发送完整空格键序列。
        $item.SetFocus()
        [DiyInstallerFixtureNative]::PostMessage($treeHandle,0x0100,[IntPtr]0x20,[IntPtr]::Zero) | Out-Null
        [DiyInstallerFixtureNative]::PostMessage($treeHandle,0x0102,[IntPtr]0x20,[IntPtr]::Zero) | Out-Null
        [DiyInstallerFixtureNative]::PostMessage($treeHandle,0x0101,[IntPtr]0x20,[IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 400
    }
    $afterState = [DiyInstallerFixtureNative]::SendMessage($treeHandle,0x1127,$treeItem,[IntPtr]0xf000).ToInt64()
    @{ Item=$item.Current.Name; Before=$beforeState; After=$afterState; ExpectedSelected=$selected; Input='组件树空格键' } |
        ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $fixture '组件勾选状态.jsonl') -Encoding UTF8
    if ((($afterState -band 0xf000) -eq 0x2000) -ne $selected) { throw '组件状态与请求不符，保留原页面。' }
}
try {
    $null = Wait-Node '下一步*' 'ControlType.Button'
    Save-Stage '01-欢迎页'
    Invoke-Button '下一步*'
    if ($Operation -ne 'game-empty') {
        $radioName = if ($Operation -in @('export','export-all')) { '导出 DIY 内容包*' } elseif ($Operation -like 'game-*') { '安装或更新游戏*' } else { '仅安装 DIY 内容*' }
        $radio = Wait-Node $radioName 'ControlType.RadioButton'
        $radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Save-Stage '02-操作选择'
        Invoke-Button '下一步*'
    }
    $null = Wait-Node '下一步*' 'ControlType.Button'
    Save-Stage '03-目录页原始'
    $main = @(Get-Windows | Where-Object { $_.Current.Name -like '*安装*' })[0]
    $edits = @(Get-Nodes $main | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and $_.Current.IsEnabled })
    if ($edits.Count -ne 1) { throw "目录页输入框数量不符：$($edits.Count)" }
    if ($Operation -eq 'game-empty' -and @(Get-Nodes $main | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.RadioButton' -and $_.Current.Name -match 'DIY|导出' }).Count -ne 0) {
        throw '无随附内容时不应出现内置 DIY 操作选择。'
    }
    if ($Operation -eq 'incompatible-export') {
        $missingGame = Join-Path $fixture '没有游戏的目录'
        New-Item -ItemType Directory -Path $missingGame | Out-Null
        $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($missingGame)
        Invoke-Button '下一步*'
        $null = Wait-Node '*没有找到游戏主程序*' 'ControlType.Text'
        Save-Stage '04a-不兼容目标提示'
        Invoke-Button '确定'
        Invoke-Button '< 上一步*'
        $radio = Wait-Node '导出 DIY 内容包*' 'ControlType.RadioButton'
        $radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Invoke-Button '下一步*'
        $null = Wait-Node '下一步*' 'ControlType.Button'
        $main = @(Get-Windows | Where-Object { $_.Current.Name -like '*安装*' })[0]
        $edits = @(Get-Nodes $main | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and $_.Current.IsEnabled })
        if ($edits.Count -ne 1) { throw '返回导出后目录输入框不唯一。' }
    }
    $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($target)
    Save-Stage '04-目标目录'
    Invoke-Button '下一步*'
    $null = if ($Operation -eq 'game-empty') { Wait-Node '游戏文件*' 'ControlType.TreeItem' } else { Wait-Node '*基础配方示例*' 'ControlType.TreeItem' }
    Save-Stage '05-组件页原始'
    if ($Operation -like 'game-*') {
        Set-TreeCheck $main '创建桌面快捷方式' $false
        if ($Operation -eq 'game-empty') {
            if (@(Get-Nodes $main | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem' -and $_.Current.Name -match 'DIY|示例' }).Count -ne 0) {
                throw '无随附内容时组件列表不应包含 DIY 示例。'
            }
        } else {
            Set-TreeCheck $main '*基础配方示例*' ($Operation -eq 'game-with-basic-diy')
            Set-TreeCheck $main '*收尾规则示例*' $false
            Set-TreeCheck $main '*城市候选示例*' ($Operation -in @('game-with-diy','game-with-mixed-and-events'))
            if (@($receipt.diyReleases | Where-Object { $_.packId -eq 'example.events' }).Count -eq 1) {
                Set-TreeCheck $main '*事件新增与替换试验*' ($Operation -eq 'game-with-mixed-and-events')
            } elseif ($Operation -eq 'game-with-mixed-and-events') { throw '测试安装器未收录事件试验包。' }
            if (@($receipt.diyReleases | Where-Object { $_.packId -eq 'example.completed' }).Count -eq 1) {
                Set-TreeCheck $main '*完成事件模式示例*' ($Operation -eq 'game-with-completion')
            } elseif ($Operation -eq 'game-with-completion') { throw '测试安装器未收录完成事件模式包。' }
        }
    } else {
        Set-TreeCheck $main '*基础配方示例*' $true
        if ($Operation -eq 'export-all') {
            Set-TreeCheck $main '*收尾规则示例*' $true
            Set-TreeCheck $main '*城市候选示例*' $true
            Set-TreeCheck $main '*事件新增与替换试验*' $true
            Set-TreeCheck $main '*完成事件模式示例*' $true
        }
    }
    Save-Stage '06-组件已选择'
    if ($Operation -eq 'cancel-supplement') {
        Invoke-Button '取消*'
        $null = Wait-Node '是*' 'ControlType.Button'
        Save-Stage '07-确认取消'
        Invoke-Button '是*'
        if (!$installerProcess.WaitForExit(15000)) { throw '确认取消后安装程序未退出。' }
        @{ Result='已取消'; Operation=$Operation; ExitCode=$installerProcess.ExitCode; Target=$target } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture '结果.json') -Encoding UTF8
        Write-Host "安装器界面夹具完成：$Operation；证据：$fixture"
        return
    }
    Invoke-Button '安装*'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 240) {
        $windows = @(Get-Windows)
        foreach ($window in $windows) {
            $buttons = @(Get-Nodes $window | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Current.Name -match '确定|OK' })
            if ($buttons.Count -gt 0) {
                Save-Stage '07-操作结果弹窗'
                $texts = @(Get-Nodes $window | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } | ForEach-Object { $_.Current.Name })
                if (($texts -join ' ') -match '失败|未完成|无法|请选择至少') { throw ('安装器提示失败：' + ($texts -join ' ')) }
                $buttons[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 600
            }
        }
        $finish = @($windows | ForEach-Object { Get-Nodes $_ } | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Current.Name -like '完成*' -and $_.Current.IsEnabled })
        if ($finish.Count -gt 0) {
            Save-Stage '08-完成页'
            $finish[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            break
        }
        if ($installerProcess.HasExited) { throw '未到完成页安装程序便退出。' }
        Start-Sleep -Milliseconds 500
    }
    if (!$installerProcess.WaitForExit(15000)) { throw '安装程序点击完成后未退出。' }
    if ($installerProcess.ExitCode -ne 0) { throw "安装程序退出码异常：$($installerProcess.ExitCode)" }
    @{ Result='完成'; Operation=$Operation; ExitCode=$installerProcess.ExitCode; Target=$target } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture '结果.json') -Encoding UTF8
    Write-Host "安装器界面夹具完成：$Operation；证据：$fixture"
}
catch {
    Save-Stage '失败时窗口'
    @{ Result='失败'; Operation=$Operation; Error=$_.Exception.ToString(); ProcessId=$installerProcess.Id } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture '结果.json') -Encoding UTF8
    throw
}
