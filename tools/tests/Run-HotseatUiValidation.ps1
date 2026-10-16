param(
    [ValidateSet('full-game')][string[]]$Cases=@('full-game'),
    [string]$SnapshotRoot='',
    [ValidateRange(1,1000)][int]$Games=1,
    [ValidateSet('enterprise,infected','enterprise','')][string]$CampaignExpansions='enterprise,infected',
    [string]$BuildInputManifestPath='',
    [string[]]$PriorityKeys=@('character:tin-man:strategy','style:city_style_mobilization_support_system:effect','style:city_style_composite_power_system:effect','style:city_style_source_stone_industrial_hub:effect','style:city_style_efficient_mobile_management_system:effect','page:cannot-sale','page:trade-sale')
)
$ErrorActionPreference='Stop'
$project='G:\NoMadCity'
$stage=Join-Path $project 'Logs\GameplayCoverageCampaign'
Set-Location -LiteralPath $project
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$run=Join-Path $stage ('组合-'+$stamp)
if(Test-Path -LiteralPath $run){throw '输出已存在，保留原件。'}
New-Item -ItemType Directory -Path $run | Out-Null
$run | Set-Content -LiteralPath (Join-Path $stage '当前组合目录.txt') -Encoding utf8
function Json($path,$value){ConvertTo-Json -InputObject $value -Depth 16 | Set-Content -LiteralPath $path -Encoding utf8}
function Processes {@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe' OR Name='tuohuang.exe'" | Select-Object ProcessId,Name,ExecutablePath,CommandLine)}
function Inputs {
    $paths=@(rg --files --hidden --no-ignore Assets Packages ProjectSettings | Sort-Object)
    if($LASTEXITCODE -ne 0){throw '构建输入枚举失败。'}
    @(foreach($relative in $paths){$file=Get-Item -LiteralPath (Join-Path $project $relative)
        [pscustomobject]@{Path=$file.FullName;RelativePath=$relative;Bytes=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}})
}
function Differences($before,$after){
    $a=@{};foreach($file in $before){$a[$file.Path]=$file.Sha256}
    $b=@{};foreach($file in $after){$b[$file.Path]=$file.Sha256}
    @(foreach($path in @($a.Keys+$b.Keys | Sort-Object -Unique)){if($a[$path] -ne $b[$path]){[pscustomobject]@{Path=$path;Before=$a[$path];After=$b[$path]}}})
}
function Start-Owned($exe,$arguments,$directory,[bool]$visible){
    $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=$exe;$info.WorkingDirectory=$directory
    $info.UseShellExecute=$false;$info.CreateNoWindow=-not $visible
    $info.WindowStyle=if($visible){[Diagnostics.ProcessWindowStyle]::Normal}else{[Diagnostics.ProcessWindowStyle]::Hidden}
    foreach($argument in $arguments){$info.ArgumentList.Add($argument)}
    [Diagnostics.Process]::Start($info)
}
$initial=Processes
Json (Join-Path $run '入场进程.json') $initial
if($initial.Count){throw '当前共享设置及诊断目录尚未按实例隔离，先保持单实例，不结束已有窗口。'}
Json (Join-Path $run '本轮范围.json') ([ordered]@{NormalVisibleFullGamesOnly=$true;RequestedMaxParallel=3;EffectiveParallel=1;
    IsolationEvidence=(Join-Path $stage '当前验收范围.json');NativeTestsRequired=$false;StateReplacementCount=0;RandomSeedOverride=$false})
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $run 'Run-HotseatUiValidation.ps1.txt')
if([string]::IsNullOrWhiteSpace($SnapshotRoot)){
    if([string]::IsNullOrWhiteSpace($BuildInputManifestPath)){$BuildInputManifestPath=(Get-Content -LiteralPath (Join-Path $stage '当前正常应用快照输入路径.txt') -Raw).Trim()}
    $expected=Get-Content -LiteralPath $BuildInputManifestPath -Raw | ConvertFrom-Json
    foreach($path in $expected.StopReceipts){$owner=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json;if($owner.WritingStopped -ne $true){throw ('必要构建尚未停写：'+$path)}}
    foreach($file in $expected.AdditionalFiles){if((Get-FileHash -LiteralPath $file.Path).Hash -ne $file.SHA256){throw ('执行器输入漂移：'+$file.Path)}}
    $before=Inputs
    $unexpected=@(Differences $expected.Files $before)
    Json (Join-Path $run '固定输入.json') $before
    Json (Join-Path $run '构建前输入差异.json') $unexpected
    Copy-Item -LiteralPath $BuildInputManifestPath -Destination (Join-Path $run '停写输入交付.json')
    if($unexpected.Count){throw '固定版本输入有未交付变化，保留差异，不构建混合版本。'}
    $gitBefore=@(git status --short --untracked-files=all -- Assets Packages ProjectSettings)
    $gitBefore | Set-Content -LiteralPath (Join-Path $run '构建前Git状态.txt') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $project 'Assets\YC\Presentation\Validation\HotseatUiValidationDriver.cs') -Destination (Join-Path $run 'HotseatUiValidationDriver.cs.txt')
    $SnapshotRoot=Join-Path $project ('Builds\ValidationSnapshots\'+$stamp+'-HotseatUiValidation')
    if(Test-Path -LiteralPath $SnapshotRoot){throw '快照已存在，不覆盖。'}
    $buildLog=Join-Path $run 'unity-build.log'
    $arguments=@('-batchmode','-projectPath',$project,'-ycBuildLocalhostDevelopment','-logFile',$buildLog)
    $build=Start-Owned 'D:\2022.3.62f1c1\Editor\Unity.exe' $arguments $project $false
    $claim=[ordered]@{At=(Get-Date -Format o);OwnerThreadId='01a13383-7ca4-7780-965c-fea872f39146';WritingStopped=$true;
        NativeActive=$false;BuildActive=$true;PlayerActive=$false;Pid=$build.Id;RunRoot=$run;OtherOwnerWriteWindowAllowed=$false;Scope='一次必要固定版本构建，之后仅可见完整对局';NativeTestsRequired=$false}
    Json (Join-Path $run 'build-start.json') $claim
    Json (Join-Path $project 'Logs\GameplayUiBugfixes\统一支付-验证交付.json') $claim
    Write-Output ('Unity必要构建 PID='+$build.Id+'；目录='+$run)
    while(-not $build.WaitForExit(1000)){}
    $code=$build.ExitCode
    $after=Inputs;$drift=@(Differences $before $after)
    Json (Join-Path $run '构建后输入.json') $after
    $gitAfter=@(git status --short --untracked-files=all -- Assets Packages ProjectSettings)
    $gitAfter | Set-Content -LiteralPath (Join-Path $run '构建后Git状态.txt') -Encoding utf8
    $success=$code -eq 0 -and [bool](Select-String -LiteralPath $buildLog -SimpleMatch 'Build Finished, Result: Success.') -and
        [bool](Select-String -LiteralPath $buildLog -SimpleMatch 'Localhost development simulator build succeeded:')
    $buildResult=[ordered]@{At=(Get-Date -Format o);ProcessId=$build.Id;ExitCode=$code;NaturalExit=$true;ForcedTermination=$false;
        WindowReleased=$build.HasExited;BuildSucceeded=$success;InputCount=$before.Count;AfterInputCount=$after.Count;InputDrift=$drift;
        GitStatusChanged=[bool](Compare-Object $gitBefore $gitAfter);BuildLog=$buildLog;BuildLogSHA256=(Get-FileHash -LiteralPath $buildLog).Hash}
    Json (Join-Path $run 'build-exit.json') $buildResult
    if(-not $success -or $drift.Count){
        Json (Join-Path $project 'Logs\GameplayUiBugfixes\统一支付-验证交付.json') ([ordered]@{At=(Get-Date -Format o);WritingStopped=$true;
            NativeActive=$false;BuildActive=$false;PlayerActive=$false;RunRoot=$run;Build=$buildResult;OtherOwnerWriteWindowAllowed=$true;
            Status='必要构建失败，保留原件，未启动Player';NormalVisibleFullGamesOnly=$true})
        throw '必要构建失败或源漂移，保留原件，不启动Player。'
    }
    $output=Join-Path $project 'Builds\LocalhostDevelopment'
    if(@(Get-ChildItem -LiteralPath $output -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw '构建输出含重解析点，不复制。'}
    Copy-Item -LiteralPath $output -Destination $SnapshotRoot -Recurse
    $files=@(foreach($file in Get-ChildItem -LiteralPath $output -Recurse -File -Force | Sort-Object FullName){
        $relative=[IO.Path]::GetRelativePath($output,$file.FullName);$hash=(Get-FileHash -LiteralPath $file.FullName).Hash
        if((Get-FileHash -LiteralPath (Join-Path $SnapshotRoot $relative)).Hash -ne $hash){throw ('快照复制不一致：'+$relative)}
        [pscustomobject]@{Path=$relative;Bytes=$file.Length;Sha256=$hash}})
    foreach($required in @('tuohuang.exe','UnityPlayer.dll','tuohuang_Data\Managed\Assembly-CSharp.dll','tuohuang_Data\StreamingAssets\Content\core\pack.json')){
        if(-not(Test-Path -LiteralPath (Join-Path $SnapshotRoot $required))){throw ('快照运行文件缺失：'+$required)}
    }
    $saved=[ordered]@{CreatedAt=(Get-Date -Format o);BuildRoot=$run;EntryPoint=(Join-Path $SnapshotRoot 'tuohuang.exe');SnapshotRoot=$SnapshotRoot;
        BuildSucceeded=$true;SourceInputManifest=(Join-Path $run '固定输入.json');InputCount=$before.Count;InputDrift=@();RuntimeFileCount=$files.Count;
        Files=$files;AllCopiedHashesMatch=$true;SupportsFullGame=$true;AmiyaOriginalViewer=$true;WorkspaceReadDependencies=@();NormalVisibleFullGamesOnly=$true;
        DriverSHA256=(Get-FileHash -LiteralPath (Join-Path $run 'HotseatUiValidationDriver.cs.txt')).Hash}
    Json (Join-Path $SnapshotRoot 'build-receipt.json') $saved
    Json (Join-Path $run '快照回执.json') $saved
}else{
    $SnapshotRoot=[IO.Path]::GetFullPath($SnapshotRoot)
    $saved=Get-Content -LiteralPath (Join-Path $SnapshotRoot 'build-receipt.json') -Raw | ConvertFrom-Json
    if($saved.SupportsFullGame -ne $true){throw '该快照不支持完整对局。'}
    foreach($file in $saved.Files){if((Get-FileHash -LiteralPath (Join-Path $SnapshotRoot $file.Path)).Hash -ne $file.Sha256){throw ('固定快照已变化：'+$file.Path)}}
    Json (Join-Path $run '复用快照回执.json') $saved
}
$results=@()
for($game=1;$game -le $Games;$game++){
    if((Processes).Count){throw '当前有效隔离上限为1，保留已有进程。'}
    $case=Join-Path $run ('full-game-局'+$game);New-Item -ItemType Directory -Path $case | Out-Null
    $arguments=@('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $case 'Player.log'),
        ('--yc-hotseat-ui-validation-output='+$case),'--yc-hotseat-ui-validation-case=full-game',
        ('--yc-ui-campaign-path='+(Join-Path $stage '整局覆盖进度.json')),('--yc-campaign-expansions='+$CampaignExpansions),
        ('--yc-campaign-requested-priorities='+($PriorityKeys -join ';')),
        ('--yc-amiya-original-viewer='+$saved.AmiyaOriginalViewer.ToString().ToLowerInvariant()))
    $player=Start-Owned (Join-Path $SnapshotRoot 'tuohuang.exe') $arguments $SnapshotRoot $true
    $started=[ordered]@{At=(Get-Date -Format o);StartedAt=(Get-Date -Format o);ProcessId=$player.Id;Executable=(Join-Path $SnapshotRoot 'tuohuang.exe');
        Arguments=$arguments;GraphicsEnabled=$true;VisibleWindowRequested=$true;ExternalMouseInput=$false;NormalMenuFlow=$true;CampaignExpansions=$CampaignExpansions}
    Json (Join-Path $case 'process-start.json') $started
    Json (Join-Path $project 'Logs\GameplayUiBugfixes\统一支付-验证交付.json') ([ordered]@{At=(Get-Date -Format o);WritingStopped=$true;
        NativeActive=$false;BuildActive=$false;PlayerActive=$true;Pid=$player.Id;RunRoot=$case;SnapshotRoot=$SnapshotRoot;NormalMenuFlow=$true;
        OtherOwnerWriteWindowAllowed=$true;ActivePlayerReadsWorkspaceSource=$false;NormalVisibleFullGamesOnly=$true;EffectiveParallel=1})
    Write-Output ('可见独立Player PID='+$player.Id+'；完整对局='+$case)
    while(-not $player.WaitForExit(1000)){}
    $code=$player.ExitCode;$reportPath=Join-Path $case '验证结果.json';$report=$null
    if(Test-Path -LiteralPath $reportPath){$report=Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json}
    $images=@(Get-ChildItem -LiteralPath $case -File -Filter '*.png' | ForEach-Object {[pscustomobject]@{Path=$_.FullName;Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
    Json (Join-Path $case '截图hash.json') $images
    $classification=if($report -and $code -in @(0,2)){'driver_reported_exit'}elseif($code -ne 0){'unexplained_abnormal_exit'}else{'exit_without_driver_report'}
    $passed=$code -eq 0 -and $report.Passed -eq $true -and $report.FullGameCompleted -eq $true -and $images.Count -gt 0
    $result=[ordered]@{At=(Get-Date -Format o);Case='full-game';Game=$game;ProcessId=$player.Id;ExitCode=$code;NaturalExit=$null;
        ForcedTermination=$false;RunnerTerminationRequested=$false;WindowReleased=$player.HasExited;ExitWasObserved=$true;
        ExitClassification=$classification;DriverReportPresent=($null -ne $report);DriverRequestedExit=$report.NaturalExitRequested;
        NormalTerminationConfirmed=$passed;GameHealthy=$passed;Passed=$passed;FullGameCompleted=$report.FullGameCompleted;
        TargetPagesCovered=$report.TargetPagesCovered;FirstFailure=$report.Error;FailureCategory=$report.FailureCategory;
        DriverContinuationPending=$report.DriverContinuationPending;Report=$reportPath;ScreenshotCount=$images.Count;SnapshotRoot=$SnapshotRoot}
    Json (Join-Path $case 'process-exit.json') $result
    $results+=$result
    Json (Join-Path $run '运行汇总.json') ([ordered]@{SnapshotRoot=$SnapshotRoot;Results=$results;ExternalInput=$false;AllOwnedWindowsReleased=$true})
    Json (Join-Path $project 'Logs\GameplayUiBugfixes\统一支付-验证交付.json') ([ordered]@{At=(Get-Date -Format o);WritingStopped=$true;
        NativeActive=$false;BuildActive=$false;PlayerActive=$false;RunRoot=$case;Exit=$result;OtherOwnerWriteWindowAllowed=$true;NormalVisibleFullGamesOnly=$true})
    Write-Output ('完整对局退出='+$code+'；报告通过='+$passed+'；原件='+$case)
    if(-not $passed){break}
}
Json (Join-Path $run '最终进程.json') (Processes)
Write-Output ('组合原件='+$run)
