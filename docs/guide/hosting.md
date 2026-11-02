# Play with friends and host a server

**On Windows.** The host runs `RO3Emu.exe host`; it prints the address to share (Windows asks once whether the server
may use the network: allow it). Friends run `RO3Emu.exe join <that address>`. For friends outside your home network,
forward TCP ports `5380`, `20000` and `33100` on your router to the host and share your public IP. Party voice chat
is off unless the host adds `--voice-port 33101` (any free port); then forward that port too.

The two labels on the server-selection screen are optional host settings. For example:

```powershell
.\RO3Emu.exe host --client 'C:\Games\Ragnarok3' --public '<tailscale-ip>' --no-launch `
  --cluster-name 'RO3 伺服器' --area-name '普隆德拉'
```

The names are read when the server starts, so restart the host after changing them. They only change display text;
the numeric cluster and area identities stay unchanged.

## Reward rates

`server/appsettings.json` has four independent server-wide reward rates. `1.0` keeps the client data value, `0.5`
halves it, and `0` disables that reward family:

```json
"RewardRates": {
  "BaseExperience": 0.5,
  "JobExperience": 0.5,
  "Zeny": 0.5,
  "MonsterDrops": 0.5
}
```

Base/JOB rates cover monster kills, quest EXP and configured AwardConfig rewards. `Zeny` covers monster Zeny and
configured reward plans; direct GM grants, shop prices, costs, existing mail escrow, refunds and player transfers stay
exact. `MonsterDrops` affects non-currency items from monster kills, so monster Zeny is not multiplied a second time.
Positive EXP and Zeny smaller than one scaled unit stay at one; an individual fractional monster item uses a
remainder roll. Valid values are `0` through `100`. The settings are read at server startup and require a restart;
they are not hot-reloaded.

**On Linux.** Download **`RO3Emu-server-linux-x64.tar.gz`**, extract it and run `server.sh`; nothing to install:

```bash
./server.sh --client /path/to/ROO_PC --host <public IP> --gm <your account>     # a game folder copied from Windows
./server.sh --download /srv/ro3/ROO_PC --host <public IP> --gm <your account>   # or only the files it needs, from Genesis
./server.sh --service ...                                                       # install it as a systemd service
```

Open TCP `5380`, `20000` and `33100`, and the voice port if you turn voice on with `--voice-port 33101`.

**Good to know**
- **GM commands** (`!help` in chat) are on for every account when you play on your own PC. A hosted server gives them
  only to the accounts you name with `--gm` ([GM access](gm-access.md)).
- **Web panel:** a website for your players (account, characters, who's online, rankings) and for you (accounts,
  bans, server numbers) on port `5381`. Open that port too, or put it behind https ([web panel](panel.md)). Admins are
  the accounts you name with `--gm`. `--panel-port <n>` moves it, `--no-panel` turns it off (RO3Emu.exe and
  `server.sh`).
- **Updates:** the web panel's Server control shows the installed version against the newest release, downloads and
  checks one, and, under systemd (`./server.sh --service`) or a Windows service wrapper, installs it or rolls it back
  through a planned restart with a database backup first. In a terminal, under RO3Emu.exe or in Docker it downloads
  and checks, then lists the steps ([web panel, Updates](panel.md#updates)).
- **Not as root:** `server.sh` refuses to run as root (`--allow-root` overrides it); run it as an ordinary user, and
  `--service` asks for sudo only to install the unit. The unit runs with `NoNewPrivileges`, `ProtectSystem=strict`,
  `ProtectHome=read-only` and a private `/tmp`: the server can write only inside the folder you extracted (its
  single-file build unpacks to `.net` there) and to a `/tmp` of its own. A data dir elsewhere
  (`Ro3Emu__DataDir`) needs `ReadWritePaths=<that dir>` in a drop-in. The data dir and its private files (the gate
  key, the database, the backups, the operator token) are readable only by the server's user: modes 0700 and 0600 on
  Linux, an access list of that user, SYSTEM and the Administrators on Windows. Your own scripts send
  that token (`data/operator.token`) to the loopback tools ([GM access](gm-access.md#the-loopback-tool)).
- **Bug reports from players:** the Feedback button in the game menu or `/bug <text>` in chat ([feedback](feedback.md)), optionally posted to a Discord channel.
- **Monsters that attack on sight as in RO1** (on by default, as a trial): set `Ro3Emu__Ro1AggressiveMonsters=false` (or
  `"Ro1AggressiveMonsters": false` under `Ro3Emu` in `appsettings.json`) to turn it off. The field monsters whose Ragnarok Online 1
  counterpart is aggressive then attack players they see, like the few that already do. This follows RO1
  (rAthena's monster database), not the original RO3 server. RO3 mixes levels on one map (Payon Forest: Greatest
  Generals at 55 among Horns at 16, on the level 16 story's path), so such a monster stays passive, as in the client,
  on an open field whose own spawns hold a common monster (at least 10% of them) more than
  `Ro3Emu__Ro1AggressiveMaxLevelGap` levels below it (default 15; `0` turns the gate off). Dungeons, towns, Territory
  War sandboxes, story and team rooms, bosses and MVPs, and monsters a map's spawns do not place keep the RO1 behaviour.
- **Backups:** the database is backed up to `data/backups/` at start and every 6 hours (newest 20 kept). All but the
  newest two are gzip-compressed (`.db.gz`, about a quarter of the size); `Ro3Emu.Server --restore-backup` takes either
  kind. `Ro3Emu__Backup__Keep`, `Ro3Emu__Backup__IntervalHours` and `Ro3Emu__Backup__Uncompressed` (or `Backup` under
  `Ro3Emu` in `appsettings.json`) change the three numbers; `Uncompressed` equal to `Keep` compresses nothing.
  A backup leaves 1 GB free for the live database (`Ro3Emu__Backup__ReserveMegabytes`): when the disk is short it removes
  the oldest copies (never the newest two), or skips the backup and logs it as critical. From 4 GB
  (`Ro3Emu__Backup__BigDatabaseMegabytes`, 0 turns this off) a restart makes no copy while the newest is younger than the
  interval, and scheduled copies are integrity checked once a day (`Ro3Emu__Backup__BigDatabaseCheckHours`).
- **Database size:** every reward, purchase and activity writes a receipt so a lost or repeated request is never paid
  twice. Receipts older than 2 days are trimmed to their hashes every hour, in small batches
  (`Ro3Emu__ReceiptRetention__Days`, `__BatchSize`, `__IntervalMinutes`; `Days` 0 keeps them whole). The space freed
  is reused, so the file stops growing; to shrink it on disk, stop the server and run `VACUUM` on `data/ro3emu.db`
  once (`sqlite3 data/ro3emu.db "VACUUM"`).
- **Test realm switches** (not the original game, off by default): `Ro3Emu__Testing__AlwaysOpen=true` opens every
  To-Do activity at any hour of any weekday (Survivor Battle, Dragon Coffin Island, Ilmata, the bonfire, Festival
  Invitation, Wedding Crashers, Hunting Challenge, Mock Battle, the Commendation Vault...), each repeating back to back
  with its own length. `Ro3Emu__Testing__UnlimitedRewards=true` lets the weekly and daily reward counts never run out:
  Realm of the Gods and Phantom Realm rewards, MVP rewards, the Commendation Vault's extra awards and Dodo's Request.
  (`"Testing": { "AlwaysOpen": true }` under `Ro3Emu` in `appsettings.json` does the same.) The server warns at startup
  while one is on. A GM turns them on or off without a restart: `!testing alwaysopen on|off`, `!testing unlimited
  on|off`, `!testing reset`, `!testing` to show them; that change lasts until the next start. The Guild League and the
  Territory Wars keep their calendars (`!league`, `!siege open`, `!twpoint`). Details:
  [activities](../systems/activities.md#test-realm-switches-ro3emutesting).
- **One session per account:** a second login takes over from the first. The client runs one instance per PC;
  `Ro3Emu.BotCli` can stand in for extra players when you test alone.

| Hardware | Minimum | Measured target (500 players) |
|---|---|---|
| CPU | 2 cores | at most 2 cores used by the server |
| RAM | 2 GB | at most 2 GB working set |
| Disk | 3 GB plus the game | SSD recommended |

## Running a public test server

A server strangers can reach (a closed test, an open beta) wants a few protections that a server among friends does
not. Each is an option and off by default, so a plain `host` or `server.sh` install behaves as before.

| Option | Default | What it does |
|---|---|---|
| `Ro3Emu:RefuseUnknownAccounts` | `false` | a game login with a name that has no account is refused instead of creating the account: accounts come only from the web panel's sign-up |
| `Ro3Emu:ChatFlood:Enabled` | `false` | chat flood protection for every account not in `Ro3Emu:GmAccounts` (GMs are exempt) |
| `Ro3Emu:ChatFlood:WindowSeconds` | `10` | the window the two line budgets count in |
| `Ro3Emu:ChatFlood:OpenLines` | `5` | lines per window on world, map, recruitment and the other open channels |
| `Ro3Emu:ChatFlood:PrivateLines` | `10` | lines per window on whispers, party, guild and friend-group chat, counted apart |
| `Ro3Emu:ChatFlood:DuplicateSeconds` | `30` | a line with the same text (case and spaces aside) to the same channel or player within this time is dropped; `0` turns it off |
| `Ro3Emu:ChatFlood:MuteSeconds` | `60` | going past a budget mutes the account for this long, on every channel, across relogs |
| `Ro3Emu:Panel:SignUpsPerAddressPerDay` | `0` (no limit) | accounts the panel's sign-up creates for one client address (an IPv6 one by its /64) in any 24 hours |
| `Ro3Emu:Panel:SignUpsPerHour` | `0` (no limit) | accounts the sign-up creates in any hour from all addresses together, a brake on a sign-up flood from many addresses |

**Accounts.** With `RefuseUnknownAccounts` an unknown name gets the client's own "Incorrect Username or Password"
popup, the same as a wrong password: the client words no other login refusal, and it cannot show a text from the
server there. The server log says `refused, no such account`. So tell your players the sign-up address
(`https://<panel>/register`) wherever they find you. The server warns at startup when the panel or its sign-up is
off, since no account could then be made. Accounts that already exist keep working.

