# One unattended run of everything CI cannot do without the client: the whole server suite against a local export, and
# the projects that build only against the client (ClientHooks, Loc, ClientTools). Takes -Ref in its own worktree under
# -Root and never touches the checkout in -Repo beyond a fetch. A failing test is run again alone, so a test that passes
# then is reported as flaky rather than hidden or called broken. Each run gets a folder in -Root\runs with report.txt,
# summary.json, tests.json, the TRX, the logs and the message coverage (RO3EMU_MSG_COVERAGE). Exit code 1 means red.
#
# It refuses to start while a test host or the game client runs, or with little memory or disk: two full suites at once
# have brought a 32 GB machine down. A refusal is reported too, at most once a day.
#
# Notifications go to ntfy with NTFY_TOPIC and optionally NTFY_SERVER, NTFY_PRIORITY, NTFY_TOKEN or NTFY_USERNAME and
# NTFY_PASSWORD from the -NotifyEnv file. Without a topic the report is only written to disk, and says so.
#
# usage: pwsh tools/nightly-tests.ps1 -Repo <checkout> -Client <ROO_PC folder> [-Root <folder>] [-Data <export>] [-Ref origin/main]
# tools/install-nightly-task.ps1 runs it every morning (docs/guide/testing.md).
param(
    [string]$Repo = (Split-Path $PSScriptRoot -Parent),
    [string]$Client = $env:RO3_CLIENT_DIR,
    [string]$Root,
    [string]$Data,
    [string]$Ref = 'origin/main',
    [string]$NotifyEnv = (Join-Path $HOME '.codex/notify-secrets.env'),
    [double]$MinFreeMemoryGB = 16,
    [double]$MinFreeDiskGB = 15,
    # A run the schedule missed starts as the machine comes up: wait until it has been up this long.
    [int]$BootDelayMinutes = 0,
    [int]$TimeoutMinutes = 60,
    [int]$KeepRuns = 14,
    [int]$KeepDumps = 3
)
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Join-Path (Split-Path $Repo -Parent) 'ro3emu-nightly' }
if (-not $Data) { $Data = Join-Path $Repo 'data' }
$runs = Join-Path $Root 'runs'
$worktree = Join-Path $Root 'worktree'
New-Item -ItemType Directory -Force $runs | Out-Null

function Read-Env([string]$path) {
    $values = @{}
    if (Test-Path -LiteralPath $path) {
        foreach ($line in Get-Content -LiteralPath $path) {
            if ($line -match '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') {
                $values[$Matches[1]] = $Matches[2].Trim('"', "'")
            }
        }
    }
    $values
}

