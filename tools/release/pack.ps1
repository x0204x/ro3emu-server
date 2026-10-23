# Builds the two release archives:
#   dist/RO3Emu-win-x64.zip               RO3Emu.exe + the self-contained server + the client hooks/loc DLLs + README
#   dist/RO3Emu-server-linux-x64.tar.gz   the self-contained server + RO3Emu (export/install) + server.sh + README
# Nothing from the game goes in: the archives are checked against an allowlist and the DLLs' embedded resources are
# checked too, and the build fails if a client DLL, Lua, config JSON or exported data would ship.
#
# The hooks DLL is compiled against the client's own DLLs (the originals from ro3emu-backup when that client is
# patched); prebuilt/client holds the build against the 20 Oct 2026 release, the one the publisher serves. Without a
# client (CI) it takes those committed DLLs, after checking they were built from the current hooks and loc sources.
# Both are built with ContinuousIntegrationBuild, so the same sources and client give the same DLLs on any machine.
#
# usage: pwsh tools/release/pack.ps1 [-ClientDir <game folder>] [-Version 1.0.0] [-UpdatePrebuiltClientDlls]
#        pwsh tools/release/pack.ps1 -UsePrebuiltClientDlls        (CI: no client on the machine)
param(
    [string]$ClientDir = $env:RO3_CLIENT_DIR,
    [string]$Version = '1.0.0',
    [switch]$UsePrebuiltClientDlls,
    [switch]$UpdatePrebuiltClientDlls
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
# Everything is staged in a fresh temp folder; only the two archives land in dist/.
$work = Join-Path ([IO.Path]::GetTempPath()) ("ro3emu-pack-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
Write-Host "staging in $work"

function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "failed ($LASTEXITCODE): $exe $($arguments -join ' ')" }
}

# 1. Client DLLs (hooks + loc).
$clientDlls = Join-Path $work 'client-dlls'
New-Item -ItemType Directory $clientDlls | Out-Null
$prebuilt = Join-Path $root 'prebuilt\client'
if ($UsePrebuiltClientDlls) {
    & (Join-Path $PSScriptRoot 'client-stamp.ps1') -Check
    Copy-Item (Join-Path $prebuilt '*.dll') $clientDlls
} else {
    if (-not (Test-Path (Join-Path $ClientDir 'ro3_Data\Managed\Assembly-CSharp.dll'))) { throw "no client at $ClientDir (or use -UsePrebuiltClientDlls)" }
    # The client's original DLLs: Managed, with the originals of anything RO3Emu patched, without our own DLLs.
    $managed = Join-Path $work 'managed'
    New-Item -ItemType Directory $managed | Out-Null
    Copy-Item (Join-Path $ClientDir 'ro3_Data\Managed\*.dll') $managed
    $backup = Join-Path $ClientDir 'ro3emu-backup'
    if (Test-Path $backup) { Copy-Item (Join-Path $backup '*.dll') $managed -Force }
    Get-ChildItem $managed -Filter 'Ro3Emu.*.dll' | Remove-Item
    Run dotnet @('build', (Join-Path $root 'src\Ro3Emu.ClientHooks'), '-c', 'Release', '--no-incremental', '-nologo', '-v', 'q', "-p:ClientManaged=$managed", '-p:ContinuousIntegrationBuild=true',
        '-o', (Join-Path $work 'hooks-build'))
    Run dotnet @('build', (Join-Path $root 'src\Ro3Emu.Loc'), '-c', 'Release', '--no-incremental', '-nologo', '-v', 'q', "-p:ClientManaged=$managed", '-p:Ro3EmuNoClientData=true',
        '-p:ContinuousIntegrationBuild=true', '-o', (Join-Path $work 'loc-build'))
    Copy-Item (Join-Path $work 'hooks-build\Ro3Emu.ClientHooks.dll') $clientDlls
    Copy-Item (Join-Path $work 'loc-build\Ro3Emu.Loc.dll') $clientDlls
    if ($UpdatePrebuiltClientDlls) {
        New-Item -ItemType Directory -Force $prebuilt | Out-Null
        Copy-Item (Join-Path $clientDlls '*.dll') $prebuilt -Force
        Set-Content (Join-Path $prebuilt 'source.sha256') (& (Join-Path $PSScriptRoot 'client-stamp.ps1')) -NoNewline
        Write-Host "updated prebuilt/client (commit it)"
    }
}