**Chat flood.** The chat budgets stay above the client's own channel cooldowns (world: one line per 5 seconds), so a
player typing normally never meets them. A refused line is shown to nobody. A repeated line just does not go out. When
a mute starts, and on each line tried during it, the player gets the client's own notice "You have been muted until
..." with the end time, in their local time. Dropped lines count against the budget too, so a script that keeps
resending is muted. Mutes are kept in memory: a server restart lifts them. Bans stay on the panel (Admin, Accounts).

**Sign-up limits.** A taken name or a refused sign-up does not use the budget, only an account actually created. The
panel's existing limit still applies on top: `Ro3Emu:LoginAttemptsPerMinute` credential checks (sign-up, login,
password change) per address and minute. Behind a reverse proxy the panel needs `Ro3Emu:Panel:TrustedProxies` to see
the real client address, or every visitor shares the proxy's address and its budget. There is no proof-of-work or
CAPTCHA: the page allows only its own scripts and a plain-http panel has no browser crypto to build one on, so the
per-address and hourly limits do that job.

**Example.** A closed test with the panel behind a TLS proxy (nginx on port 8443, [Behind nginx with
https](panel.md#behind-nginx-with-https)) at `https://playnusky.com:8443`. With the unit `server.sh --service`
installs, put this in `/etc/systemd/system/ro3emu.service.d/closed-test.conf`:

```ini
[Service]
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=Ro3Emu__GmAccounts__0=your-admin-login
Environment=Ro3Emu__RefuseUnknownAccounts=true
Environment=Ro3Emu__Panel__Enabled=true
Environment=Ro3Emu__Panel__Registration=true
Environment=Ro3Emu__Panel__BindAddress=127.0.0.1
Environment=Ro3Emu__Panel__TrustedProxies__0=127.0.0.1
Environment=Ro3Emu__Panel__SignUpsPerAddressPerDay=3
Environment=Ro3Emu__Panel__SignUpsPerHour=30
Environment=Ro3Emu__ChatFlood__Enabled=true
```

then `sudo systemctl daemon-reload && sudo systemctl restart ro3emu`. Players sign up at
`https://playnusky.com:8443/register` and log in to the game with that account; the chat numbers above are the
defaults (`Environment=Ro3Emu__ChatFlood__OpenLines=3` and so on to change one).

## Purchase links

The game's recharge, shop top-up and monthly-card buttons open Genesis' payment page. RO3Emu never sends a
player there: a payment made there reaches Genesis, not your server. Without any option the buttons open
nothing. To send players to your own page (donations, a store), give its address every time you patch the game:

```
RO3Emu.exe join 203.0.113.10 --purchase-url https://example.com/donate
```

`start` and `host` take the option too. Only an `http` or `https` address is accepted. The patcher writes it into the
client hooks it installs; the redirect sits on `UnityEngine.Application.OpenURL`, the call every one of those buttons
ends in (`OpenUrlPatch`, `Ro3Emu.ClientHooks.UrlRewrite`).
