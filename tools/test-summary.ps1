param(
    # Folder holding the .trx files of a test run (dotnet test --logger trx --results-directory <folder>). With coverage
    # (--collect "XPlat Code Coverage") each project needs its own subfolder, named like its .trx (TestResults/server).
    [Parameter(Mandatory)] [string]$Results,
    # The fewest tests each project has to execute (tests/test-floor.txt). The run fails below it, and when the file
    # holds a lower number than in the parent commit: the floor only goes up. Each project's .trx must then be the only
    # one of its name, in the folder of that name (TestResults/server/server.trx).
    [string]$Floor
)

# One line per .trx file (named after its project, e.g. LogFileName=server.trx): how many tests executed, how many were
# skipped, and how many of the skipped ones need the game data exported from a client (ClientDataFact, see
# docs/guide/testing.md). Then one line per coverage file and the floor check. In GitHub Actions the lines also go to
# the job summary and a floor failure becomes an error annotation.
$ErrorActionPreference = 'Stop'
$clientData = 'Needs the game data exported from your own client'
$invariant = [cultureinfo]::InvariantCulture
$lines = [System.Collections.Generic.List[string]]::new()
$errors = [System.Collections.Generic.List[string]]::new()
$executed = @{}

if (Test-Path -LiteralPath $Results) {
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $resultsRoot = (Resolve-Path -LiteralPath $Results).Path
    foreach ($trx in Get-ChildItem -LiteralPath $Results -Filter '*.trx' -File -Recurse | Sort-Object Name) {
        if ($Floor) {
            # A file of the same name somewhere else must not stand in for the project's own results.
            $own = [System.IO.Path]::Combine($resultsRoot, $trx.BaseName, $trx.Name)
            if ($trx.FullName -ne $own) { $errors.Add("$($trx.Name): found at $($trx.FullName), expected only at $own") }
        }
        [xml]$doc = Get-Content -LiteralPath $trx.FullName -Raw
        $tests = @(Select-Xml -Xml $doc -Namespace $ns -XPath '//t:UnitTestResult' | ForEach-Object Node)
        $skipped = @($tests | Where-Object outcome -eq 'NotExecuted')
        $needData = @($skipped | Where-Object { $_.Output.ErrorInfo.Message -like "*$clientData*" })
        $failed = @($tests | Where-Object outcome -eq 'Failed')
        # Only a test that ran counts for the floor: not a skipped one, and not one that stayed Pending or Inconclusive.
        $ran = @($tests | Where-Object outcome -in 'Passed', 'Failed').Count
        $executed[$trx.BaseName] = $ran
        $lines.Add("$($trx.BaseName): $ran executed ($($failed.Count) failed), " +
            "$($skipped.Count) skipped ($($needData.Count) need client data)")
    }

    # Cobertura files from coverlet: the project is the first folder under $Results. VSTest leaves a second copy of the
    # same file in the collector's In folder, so one per project.
    $root = (Resolve-Path -LiteralPath $Results).Path
    $covered = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Results -Filter 'coverage.cobertura.xml' -File -Recurse | Sort-Object FullName) {
        $project = [System.IO.Path]::GetRelativePath($root, $file.FullName).Split([char[]]'\/')[0]
        if ($covered.ContainsKey($project)) { continue }
        $covered[$project] = $true
        [xml]$cov = Get-Content -LiteralPath $file.FullName -Raw
        $lineRate = [double]::Parse($cov.coverage.'line-rate', $invariant)
        $branchRate = [double]::Parse($cov.coverage.'branch-rate', $invariant)
        $lines.Add([string]::Format($invariant, '{0} coverage: {1:0.0}% of lines, {2:0.0}% of branches',
            $project, $lineRate * 100, $branchRate * 100))
    }
}
else { $lines.Add("No test results in $Results") }

function Read-Floor([string[]]$text) {
    $floor = [ordered]@{}
    foreach ($line in $text) {
        $line = ($line -replace '#.*$', '').Trim()
        if (-not $line) { continue }
        $name, $count = $line -split '\s+'
        $floor[$name] = [int]$count
    }
    $floor
}

if ($Floor) {
    $current = Read-Floor (Get-Content -LiteralPath $Floor)
    foreach ($name in $current.Keys) {
        if (-not $executed.ContainsKey($name)) { $errors.Add("${name}: no test results, the floor is $($current[$name])") }
        elseif ($executed[$name] -lt $current[$name]) {
            $errors.Add("${name}: $($executed[$name]) executed, below the floor of $($current[$name]) in $Floor")
        }
    }
    # The same file in the parent commit (the base branch of a pull request; CI checks out two commits). Locally a clone
    # without the parent, or a parent without the file, has nothing to compare with; in CI that means the floor or the
    # checkout was changed so that nothing compares it, so it fails.
    $path = git ls-files --full-name -- $Floor 2>$null
    $parent = if ($path) { git show "HEAD^1:$path" 2>$null } else { $null }
    if ($LASTEXITCODE -eq 0 -and $parent) {
        $previous = Read-Floor $parent
        foreach ($name in $previous.Keys) {
            if (-not $current.Contains($name) -or $current[$name] -lt $previous[$name]) {
                $errors.Add("${name}: the floor went down from $($previous[$name]) to $($current[$name] ?? 'none') " +
                    "($Floor only goes up)")
            }
        }
    }
    elseif ($env:GITHUB_ACTIONS) { $errors.Add("No floor in the parent commit to compare $Floor with") }
    else { $lines.Add("No floor in the parent commit to compare $Floor with") }
    if ($errors.Count -eq 0) { $lines.Add("Floor met: " + (($current.Keys | ForEach-Object { "$_ $($current[$_])" }) -join ', ')) }
}

$lines | ForEach-Object { Write-Output $_ }
$errors | ForEach-Object { Write-Output ($env:GITHUB_ACTIONS ? "::error title=Executed tests::$_" : "Error: $_") }
if ($env:GITHUB_STEP_SUMMARY) { @($lines) + @($errors) | ForEach-Object { "- $_" } | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
# Always an explicit code: Actions runs the script with ". 'file'" and exits with $LASTEXITCODE, which a failed
# "git show" of a parent without the floor file leaves at 128.
exit ($errors.Count -gt 0 ? 1 : 0)
