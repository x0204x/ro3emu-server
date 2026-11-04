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
`GameDataDir`. A `ServerHost.Create` without it gets the fixture anyway (`TestHostDefaults`), but a bare `AccountStore`
does not: it looks for `data/config` in the folders above the test binaries and silently uses your client's export,
so the test passes on your machine and not in CI. A client-data test sets `GameDataDir` to the repository's `data/`.

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
the Hunting Challenge schedule, boss, rewards and award groups; the six denomination tables the economy catalogue reads;
one shop (class 900) with a single-item and a bundle commodity.

## Checking what CI runs

`RO3EMU_NO_CLIENT_DATA=1` makes `ClientData.Available` false on a machine that has the export, so the client-data
tests skip as they do in CI:

```bash
RO3EMU_NO_CLIENT_DATA=1 dotnet test tests/Ro3Emu.Tests --logger "trx;LogFileName=server.trx" --results-directory TestResults
pwsh tools/test-summary.ps1 -Results TestResults   # server: N executed (0 failed), M skipped (M need client data)
```

CI prints the same line per test project in its job summary, and it fails when a project executed fewer tests than
`tests/test-floor.txt` says. That catches a change that silently skips tests. When you add tests, raise the number to
what CI now prints. CI refuses a number lower than the one in the parent commit, so the floor only goes up.

A failed run uploads its results as the `test-results-<os>` artifact: the `.trx` files, with every test's outcome and
output. A test still running after 5 minutes counts as hung (`--blame-hang-timeout 5m`; 15 minutes on main's Linux run,
because the test host takes minutes to write coverage as it exits). The run then stops, and the artifact also holds
the list of the tests that were running and a dump of the test host. Main's Linux run measures
coverage and prints it in the job summary, and the `coverage` artifact holds the Cobertura files.

## Quarantine

A test that fails now and then gets fixed: reproduce it (many runs, with CPU load if needed), prove the cause, and wait
on a hook, an event or the state itself. A longer delay or a weaker assertion is not a fix. Only a test that can't be
fixed yet may go into quarantine: `Trait("Category", "Flaky")` on the test and a row below, for two weeks at most. By
then it is fixed or deleted.

The required `test` and the release run skip `Category=Flaky`. The `quarantine` job runs those tests with up to two
retries, is not a required check and can't block a merge. The nightly run has no such filter, so it still reports them.

The floor stays where it is. A quarantined test stops counting in `tests/test-floor.txt` and the floor never goes down,
so if the required run has no spare tests to cover it, the same pull request adds a test or doesn't quarantine.

| Test | Since | Why it can't be fixed yet |
| --- | --- | --- |
| none | | |

## Snapshot tests

Some tests compare what the server does with a checked-in list in `tests/Ro3Emu.Tests/Snapshots/`:

- `dispatcher-admission.txt`: how the dispatcher admits every message of the client's table (client data).
- `receipt-field-names.txt`: the field and property names that `EventPlanFingerprint` and `EconomyChildIntent`
  write into receipts for a `DbPlan` and the values it holds. A rename makes an operation recorded before it refuse
  its replay, so this one runs without client data, in CI too.

A failing snapshot test lists what changed. Regenerate the file with `RO3EMU_UPDATE_SNAPSHOTS=1` only for an intended
change, and say in the pull request why it is safe.

## The nightly run

CI has no client, so more than half of the server's tests never run there and nothing builds the projects that compile
against the client. `tools/nightly-tests.ps1` does both on a machine that has an export, once a day:

```powershell
pwsh tools/install-nightly-task.ps1 -Client D:\RO3\ROO_PC      # daily at 06:00, -At to change; -Uninstall removes it
pwsh tools/nightly-tests.ps1 -Client D:\RO3\ROO_PC             # one run now
Start-ScheduledTask 'RO3Emu nightly tests'                     # the same run as the schedule makes
```

Each run takes `origin/main` into its own worktree next to your checkout (`ro3emu-nightly`, or `-Root`), links its
`data` to your export (`-Data`) and leaves your checkout alone. It runs the whole server suite at below normal priority
and builds `Ro3Emu.ClientHooks`, `Ro3Emu.Loc` and `Ro3Emu.ClientTools`. If the machine was off at 06:00 the task starts
when it comes back, once it has been up for 30 minutes (`-BootDelayMinutes`). It runs only while you are logged on,
opens no window, and needs no admin rights.

It does not start while a test host or the game client runs, or with under 16 GB of memory or 15 GB of disk free: two
full suites at once take a machine with 32 GB down. Don't start a full suite of your own in the morning without
checking `Get-Process testhost`.

`-Root\latest.txt` holds the last report, `-Root\skipped.log` the refusals, and `-Root\runs\<time>-<commit>\` one run:
`report.txt`, `summary.json`, `tests.json`, the `.trx` and the hang dump under `results\`, the logs, and
`msg-coverage.json`. The newest 14 runs stay, and the newest 3 keep their dumps. A test that fails and then passes when
run again alone counts as flaky: the run is reported as such, naming the test and how often it was flaky in the kept
runs. A test that fails both times makes the run red, with its first error.

For a push to your phone, put `NTFY_TOPIC` (and `NTFY_SERVER`, `NTFY_TOKEN` if you use them) in the file `-NotifyEnv`
names, by default `~/.codex/notify-secrets.env`. It is never copied or logged. Without a topic the report stays on disk
and says it was not sent.
