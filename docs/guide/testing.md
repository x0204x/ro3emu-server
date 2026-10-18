# Server tests and game data

Most server behaviour reads the client's own tables (`data/config`, `data/sceneeditor`, `data/skills`, `data/nav`,
`msgtable.tsv`), which you export from your own client and which are not in git. A server test therefore runs on one of
two data sets:

| Data set | Attribute | Runs in CI | Use it for |
|---|---|---|---|
| The synthetic fixture, `tests/Ro3Emu.Tests/Fixtures/GameData` | `[Fact]` / `[Theory]` | yes | rules that hold whatever the content is: a failed save never loses a reward, a restart settles once, who owns what, escrow and receipts, reconnects |
| The client's export, `data/` | `[ClientDataFact]` / `[ClientDataTheory]` | no (skipped) | the real content: a real template's award, a boss's skills, a quest chain, a shop's prices |

Prefer the fixture. Use the client's data only when the assertion is about the real numbers or rows, and keep such a
test next to its fixture sibling rather than moving the rule test onto client data.

## Writing a test on the fixture

Start a whole server on it:

```csharp
[Fact]
public async Task AClaimIsGrantedOnce()
{
    await using var host = await SyntheticHost.StartAsync(o => o.SeasonStart = "2026-10-05");
    await using var alice = await host.InWorldAsync("Alice");   // logs in, creates a role, enters the town (10111)
    var mail = host.Service<MailService>();
    ...
    await host.RestartAsync();                                  // same data dir, new server
}
```

Or keep a class's own host setup and point it at the fixture: `o.GameDataDir = SyntheticData.Dir`. Always set
`GameDataDir` (also for a bare `AccountStore`): without it the server looks for `data/config` in the folders above the
test binaries and silently uses your client's export, so the test passes on your machine and not in CI.

When a class has both kinds of tests, put the host and helpers in an abstract base with an abstract `GameDataDir` and
derive one class per data set (see `GuildHuntingHost`, `MailTestsHost`, `AuctionTestsHost`).

## Adding to the fixture

- Every value is invented. Never copy rows from the client's tables: they are the publisher's data. Ids the server
  code names (an activity, a mail template, the starter kit's items) have to match, everything else is made up.
- Add only the rows and fields the code under test reads, with the field names the server reads (`GameData.Int(row,
  "_iFoo")`). A table is a JSON object keyed by id, or an array of rows with `_iID`. A missing table is empty.
- The server log names every table it looked for and did not find (`No ...\config\X.json`): that is the list to check
  when a fixture test fails.
- Once `MapConfig` has a row, scenes it does not know are refused; add the map (and a scene editor file with its born
  point) before warping to it. There is no `msgtable.tsv` (unrouted requests get no reply) and no nav grid (every map
  is walkable everywhere).
- `SyntheticFixtureTests` preloads every table, so a malformed file fails there first.

What it holds today: the town map 10111 and the guild home 4000101 with their scene editor points; the starter kit,
the currencies 1, 2, 102 and the virtual EXP items 100/101; bag capacities and a three-level EXP curve; the mail
templates of mail, market, auction and Guild Hunt; one auction lot item with its `AuctionConfig` row; the guild icons;
the Hunting Challenge schedule, boss, rewards and award groups; the six denomination tables the economy catalogue reads.

## Checking what CI runs

`RO3EMU_NO_CLIENT_DATA=1` makes `ClientData.Available` false on a machine that has the export, so the client-data
tests skip as they do in CI:

```bash
RO3EMU_NO_CLIENT_DATA=1 dotnet test tests/Ro3Emu.Tests --logger "trx;LogFileName=server.trx" --results-directory TestResults
pwsh tools/test-summary.ps1 -Results TestResults   # server: N executed (0 failed), M skipped (M need client data)
```

CI prints the same line per test project in its job summary.
