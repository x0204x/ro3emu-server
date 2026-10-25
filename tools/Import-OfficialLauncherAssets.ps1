param(
    [Parameter(Mandatory = $true)]
    [string] $ReferenceDirectory
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $ReferenceDirectory 'assets'
$target = Join-Path $PSScriptRoot '..\src\Ro3Emu.Launcher\PrivateAssets\Assets'

if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "找不到已驗證的啟動器資產目錄：$source"
}

$source = (Resolve-Path -LiteralPath $source).Path
$targetParent = Split-Path -Parent $target
New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
}
Copy-Item -LiteralPath $source -Destination $target -Recurse

$icon = Join-Path $ReferenceDirectory 'app.ico'
if (Test-Path -LiteralPath $icon -PathType Leaf) {
    Copy-Item -LiteralPath $icon -Destination (Join-Path $target 'app.ico') -Force
}

$count = (Get-ChildItem -LiteralPath $target -File -Recurse).Count
Write-Host "已匯入 $count 個本機私有介面資產到 $target"
