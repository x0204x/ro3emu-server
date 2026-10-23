; Diagnostics of the server's source generators not yet in a release (Roslyn analyzer release tracking).

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RO3CFG001 | ConfigTables | Error | A [ConfigTable] type is not a partial record struct over (JsonElement Row)
RO3CFG002 | ConfigTables | Error | A [ConfigField] property has a type no config reader supports
RO3CFG003 | ConfigTables | Error | A [ConfigField] property is not a get-only partial property
RO3CFG004 | ConfigTables | Error | A config view declares the same field twice
