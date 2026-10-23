# Reading the client's config tables

The server reads the client's own configuration: the Lua tables that `tools/ConfigExport` exports to
`data/config/*.json`. Each table the server uses has a typed view in `src/Ro3Emu.Server/Data/Config/Tables`, and the
server checks the exported data against those views before it admits players.

## A view

```csharp
[ConfigTable("SkillConfig")]
public readonly partial record struct SkillConfig(JsonElement Row)
{
    [ConfigField("_iCD")] public partial long Cd { get; }
    [ConfigField("_kScriptLogic")] public partial string ScriptLogic { get; }
    [ConfigField("_kBuff1", Optional = true)] public partial long[][] Buff1 { get; }
}
```

- The attribute holds the client's table and field names, so the name is written once and the 1:1 rule can cite it.
- The property's type selects how the value is read:

  | Type | Reads |
  |---|---|
  | `long`, `int` | an integer |
  | `double` | a number, also a numeric string ("2000") |
  | `bool` | `true`/`false` or a number other than 0 |
  | `string` | a text |
  | `long[]`, `double[]`, `string[]` | a list |
  | `long[][]`, `double[][]` | a list of lists (`[[id, chance, ms], ...]`) |
  | `(long Id, double Value)[]` | `[[id, value, ...], ...]` as pairs |
  | `JsonElement?` | the raw value, for shapes no other type describes |

- An absent value reads as 0, "" or [], as before; Lua's empty list `{}` reads as [].
- Integers are exact beyond 2^53, which a `double` would round.
- `Grouped = true` marks a table whose key holds a list of rows (AwardConfig). Its `Get` returns that list.

The source generator (`src/Ro3Emu.Server.Generators`) implements the properties and adds:

- `TableName`;
- `Info`, the declared fields;
- `Get(data, id)`;
- `All(data)`.

It also lists every view in `ConfigTables.All`.

```csharp
if (SkillConfig.Get(_data, id) is { } skill && skill.Cd > 0) ...
foreach (var skill in SkillConfig.All(_data)) ...
var skill = new SkillConfig(row); // a row the code already holds
```

To read a new field, add a property to the view; don't write the field name in the calling code.
`ConfigViewTests.ClientFieldNamesLiveInTheConfigViews` counts the field names still written outside the views. The
count only goes down.

## The startup check

`GameData.Preload` runs `ConfigValidator` over every view before the gates open:

- **Every data set:** each present value must have its field's kind. A text where the view reads an integer would read
  as 0 in game, so it stops the server instead.
- **A client export (`data/export.json`):** additionally, every declared table must exist and every row must carry every
  declared field, unless the view marks the table or field `Optional`.

A mismatch stops the server with each table, field and row named:

```
The game data does not match the server's config tables (Data/Config):
  SkillConfig._iCD: missing in 3 of 20114 rows (first: 1001)
```

This is what a misspelt field name or a client update that renames a column looks like. Fix the view (or the
export), not the check. Hand-made data sets, such as the test fixture `tests/Ro3Emu.Tests/Fixtures/GameData`, have no
`export.json` and may leave fields out.

`ConfigViewTests.TheClientExportMatchesEveryDeclaredView` runs the strict check on your exported data.
