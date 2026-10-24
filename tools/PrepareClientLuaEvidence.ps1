[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ClientDir,

    [Parameter(Mandatory)]
    [string] $UnluacJar,

    [string] $JavaExe = 'java'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $ClientDir 'ro3_Data\StreamingAssets\Recovery\LuaPayload'
$sourceRelative = 'Logic\Asset\LC_DAL_MVP.lua.bytes'
$source = Join-Path $payload $sourceRelative
$evidence = Join-Path $repo 're\clientB'
$luacDir = Join-Path $evidence 'luac\Logic\Asset'
$luaDir = Join-Path $evidence 'lua-src\Logic\Asset'
$decoded = Join-Path $luacDir 'LC_DAL_MVP.luac'
$output = Join-Path $luaDir 'LC_DAL_MVP.lua'

foreach ($required in @($source, $UnluacJar)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required file is missing: $required"
    }
}

New-Item -ItemType Directory -Force -Path $luacDir, $luaDir | Out-Null

# LuaDecode mirrors its input tree. Decode the Asset directory into the ignored evidence tree, then use only the one
# module required by the D test. Client files remain read-only.
& dotnet run --project (Join-Path $repo 'tools\LuaDecode\LuaDecode.csproj') -c Release -- `
    (Split-Path -Parent $source) $luacDir
if ($LASTEXITCODE -ne 0) { throw "LuaDecode failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $decoded -PathType Leaf)) { throw "Decoded Lua is missing: $decoded" }

& $JavaExe -jar $UnluacJar --output $output $decoded
if ($LASTEXITCODE -ne 0) { throw "unluac failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw "Decompiled Lua is missing: $output" }

$receipt = [ordered]@{
    Format = 1
    ClientDir = (Resolve-Path -LiteralPath $ClientDir).Path
    SourceRelativePath = $sourceRelative.Replace('\', '/')
    SourceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
    OutputSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $output).Hash
    UnluacSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $UnluacJar).Hash
    GeneratedUtc = [DateTime]::UtcNow.ToString('O')
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'decompile.json') -Encoding utf8
$receipt | Format-List