# 2. Publish: self-contained single files, so players and hosts need no .NET.
function Publish([string]$project, [string]$rid, [string]$out) {
    # No IIS web.config and no static web assets manifest: the server is run directly.
    Run dotnet @('publish', (Join-Path $root $project), '-c', 'Release', '-r', $rid, '--self-contained', '-nologo', '-v', 'q',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=embedded', "-p:Version=$Version",
        '-p:IsTransformWebConfigDisabled=true', '-p:StaticWebAssetsEnabled=false', '-o', $out)
    Get-ChildItem $out -Filter '*.pdb' | Remove-Item
}
$win = Join-Path $work 'win\RO3Emu'
Publish 'src\Ro3Emu.Setup' 'win-x64' $win
Publish 'src\Ro3Emu.Server' 'win-x64' (Join-Path $win 'server')
New-Item -ItemType Directory (Join-Path $win 'client') | Out-Null
Copy-Item (Join-Path $clientDlls '*.dll') (Join-Path $win 'client')
Copy-Item (Join-Path $PSScriptRoot 'README-windows.txt') (Join-Path $win 'README.txt')

$linux = Join-Path $work 'linux\RO3Emu-server'
Publish 'src\Ro3Emu.Setup' 'linux-x64' $linux
Publish 'src\Ro3Emu.Server' 'linux-x64' (Join-Path $linux 'server')
Copy-Item (Join-Path $PSScriptRoot 'server.sh') $linux
Copy-Item (Join-Path $PSScriptRoot 'README-linux.txt') (Join-Path $linux 'README.txt')

# 3. Nothing from the game: exactly these files, and the DLLs carry only our own resources.
$allowed = @{
    win = @('RO3Emu.exe', 'README.txt', 'server/Ro3Emu.Server.exe', 'server/appsettings.json', 'server/appsettings.Development.json',
        'client/Ro3Emu.ClientHooks.dll', 'client/Ro3Emu.Loc.dll')
    linux = @('RO3Emu', 'README.txt', 'server.sh', 'server/Ro3Emu.Server', 'server/appsettings.json', 'server/appsettings.Development.json')
}
$forbidden = '(?i)(Assembly-CSharp|UnityEngine|Unity\.TextMeshPro|Happyengine|Phonest|HUD\.dll|xlua|\.lua|\.luac|\.bytes$|\.bundle$|msgtable|loc_en|loc_sids|\.db$|\.pem$|MapResource|sceneeditor|_dynamicmapdata)'
function Check-Files([string]$dir, [string[]]$expected) {
    $actual = Get-ChildItem $dir -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($dir, $_.FullName).Replace('\', '/') } | Sort-Object
    $extra = $actual | Where-Object { $_ -notin $expected }
    $missing = $expected | Where-Object { $_ -notin $actual }
    if ($extra) { throw "unexpected files in $dir (not on the allowlist): $($extra -join ', ')" }
    if ($missing) { throw "missing from $dir`: $($missing -join ', ')" }
    $bad = $actual | Where-Object { $_ -match $forbidden }
    if ($bad) { throw "forbidden (game) files in $dir`: $($bad -join ', ')" }
    $json = $actual | Where-Object { $_ -match '\.json$' -and $_ -notmatch '(^|/)appsettings(\.Development)?\.json$' }
    if ($json) { throw "JSON other than appsettings in $dir`: $($json -join ', ')" }
}
function Resources([string]$dll) {
    $stream = [IO.File]::OpenRead($dll)
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        $md = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        return @($md.ManifestResources | ForEach-Object { $md.GetString($md.GetManifestResource($_).Name) })
    } finally { $stream.Dispose() }
}
Check-Files $win $allowed.win
Check-Files $linux $allowed.linux
$locResources = Resources (Join-Path $win 'client\Ro3Emu.Loc.dll')
if (($locResources -join ',') -ne 'en_overrides.tsv') { throw "Ro3Emu.Loc.dll embeds client text: $($locResources -join ', ') (build it with -p:Ro3EmuNoClientData=true)" }
$textures = Get-ChildItem (Join-Path $root 'src\Ro3Emu.ClientHooks\Textures') -Filter '*.png' | ForEach-Object { 'textures/' + $_.Name }
$hookResources = Resources (Join-Path $win 'client\Ro3Emu.ClientHooks.dll')
$foreign = $hookResources | Where-Object { $_ -notin $textures }
if ($foreign) { throw "Ro3Emu.ClientHooks.dll embeds resources that are not in src/Ro3Emu.ClientHooks/Textures: $($foreign -join ', ')" }
Write-Host "contents checked: no game files"

