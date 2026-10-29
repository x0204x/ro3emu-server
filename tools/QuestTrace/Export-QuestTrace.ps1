[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string[]] $Path,

    [Parameter(Mandatory, Position = 1)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

# First-stage quest scope.  Do not broaden this list silently: the output is intended to be
# small enough to review line by line and safe to share without chat or login traffic.
$targetScenes = [System.Collections.Generic.HashSet[long]]::new([long[]]@(1021301, 1022001, 1051001, 10220))
$targetTasks = [System.Collections.Generic.HashSet[long]]::new([long[]]@(
    10102100, 10102110,
    10102200, 10102210, 10102220,
    10103120, 10103130
))
$targetInteractions = [System.Collections.Generic.HashSet[long]]::new([long[]]@(40022001))
$allowedIds = [System.Collections.Generic.HashSet[long]]::new([long[]]@(
    5027, 30005, 30108, 30400, 30401, 30402, 30403, 30404, 31214
))

$handlers = @{
    5027  = 'TaskService.SyncTasksAsync -> client AUTO_SYNC DBTaskInfoIncrement'
    30005 = 'SceneService.SceneLoadedAsync'
    30108 = 'DungeonService/InstanceService enter-scene notification'
    30400 = 'DungeonService.EnterAsync'
    30401 = 'DungeonService Snapshot/BaseInfo (wave and goal vars)'
    30402 = 'DungeonService.LeaveAsync'
    30403 = 'DungeonService.SettleAsync result; Completed callbacks run before this notification'
    30404 = 'DungeonService Event (wave/stage transition)'
    31214 = 'TaskService.OnClientInteractAsync'
}

function Get-Value {
    param([object] $Object, [string] $Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Select-MapKeys {
    param([object] $Map, [System.Collections.Generic.HashSet[long]] $Keys)
    $selected = [ordered]@{}
    if ($null -eq $Map) { return $selected }
    foreach ($property in $Map.PSObject.Properties) {
        $key = 0L
        if ([long]::TryParse($property.Name, [ref]$key) -and $Keys.Contains($key)) {
            $selected[$property.Name] = $property.Value
        }
    }
    return $selected
}

function Select-TaskSyncBody {
    param([object] $Body)
    if ($null -eq $Body -or $Body -is [string]) { return $null }
    $infos = Select-MapKeys (Get-Value $Body 'infos') $targetTasks
    $completed = Select-MapKeys (Get-Value $Body 'completeTaskNum') $targetTasks
    if ($infos.Count -eq 0 -and $completed.Count -eq 0) { return $null }
    return [ordered]@{
        version = Get-Value $Body 'version'
        syncAllMark = Get-Value $Body 'syncAllMark'
        infos = $infos
        completeTaskNum = $completed
    }
}

function Test-TargetSceneBody {
    param([object] $Body)
    if ($null -eq $Body -or $Body -is [string]) { return $false }
    foreach ($name in @('sceneEntry', 'dungeonEntry', 'sceneId')) {
        $value = Get-Value $Body $name
        $number = 0L
        if ($null -ne $value -and [long]::TryParse([string]$value, [ref]$number) -and $targetScenes.Contains($number)) {
            return $true
        }
    }
    return $false
}

function Select-Body {
    param([long] $Id, [object] $Body)
    switch ($Id) {
        5027 { return Select-TaskSyncBody $Body }
        31214 {
            if ($null -eq $Body -or $Body -is [string]) { return $null }
            $interaction = 0L
            $taskId = Get-Value $Body 'taskId'
            if (-not [long]::TryParse([string]$taskId, [ref]$interaction) -or -not $targetInteractions.Contains($interaction)) {
                return $null
            }
            return [ordered]@{
                result = Get-Value $Body 'result'
                taskId = $interaction
                goalCount = Get-Value $Body 'goalCount'
            }
        }
        { $_ -in 30401, 30403, 30404, 30108 } {
            if (-not (Test-TargetSceneBody $Body)) { return $null }
            return $Body
        }
        { $_ -in 30400, 30005 } {
            # Requests carry the scene; responses may carry only result. Keep both because the
            # input is already account-scoped and these are required request/response receipts.
            return $Body
        }
        30402 { return $Body }
        default { return $null }
    }
}

$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$utf8 = [System.Text.UTF8Encoding]::new($false)
$writer = [System.IO.StreamWriter]::new($OutputPath, $false, $utf8)
$read = 0L
$written = 0L
try {
    foreach ($inputPath in $Path) {
        foreach ($line in [System.IO.File]::ReadLines((Resolve-Path -LiteralPath $inputPath))) {
            $read++
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $row = $line | ConvertFrom-Json -Depth 100 }
            catch { continue }
            $id = 0L
            if (-not [long]::TryParse([string](Get-Value $row 'id'), [ref]$id) -or -not $allowedIds.Contains($id)) { continue }
            $body = Select-Body $id (Get-Value $row 'body')
            if ($null -eq $body) { continue }
            $safe = [ordered]@{
                t = Get-Value $row 't'
                acc = '<redacted>'
                dir = Get-Value $row 'dir'
                id = $id
                name = Get-Value $row 'name'
                handler = $handlers[[int]$id]
                body = $body
            }
            $writer.WriteLine(($safe | ConvertTo-Json -Depth 100 -Compress))
            $written++
        }
    }
}
finally {
    $writer.Dispose()
}

[pscustomobject]@{
    InputLines = $read
    OutputLines = $written
    OutputPath = (Resolve-Path -LiteralPath $OutputPath).Path
    Scope = '10102100/1021301, 10102200/1022001 + 40022001/31214, 10103120/1051001'
}
