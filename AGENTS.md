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
- Validate every client request against the configuration and refuse it with an `ErrorCodes` value.
- Name message ids, error codes, attribute ids and feature ids; don't scatter numeric literals. Reuse the existing
  constant before adding one.
- Keep methods short and split along the steps they perform. Match the surrounding file's naming and comment style.

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
