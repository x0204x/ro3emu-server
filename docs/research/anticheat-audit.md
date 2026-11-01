# Anti-cheat audit

What the server checks against a modified client, vector by vector, before and after the anti-cheat work on branch
`feat/anticheat` (base `1fa6b91`). The client is assumed hostile: it is patched (Unity and Lua) and can send any message
with any field, open two sessions of one account and drop the connection at any moment. How the checks work and which
options they take is in [anti-cheat](../systems/anticheat.md).

Ratings: **safe** (a modified client gains nothing), **weak** (a check exists but can be beaten, or only reports),
**open** (nothing stops it). "Before" cites the base commit, "after" the branch. Every tolerance the server adds is
*(inferred)*: the original server's limits are not in the client.

## The finding behind most of the others

Every distance check (skill range, quest dialog and area, gathering, NPC shop, pickup, interaction reach) measures from
`GameSession.Position`, and that position is the client's last walk (`RoleSessionService.WalkAsync`). Before, the walk
check defaulted to report only: `ServerOptions.WalkCheck = "log"` (`ServerOptions.cs:66`), and
`SceneService.AcceptWalkAsync` (`SceneService.cs:486-517`) returned true for a teleport, a wall or a speed hack, so a
forged walk next to the target beat every one of those checks. Its speed rule also allowed `speed x seconds x 1.5 + 1.5 m`
per walk with the time clamped to at least 0.05 s, so walks 50 ms apart moved 1.5 m each whatever the speed (30 m/s),
and a walk that was not a number (`NaN`) was stored as the position, after which every `d > range` comparison was false.
In-game logs of honest play (10.10) also showed the per-walk rule refusing a lag burst ("2.8 m in 0.11 s, max 2.6").

## Movement

| Vector | Before | After |
|---|---|---|
| Teleport (one far walk) | **open**: `WalkCheck` "log" lets it through (`SceneService.cs:510-514`) | **safe**: `WalkCheck` defaults to "enforce"; a walk past the distance budget is put back (`SceneService.WalkCheck.cs:24-47`) and counted (`walk`) |
| Speed hack | **open**: the per-walk 1.5 m slack, report only | **safe**: a distance budget (`WalkAllowance`) grows at 1.5 x the speed attribute up to 5 s of it; walks spend it however they are split |
| Walls, off the nav grid | **weak**: checked against the grid, report only | **safe** with lenient edges: the destination must be on or next to a walkable cell, the leg must cross walkable cells or A* must find a way round within the budget (`NavProblem`, `SceneService.WalkCheck.cs:77-86`); a player standing off the grid (an arrival in scenery) is not held to it |
| Position that is not a number | **open**: `NaN` stored, range checks then pass | **safe**: refused in every mode but "off" |
| Moving while rooted, stunned or dead | **weak**: the walk is dropped (`RoleSessionService.cs:334`), the next one may claim the distance | **safe**: the budget saves nothing while the walk is forbidden (`HoldWalk`, `RoleSessionService.cs:342-347`) |
| Moving while casting | **weak**: only a reported move cancels the cast (`CombatService.Casting.cs:639`) | **safe**: a cast that holds the caster (`_bMovement` 0) holds the budget until it ends (`CombatService.Casting.cs:589`), and a walk that sets off ends such a cast (`:700`); a cancelled cast gives the budget back (`:673`) |
| Flight speed | **weak**: the same per-message slack (`WorldCoreService.Movement.cs:62-70`) | **safe**: a separate budget at the flight speed (`WorldCoreService.Movement.cs:72`), counted (`flight`) |
| Position spoofing for range | **open** (see above) | **safe** with the walk check, plus the cast slack below |
| Cast range while walking | **weak**: a "walking" caster got `speed x 5 s` of range in any direction (`WalkSlack`, `CombatService.Casting.cs:319-321`) | **safe**: the slack lies along the walk's own direction (`WalkReq.dir`, what other clients are shown) with 1 m sideways (`WalkPoint`, `CombatService.Casting.cs:340`) |

