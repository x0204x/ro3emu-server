# Prepare missing task award groups

Some exported data sets have a `TaskConfig._iAward` reference whose row is missing from
`MultiverseGroupConfig`. `tools/release/prepare-task-award-groups.ps1` prepares a candidate
table using a second local export. It embeds no client configuration.

Both arguments are export roots containing `config/*.json`. The source needs
`MultiverseGroupConfig.json`; the target needs that table, `TaskConfig.json`, and the grouped
`AwardConfig.json`. Use PowerShell 7:

```powershell
pwsh tools/release/prepare-task-award-groups.ps1 `
  -SourceDataDirectory C:\Exports\reference `
  -TargetDataDirectory C:\Exports\server `
  -OutputFile C:\Candidates\MultiverseGroupConfig.json
```

The output parent must already exist. The output must be outside both export directories
and must not exist. The tool preserves every target group, copying only task-referenced
groups absent from the target. Each referenced row must have a matching `_iID`, `_itype=0`,
integer `_iValue_*` fields, and every nonzero value must name a nonempty group in the target's
`AwardConfig`. Missing rows, duplicate keys, invalid types, and mismatches stop the tool before
it creates an output. Integer IDs and existing row values are preserved without floating-point
conversion. The result reports counts and the candidate's SHA-256.

Review the candidate and test it with an isolated copy of the target export. Deployment is a
separate operation: stop the server normally, back up its current table and database, and
replace only the reviewed configuration. This tool does not deploy, change a database, or
overwrite any input. A source row's presence establishes its exported mapping; it does not
prove the original server's reward timing or Base/Job calculation.
