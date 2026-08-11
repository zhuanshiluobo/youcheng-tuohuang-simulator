param(
    [string]$UnityPath,
    [string]$UnityVersion = "2022.3.62f2c1",
    [string]$ProjectPath,
    [string]$TestFilter,
    [ValidateSet("EditMode", "PlayMode")]
    [string]$TestPlatform = "EditMode",
    [string]$OutputDirectory,
    [string]$LogFile,
    [string]$ResultsFile,
    [int]$ResultsWaitTimeoutSeconds = 120,
    [int]$ResultsStableMilliseconds = 1000,
    [int]$StatusIntervalSeconds = 10,
    [int]$MaxTestSeconds = 900,
    [int]$NoLogTimeoutSeconds = 90,
    [switch]$NoGraphics,
    [switch]$SkipProjectLockCheck,
    [string[]]$ExtraUnityArgs = @()
)

$ErrorActionPreference = "Stop"

trap {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

function Resolve-ScriptRoot {
    if ($PSScriptRoot) {
        return $PSScriptRoot
    }

    return Split-Path -Parent $MyInvocation.MyCommand.Path
}

function Add-UnityCandidate {
    param(
        [System.Collections.Generic.List[string]]$Candidates,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    if ((Split-Path -Leaf $Path) -ieq "Unity.exe") {
        $Candidates.Add($Path)
        return
    }

    $Candidates.Add((Join-Path $Path "Unity.exe"))
    $Candidates.Add((Join-Path $Path "Editor\Unity.exe"))
}

function Get-UnityEditorCandidates {
    param([string]$Version)

    $candidates = [System.Collections.Generic.List[string]]::new()

    Add-UnityCandidate $candidates ([Environment]::GetEnvironmentVariable("UNITY_2022_3_62F2C1"))

    $programRoots = @(
        ${env:ProgramFiles},
        ${env:ProgramFiles(x86)}
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($root in $programRoots) {
        $candidates.Add((Join-Path $root "Unity\Hub\Editor\$Version\Editor\Unity.exe"))
    }

    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        if ([string]::IsNullOrWhiteSpace($drive.Root)) {
            continue
        }

        $candidates.Add((Join-Path $drive.Root "$Version\Editor\Unity.exe"))
        $candidates.Add((Join-Path $drive.Root "Unity\Hub\Editor\$Version\Editor\Unity.exe"))
        $candidates.Add((Join-Path $drive.Root "Program Files\Unity\Hub\Editor\$Version\Editor\Unity.exe"))
        $candidates.Add((Join-Path $drive.Root "Program Files (x86)\Unity\Hub\Editor\$Version\Editor\Unity.exe"))
    }

    if ($IsMacOS) {
        $candidates.Add("/Applications/Unity/Hub/Editor/$Version/Unity.app/Contents/MacOS/Unity")
    }

    if ($IsLinux) {
        foreach ($root in @($env:HOME, "/opt", "/usr/local")) {
            if ([string]::IsNullOrWhiteSpace($root)) {
                continue
            }

            $candidates.Add((Join-Path $root "Unity/Hub/Editor/$Version/Editor/Unity"))
        }
    }

    foreach ($envName in @("UNITY_EDITOR_PATH", "UNITY_PATH")) {
        Add-UnityCandidate $candidates ([Environment]::GetEnvironmentVariable($envName))
    }

    return $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
}

function Resolve-UnityEditorPath {
    param(
        [string]$RequestedPath,
        [string]$Version
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $expandedPath = [Environment]::ExpandEnvironmentVariables($RequestedPath)
        if (Test-Path -LiteralPath $expandedPath -PathType Leaf) {
            return (Resolve-Path -LiteralPath $expandedPath).Path
        }

        throw "Unity Editor was not found at -UnityPath '$RequestedPath'."
    }

    foreach ($candidate in Get-UnityEditorCandidates $Version) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $defaultHubPath = "C:\Program Files\Unity\Hub\Editor\$Version\Editor\Unity.exe"
    throw "Unity Editor $Version was not found. Install it with Unity Hub, set UNITY_EDITOR_PATH, or pass -UnityPath. Preferred Hub path: $defaultHubPath"
}

function Get-PathForUnityArgument {
    param([string]$Path)

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-OutputFilePath {
    param([string]$Path)

    $expandedPath = [Environment]::ExpandEnvironmentVariables($Path)
    if (-not [System.IO.Path]::IsPathRooted($expandedPath)) {
        $expandedPath = Join-Path (Get-Location).Path $expandedPath
    }

    $directory = Split-Path -Parent $expandedPath
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    return $expandedPath
}

function ConvertTo-ComparablePathText {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    $text = [Environment]::ExpandEnvironmentVariables($Value)
    $text = $text.Replace([char]47, [char]92).Trim()
    $text = $text.Trim([char]34)
    $text = $text.TrimEnd([char]92)

    return $text.ToUpperInvariant()
}

function Get-UnityProcessesForProject {
    param([string]$Path)

    $comparableProjectPath = ConvertTo-ComparablePathText $Path
    $matches = @()

    if (-not ($env:OS -eq "Windows_NT")) {
        return $matches
    }

    try {
        $unityProcesses = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" -ErrorAction Stop
    }
    catch {
        Write-Host "WARNING: Could not inspect Unity processes before launching tests: $($_.Exception.Message)" -ForegroundColor Yellow
        return $matches
    }

    foreach ($process in $unityProcesses) {
        $commandLine = [string]$process.CommandLine
        if ([string]::IsNullOrWhiteSpace($commandLine)) {
            continue
        }

        $comparableCommandLine = ConvertTo-ComparablePathText $commandLine
        if ($comparableCommandLine.Contains($comparableProjectPath)) {
            $matches += [pscustomobject]@{
                ProcessId = $process.ProcessId
                CommandLine = $commandLine
            }
        }
    }

    return $matches
}

function Assert-UnityProjectIsAvailable {
    param([string]$Path)

    $unityProcesses = @(Get-UnityProcessesForProject $Path)
    if ($unityProcesses.Count -gt 0) {
        Write-Host "ERROR: Detected an existing Unity Editor instance using this project. Unity batchmode cannot open the same project twice." -ForegroundColor Red
        Write-Host "Project Path: $Path"
        foreach ($process in $unityProcesses) {
            Write-Host "Existing Unity PID: $($process.ProcessId)"
            Write-Host "Command Line: $($process.CommandLine)"
        }
        Write-Host "Close the existing Unity Editor for this project, then run this script again." -ForegroundColor Yellow
        exit 1
    }

    $lockFile = Join-Path $Path "Temp\UnityLockfile"
    if (Test-Path -LiteralPath $lockFile -PathType Leaf) {
        $lockItem = Get-Item -LiteralPath $lockFile
        Write-Host "ERROR: Detected Unity project lock file before launching tests." -ForegroundColor Red
        Write-Host "Project Path: $Path"
        Write-Host "Lock File: $($lockItem.FullName)"
        Write-Host "Last Write Time: $($lockItem.LastWriteTime)"
        Write-Host "Close the existing Unity Editor for this project and run this script again." -ForegroundColor Yellow
        Write-Host "If no Unity Editor is running, this may be a stale lock file. Inspect that exact file manually or rerun with -SkipProjectLockCheck to let Unity decide." -ForegroundColor Yellow
        exit 1
    }
}

function Test-UnityProjectLockFailureInLog {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $patterns = @(
        "another Unity instance",
        "Multiple Unity instances cannot open the same project",
        "Fatal Error!"
    )

    return [bool](Select-String -LiteralPath $Path -Pattern $patterns -SimpleMatch -Quiet)
}

function Stop-StartedUnityProcess {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    $Process.Refresh()
    if ($Process.HasExited) {
        return
    }

    Write-Host "Stopping Unity test process PID=$($Process.Id)..." -ForegroundColor Yellow
    Stop-Process -Id $Process.Id -Force
}

function Get-RecentUnityLogLine {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $patterns = @(
        "RefreshInfo:",
        "AssetDatabase:",
        "Compiling",
        "Compilation",
        "RunTests",
        "Test run",
        "Fatal Error!",
        "error CS",
        "Script compilation failed",
        "another Unity instance",
        "Multiple Unity instances"
    )

    return Select-String -LiteralPath $Path -Pattern $patterns -SimpleMatch | Select-Object -Last 1
}

function Invoke-UnityTestsWithTimeout {
    param(
        [string]$UnityExe,
        [string]$WorkingDirectory,
        [string[]]$Arguments,
        [string]$UnityLogFile,
        [int]$StatusEverySeconds,
        [int]$MaxSeconds,
        [int]$NoLogSeconds
    )

    $process = Start-Process -FilePath $UnityExe -WorkingDirectory $WorkingDirectory -ArgumentList $Arguments -PassThru
    $startedAt = Get-Date
    $lastStatusAt = $startedAt.AddSeconds(-[Math]::Max(1, $StatusEverySeconds))

    while (-not $process.HasExited) {
        Start-Sleep -Seconds 1
        $process.Refresh()

        $now = Get-Date
        $elapsed = [int]($now - $startedAt).TotalSeconds

        if ($MaxSeconds -gt 0 -and $elapsed -ge $MaxSeconds) {
            Stop-StartedUnityProcess -Process $process
            throw "Unity EditMode test run timed out after ${elapsed}s before Unity exited. Log: $UnityLogFile"
        }

        if ($NoLogSeconds -gt 0 -and
            $elapsed -ge $NoLogSeconds -and
            -not (Test-Path -LiteralPath $UnityLogFile -PathType Leaf)) {
            Stop-StartedUnityProcess -Process $process
            throw "Unity did not create an EditMode test log within ${elapsed}s. Log: $UnityLogFile"
        }

        if ($StatusEverySeconds -le 0 -or ($now - $lastStatusAt).TotalSeconds -lt $StatusEverySeconds) {
            continue
        }

        $lastStatusAt = $now
        Write-Host "Unity EditMode tests still running... PID=$($process.Id), elapsed=${elapsed}s"

        $progressLine = Get-RecentUnityLogLine $UnityLogFile
        if ($null -ne $progressLine) {
            Write-Host $progressLine.Line
        }
        elseif (Test-Path -LiteralPath $UnityLogFile -PathType Leaf) {
            Write-Host "Unity log exists; waiting for test results..."
        }
        else {
            Write-Host "Waiting for Unity to create EditMode test log..."
        }
    }

    $process.Refresh()
    return $process.ExitCode
}

function Wait-ForStableFile {
    param(
        [string]$Path,
        [int]$TimeoutSeconds,
        [int]$StableMilliseconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastLength = -1
    $lastWriteTimeUtc = [DateTime]::MinValue
    $stableSince = $null

    while ((Get-Date) -le $deadline) {
        try {
            if (Test-Path -LiteralPath $Path -PathType Leaf) {
                $item = Get-Item -LiteralPath $Path
                if ($item.Length -gt 0) {
                    if ($item.Length -eq $lastLength -and $item.LastWriteTimeUtc -eq $lastWriteTimeUtc) {
                        if ($null -eq $stableSince) {
                            $stableSince = Get-Date
                        }

                        if (((Get-Date) - $stableSince).TotalMilliseconds -ge $StableMilliseconds) {
                            return $true
                        }
                    }
                    else {
                        $lastLength = $item.Length
                        $lastWriteTimeUtc = $item.LastWriteTimeUtc
                        $stableSince = $null
                    }
                }
            }
        }
        catch {
            $stableSince = $null
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Get-TestRunAttributeInt {
    param(
        [hashtable]$Attributes,
        [string]$Name
    )

    $value = $Attributes[$Name]
    $number = 0
    if ([int]::TryParse($value, [ref]$number)) {
        return $number
    }

    return 0
}

function Read-UnityTestRunSummary {
    param([string]$Path)

    $reader = [System.IO.StreamReader]::new($Path, [System.Text.Encoding]::UTF8, $true)
    try {
        $buffer = ""
        while (-not $reader.EndOfStream -and $buffer.Length -lt 16384) {
            $buffer += $reader.ReadLine()
            $match = [regex]::Match($buffer, '<test-run\b[^>]*>')
            if ($match.Success) {
                $attributes = @{}
                foreach ($attributeMatch in [regex]::Matches($match.Value, '([A-Za-z_][\w:.-]*)="([^"]*)"')) {
                    $attributes[$attributeMatch.Groups[1].Value] = $attributeMatch.Groups[2].Value
                }

                return [pscustomobject]@{
                    Result = $attributes["result"]
                    Total = Get-TestRunAttributeInt $attributes "total"
                    Passed = Get-TestRunAttributeInt $attributes "passed"
                    Failed = Get-TestRunAttributeInt $attributes "failed"
                    Inconclusive = Get-TestRunAttributeInt $attributes "inconclusive"
                    Skipped = Get-TestRunAttributeInt $attributes "skipped"
                    Duration = $attributes["duration"]
                }
            }
        }
    }
    finally {
        $reader.Dispose()
    }

    return $null
}

$scriptRoot = Resolve-ScriptRoot
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $scriptRoot "..\..")).Path

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = $repoRoot
}

$ProjectPath = Get-PathForUnityArgument $ProjectPath

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $ProjectPath "Logs\${TestPlatform}Tests"
}

if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

if ([string]::IsNullOrWhiteSpace($LogFile)) {
    $LogFile = Join-Path $OutputDirectory "$($TestPlatform.ToLowerInvariant())-$timestamp.log"
}

if ([string]::IsNullOrWhiteSpace($ResultsFile)) {
    $ResultsFile = Join-Path $OutputDirectory "$($TestPlatform.ToLowerInvariant())-$timestamp.xml"
}

$LogFile = Resolve-OutputFilePath $LogFile
$ResultsFile = Resolve-OutputFilePath $ResultsFile

$resolvedUnityPath = Resolve-UnityEditorPath $UnityPath $UnityVersion

if (-not $SkipProjectLockCheck) {
    Assert-UnityProjectIsAvailable $ProjectPath
}

$unityArgs = @(
    "-batchmode",
    "-projectPath", $ProjectPath,
    "-runTests",
    "-testPlatform", $TestPlatform,
    "-testResults", $ResultsFile,
    "-logFile", $LogFile
)

if ($NoGraphics) {
    $unityArgs += "-nographics"
}

if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
    $unityArgs += @("-testFilter", $TestFilter)
}

if ($ExtraUnityArgs.Count -gt 0) {
    $unityArgs += $ExtraUnityArgs
}

Write-Host "Unity Editor: $resolvedUnityPath"
Write-Host "Project Path: $ProjectPath"
Write-Host "Test Platform: $TestPlatform"
if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
    Write-Host "Test Filter: $TestFilter"
}
Write-Host "Log File: $LogFile"
Write-Host "Results File: $ResultsFile"
Write-Host "Results Wait Timeout: $ResultsWaitTimeoutSeconds second(s)"
Write-Host "Max Test Timeout: $MaxTestSeconds second(s)"
Write-Host "No Log Timeout: $NoLogTimeoutSeconds second(s)"

$exitCode = Invoke-UnityTestsWithTimeout `
    -UnityExe $resolvedUnityPath `
    -WorkingDirectory $ProjectPath `
    -Arguments $unityArgs `
    -UnityLogFile $LogFile `
    -StatusEverySeconds $StatusIntervalSeconds `
    -MaxSeconds $MaxTestSeconds `
    -NoLogSeconds $NoLogTimeoutSeconds

Write-Host "Unity exited with code $exitCode"
Write-Host "Log File: $LogFile"
Write-Host "Results File: $ResultsFile"

if (-not (Wait-ForStableFile $ResultsFile $ResultsWaitTimeoutSeconds $ResultsStableMilliseconds)) {
    if (Test-UnityProjectLockFailureInLog $LogFile) {
        Write-Host "ERROR: Unity did not create a test results file because it hit a startup fatal error before tests ran." -ForegroundColor Red
        Write-Host "A common cause is another Unity Editor instance already opening this project. Check the log for the exact Unity message." -ForegroundColor Yellow
        Write-Host "Log File: $LogFile"
        exit 1
    }

    Write-Host "ERROR: Unity did not create a stable test results file within $ResultsWaitTimeoutSeconds second(s)." -ForegroundColor Red
    Write-Host "Check the log for startup failures before tests ran." -ForegroundColor Red
    Write-Host "Log File: $LogFile"
    exit 1
}

$testRunSummary = Read-UnityTestRunSummary $ResultsFile
if ($null -eq $testRunSummary -or [string]::IsNullOrWhiteSpace($testRunSummary.Result)) {
    Write-Host "ERROR: Test results file exists but the <test-run> summary could not be read." -ForegroundColor Red
    Write-Host "Results File: $ResultsFile"
    exit 1
}

Write-Host ("Test Results: result={0}, total={1}, passed={2}, failed={3}, inconclusive={4}, skipped={5}, duration={6}s" -f `
    $testRunSummary.Result,
    $testRunSummary.Total,
    $testRunSummary.Passed,
    $testRunSummary.Failed,
    $testRunSummary.Inconclusive,
    $testRunSummary.Skipped,
    $testRunSummary.Duration)

if ($testRunSummary.Result -eq "Passed" -and $testRunSummary.Failed -eq 0) {
    if ($exitCode -ne 0) {
        Write-Host "WARNING: Unity exited with code $exitCode, but the test results XML reports all tests passed. Treating the XML result as authoritative." -ForegroundColor Yellow
    }

    exit 0
}

Write-Host "ERROR: EditMode tests did not pass. See the results XML and Unity log for details." -ForegroundColor Red
exit 1
