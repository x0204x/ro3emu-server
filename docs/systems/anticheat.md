# Anti-cheat

The client is the player's: it can be patched and can send any message with any field. The server keeps the original's
rules (the [1:1 rule](../../AGENTS.md)) and adds checks only where an unmodified client never sends what it refuses. The
vector-by-vector audit is in [anti-cheat audit](../research/anticheat-audit.md). Every tolerance below is *(inferred)*:
the original server's limits are not in the client.

## What is refused

| Check | Where | Counted as |
|---|---|---|
| A walk farther than the speed allows, through a wall, off the nav grid, or to a position that is not a number | `SceneService.AcceptWalkAsync` | `walk` |
| A flight move farther than the flight speed allows | `WorldCoreService.FlyMoveAsync` | `flight` |
| A cannon shot that would land beyond the aim indicator's reach | `TerritoryWarsService.LaunchBulletAsync` | `cannon` |
| A quest area (31209) reported away from the region | `TaskService.OnAreaAsync` | `quest-area` |
| A quest interaction (31214) reported away from its NPC, when the map places it | `TaskService.OnClientInteractAsync` | `quest-interact` |
| Game messages past the session's budget | `GameMessageHandler.WithinRate` | `flood` |
| A seventh role on one account | `RoleSessionService.CreateRoleAsync` | `role-limit` |
| A chat line too long or past the chat budget | `ChatService.SendTextAsync` | `chat` |
| Saved settings past 256 KiB | `RoleSessionService.SaveSettingAsync` | `payload` |
| A raw 30012 or 30131 to where the client would not send one | `SceneService.TravelPasses` | `travel` |

Corrected without a refusal (an honest client can send these too):

- A friendly area skill released around the caster (`_iRelease` 0, 2-6) is centred on the caster, as an enemy one is;
  a target point beyond the skill's reach is replaced by the caster's position.
- A normal attack at another target during a swing is taken, but starts when that swing ends.
- Guide ids that are no GuideConfig row are not saved.
- A party's chat channel refuses outsiders.

### The walk budget

`WalkAllowance` holds the distance the client may still report. It grows by 1.5 times the speed attribute per second,
up to 5 seconds of it (the tolerance and time the old per-walk check used), and every accepted walk spends its length.
Walks held back by lag and arriving together spend what the lag saved up; a client that reports more distance than its
speed covers runs dry however it splits it into messages. The budget saves nothing while the player cannot walk: while
dead or rooted, and during a cast that holds the caster in place (`SkillConfig._bMovement` 0), until it ends or is
cancelled. A walk that sets off during such a cast ends the cast.

The nav grid is applied leniently, because the client walks on a navmesh and the grid has 1 m cells: the destination
must be on or next to a walkable cell (any height), and the leg from the last walk must cross walkable cells, or A* must
find a way round the corner it grazes within the budget. A player standing off the grid (an arrival in scenery) is not
held to it until it is back on. A walk refused in "enforce" mode puts the player back where the server has it
(`TeleportInMapAsync`).

A cast while walking: the client casts on reaching range and stops just after, so a walking caster's range allows the
distance walked since its last walk, measured along the walk's direction (`WalkReq.dir`) with 1 m to the side.

### Travel

A portal or Kafra is checked at its NPC, and refuses a dead role. 30012 and 30131 have no NPC, so they are held to what
the client sends them for (`SceneService.Travel.cs`). 30012 may go to the role's own map, the main city (10100,
`AutoBattleAutoBackMainCityID`, `OnHookReturnMap`), a Territory Wars map, the GvG transfer's 10110 from a dungeon or Territory Wars, and any
map while flying (31083, which needs the fly buff). 30131 may go to a scene and point that an authored action 141 names
(`TaskConfig`, `DialogueConfig`, `TypewriterConfig`). Anything else gets `ErrorCodes.Parameter`, spends nothing and leaves
the role where it is; the client only shows the error.

## Options

`ServerOptions.WalkCheck`: `enforce` (default), `log` (refusals are counted and let through; a position that is not a
number is still refused) or `off`.

`ServerOptions.TravelCheck`: the same three for the travel check. The load test places its bots with a raw 30012, so a
remote server it runs against needs `log` (`LocalServer` sets it).

`Ro3Emu:AntiCheat`:

| Option | Default | What |
|---|---|---|
| `MessagesPerSecond`, `MessageBurst` | 120, 600 | game messages per session; 0 turns the limit off |
| `MessageLimits` | `{"1006": "0.5/5", "1007": "0.5/5"}` | tighter budgets per message id, `perSecond/burst` (role create, role select) |
| `MicroRequests` | `20/100` | frames per micro gate connection once signed in, `perSecond/burst` (heartbeats are free); over it a request is answered "too many requests" without running, and 1000 refused frames in 10 s close the connection. The budget counts frames, so requests that take a list also have their own limit (50 roles per `getAttributeBatchByVersion`, the client's own batch size) |
| `MicroRequestsBeforeSignIn` | `2/10` | the same before `verifySdk` binds an account; over it the connection is closed |
| `ChatLines` | `1/10` | chat lines per session |
| `ChatMaxLength` | 500 | characters per chat line |
| `MaxRolesPerAccount` | 6 | `LC_SelectRoleManager.m_iMaxRoleNumber` |
| `FlushSeconds` | 30 | how often the counts are written |
| `KeepDays` | 30 | how long the counts are kept |
| `Alerts:WindowHours` | 1 | the panel list's default window |
| `Alerts:Refusals` | 20 | refusals of one account in the window that list it |
| `Alerts:GainFactor` | 10 | a gain is listed past this many times the window's median... |
| `Alerts:ZenyFloor`, `Alerts:ItemFloor`, `Alerts:ExpFloor` | 1 000 000, 2 000, 5 000 000 | ...and at least this much |

## Counting and alerts

`CheatMonitor` counts each refusal per account, character, kind and hour, writes the counts to `anticheat_refusal`
every `FlushSeconds`, and logs it as a warning:

```
Anticheat walk account alice role 12345 Alice: scene 10111 341.6,292.0 -> 541.6,-7.6,292.0: too fast (200.0 m, 54.0 m allowed at 7.0 m/s) (#1 of this kind since start)
```

the first five of each account and kind, then every 50th. It also keeps the base and job EXP each character is
credited per hour (`anticheat_exp`, `CharacterService.ExpCredited`), which the economy ledger does not record. Both
tables are kept `KeepDays` days.

The web panel's **Suspicious activity** page (`/admin/suspicious`) lists accounts past `Alerts:Refusals` and characters
whose Zeny, items (ledger source legs) or base EXP in the window pass `Alerts:GainFactor` times the median and the
floor, and on an Admin's request the duplication check's flags. See [web panel](../guide/panel.md#suspicious-activity).

## Tests

`AntiCheatTests` (the budget, walks, floods, roles, chat, settings, guides, and on client data the area report),
`AntiCheatPanelTests` (the panel list), `TravelCheckTests`, `SkillCombatTests.ClicksAlternatingBetweenTwoTargetsDoNotSwingFaster`
and `TerritoryWarsTests.ACannonShotThatWouldLandBeyondTheAimIsRefused` play the modified client; the existing walking-cast,
swing and quest tests play the honest one. Tests run with `WalkCheck` and `TravelCheck` "log" (`TestHostDefaults`), since
most put a bot where they need it with one walk or one 30012; the anti-cheat and travel tests set "enforce".