## Skills and combat

| Vector | Before | After |
|---|---|---|
| Cooldowns | **safe**: server clock (`CombatService.Casting.cs:107`, `StartCooldownAsync :466-496`) | safe |
| SP, HP and item costs | **safe** for casts (`ResolveCosts :395`, `ValidateCosts :433`) | safe (see "left open" for the auto-attack loop) |
| Chant skipped | **safe**: the impact is scheduled from server data (`:557-558`) | safe |
| Range and line of sight | **weak**: range from the forged position; sight only through siege air walls (`:369-371`), which is the client's own rule on open maps (7a84354) | **safe** (position now enforced); line of sight unchanged, 1:1 |
| Release 0/2-6 point (friendly areas) | **open**: an ally area took the client's `TargetPos` as its centre, unchecked (`CombatService.Effects.cs:161-163`): Blessing (30 m) on any group anywhere on the map | **safe**: centred on the caster as enemy areas already were (`CombatService.Effects.cs:164`); a point beyond the skill's reach is replaced by the caster's (`CombatService.Casting.cs:393`), which talent procs read |
| Target validity | **safe**: monsters alive and in the scene (`:285`, `CombatService.Pvp.cs:17-19`), players in the scene, invented ids refused (`:340`), `TargetPos` rewritten for release 1 (`:341`) | safe |
| Attack speed | **open**: a swing at another target restarted the swing (`SwingInFlight` needed the same target, `:269-272`), and 31124 with a normal attack as `nxtSkill` cancelled the swing first (`:595-607`): 3-5x attack speed by alternating clicks | **safe**: a turn to another target is taken but starts when the swing in flight ends (`SwingTurnLeft`, `:277`, used at `:138`); 31124 goes through the same rule (`:646`) |
| Skills not learned or too high | **safe**: `Learned` (`:276-283`), mode rows, passives, extra skills | safe |
| Client damage or hit claims | **safe**: damage, hit, crit, kill and area targets are the server's; `targetsId` is never read; `comboIndex` is only echoed | safe |
| Siege cannon | **open**: the shot's `speed` and `gravity` are the client's, only `speed > 0` checked (`TerritoryWarsService.CannonShots.cs:56`): no gravity reached anywhere along the aim | **safe**: a shot landing beyond `_fIndicatorRadius` + 15 m is refused and counted (`cannon`, `TerritoryWarsService.CannonShots.cs:69`) |

## Economy and items

Every change of items or Zeny goes through one pattern: the request takes its reservations (`ReservationCoordinator`),
stages on clones, writes payment, goods and the receipt in one transaction, then publishes in memory from the commit
callback; `operation_receipt.source_key` is unique. No duplication path was found.

| Vector | Rating (before = after) | Evidence |
|---|---|---|
| Player exchange (list, buy, delist, reprice) | safe | GUID in the bag, stack and shelve limits, price range, `quantity x quote == total`, own listing refused, re-checked inside the transaction (`TradeService.Mutations.cs:24-34,105-134,203`) |
| Mail claim and claim-all | safe | mailbox read from the database under the state and mailbox locks, `Claimed` checked, ids de-duplicated, revision guarded (`MailService.cs:695-753`) |
| Auction, prebuy, preorders, stalls | safe | positive and capped prices and counts, own offers refused, long arithmetic (`AuctionService.Bidding.cs:20-25`, `TradeService.Preorders.cs:24-31`, `StallsService.Player.cs:110-237`) |
| NPC shop | safe | price from config, `count >= 1`, `perUnit x count` capped, period limits committed with the payment (`ShopService.cs:131-199`) |
| Crafting, refine, enchant, decompose, forge | safe | materials taken server side through the exchange; the client's cost must equal the config's (`EquipmentService.cs:118-123,333-377`, `LifeJobService.cs:485-519`) |
| Bag moves | safe | only sort and use exist; use count 1..999 and within the stack (`CharacterService.Bag.cs:67-69`) |
| Two sessions, relog mid-operation | safe | a new login kicks the old one and waits for its handover (`SessionRegistry.cs:41-69`, `RoleSessionService.cs:128-129`, `OwnerRegistry.cs:102-103`, `RoleHandover.cs:70-89`) |
| Negative or overflowing counts | safe | every client count traced has a positive or clamped check |
| Choice boxes, treasure maps | safe | index within the options, units add up (`ItemBoxService.cs:78-79`) |
| Gift codes | safe | re-read in the redeeming transaction, redemption key is a primary key (`GiftCodePlans.cs:33,105-121`) |
| Selling above the price | safe | no NPC sell path; prices snapped to config |
| Duplication check (panel) | **weak** before: a partial exchange listing or stall offer keeps the stack's guid, so the check's worst flag ("Instance seen twice") fired on normal play (`PanelBackend.ItemTracking.cs:69-75`) | **fixed**: one character's stack and the part of it they listed are not flagged (`PartialListing`, `PanelBackend.ItemTracking.cs:75,90-93`); two characters holding one guid still are |

