# Contributing to RO3Emu

Thanks for helping improve the Ragnarok Online 3 server emulator. Bug reports, client evidence, documentation fixes
and focused code changes are welcome. Start with an [issue](https://github.com/aethai/ro3emu-server/issues/new/choose)
for behavior that needs discussion, especially when the original server's rule is unknown.

## Set up and check a change

Use the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). From a PowerShell terminal in the repository
root, these checks work without the game client:

```powershell
dotnet build src/Ro3Emu.Server/Ro3Emu.Server.csproj -c Release
dotnet build tools/LuaDecode/LuaDecode.csproj -c Release
dotnet build tools/ConfigExport/ConfigExport.csproj -c Release
dotnet test tests/Ro3Emu.Patcher.Tests/Ro3Emu.Patcher.Tests.csproj -c Release
```

The full solution includes `Ro3Emu.ClientHooks` and `Ro3Emu.Loc`, which compile against DLLs from your own RO3 PC
client. The release ships their build from `prebuilt/client`, and CI fails when it no longer matches their sources: a
change to either project also refreshes it with `pwsh tools/release/pack.ps1 -UpdatePrebuiltClientDlls` on a machine
with the client. The server tests run without the client: tests of rules use an invented data set, and tests of real content
are skipped unless you have [exported game data](README.md#running-from-source) ([testing guide](docs/guide/testing.md)):

```powershell
dotnet test tests/Ro3Emu.Tests/Ro3Emu.Tests.csproj -c Release --filter "Category!=Slow"
```

Compiler and analyzer warnings fail the build (`Directory.Build.props`) in every project. `Ro3Emu.ClientHooks` and
`Ro3Emu.Loc` keep a few named rules as warnings (`WarningsNotAsErrors` in their project files, with the reason); they
build only against a local client, so CI does not compile them. CI checks the C# layout set in `.editorconfig`.
Before you push, run:

```powershell
dotnet format whitespace --folder
```

Use the SDK feature band that `global.json` pins (10.0.4xx): other bands lay out some statements differently and
would reformat unrelated files, and they count some analyzer rules differently. CI checks the analyzer baseline in
`.editorconfig` with `dotnet run --project tools/AnalyzerBaseline -c Release`; when an SDK update changes a count, the
check prints the new numbers to write.

Package versions live in `Directory.Packages.props`: a `PackageReference` names the package without a version.

Report which checks you ran and which you could not run. Do not treat the client-independent CI check as proof of a
playable client or complete game behavior.

## Evidence and changes

- Use the client's Lua, protobuf messages and config tables as references. Point to the relevant client path and
  symbol or message ID; `docs/research/` and `docs/systems/` show the project's existing evidence format. Mark
  original-server behavior that the client cannot establish as *(inferred)*.
- Keep game state changes on the server's game loop. Add focused tests for new behavior and, when the exported data
  is available, an integration test covering the affected client/server flow.
- Keep pull requests focused. Explain the player-visible or developer-visible change, the evidence for it and the
  validation you performed. Use the pull request template.
- Do not commit the PC client, proprietary DLLs, exported `data/` or `re/`, account credentials or logs containing
  private information. The repository's `.gitignore` excludes the export directories; check your diff before sending.

For a local setup and an overview of current systems and known limits, see the [README](README.md) and
[documentation index](docs/README.md).
