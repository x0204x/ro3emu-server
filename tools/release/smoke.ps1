# Clean-machine test of the release zip, as a stranger would run it, for each given client:
#   1. extract dist/RO3Emu-win-x64.zip into a fresh temp folder;
#   2. copy only what is needed from the client (ro3.exe, the original managed DLLs, the Lua payload, the VFS index and
#      the VFS files with the walk maps) into that folder: the real client is only read;
#   3. run RO3Emu.exe start --no-launch with a stripped environment (no dotnet, java or node on PATH, no DOTNET_*):
#      it exports the game data, starts the bundled server and patches the copied client;
#   4. checks the export, the patch (server URL in Happyengine.Network.dll, hooks + loc installed) and that the server
#      process loads no machine-wide .NET;
#   5. logs a bot in to the bundled server (src/Ro3Emu.BotCli, built here as the test's own tool) and enters the world;
#   6. RO3Emu.exe join against the same server, RO3Emu.exe restore (the copied DLLs are the originals again), and a
#      clean stop of the server through RO3Emu.exe.
# Nothing is launched from the game (ro3.exe is never started).
#
# usage: pwsh tools/release/smoke.ps1 [-Zip dist/RO3Emu-win-x64.zip] [-Clients <game folder>,...] [-Keep]
param(
    [string]$Zip,
    [string[]]$Clients = @($env:RO3_CLIENT_DIR),
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Zip) { $Zip = Join-Path $root 'dist\RO3Emu-win-x64.zip' }
$Zip = (Resolve-Path $Zip).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("ro3emu-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
Write-Host "smoke test in $work"

# The test's own bot (not part of the release): built with the SDK on this machine.
$botDir = Join-Path $work 'bot'
& dotnet build (Join-Path $root 'src\Ro3Emu.BotCli') -c Release -nologo -v q -o $botDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'BotCli build failed' }
$bot = Join-Path $botDir 'Ro3Emu.BotCli.exe'

function FreePort {
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); return $p
}