## Quests and progress

| Vector | Before | After |
|---|---|---|
| Kill, use, craft and level objectives | **safe**: counted from server events (`TaskObjectives.cs:389-514`, `TaskService.cs:89-104`) | safe |
| Talk to NPC or point (31208) | **weak**: 12 m from the forged position (`TaskService.cs:200-206`, `TaskActions.cs:177-181`) | **safe** (position enforced) |
| Reach area (31209) | **open**: no position check, "the region itself is trusted to the client" (`TaskService.cs:210-211`) | **safe**: the player must be near the region (`SceneService.NearRegion`, `TaskService.cs:215`), counted (`quest-area`) |
| Client interaction (31214) | **open**: only a running task and map (`TaskService.cs:220-224`) | **weak to safe**: when the map places the NPC whose `_iInteractID` lists the interaction, the player must be within 15 m of one (`TaskService.cs:235`), counted (`quest-interact`); an NPC the client spawns itself cannot be checked (44 of the 52 configured interactions name an NPC) |
| Cutscene end (84011), guide (31210), download task | **by design**: the client can skip these; no reward beyond the step | unchanged |
| Turn-in twice, skipped steps, unavailable quests | **safe**: status, prerequisites, conditions, quest lock, one atomic write (`TaskService.cs:335-364,581-612`) | safe |
| Achievements, daily, battle pass, month card, first-heart, growth, titles | **safe**: claimed flags under per-player locks (`AchievementService.cs:721-852`, `DailyActivityService.cs:407-428`, `NewActivityService.BattlePass.cs:92-99`, ...) | safe |
| Dungeon, tower and battle results | **safe**: the client's result messages have empty handlers (`CoreMessages.cs:110-128`); wins come from server kills | safe |
| AFK and offline rewards | **safe**: server time (`WildAfkService.cs:293-305`), the offline-reward message is refused | safe |
| Gathering | **safe** for time and distance (server timer, `Near` and the start position re-checked, `GatheringService.cs:257-268,345-354,564-577`) | safe |
| Gathering mini-game rating | **weak**: `FinishLifeJobGameReq.Rating` is the client's, 1..5 (`GatheringService.cs:363-373`) | left open (see below) |
| Life-job forge batch | **weak**: one item's wait allows `ItemNum` up to 1000 (`LifeJobService.cs:473-488`); materials and stamina are charged per item | left open (see below) |

## Protocol

