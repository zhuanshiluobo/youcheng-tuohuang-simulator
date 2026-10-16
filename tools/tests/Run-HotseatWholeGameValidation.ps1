[CmdletBinding()]
param(
    [ValidateSet('all','core','enterprise','infected')][string]$Mode='all',
    [string]$UnityPath='D:\2022.3.62f1c1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [string]$StableInputManifest,
    [string]$PriorityRoot,
    [string[]]$PriorCoverageReports=@(),
    [Nullable[int]]$ExplicitSeed,
    [switch]$IncludeLegacyUiBottomHintCheck,
    [ValidateRange(60,900)][int]$OwnedShutdownGraceSeconds=180,
    [switch]$PrepareOnly
)
$ErrorActionPreference='Stop'
$project='G:\NoMadCity'
$logRoot=Join-Path $project 'Logs\ExpansionValidation\HotseatWholeGames'
$utf8=[Text.UTF8Encoding]::new($false)
function Write-NewJson([string]$Path,$Value) {
    if(Test-Path -LiteralPath $Path){throw "禁止覆盖本批原件：$Path"}
    [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 50)+[Environment]::NewLine,$utf8)
}
function Get-InputDrift($Inputs) {
    @($Inputs | Where-Object {
        -not (Test-Path -LiteralPath $_.Path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256
    })
}
function Confirm-AllowedInput([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    if(-not $full.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($project+'\游城拓荒\UI素材\',[StringComparison]::OrdinalIgnoreCase) -or
        $full -eq $project+'\游城拓荒\UI素材'){throw '禁止读取项目外或受限原档案输入。'}
    $cursor=$full
    while($cursor -and $cursor -ne $project){
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){
            throw '实际输入包含重解析点，不能间接访问其他目录。'
        }
        $cursor=Split-Path -Parent $cursor
    }
}
if([string]::IsNullOrWhiteSpace($OutputDirectory)){
    $OutputDirectory=Join-Path $logRoot ('Run-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $OutputDirectory.StartsWith($logRoot+'\',[StringComparison]::OrdinalIgnoreCase)){
    throw '输出必须是本项目HotseatWholeGames内的独立子目录。'
}
if(Test-Path -LiteralPath $OutputDirectory){throw '输出已存在，不覆盖旧结果。'}
if(-not $PrepareOnly){
    $busy=@(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'Unity.exe' -and $_.CommandLine -like ('*'+$project+'*')
    })
    if($busy.Count -gt 0){throw ('Unity窗口未释放；不结束进程、不清锁。PID：'+($busy.ProcessId -join ','))}
    if([string]::IsNullOrWhiteSpace($StableInputManifest)){throw '缺少当前热座实际输入停写稳定点；可先使用-PrepareOnly。'}
    Confirm-AllowedInput $StableInputManifest
    $stable=Get-Content -LiteralPath $StableInputManifest -Raw -Encoding utf8 | ConvertFrom-Json -Depth 50
    if($stable.ReadyForNativeHotseat -ne $true -or $stable.InputWritesStopped -ne $true -or @($stable.Inputs).Count -eq 0){
        throw '稳定点须明确ReadyForNativeHotseat/InputWritesStopped并列出准确Inputs。'
    }
    foreach($entry in $stable.Inputs){Confirm-AllowedInput $entry.Path}
    if((Get-InputDrift $stable.Inputs).Count -ne 0){throw '热座稳定点输入已变化，不启动Unity。'}
    if(-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)){throw '指定Unity不存在。'}
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
# 定向枚举实际编译源及内容数据，绝不枚举受限原档案或调用UI生成器。
Push-Location $project
try {
    $sourcePaths=@(& rg --files Assets/YC -g '*.cs' -g '*.cs.meta' -g '*.asmdef' -g '*.asmdef.meta')
    if($LASTEXITCODE -ne 0){throw '实际脚本输入枚举失败。'}
    $contentPaths=@(& rg --files Assets/StreamingAssets/Content -g '*.json' -g '*.lua')
    if($LASTEXITCODE -ne 0){throw '实际内容输入枚举失败。'}
} finally {Pop-Location}
$paths=@($sourcePaths+$contentPaths+@('Packages\manifest.json','Packages\packages-lock.json',
    'ProjectSettings\ProjectVersion.txt','tools\tests\Run-HotseatWholeGameValidation.ps1',
    'tools\validation\Update-ProductionGameCoveragePriorities.ps1') |
    ForEach-Object {[IO.Path]::GetFullPath((Join-Path $project $_))} | Sort-Object -Unique)
if(-not [string]::IsNullOrWhiteSpace($PriorityRoot)){
    $PriorityRoot=[IO.Path]::GetFullPath($PriorityRoot)
    if(-not $PriorityRoot.StartsWith($project+'\Builds\ValidationSnapshots\',[StringComparison]::OrdinalIgnoreCase) -and
        -not $PriorityRoot.StartsWith($project+'\Logs\ExpansionValidation\',[StringComparison]::OrdinalIgnoreCase)){throw '覆盖优先级须来自本项目明确验证证据。'}
    foreach($m in @('core','enterprise','infected')){
        $priorityPath=Join-Path $PriorityRoot ($m+'.json')
        if(Test-Path -LiteralPath $priorityPath -PathType Leaf){$paths += $priorityPath}
    }
}
foreach($path in $PriorCoverageReports){Confirm-AllowedInput $path;$paths += [IO.Path]::GetFullPath($path)}
foreach($path in $paths){Confirm-AllowedInput $path}
$inputs=@($paths | ForEach-Object { [pscustomobject]@{Path=$_;Sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
Write-NewJson (Join-Path $OutputDirectory '准确运行输入.json') $inputs
Write-NewJson (Join-Path $OutputDirectory '入口准备回执.json') ([ordered]@{
    RecordedUtc=[DateTime]::UtcNow.ToString('o');Mode=$Mode;PrepareOnly=[bool]$PrepareOnly;
    ReadyForNativeHotseat=$false;InputWritesStopped=$false;Inputs=$inputs;
    FormalLaunch='LaunchMode.Local / GameSessionBootstrapper.Build(context,false,false)';
    CommandPath='GameSession.Submit(GameCommand)';SmokeStateUsed=$false;
    StateFixtureInjection=$false;UiInputUsed=$false;UiRebuildRequested=$false;
    RequiresActualUnityWindow=$true;RequiresActualInputStablePoint=$true;
    RequiresEntireClientWindowProtocol=$false;ExplicitSeed=$ExplicitSeed
})
if($PrepareOnly){
    [pscustomobject]@{OutputDirectory=$OutputDirectory;PreparedInputs=$inputs.Count;
        NativeStarted=$false;ReadyForNativeHotseat=$false;Reason='只准备真实输入；尚未取得实际运行窗口及源停写稳定点。'} | ConvertTo-Json -Compress
    return
}
# 稳定点必须覆盖此入口的全部实际源；不能把不完整的种子回执当整个编译输入稳定。
$stableByPath=@{};foreach($entry in $stable.Inputs){$stableByPath[[IO.Path]::GetFullPath($entry.Path)]=$entry.Sha256}
foreach($entry in $inputs){
    if(-not $stableByPath.ContainsKey($entry.Path) -or $stableByPath[$entry.Path] -ne $entry.Sha256){
        throw ('当前稳定点未包含准确实际输入：'+$entry.Path)
    }
}
$methodByMode=[ordered]@{
    core='YC.Tests.EditMode.HotseatWholeGameTests.Core_FormalLocalLaunch_CompletesEightRounds';
    enterprise='YC.Tests.EditMode.HotseatWholeGameTests.Enterprise_FormalLocalLaunch_CompletesEightRounds';
    infected='YC.Tests.EditMode.HotseatWholeGameTests.Infected_FormalLocalLaunch_CompletesEightRounds'
}
$modes=if($Mode -eq 'all'){@('core','enterprise','infected')}else{@($Mode)}
$hotseatMethods=@($modes | ForEach-Object {$methodByMode[$_]})
$methods=@($hotseatMethods)
if($IncludeLegacyUiBottomHintCheck){
    $methods += 'YC.Tests.EditMode.LegacyCharacterGameplayTextUiTests.LegacyBottomHintsPreserveDocumentsAndInvalidateChangedPending'
}
$xmlPath=Join-Path $OutputDirectory 'test-results.xml'
$logPath=Join-Path $OutputDirectory 'unity-tests.log'
$info=[Diagnostics.ProcessStartInfo]::new()
$info.FileName=$UnityPath;$info.WorkingDirectory=$project
$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
foreach($argument in @('-batchmode','-runTests','-projectPath',$project,'-testPlatform','EditMode',
    '-testFilter',($methods -join ';'),'-testResults',$xmlPath,'-logFile',$logPath)){$info.ArgumentList.Add($argument)}
$info.Environment['YC_HOTSEAT_OUTPUT_ROOT']=$OutputDirectory
$info.Environment.Remove('YC_HOTSEAT_PRIORITY_ROOT') | Out-Null
$info.Environment.Remove('YC_HOTSEAT_EXPLICIT_SEED') | Out-Null
if($PriorityRoot){$info.Environment['YC_HOTSEAT_PRIORITY_ROOT']=$PriorityRoot}
if($null -ne $ExplicitSeed){$info.Environment['YC_HOTSEAT_EXPLICIT_SEED']=$ExplicitSeed.ToString([Globalization.CultureInfo]::InvariantCulture)}
# 在启动前最后再查真实同项目Unity和源，不删除任何锁文件。
if(@(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'Unity.exe' -and $_.CommandLine -like ('*'+$project+'*') }).Count -gt 0){throw '启动前Unity窗口被占用。'}
if((Get-InputDrift $inputs).Count -ne 0){throw '启动前实际输入发生变化。'}
$process=[Diagnostics.Process]::Start($info)
$heldProcessHandle=$process.Handle
$ownedIdentity=Get-CimInstance Win32_Process | Where-Object {$_.ProcessId -eq $process.Id}
if($null -eq $ownedIdentity){throw '新启动子进程身份未捕获，禁止按名称清理。'}
Write-NewJson (Join-Path $OutputDirectory 'native-start.json') ([ordered]@{
    StartedUtc=[DateTime]::UtcNow.ToString('o');ProcessId=$process.Id;Methods=$methods;
    Mode=$Mode;InputCount=$inputs.Count;StableInputManifest=$StableInputManifest;
    CreationDate=$ownedIdentity.CreationDate;ExecutablePath=$ownedIdentity.ExecutablePath;CommandLine=$ownedIdentity.CommandLine;
    StableInputManifestSha256=(Get-FileHash -LiteralPath $StableInputManifest -Algorithm SHA256).Hash;
    UiRebuildRequested=$false;ForcedTermination=$false;OldPassedNativeChecksRepeated=$false
})
[pscustomobject]@{OutputDirectory=$OutputDirectory;ProcessId=$process.Id;SelectedModes=$modes} | ConvertTo-Json -Compress
$completion=$null
$shutdownObservedAt=$null
$forcedTermination=$false
$shutdownCleanupAttempted=$false
while(-not $process.WaitForExit(1000)){
    if($null -eq $completion -and (Test-Path -LiteralPath $xmlPath)){
        try {
            $xml=[Xml.XmlDocument]::new();$xml.Load($xmlPath)
            $cases=@($xml.SelectNodes('//test-case'))
            $names=@($cases | ForEach-Object {$_.fullname})
            if(-not [string]::IsNullOrWhiteSpace($xml.DocumentElement.GetAttribute('result'))){
                $completion=[ordered]@{RecordedUtc=[DateTime]::UtcNow.ToString('o');
                    InputCount=$inputs.Count;SourceDriftPaths=@(Get-InputDrift $inputs | ForEach-Object {$_.Path});
                    XmlSha256=(Get-FileHash -LiteralPath $xmlPath -Algorithm SHA256).Hash;
                    Cases=@($cases | ForEach-Object {[ordered]@{FullName=$_.fullname;Result=$_.result;Assertions=$_.asserts}});
                    ExpectedCasesPresent=($cases.Count -eq $methods.Count -and @($methods | Where-Object {$_ -notin $names}).Count -eq 0);
                    ActualExitCode=$null;NativeNaturalExitConfirmed=$false;
                    InputWindow='全部选定检查完成后的观察；后续授权源码修改不追改此执行窗口'}
                Write-NewJson (Join-Path $OutputDirectory '检查完成观察.json') $completion
            }
        } catch [System.Xml.XmlException] { }
          catch [System.IO.IOException] { if(($_.Exception.HResult -band 65535) -ne 32){throw} }
    }
    # 仅清理本入口创建、测试已结束且明确Shutdown后持续挂起的准确子进程。
    if(-not $shutdownCleanupAttempted -and $null -ne $completion -and (Test-Path -LiteralPath $logPath)){
        $tail=(Get-Content -LiteralPath $logPath -Tail 48 -Encoding utf8) -join [Environment]::NewLine
        if($tail -match 'RunFinished' -and $tail -match 'Input System module state changed to: Shutdown\.'){
            if($null -eq $shutdownObservedAt){$shutdownObservedAt=[DateTime]::UtcNow}
            $logItem=Get-Item -LiteralPath $logPath
            if(([DateTime]::UtcNow-$shutdownObservedAt).TotalSeconds -ge $OwnedShutdownGraceSeconds -and
                ([DateTime]::UtcNow-$logItem.LastWriteTimeUtc).TotalSeconds -ge $OwnedShutdownGraceSeconds){
                $live=Get-CimInstance Win32_Process | Where-Object {$_.ProcessId -eq $process.Id}
                if($null -eq $live){continue}
                if($live.CreationDate.ToUniversalTime().Ticks -ne $ownedIdentity.CreationDate.ToUniversalTime().Ticks -or
                    $live.CommandLine -ne $ownedIdentity.CommandLine -or $live.ExecutablePath -ne $ownedIdentity.ExecutablePath -or
                    $process.MainWindowHandle -ne [IntPtr]::Zero -or
                    [Math]::Abs($process.StartTime.ToUniversalTime().Ticks-$live.CreationDate.ToUniversalTime().Ticks) -gt 10){
                    throw '退出挂起子进程身份变化，未执行终止。'
                }
                foreach($pair in @(@($xmlPath,'test-results-before-cleanup.xml'),@($logPath,'unity-tests-before-cleanup.log'))){
                    $read=[IO.File]::Open($pair[0],[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
                    try{$write=[IO.File]::Open((Join-Path $OutputDirectory $pair[1]),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
                        try{$read.CopyTo($write)}finally{$write.Dispose()}
                    }finally{$read.Dispose()}
                }
                Write-NewJson (Join-Path $OutputDirectory 'shutdown-cleanup-before.json') ([ordered]@{
                    RecordedUtc=[DateTime]::UtcNow.ToString('o');ProcessId=$process.Id;CreationDate=$live.CreationDate;
                    ExecutablePath=$live.ExecutablePath;CommandLine=$live.CommandLine;IdentityMatched=$true;
                    ShutdownObservedUtc=$shutdownObservedAt;GraceSeconds=$OwnedShutdownGraceSeconds;
                    LogLastWriteUtc=$logItem.LastWriteTimeUtc;XmlSha256=(Get-FileHash -LiteralPath $xmlPath).Hash;
                    PreservedLogSha256=(Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'unity-tests-before-cleanup.log')).Hash;
                    Scope='仅准确自有子进程，不按名/树终止、不清锁'
                })
                $recheck=Get-CimInstance Win32_Process | Where-Object {$_.ProcessId -eq $process.Id}
                if($null -eq $recheck){continue}
                if($recheck.CreationDate.ToUniversalTime().Ticks -ne $live.CreationDate.ToUniversalTime().Ticks -or
                    $recheck.CommandLine -ne $live.CommandLine -or $recheck.ExecutablePath -ne $live.ExecutablePath){throw '终止前身份变化，未执行终止。'}
                if(-not $process.HasExited){
                    $shutdownCleanupAttempted=$true
                    try{$process.Kill();$forcedTermination=$true}
                    catch{if(-not $process.HasExited){throw}}
                    if(-not $process.WaitForExit(10000)){throw '准确自有PID收尾后未确认退出，不处理其他进程。'}
                }
            }
        } else {$shutdownObservedAt=$null}
    }
}
$process.Refresh();$exitCode=$process.ExitCode
if($null -eq $completion){
    $completion=[ordered]@{RecordedUtc=[DateTime]::UtcNow.ToString('o');
        InputCount=$inputs.Count;SourceDriftPaths=@(Get-InputDrift $inputs | ForEach-Object {$_.Path});Cases=@()}
    if(Test-Path -LiteralPath $xmlPath){
        [xml]$xml=Get-Content -LiteralPath $xmlPath -Raw -Encoding utf8
        $completion.Cases=@($xml.SelectNodes('//test-case') | ForEach-Object {[ordered]@{FullName=$_.fullname;Result=$_.result;Assertions=$_.asserts}})
    }
    Write-NewJson (Join-Path $OutputDirectory '检查完成观察.json') $completion
}
$results=@($modes | ForEach-Object {
    $resultPath=Join-Path (Join-Path $OutputDirectory $_) 'result.json'
    if(Test-Path -LiteralPath $resultPath){Get-Content -LiteralPath $resultPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 20}
})
foreach($m in $modes){
    $modeRoot=Join-Path $OutputDirectory $m
    $coveragePath=Join-Path $modeRoot 'content-coverage.json'
    if(Test-Path -LiteralPath $coveragePath){
        $actual=Get-Content -LiteralPath $coveragePath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 20
        $prior=@();$excluded=@()
        foreach($path in $PriorCoverageReports){
            $previous=Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json -Depth 20
            if($previous.ContentPackHash -eq $actual.ContentPackHash -and
                (@($previous.EnabledExpansionIds|Sort-Object) -join ',') -eq (@($actual.EnabledExpansionIds|Sort-Object) -join ',')){$prior += $path}
            else{$excluded += $path}
        }
        Write-NewJson (Join-Path $modeRoot '覆盖继承范围.json') ([ordered]@{IncludedPriorReports=$prior;ExcludedDifferentModeOrHash=$excluded})
        & (Join-Path $project 'tools\validation\Update-ProductionGameCoveragePriorities.ps1') -ReportPaths @($prior+$coveragePath) `
            -RoutePlanPath (Join-Path $modeRoot 'actual-route-plan.json') -CatalogPath (Join-Path $modeRoot 'active-catalog.json') `
            -OutputPath (Join-Path $modeRoot 'cumulative-coverage.json') -NextPlanPath (Join-Path $modeRoot 'next-route-plan.json')
    }
}
$caseNames=@($completion.Cases | ForEach-Object {$_.FullName})
$hotseatCases=@($completion.Cases | Where-Object {$_.FullName -in $hotseatMethods})
$additionalCases=@($completion.Cases | Where-Object {$_.FullName -notin $hotseatMethods})
$gameChecksPassed=$completion.SourceDriftPaths.Count -eq 0 -and
    $hotseatCases.Count -eq $hotseatMethods.Count -and @($hotseatCases | Where-Object {$_.Result -ne 'Passed'}).Count -eq 0 -and
    @($hotseatMethods | Where-Object {$_ -notin $caseNames}).Count -eq 0 -and $results.Count -eq $modes.Count -and
    @($results | Where-Object {$_.Succeeded -ne $true -or $_.EffectChainHealthy -ne $true}).Count -eq 0
$allSelectedChecksPassed=$completion.Cases.Count -eq $methods.Count -and
    @($completion.Cases | Where-Object {$_.Result -ne 'Passed'}).Count -eq 0 -and
    @($methods | Where-Object {$_ -notin $caseNames}).Count -eq 0
$nativeNaturalExitPassed=(-not $forcedTermination -and $exitCode -eq 0)
$passed=$gameChecksPassed -and $allSelectedChecksPassed -and ($nativeNaturalExitPassed -or $forcedTermination)
Write-NewJson (Join-Path $OutputDirectory 'native-exit.json') ([ordered]@{
    RecordedUtc=[DateTime]::UtcNow.ToString('o');ProcessId=$process.Id;ExitCode=$exitCode;
    NaturalExit=(-not $forcedTermination);Passed=$passed;Cases=$completion.Cases;Modes=$modes;Results=$results;
    CriticalInputWindowObservation=$completion;ForcedTermination=$forcedTermination;
    NativeNaturalExitPassed=$nativeNaturalExitPassed;HotseatGameChecksPassed=$gameChecksPassed;
    AdditionalAffectedUiCases=$additionalCases;AllSelectedChecksPassed=$allSelectedChecksPassed;
    ShutdownCleanupRequired=$forcedTermination;ActualNativeExitCode=$exitCode;
    HotseatWholeGameValidated=$gameChecksPassed;MultiplayerSynchronizationValidated=$false;
    ManualUiAcceptance='待用户人工验收'
})
[pscustomobject]@{OutputDirectory=$OutputDirectory;ExitCode=$exitCode;Passed=$passed;Modes=$modes} | ConvertTo-Json -Compress
if(-not $passed){exit 1}
