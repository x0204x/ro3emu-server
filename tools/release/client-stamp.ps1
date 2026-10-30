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
    # Git 會把未追蹤檔與索引檔分段輸出，不能假定合併後已有固定順序。
    # 用 Ordinal 排序，避免同一份來源只因 git add 就改變預建 DLL 的來源戳記。
    # 雙端共用的箭矢補售政策也是 Hooks 編譯輸入；改價時須使預建來源戳記失效。
    [string[]]$files = @(git ls-files --cached --others --exclude-standard -- src/Ro3Emu.ClientHooks src/Ro3Emu.Loc src/Shared/ArrowShopCatalog.cs |
        Where-Object { $_ -notmatch '/(bin|obj)/' })
    [Array]::Sort($files, [StringComparer]::Ordinal)
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