| Vector | Before | After |
|---|---|---|
| Replay, out of order | **safe** for gain: no nonce, but every reward path is idempotent (claimed flags, statuses, source keys) | safe |
| Another role's entity | **safe**: pets, items, mail, auctions, listings, teams and guilds are looked up through the session's own role (`PetService.cs:244`, `MailService.cs:698-724`, `TradeService.Mutations.cs:203,235`, `TeamService.cs:215`, `GuildService.cs:733`); role select checks the account twice (`RoleSessionService.cs:120,197`) | safe |
| Another party's chat channel | **open**: any channel but `2_`/`5_` could be joined and posted to (`ChatService.cs:183`) | **safe**: a party's `3_<id>` (and `@TGS#_3_<id>`) channel is its members' (`ChatService.cs:185`, `TeamService.ChannelAllowed`); ids that are no party (Survivor teams) stay open |
| Unbounded saved data | **open**: guide ids and records (`GuideService.cs:31-36`) and the settings maps (`RoleSessionService.cs:369-371`) took any key and were saved and sent back at every login | **safe**: guides only from GuideConfig, 256 per update (`GuideService.cs:34-35`); settings past 256 KiB refused (`RoleSessionService.cs:384`, `payload`) |
| Chat line length and rate | **open**: no limit; every line logged (`ChatService.cs:285-353`) | **safe**: 500 characters, 1 line a second with a burst of 10, GM commands exempt (`ChatService.cs:312`, `chat`) |
| Roles per account | **open**: no limit (`RoleSessionService.cs:74-91`) | **safe**: `LC_SelectRoleManager.m_iMaxRoleNumber` (6) (`RoleSessionService.cs:82`, `role-limit`) |
| Message flooding | **open**: no per-session limit on the game gate | **safe**: 120 messages a second with a burst of 600 per session, role create and select 0.5 a second with a burst of 5; dropped before they reserve anything (`GameMessageHandler.cs:27`, `flood`) |
| Frames, msgpack | **safe**: 1 MiB frame, capped decompression, msgpack depth 32 (`PhonestChannel.cs:18,103,129`, `MicroServiceRouter.cs:27-28`) | safe |
| Large client lists (shop batch buy, trade multi-query, auction get, guild lists, totems, voice, micro attribute batch) | **weak** (cost, not gain) | bounded by the frame size and the message budget, not capped one by one |
| Guild requests reserving other players' keys before the permission check | **weak** (contention, not gain) (`GuildService.Ownership.cs:71-102`) | unchanged, bounded by the message budget |
| Micro gate rate | **weak** (cost, not gain) | unchanged |
| A malformed protobuf | closes the connection without a log line (`GateListener.cs:159`) | unchanged |
| GM access with an empty `GmAccounts` | everyone outside Production (`GmCommands.cs:48-53`) | unchanged: configuration, see [security](../systems/security.md) |

## Bots and macros

The server sees when each message arrives, which messages, and how long a session stays in the world. What is realistic:

- **Gains far above the realm**: Zeny, items and EXP per character per hour against the median of the characters that
  gained any in the same window. Bots farm far more than players; the panel lists them (`Suspicious activity`).
- **Refusals**: a bot built on a modified client trips the walk, rate and quest checks; each is counted per account.
- **Time online**: `account_login` keeps logins; a session in the world 20+ hours a day is visible there.
- Not realistic from the server alone: input cadence (the client sends results of input, not keystrokes; a walk goes out
  per turn, a cast per click, both at the pace the client's own timers allow), mouse paths, window focus. A well-made bot
  on the unmodified client sends exactly what a player sends.

## Left open, and why

- **Gathering mini-game rating**: the mini-game runs in the client and only its result is sent; the server cannot
  replay it. The gain is the extra award of a better rating on a node the player really gathers at.
- **Forge batch wait**: whether the original waits once per batch or once per item is not established from the client;
  materials, stamina and EXP are already charged per item, so only time is gained.
- **Auto-attack HP/SP costs**: the server's own auto-attack loop pays item costs but not HP/SP
  (`CombatService.Casting.cs` `AdmitAutomaticCostAsync`); an honest client gets the same, so it is a fidelity question,
  not a modified-client one.
- **A stun landing mid-chant** does not cancel the cast, and a monster's "cannot be damaged" state is not checked for
  player damage: server-side fidelity, not something a client chooses.
- **Interactions with client-spawned NPCs** (8 of 52 configured): no position the server knows.
- **Cost of large requests** (see Protocol): bounded by the message budget, not by a cap per list.