# A process with nothing but Windows on PATH and no .NET, Java or Node variables; its output is collected and logged.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
public sealed class SmokeRun
{
    public Process Process;
    public ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
    StreamWriter _log;
    public static SmokeRun Start(string exe, string[] args, string log, string[] keep)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var k in keep) { var v = Environment.GetEnvironmentVariable(k); if (v != null) psi.Environment[k] = v; }
        var root = Environment.GetEnvironmentVariable("SystemRoot");
        psi.Environment["PATH"] = root + @"\System32;" + root + ";" + root + @"\System32\Wbem";
        psi.Environment["DOTNET_ROOT"] = @"C:\no-dotnet-here";
        var run = new SmokeRun { Process = new Process { StartInfo = psi } };
        run._log = new StreamWriter(log) { AutoFlush = true };
        DataReceivedEventHandler h = (s, e) => { if (e.Data != null) { run.Lines.Enqueue(e.Data); lock (run._log) run._log.WriteLine(e.Data); } };
        run.Process.OutputDataReceived += h;
        run.Process.ErrorDataReceived += h;
        run.Process.Start();
        run.Process.BeginOutputReadLine();
        run.Process.BeginErrorReadLine();
        return run;
    }
    public bool Has(string pattern) { return Lines.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, pattern)); }
    public string Tail(int n) { var all = Lines.ToArray(); return string.Join(" | ", all.Skip(Math.Max(0, all.Length - n))); }
    public void Finish(int ms) { Process.WaitForExit(ms); Process.WaitForExit(); lock (_log) _log.Dispose(); }
}
'@
$keepVars = @('SystemRoot', 'SystemDrive', 'windir', 'TEMP', 'TMP', 'USERPROFILE', 'USERNAME', 'APPDATA', 'LOCALAPPDATA', 'ProgramData',
    'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'COMPUTERNAME', 'NUMBER_OF_PROCESSORS', 'PROCESSOR_ARCHITECTURE', 'OS', 'PATHEXT', 'ComSpec', 'HOMEDRIVE', 'HOMEPATH')
function StrippedStart([string]$exe, [string[]]$arguments, [string]$log) { [SmokeRun]::Start($exe, $arguments, $log, $keepVars) }

function WaitFor($run, [string]$pattern, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ($run.Has($pattern)) { return $true }
        if ($run.Process.HasExited) { Start-Sleep -Milliseconds 500; return $run.Has($pattern) }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Finish($run) { $run.Finish(90000) }

function Sha([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash }

# A string literal in a DLL (UTF-16 in the #US heap, at an even or odd offset).
function Utf16Contains([string]$file, [string]$text) {
    $bytes = [IO.File]::ReadAllBytes($file)
    return ([Text.Encoding]::Unicode.GetString($bytes).Contains($text) -or [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1).Contains($text))
}

$results = @()
$n = 0
foreach ($client in $Clients) {
    $n++
    $name = "client$n"
    $case = Join-Path $work $name
    $result = [ordered]@{ client = $client; steps = [ordered]@{} }
    $results += $result
    function Pass([string]$step, [string]$detail = '') { $result.steps[$step] = "ok $detail".Trim(); Write-Host "  [ok] $step $detail" -ForegroundColor Green }
    function Fail([string]$step, [string]$detail) { $result.steps[$step] = "FAILED $detail"; Write-Host "  [FAILED] $step $detail" -ForegroundColor Red; throw "$step failed: $detail" }
    Write-Host "`n== $client" -ForegroundColor Cyan
    try {
        # 1. A fresh copy of the release.
        $app = Join-Path $case 'app'
        [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $app)
        $exe = Join-Path $app 'RO3Emu\RO3Emu.exe'
        Pass 'extract' ((Get-ChildItem $app -Recurse -File).Count.ToString() + ' files')

        # 2. The client files RO3Emu needs, copied; the managed DLLs as the publisher ships them.
        $game = Join-Path $case 'game'
        $managed = Join-Path $game 'ro3_Data\Managed'
        New-Item -ItemType Directory -Force $managed | Out-Null
        Copy-Item (Join-Path $client 'ro3.exe') $game
        Copy-Item (Join-Path $client 'ro3_Data\Managed\*') $managed
        if (Test-Path (Join-Path $client 'ro3emu-backup')) { Copy-Item (Join-Path $client 'ro3emu-backup\*.dll') $managed -Force }
        Get-ChildItem $managed -Filter 'Ro3Emu.*.dll' | Remove-Item
        $originals = @{}
        Get-ChildItem $managed -File | ForEach-Object { $originals[$_.Name] = Sha $_.FullName }
        $release = Join-Path $client '.ro3-update\installed-release.txt'
        if (Test-Path $release) { New-Item -ItemType Directory -Force (Join-Path $game '.ro3-update') | Out-Null; Copy-Item $release (Join-Path $game '.ro3-update') }
        $payload = 'ro3_Data\StreamingAssets\Recovery\LuaPayload'
        & robocopy (Join-Path $client $payload) (Join-Path $game $payload) /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { Fail 'copy client' "robocopy $LASTEXITCODE" }
        # Which VFS files hold the walk maps: an export of the real client into a scratch folder (read only) lists them.
        $listing = & $exe export $client (Join-Path $case 'inputs-data') --list-inputs --home (Join-Path $case 'inputs-home') --yes
        $vfs = @($listing | Where-Object { $_ -like 'input *' } | ForEach-Object { $_.Substring(6) } | Where-Object { $_ -match '\\VFS\\' })
        foreach ($f in $vfs) {
            $target = Join-Path $game ([IO.Path]::GetRelativePath($client, $f))
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item $f $target
        }
        $copied = (Get-ChildItem $game -Recurse -File | Measure-Object Length -Sum)
        Pass 'copy client' ("{0} files, {1:N0} MB ({2} VFS files)" -f $copied.Count, ($copied.Sum / 1MB), $vfs.Count)

        # 3. RO3Emu.exe start, stripped environment.
        $rhome = Join-Path $case 'home'
        $ports = @((FreePort), (FreePort), (FreePort))
        $check = StrippedStart $exe @('check', '--client', $game, '--home', $rhome, '--yes') (Join-Path $case 'check.log')
        Finish $check
        if ($check.Process.ExitCode -ne 0) { Fail 'check' $check.Tail(20) }
        Pass 'check' 'patch targets and every hook reference resolve'

        $whereLog = Join-Path $case 'where.log'
        $where = StrippedStart "$env:SystemRoot\System32\cmd.exe" @('/c', 'where dotnet & where java & where node & echo PATH=%PATH%') $whereLog
        Finish $where
        $found = @($where.Lines.ToArray()) | Where-Object { $_ -match '\\(dotnet|java|node)(\.exe)?$' }
        if ($found) { Fail 'stripped environment' ($found -join ', ') }
        Pass 'stripped environment' 'no dotnet, java or node reachable'

        $watch = [Diagnostics.Stopwatch]::StartNew()
        $run = StrippedStart $exe @('start', '--client', $game, '--home', $rhome, '--no-launch', '--yes',
            '--http-port', $ports[0], '--micro-port', $ports[1], '--game-port', $ports[2]) (Join-Path $case 'start.log')
        if (-not (WaitFor $run 'Server running' 600)) { Fail 'start' ("did not reach 'Server running': " + $run.Tail(15)) }
        Pass 'start' ("export + server + patch in {0:N0} s" -f $watch.Elapsed.TotalSeconds)

        # 4. What it produced.
        $data = Join-Path $rhome 'data'
        $stamp = Get-Content (Join-Path $data 'export.json') -Raw | ConvertFrom-Json
        $tables = (Get-ChildItem (Join-Path $data 'config') -Filter '*.json').Count
        $nav = (Get-ChildItem (Join-Path $data 'nav')).Count
        $msgs = (Get-Content (Join-Path $data 'msgtable.tsv')).Count
        if ($tables -lt 400 -or $nav -lt 100 -or $msgs -lt 1000) { Fail 'export' "tables $tables, nav $nav, messages $msgs" }
        if (@($stamp.FailedTables).Count -gt 0) { Fail 'export' ('incomplete, tables not exported: ' + (@($stamp.FailedTables) -join ', ')) }
        Pass 'export' "tables $tables, nav $nav, messages $msgs, skills $((Get-ChildItem (Join-Path $data 'skills')).Count), sceneeditor $((Get-ChildItem (Join-Path $data 'sceneeditor')).Count)"

        $url = "http://127.0.0.1:$($ports[0])"
        $network = Join-Path $managed 'Happyengine.Network.dll'
        if (-not (Utf16Contains $network "$url/sdk/login")) { Fail 'patch' "no $url/sdk/login in Happyengine.Network.dll" }
        foreach ($f in @('Ro3Emu.ClientHooks.dll', 'Ro3Emu.Loc.dll')) { if (-not (Test-Path (Join-Path $managed $f))) { Fail 'patch' "$f not installed" } }
        if (-not (Test-Path (Join-Path $game 'ro3emu\loc_en.tsv'))) { Fail 'patch' 'no ro3emu\loc_en.tsv' }
        $loc = (Get-Content (Join-Path $game 'ro3emu\loc_en.tsv')).Count
        Pass 'patch' "client points at $url; hooks, loc and $loc English texts installed"

        $server = Get-Process Ro3Emu.Server -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$app*" }
        if (-not $server) { Fail 'server process' 'not found' }
        $machineDotnet = @($server.Modules | Where-Object { $_.FileName -like '*\dotnet\shared\*' -or $_.FileName -like '*\Program Files\dotnet\*' })
        if ($machineDotnet) { Fail 'self-contained' ($machineDotnet.FileName -join ', ') }
        Pass 'self-contained' "server pid $($server.Id) loads no machine-wide .NET ($($server.Modules.Count) modules)"

        # 5. A bot logs in and enters the world.
        $botName = "Smoke$n"
        $botOut = 'wait 2' | & $bot $botName $url $data 2>&1
        $line = @($botOut) -match "^$botName \(\d+\) in world at"
        if (-not $line) { Fail 'bot login' (@($botOut) -join ' | ') }
        Pass 'bot login' $line[0]

        # 6. Join (patch only, no launch) against the same server, then restore, then stop.
        $join = StrippedStart $exe @('join', "127.0.0.1:$($ports[0])", '--client', $game, '--home', $rhome, '--no-launch', '--yes') (Join-Path $case 'join.log')
        Finish $join
        if ($join.Process.ExitCode -ne 0 -or -not (Utf16Contains $network "$url/sdk/login")) { Fail 'join' $join.Tail(20) }
        Pass 'join' 'patched for the server by address'

        $run.Process.StandardInput.WriteLine('quit')
        $run.Process.StandardInput.Flush()
        Finish $run
        $stopped = -not (Get-Process -Id $server.Id -ErrorAction SilentlyContinue)
        if ($run.Process.ExitCode -ne 0 -or -not $stopped) { Fail 'stop' "exit $($run.Process.ExitCode), server stopped $stopped" }
        $saved = $run.Has('Your characters are saved')
        Pass 'stop' "RO3Emu exit 0, server stopped cleanly$(if ($saved) { '' } else { ' (no save message)' })"

        $restore = StrippedStart $exe @('restore', '--client', $game, '--home', $rhome, '--yes') (Join-Path $case 'restore.log')
        Finish $restore
        $changed = @($originals.Keys | Where-Object { (Sha (Join-Path $managed $_)) -ne $originals[$_] })
        $leftovers = @(Get-ChildItem $managed -Filter 'Ro3Emu.*.dll') + @(Get-ChildItem (Join-Path $game 'ro3emu') -Filter 'loc_en.tsv' -ErrorAction SilentlyContinue)
        if ($restore.Process.ExitCode -ne 0 -or $changed -or $leftovers) { Fail 'restore' "changed: $($changed -join ', '); left: $($leftovers.Name -join ', ')" }
        Pass 'restore' 'managed DLLs identical to the originals again'
        $result.ok = $true
    } catch {
        $result.ok = $false
        $result.error = $_.Exception.Message
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        Get-Process Ro3Emu.Server, RO3Emu -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$case*" } | Stop-Process -Force
    }
}

Write-Host "`n== results" -ForegroundColor Cyan
$results | ForEach-Object { Write-Host ("{0}: {1}" -f $_.client, $(if ($_.ok) { 'PASS' } else { 'FAIL - ' + $_.error })) }
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $work 'results.json')
Write-Host "logs and results: $work"
if (-not $Keep) {
    # Only the copied game files go (they are large); logs, exports and results stay for reading.
    foreach ($i in 1..$n) { $g = Join-Path $work "client$i\game"; if (Test-Path $g) { Remove-Item -Recurse -Force $g } }
}
if ($results | Where-Object { -not $_.ok }) { exit 1 }
