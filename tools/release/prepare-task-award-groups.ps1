#requires -Version 7.0
# Prepare a new MultiverseGroupConfig candidate from two local client exports.
# No game data is embedded in this tool. It never changes an export or an existing output.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDataDirectory,
    [Parameter(Mandatory)][string]$TargetDataDirectory,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-UniqueProperties([System.Text.Json.JsonElement]$Row, [string]$Label) {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($property in $Row.EnumerateObject()) {
        if (-not $names.Add($property.Name)) { throw "$Label contains a duplicate property." }
    }
}

function Read-Table([string]$Directory, [string]$Name, [bool]$Grouped = $false) {
    $path = Join-Path $Directory "config/$Name.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing table: $path" }
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($path))
    try {
        if ($document.RootElement.ValueKind -ne 'Object') { throw "$Name must be a JSON object." }
        $rows = [Collections.Generic.Dictionary[string, System.Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
        foreach ($property in $document.RootElement.EnumerateObject()) {
            $id = 0L
            if (-not [long]::TryParse($property.Name, [ref]$id) -or $id -lt 0 -or "$id" -cne $property.Name) {
                throw "$Name contains a noncanonical integer key."
            }
            if ($rows.ContainsKey($property.Name)) { throw "$Name contains a duplicate key." }
            $expected = if ($Grouped) { 'Array' } else { 'Object' }
            if ($property.Value.ValueKind -ne $expected) { throw "$Name row $id must be a JSON $expected." }
            if (-not $Grouped) { Assert-UniqueProperties $property.Value "$Name row $id" }
            $rows.Add($property.Name, $property.Value)
        }
        return [pscustomobject]@{ Path = $path; Document = $document; Rows = $rows }
    } catch { $document.Dispose(); throw }
}

function Read-Integer([System.Text.Json.JsonElement]$Row, [string]$Field, [string]$Label) {
    $value = [System.Text.Json.JsonElement]::new()
    $number = 0L
    if (-not $Row.TryGetProperty($Field, [ref]$value) -or $value.ValueKind -ne 'Number' -or
        -not $value.TryGetInt64([ref]$number)) { throw "$Label requires integer $Field." }
    return $number
}

function Assert-AwardGroup([string]$Id, [System.Text.Json.JsonElement]$Row, $Awards) {
    if ((Read-Integer $Row '_iID' "Group $Id") -ne [long]$Id) { throw "Group $Id has a mismatched _iID." }
    if ((Read-Integer $Row '_itype' "Group $Id") -ne 0) { throw "Group $Id is not an award group (_itype=0)." }
    $fields = @($Row.EnumerateObject() | Where-Object Name -Like '_iValue_*')
    if ($fields.Count -eq 0) { throw "Group $Id has no _iValue_* fields." }
    foreach ($field in $fields) {
        $award = Read-Integer $Row $field.Name "Group $Id"
        if ($award -lt 0) { throw "Group $Id has a negative award reference." }
        if ($award -eq 0) { continue }
        if (-not $Awards.ContainsKey("$award") -or $Awards["$award"].GetArrayLength() -eq 0) {
            throw "Group $Id references missing or empty target AwardConfig group $award."
        }
    }
}

function Test-Within([string]$Path, [string]$Directory) {
    $prefix = $Directory.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return $Path.Equals($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($prefix + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

$source = (Resolve-Path -LiteralPath $SourceDataDirectory).ProviderPath
$target = (Resolve-Path -LiteralPath $TargetDataDirectory).ProviderPath
if (-not (Test-Path -LiteralPath $source -PathType Container) -or
    -not (Test-Path -LiteralPath $target -PathType Container)) { throw 'Both export paths must be directories.' }
$output = [IO.Path]::GetFullPath($OutputFile)
$outputParent = (Resolve-Path -LiteralPath ([IO.Path]::GetDirectoryName($output))).ProviderPath
$output = Join-Path $outputParent ([IO.Path]::GetFileName($output))
if ((Test-Within $output $source) -or (Test-Within $output $target)) { throw 'OutputFile must be outside both input export directories.' }
if (Test-Path -LiteralPath $output) { throw 'OutputFile already exists; choose a new candidate path.' }

$tables = [Collections.Generic.List[object]]::new()
try {
    $sourceGroups = Read-Table $source 'MultiverseGroupConfig'; $tables.Add($sourceGroups)
    $targetGroups = Read-Table $target 'MultiverseGroupConfig'; $tables.Add($targetGroups)
    $tasks = Read-Table $target 'TaskConfig'; $tables.Add($tasks)
    $awards = Read-Table $target 'AwardConfig' $true; $tables.Add($awards)
    $needed = [Collections.Generic.SortedSet[long]]::new()
    foreach ($task in $tasks.Rows.GetEnumerator()) {
        $group = Read-Integer $task.Value '_iAward' "Task $($task.Key)"
        if ($group -lt 0) { throw "Task $($task.Key) has a negative award group." }
        if ($group -gt 0) { [void]$needed.Add($group) }
    }
    $additions = [Collections.Generic.List[string]]::new()
    foreach ($id in $needed) {
        $key = "$id"
        if ($targetGroups.Rows.ContainsKey($key)) { $row = $targetGroups.Rows[$key] }
        elseif ($sourceGroups.Rows.ContainsKey($key)) { $row = $sourceGroups.Rows[$key]; $additions.Add($key) }
        else { throw "Task award group $key is absent from both exports." }
        Assert-AwardGroup $key $row $awards.Rows
    }
    $buffer = [IO.MemoryStream]::new()
    try {
        $writer = [System.Text.Json.Utf8JsonWriter]::new($buffer)
        try {
            $writer.WriteStartObject()
            foreach ($property in $targetGroups.Document.RootElement.EnumerateObject()) {
                $writer.WritePropertyName($property.Name); $property.Value.WriteTo($writer)
            }
            foreach ($id in $additions) {
                $writer.WritePropertyName($id); $sourceGroups.Rows[$id].WriteTo($writer)
            }
            $writer.WriteEndObject(); $writer.Flush()
        } finally { $writer.Dispose() }
        $bytes = $buffer.ToArray()
    } finally { $buffer.Dispose() }
    $file = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $file.Write($bytes, 0, $bytes.Length) } finally { $file.Dispose() }
    [pscustomobject]@{ OutputFile = $output; ExistingGroups = $targetGroups.Rows.Count;
        ReferencedGroups = $needed.Count; AddedGroups = $additions.Count;
        Sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash }
} finally { foreach ($table in $tables) { $table.Document.Dispose() } }
