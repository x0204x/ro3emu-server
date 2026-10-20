param(
    # Folder holding the .trx files of a test run (dotnet test --logger trx --results-directory <folder>).
    [Parameter(Mandatory)] [string]$Results
)

# One line per .trx file (named after its project, e.g. LogFileName=server.trx): how many tests executed, how many were
# skipped, and how many of the skipped ones need the game data exported from a client (ClientDataFact, see
# docs/guide/testing.md). In GitHub Actions the lines also go to the job summary.
$ErrorActionPreference = 'Stop'
$clientData = 'Needs the game data exported from your own client'
if (-not (Test-Path -LiteralPath $Results)) { Write-Output "No test results in $Results"; return }
$ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
$lines = foreach ($trx in Get-ChildItem -LiteralPath $Results -Filter '*.trx' -File -Recurse | Sort-Object Name) {
    [xml]$doc = Get-Content -LiteralPath $trx.FullName -Raw
    $tests = @(Select-Xml -Xml $doc -Namespace $ns -XPath '//t:UnitTestResult' | ForEach-Object Node)
    $skipped = @($tests | Where-Object outcome -eq 'NotExecuted')
    $needData = @($skipped | Where-Object { $_.Output.ErrorInfo.Message -like "*$clientData*" })
    $failed = @($tests | Where-Object outcome -eq 'Failed')
    "$($trx.BaseName): $($tests.Count - $skipped.Count) executed ($($failed.Count) failed), $($skipped.Count) skipped " +
        "($($needData.Count) need client data)"
}
$lines | ForEach-Object { Write-Output $_ }
if ($env:GITHUB_STEP_SUMMARY) { $lines | ForEach-Object { "- $_" } | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