# Returns what happened, for the report. Never lets a failed notification end the run, and never shows the credentials.
function Send-Notice([string]$title, [string]$body, [string]$priority, [string]$tags) {
    $config = Read-Env $NotifyEnv
    if (-not $config['NTFY_TOPIC']) { return "not sent: no NTFY_TOPIC in $NotifyEnv" }
    $server = ($config['NTFY_SERVER'] ? $config['NTFY_SERVER'] : 'https://ntfy.sh').TrimEnd('/')
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($title))
    $headers = @{ Title = "=?UTF-8?B?${encoded}?="; Priority = $priority; Tags = $tags }
    if ($config['NTFY_TOKEN']) { $headers.Authorization = "Bearer $($config['NTFY_TOKEN'])" }
    elseif ($config['NTFY_USERNAME']) {
        $pair = [Text.Encoding]::UTF8.GetBytes("$($config['NTFY_USERNAME']):$($config['NTFY_PASSWORD'])")
        $headers.Authorization = "Basic $([Convert]::ToBase64String($pair))"
    }
    try {
        $text = [Text.Encoding]::UTF8.GetBytes($body.Length -gt 3500 ? $body.Substring(0, 3500) : $body)
        Invoke-WebRequest -Method Post -Uri "$server/$($config['NTFY_TOPIC'])" -Headers $headers -Body $text -SkipHeaderValidation `
            -TimeoutSec 30 -UseBasicParsing | Out-Null
        'sent'
    }
    catch { "not sent: $($_.Exception.GetType().Name)" }
}

# A refusal is told once a day, so that a machine busy every morning does not repeat itself.
function Skip([string]$why) {
    $stamp = Join-Path $Root 'last-skip.txt'
    $today = (Get-Date).ToString('yyyy-MM-dd')
    $told = (Test-Path -LiteralPath $stamp) -and (Get-Content -LiteralPath $stamp -Raw).Trim() -eq $today
    $notice = $told ? 'not sent: told once today' : (Send-Notice 'RO3Emu nightly: skipped' $why 'low' 'fast_forward')
    Set-Content -LiteralPath $stamp -Value $today
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm') skipped: $why (notification: $notice)"
    Add-Content -LiteralPath (Join-Path $Root 'skipped.log') -Value $line
    Write-Output $line
    exit 0
}

function Why-Not-Now {
    $busy = @(Get-Process testhost -ErrorAction SilentlyContinue).Count
    if ($busy -gt 0) { return "$busy test host(s) running" }
    if (Get-Process ro3 -ErrorAction SilentlyContinue) { return 'the game client is running' }
    if ($IsWindows) {
        $os = Get-CimInstance Win32_OperatingSystem
        $free = $os.FreeVirtualMemory / 1MB
        if ($free -lt $MinFreeMemoryGB) { return ('{0:N1} GB of memory free, needs {1}' -f $free, $MinFreeMemoryGB) }
    }
    foreach ($place in @($Root, [IO.Path]::GetTempPath())) {
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $place).Path))
        $free = $drive.AvailableFreeSpace / 1GB
        if ($free -lt $MinFreeDiskGB) { return ('{0:N1} GB free on {1}, needs {2}' -f $free, $drive.Name, $MinFreeDiskGB) }
    }
}

# Runs a command with its output in a log. Returns the exit code, or -1 when it ran out of time and was killed.
function Invoke-Logged([string]$arguments, [string]$log, [int]$minutes) {
    $p = Start-Process dotnet -ArgumentList $arguments -WorkingDirectory $worktree -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $p.Handle
    if ($p.WaitForExit($minutes * 60000)) { return $p.ExitCode }
    & taskkill /T /F /PID $p.Id 2>&1 | Out-Null
    -1
}

function Read-Trx([string]$path) {
    $results = @{}
    $errors = @{}
    if (-not (Test-Path -LiteralPath $path)) { return @{ Found = $false; Results = $results; Errors = $errors } }
    [xml]$doc = Get-Content -LiteralPath $path -Raw
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    foreach ($node in (Select-Xml -Xml $doc -Namespace $ns -XPath '//t:UnitTestResult' | ForEach-Object Node)) {
        $results[$node.testName] = $node.outcome
        if ($node.outcome -eq 'Failed') {
            $message = "$($node.Output.ErrorInfo.Message)".Trim() -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 3
            $errors[$node.testName] = ($message -join ' ')
        }
    }
    @{ Found = $true; Results = $results; Errors = $errors }
}

function Clip([string]$text, [int]$length) { $text.Length -le $length ? $text : $text.Substring(0, $length) + '...' }

# The newest folders of a run stay; a dump is several GB, so only the newest few runs keep theirs.
function Clear-OldRuns {
    $all = @(Get-ChildItem -LiteralPath $runs -Directory | Where-Object { -not $_.LinkType } | Sort-Object Name -Descending)
    $all | Select-Object -Skip $KeepRuns | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    $all | Select-Object -First $KeepRuns | Select-Object -Skip $KeepDumps | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Recurse -Filter '*.dmp' -File | Remove-Item -Force
    }
}

# A warning when the client changed after its export: `RO3Emu.exe export` is run by hand.
function Export-Warning {
    $export = Join-Path $Data 'config/PublicParamConfig.json'
    if (-not (Test-Path -LiteralPath $export)) { return "no export in $Data" }
    if (-not $Client -or -not (Test-Path -LiteralPath $Client)) { return $null }
    # Not the managed DLLs: patching the client rewrites those.
    $marks = @('ro3.exe', 'UnityPlayer.dll', 'ro3_Data/globalgamemanagers') | ForEach-Object { Join-Path $Client $_ } | Where-Object { Test-Path -LiteralPath $_ }
    $newest = @($marks | ForEach-Object { (Get-Item -LiteralPath $_).LastWriteTime })
    $payload = Join-Path $Client 'Recovery/LuaPayload'
    if (Test-Path -LiteralPath $payload) { $newest += (Get-Item -LiteralPath $payload).LastWriteTime }
    $changed = ($newest | Sort-Object -Descending | Select-Object -First 1)
    $exported = (Get-Item -LiteralPath $export).LastWriteTime
    if ($changed -gt $exported) { return "the client ($($changed.ToString('yyyy-MM-dd'))) is newer than its export ($($exported.ToString('yyyy-MM-dd'))): run RO3Emu.exe export" }
}

$lock = [Threading.Mutex]::new($false, 'Local\Ro3EmuNightlyTests')
if (-not $lock.WaitOne(0)) { Write-Output 'another nightly run is active'; exit 0 }

if ($BootDelayMinutes -gt 0) {
    $wait = [TimeSpan]::FromMinutes($BootDelayMinutes) - [TimeSpan]::FromMilliseconds([Environment]::TickCount64)
    if ($wait -gt [TimeSpan]::Zero) { Start-Sleep -Seconds ([int]$wait.TotalSeconds) }
}
$reason = Why-Not-Now
if ($reason) { Skip $reason }

$started = Get-Date
$process = [Diagnostics.Process]::GetCurrentProcess()
# Children inherit below normal, so a game or a build next to this run stays smooth.
if ($IsWindows) { $process.PriorityClass = 'BelowNormal' }
$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
if ($Client) { $env:RO3_CLIENT_DIR = $Client }

$status = 'green'
$lines = [Collections.Generic.List[string]]::new()
$sha = 'unknown'
$run = $null
try {
    & git -C $Repo fetch --quiet origin main
    if ($LASTEXITCODE -ne 0) { throw 'git fetch failed' }
    if (-not (Test-Path -LiteralPath $worktree)) {
        & git -C $Repo worktree prune
        & git -C $Repo worktree add --quiet --detach $worktree $Ref
    }
    else { & git -C $worktree checkout --quiet --force --detach $Ref }
    if ($LASTEXITCODE -ne 0) { throw "git could not check out $Ref" }
    $sha = (& git -C $worktree rev-parse --short=8 HEAD).Trim()

    $link = Join-Path $worktree 'data'
    if (Test-Path -LiteralPath $link) {
        $item = Get-Item -LiteralPath $link -Force
        if (-not $item.LinkType) { throw "$link is a real folder, not a link" }
        if ($item.Target -ne (Resolve-Path -LiteralPath $Data).Path) { [IO.Directory]::Delete($link, $false) }
    }
    if (-not (Test-Path -LiteralPath $link)) {
        if ($IsWindows) { New-Item -ItemType Junction -Path $link -Target $Data | Out-Null }
        else { New-Item -ItemType SymbolicLink -Path $link -Target $Data | Out-Null }
    }

    $run = Join-Path $runs ('{0}-{1}' -f $started.ToString('yyyyMMdd-HHmmss'), $sha)
    New-Item -ItemType Directory $run | Out-Null
    $env:RO3EMU_MSG_COVERAGE = Join-Path $run 'msg-coverage.json'

    $warning = Export-Warning
    if ($warning) { $lines.Add("warning: $warning") }

    $results = Join-Path $run 'results'
    $testArgs = "test tests/Ro3Emu.Tests -c Release --logger `"trx;LogFileName=server.trx`" --results-directory `"$results`" --blame-hang-timeout 10m --blame-hang-dump-type mini"
    $exit = Invoke-Logged $testArgs (Join-Path $run 'tests.log') $TimeoutMinutes
    $trx = Read-Trx (Join-Path $results 'server.trx')
    $outcomes = $trx.Results
    $failed = @($outcomes.Keys | Where-Object { $outcomes[$_] -eq 'Failed' } | Sort-Object)
    $executed = @($outcomes.Values | Where-Object { $_ -in 'Passed', 'Failed' }).Count
    $skipped = $outcomes.Count - $executed
    $broken = [Collections.Generic.List[string]]::new()
    $flaky = [Collections.Generic.List[string]]::new()

    if ($exit -eq -1) { $status = 'red'; $lines.Add("the test run took over $TimeoutMinutes minutes and was stopped") }
    if (-not $trx.Found) {
        $status = 'red'
        $tail = Get-Content -LiteralPath (Join-Path $run 'tests.log') -ErrorAction SilentlyContinue | Where-Object { $_ -match ' error |Failed|Exception' } | Select-Object -First 2
        $lines.Add("no test results (exit $exit): $(Clip ($tail -join ' ') 300)")
    }
    elseif ($exit -ne 0 -and $failed.Count -eq 0) {
        $status = 'red'
        $lines.Add("the test run ended with exit $exit and no failed test: a crash or a hang (see tests.log and the Sequence file)")
    }
    if ($trx.Found -and $skipped -gt 100) {
        $status = 'red'
        $lines.Add("$skipped tests skipped: they did not see the client's export in $Data")
    }

    # Each failed test once more, alone: passing then makes it flaky.
    if ($failed.Count -gt 0 -and $failed.Count -le 30) {
        $methods = @($failed | ForEach-Object { ($_ -replace '\(.*$', '') } | Sort-Object -Unique)
        $filter = ($methods | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
        $againDir = Join-Path $run 'rerun'
        $againExit = Invoke-Logged "test tests/Ro3Emu.Tests -c Release --no-build --filter `"$filter`" --logger `"trx;LogFileName=rerun.trx`" --results-directory `"$againDir`" --blame-hang-timeout 10m" `
            (Join-Path $run 'rerun.log') 20
        $again = (Read-Trx (Join-Path $againDir 'rerun.trx')).Results
        foreach ($name in $failed) {
            if ($again[$name] -eq 'Passed') { $flaky.Add($name) } else { $broken.Add($name) }
        }
        if ($againExit -eq -1) { $lines.Add('the rerun of the failed tests ran out of time') }
    }
    else { $failed | ForEach-Object { $broken.Add($_) } }
    if ($broken.Count -gt 0) { $status = 'red' }
    elseif ($flaky.Count -gt 0 -and $status -eq 'green') { $status = 'flaky' }

    # What CI cannot build: these compile against the client.
    $builds = [ordered]@{}
    if ($Client -and (Test-Path -LiteralPath $Client)) {
        foreach ($project in 'Ro3Emu.ClientHooks', 'Ro3Emu.Loc', 'Ro3Emu.ClientTools') {
            $log = Join-Path $run "build-$project.log"
            $code = Invoke-Logged "build src/$project -c Release -nologo -v q" $log 15
            $builds[$project] = $code
            if ($code -ne 0) {
                $status = 'red'
                $first = Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | Where-Object { $_ -match ' error ' } | Select-Object -First 1
                $lines.Add("build of $project failed: $(Clip "$first" 300)")
            }
        }
    }
    else { $lines.Add("client builds not run: no client in '$Client'") }

    # Against the last run: what changed, and how often a test has been flaky lately.
    $previous = Get-ChildItem -LiteralPath $runs -Directory | Where-Object { $_.FullName -ne $run -and (Test-Path -LiteralPath (Join-Path $_.FullName 'tests.json')) } |
        Sort-Object Name -Descending | Select-Object -First 1
    $flips = @()
    if ($previous) {
        $before = Get-Content -LiteralPath (Join-Path $previous.FullName 'tests.json') -Raw | ConvertFrom-Json -AsHashtable
        $flips = @($outcomes.Keys | Where-Object { $before.ContainsKey($_) -and $before[$_] -ne $outcomes[$_] -and $before[$_] -in 'Passed', 'Failed' -and $outcomes[$_] -in 'Passed', 'Failed' } | Sort-Object)
        $gone = @($before.Keys | Where-Object { -not $outcomes.ContainsKey($_) }).Count
        if ($gone -gt 0 -and $trx.Found) { $lines.Add("$gone tests of the last run are gone") }
    }
    $history = @{}
    Get-ChildItem -LiteralPath $runs -Directory | Where-Object { $_.FullName -ne $run } | ForEach-Object {
        $file = Join-Path $_.FullName 'summary.json'
        if (Test-Path -LiteralPath $file) {
            foreach ($name in @((Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).flaky)) { if ($name) { $history[$name] = 1 + ($history[$name] ?? 0) } }
        }
    }

    $outcomes | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $run 'tests.json')
    $minutes = [int]((Get-Date) - $started).TotalMinutes
    $head = "$sha`: $executed tests, $($broken.Count) failed, $($flaky.Count) flaky, $skipped skipped, $minutes min"
    foreach ($name in ($broken | Select-Object -First 15)) { $lines.Add("FAILED $name`: $(Clip "$($trx.Errors[$name])" 240)") }
    if ($broken.Count -gt 15) { $lines.Add("and $($broken.Count - 15) more") }
    foreach ($name in $flaky) { $lines.Add("flaky $name (passed alone; earlier in the kept runs: $($history[$name] ?? 0)): $(Clip "$($trx.Errors[$name])" 160)") }
    foreach ($name in ($flips | Where-Object { $_ -notin $broken -and $_ -notin $flaky } | Select-Object -First 10)) { $lines.Add("changed since the last run: $name $($before[$name]) to $($outcomes[$name])") }

    [ordered]@{
        sha = $sha; ref = $Ref; started = $started.ToString('o'); minutes = $minutes; status = $status
        executed = $executed; skipped = $skipped; failed = @($broken); flaky = @($flaky); flips = $flips; builds = $builds
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'summary.json')
}
catch {
    $status = 'red'
    $head = "$sha`: the nightly run itself failed"
    $lines.Add("$($_.Exception.Message)")
}

$body = (@($head) + $lines) -join "`n"
$title = "RO3Emu nightly: $status $sha"
$notice = switch ($status) {
    'green' { Send-Notice $title $body 'low' 'white_check_mark' }
    'flaky' { Send-Notice $title $body 'default' 'warning' }
    default { Send-Notice $title $body 'high' 'x' }
}
$report = "$title`n$body`n`nnotification: $notice`n"
if ($run) { Set-Content -LiteralPath (Join-Path $run 'report.txt') -Value $report }
Set-Content -LiteralPath (Join-Path $Root 'latest.txt') -Value $report
Write-Output $report
Clear-OldRuns
exit ($status -eq 'red' ? 1 : 0)
