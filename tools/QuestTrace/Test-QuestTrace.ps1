[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path ([System.IO.Path]::GetTempPath()) ('ro3-quest-trace-' + [guid]::NewGuid().ToString('N') + '.jsonl')
try {
    $result = & (Join-Path $root 'Export-QuestTrace.ps1') `
        -Path (Join-Path $root 'testdata\traffic-sample.jsonl') -OutputPath $out
    if ($result.InputLines -ne 7 -or $result.OutputLines -ne 4) {
        throw "Unexpected line counts: input=$($result.InputLines), output=$($result.OutputLines)"
    }
    $text = Get-Content -LiteralPath $out -Raw
    foreach ($forbidden in @('alice', '99999999', 'private text')) {
        if ($text.Contains($forbidden, [StringComparison]::Ordinal)) {
            throw "Sanitized output retained forbidden text: $forbidden"
        }
    }
    foreach ($required in @('10102100', '10102110', '1021301', '40022001', '<redacted>')) {
        if (-not $text.Contains($required, [StringComparison]::Ordinal)) {
            throw "Sanitized output lost required text: $required"
        }
    }
    [pscustomobject]@{ Passed = $true; OutputLines = $result.OutputLines }
}
finally {
    Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
}
