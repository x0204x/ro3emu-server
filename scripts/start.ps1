# Developers: build RO3Emu from source and run it with this repository as its home (data\, logs\), the same program
# players get as RO3Emu.exe in the release zip (README "Quick start"). Needs only the .NET 10 SDK: the game data export
# is plain C# now (no Java, Node.js or decompiler).
# usage: scripts\start.cmd [RO3Emu options] | scripts\join.cmd [address]
#        powershell -ExecutionPolicy Bypass -File start.ps1 [-ClientDir <folder with ro3.exe>] [-Reexport] [-Join [-Server <address>]]
#        [any RO3Emu.exe arguments, e.g. host --public 203.0.113.10]
param(
    [string]$ClientDir,
    [switch]$Reexport,
    [switch]$Join,
    [string]$Server,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent # the repository (this script lives in scripts/)

function Fail($text) { Write-Host ''; Write-Host $text -ForegroundColor Red; Read-Host 'Press Enter to close'; exit 1 }
function Run($exe, [string[]]$arguments) {
    $ErrorActionPreference = 'Continue'
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { Fail "Failed: $exe $($arguments -join ' ')" }
}

try { $sdk = (& dotnet --list-sdks 2>$null) -match '^10\.' } catch { $sdk = $null }
if (-not $sdk) {
    Fail "The .NET 10 SDK is needed to build from source (https://dotnet.microsoft.com/download/dotnet/10.0, or winget install Microsoft.DotNet.SDK.10). Players: use RO3Emu.exe from the release zip instead, it needs nothing installed."
}

$clientBuild = @()
if ($ClientDir) {
    # The client hooks and the translation patch compile against this client's DLLs, not the default folder's.
    $ClientDir = (Resolve-Path -LiteralPath $ClientDir).Path
    $managed = Join-Path $ClientDir 'ro3_Data\Managed'
    if (-not (Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll'))) { Fail "No game found in $ClientDir (expected ro3_Data\Managed\Assembly-CSharp.dll)." }
    $env:RO3_CLIENT_DIR = $ClientDir
    $clientBuild = @("-p:ClientManaged=$managed")
}

Write-Host '== Building' -ForegroundColor Cyan
foreach ($project in 'src/Ro3Emu.Server', 'src/Ro3Emu.ClientHooks', 'src/Ro3Emu.Loc', 'src/Ro3Emu.Setup') {
    $extra = if ($project -in 'src/Ro3Emu.ClientHooks', 'src/Ro3Emu.Loc') { $clientBuild } else { @() }
    Run dotnet (@('build', (Join-Path $root $project), '-c', 'Release', '-v', 'q', '-nologo', '-clp:ErrorsOnly') + $extra)
}

$arguments = @()
if ($Join -or $Server) { $arguments += 'join'; if ($Server) { $arguments += $Server } }
if ($Rest) { $arguments += $Rest }
if ($ClientDir) { $arguments += @('--client', $ClientDir) }
if ($Reexport) { $arguments += '--reexport' }
$arguments += @('--home', $root)
& (Join-Path $root 'src\Ro3Emu.Setup\bin\Release\net10.0\RO3Emu.exe') @arguments
exit $LASTEXITCODE