# 4. Archives (the tar keeps the executable bits Linux needs).
$zip = Join-Path $dist 'RO3Emu-win-x64.zip'
$tgz = Join-Path $dist 'RO3Emu-server-linux-x64.tar.gz'
foreach ($f in @($zip, $tgz)) { if (Test-Path $f) { Remove-Item $f } }
[IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $win), $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

$executables = @('RO3Emu', 'server.sh', 'server/Ro3Emu.Server')
$out = [IO.File]::Create($tgz)
$gzip = [IO.Compression.GZipStream]::new($out, [IO.Compression.CompressionLevel]::Optimal)
$tar = [Formats.Tar.TarWriter]::new($gzip, [Formats.Tar.TarEntryFormat]::Pax, $false)
try {
    $base = Split-Path $linux
    foreach ($file in Get-ChildItem $linux -Recurse -File) {
        $rel = [IO.Path]::GetRelativePath($linux, $file.FullName).Replace('\', '/')
        $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, 'RO3Emu-server/' + $rel)
        $entry.Mode = if ($rel -in $executables) { [IO.UnixFileMode]493 } else { [IO.UnixFileMode]420 } # 0755 / 0644
        $entry.ModificationTime = $file.LastWriteTimeUtc
        $data = [IO.File]::OpenRead($file.FullName)
        try { $entry.DataStream = $data; $tar.WriteEntry($entry) } finally { $data.Dispose() }
    }
} finally { $tar.Dispose(); $gzip.Dispose(); $out.Dispose() }

# 5. The archives once more, as a player would open them.
$z = [IO.Compression.ZipFile]::OpenRead($zip)
try { $inZip = @($z.Entries | Where-Object { $_.Length -gt 0 -or -not $_.FullName.EndsWith('/') } | ForEach-Object { $_.FullName.Replace('\', '/') }) } finally { $z.Dispose() }
$expectedZip = $allowed.win | ForEach-Object { "RO3Emu/$_" }
if ((Compare-Object ($inZip | Sort-Object) ($expectedZip | Sort-Object))) { throw "zip contents differ from the allowlist: $($inZip -join ', ')" }
$inTar = @()
$reader = [Formats.Tar.TarReader]::new([IO.Compression.GZipStream]::new([IO.File]::OpenRead($tgz), [IO.Compression.CompressionMode]::Decompress))
try { while ($null -ne ($e = $reader.GetNextEntry())) { $inTar += $e.Name } } finally { $reader.Dispose() }
$expectedTar = $allowed.linux | ForEach-Object { "RO3Emu-server/$_" }
if ((Compare-Object ($inTar | Sort-Object) ($expectedTar | Sort-Object))) { throw "tar contents differ from the allowlist: $($inTar -join ', ')" }

Write-Host ''
foreach ($f in @($zip, $tgz)) { Write-Host ("{0}  {1:N1} MB" -f $f, ((Get-Item $f).Length / 1MB)) }
Write-Host 'contents:'
$inZip | ForEach-Object { Write-Host "  $_" }
$inTar | ForEach-Object { Write-Host "  $_" }
Write-Host "staging left in $work (delete it when done)"
