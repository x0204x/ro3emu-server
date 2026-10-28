# The stamp of the hooks and loc sources, as git hashes them (line endings normalized). prebuilt/client/source.sha256
# holds the stamp of the sources its DLLs were built from.
#
# usage: pwsh tools/release/client-stamp.ps1          prints the stamp of the current sources
#        pwsh tools/release/client-stamp.ps1 -Check   fails when prebuilt/client was built from other sources
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

Push-Location $root
try {
    # Include ordinary untracked project inputs as well as tracked files.  A new
    # hook source must change the stamp before it is staged; otherwise -Check
    # could incorrectly bless DLLs that were built without that source file.
    # git ls-files merges cache and worktree entries in Git's canonical path
    # order, so the stamp is stable before and after a new input is staged.
    $files = @(git ls-files --cached --others --exclude-standard -- src/Ro3Emu.ClientHooks src/Ro3Emu.Loc |
        Where-Object { $_ -notmatch '/(bin|obj)/' })
    if ($files.Count -eq 0) {
        throw 'No Ro3Emu.ClientHooks/Ro3Emu.Loc source inputs were found.'
    }
    $hashes = @($files | git hash-object --stdin-paths)
    $text = (0..($files.Count - 1) | ForEach-Object { "$($files[$_]) $($hashes[$_])" }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))
    $current = [Convert]::ToHexStringLower($sha)
} finally { Pop-Location }

if (-not $Check) { return $current }
$stamp = (Get-Content (Join-Path $root 'prebuilt\client\source.sha256') -Raw).Trim()
if ($stamp -ne $current) {
    throw "prebuilt/client was built from other hooks/loc sources ($stamp, now $current). On a machine with the current client run: pwsh tools/release/pack.ps1 -UpdatePrebuiltClientDlls, then commit prebuilt/client."
}
Write-Host "prebuilt/client matches the hooks and loc sources ($current)"
