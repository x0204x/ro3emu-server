# Working on RO3Emu

Rules for anyone changing this repository, people and coding agents alike. [CONTRIBUTING.md](CONTRIBUTING.md) and
[docs/guide/testing.md](docs/guide/testing.md) have the details.

## Before you push

```bash
dotnet build src/Ro3Emu.Server/Ro3Emu.Server.csproj -c Release
dotnet test tests/Ro3Emu.Tests -c Release --filter "Category!=Slow"
dotnet format whitespace --folder
```

- Warnings fail the build (`Directory.Build.props`). Fix the cause; don't add `#pragma warning disable`, `NoWarn` or
  a `!` that only silences the compiler.
- Package versions live in `Directory.Packages.props`; a `PackageReference` has no `Version`.
- Analyzer rules that older code still breaks are listed under "Analyzer baseline" in `.editorconfig`. Don't add new
  violations of them; lower a count only by fixing code, and remove a rule from the list once it is at zero.

## The 1:1 rule

The server reproduces the original game server. Every rule comes from the client (its Lua, protobuf messages and
config tables) and the code says where: the client file and symbol, the message id or the config field. A rule the
client cannot establish is marked `(inferred)` where it is implemented. Never present a guess as original behavior.

## Code

- Game state changes only on the game loop (`GameLoop`). Persist first, then change the in-memory state from the
  commit callback.
- The field names of a `DbPlan` and of the values it holds are written into receipts. Don't rename or move them:
  `ReceiptFieldNamesTests` fails, and an operation recorded before the change would refuse its replay.
- Validate every client request against the configuration and refuse it with an `ErrorCodes` value.
- Read configuration through `GameData` (`Table`, `Row`, `GameData.Int(row, "_iField")`) and maps through
  `WorldData`, with the client's own table and field names. Don't copy config values into code.
- Name message ids, error codes, attribute ids and feature ids; don't scatter numeric literals. Reuse the existing
  constant before adding one:
  - a message id shared across services goes in `MsgId`, one that only its service handles is a `public const uint`
    of that service (`GatheringService.Interaction`);
  - result codes go in `ErrorCodes`, with the client's text in a comment;
  - a function, activity or condition id is a named `const` of the service that owns it
    (`AdventureGroupService.Function`).
- Keep a method under about 40 lines and split it along the steps it performs; a longer one needs a reason.
- Style: private fields are `_camelCase`, `const` and `static readonly` fields are PascalCase, collections are
  created with `[]`, and a class whose constructor only stores its parameters uses a primary constructor. CI
  enforces these (`.editorconfig` and its baseline). Match the surrounding file's comment style.

## Tests

- A rule that holds whatever the content is gets a `[Fact]` on the synthetic data set
  (`o.GameDataDir = SyntheticData.Dir`). Real content uses `[ClientDataFact]`, which CI skips.
- Every value in `tests/Ro3Emu.Tests/Fixtures/GameData` is invented. Never copy rows from the client's tables.

## What goes into git

- Code, tests, and documentation of how a system works: `docs/systems/<system>.md`, client evidence in
  `docs/research/<area>-evidence.md`.
- Not: working notes, per-issue or per-PR write-ups, investigation logs, client files, exported `data/` or `re/`,
  credentials, logs with personal data.
- Commit messages: a short imperative subject in English, an optional body that explains why. No tool attribution
  trailers.
