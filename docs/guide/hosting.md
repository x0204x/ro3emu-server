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
- **Bug reports from players:** the Feedback button in the game menu or `/bug <text>` in chat ([feedback](feedback.md)).
- **Monsters that attack on sight as in RO1** (on by default, as a trial): set `Ro3Emu__Ro1AggressiveMonsters=false` (or
  `"Ro1AggressiveMonsters": false` under `Ro3Emu` in `appsettings.json`) to turn it off. The field monsters whose Ragnarok Online 1
  counterpart is aggressive then attack players they see, like the few that already do. This follows RO1
  (rAthena's monster database), not the original RO3 server.
- **Backups:** the database is backed up to `data/backups/` at start and every 6 hours (newest 20 kept).
- **One session per account:** a second login takes over from the first. The client runs one instance per PC;
  `Ro3Emu.BotCli` can stand in for extra players when you test alone.

| Hardware | Minimum | Measured target (500 players) |
|---|---|---|
| CPU | 2 cores | at most 2 cores used by the server |
| RAM | 2 GB | at most 2 GB working set |
| Disk | 3 GB plus the game | SSD recommended |
