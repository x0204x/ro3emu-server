# Activities and events for playbook 101

This implementation covers the 47 `NewActivityConfig` rows whose `_kMultiverseArray` contains 101 or 0. `ActivityClock` evaluates their `TimeLimitConfig` windows, including absolute timestamps, server-open offsets, season-stage boundaries and repeated hour/day/week/month segments. The realm opening anchor is `SeasonService.Start`. Eligibility uses the actual activity condition and book; being listed in 5013 does not authorize a mutation.

| Rows | Behavior and protocol |
| --- | --- |
| 102 | Season preparation donation/global milestones, 82960-82962, 5013 |
| 103, 104 | Level and function-preview awards, 82976 plus legacy 83200/83201, 5013 |
| 105 | Fragment requests, gift transfer, craft and progress, 82963-82967/82976 |
| 106 | Treasure map consumption, authored hunt point, NPC interaction and actual encounter settlement, 5064/83251 |
| 107, 108-110, 115, 116 | Configured season/racing/emblem/guild schedule and navigation projections; leaderboard participation remains owned by its gameplay source |
| 111 | Persistent world-exploration nodes and piece consumption, 5042/83450/82976 |
| 112 | Journey act/node/event graph and authored boss dungeons 8100002/4/5/6/7 |
| 113 | Seven distinct eligible login/connected civil days, configured rewards and durable claims |
| 114 | Seven-Day Goal (FirstHeartService): 5049 day locks, task progress and score bar from base level 10, 83500 task and 83501 score claims, role blob `first_heart` |
| 999 | Product/gift-pack activity schedule; paid order and payment reports receive a refusal |
| 117 | Season Eve authored clues and seven-note chapters, shared cycle progress, milestone claims, wishes and durable end-of-cycle reward mail |
| 118 | Prontera invasion rounds, actual invaders, donation, guild/city state and settlement |
| 119 | Training ground, real measured damage, configured race/element/size, best DPS and milestone claims, 5069/84061-84067/30106 |
| 199 | Owned month-card entitlements, expiry, daily claims, 5013/5054 |
| 200, 201, 9201 | Battle Pass EXP, premium entitlement, rewards, level purchase and gifts |
| 1000-1023 | Gacha banner schedules; the `DrawConfig` consumer determines drawable pools, 5011/83410, with owned-ticket costs and pet/item rewards |

The MG_Activity routes are 43501-43503 and 43504's event variant. To-Do admission respects weekly HHMM windows and the early-server custom rotation. The named gameplay paths include Commendation Vault (OnePiece), Kafra Survivor, Dragon Coffin Island, Festival Invitation, and guild campfire. Actual actors use the shared scene-load/AOI/combat lifecycle and private rooms use `InstanceService`; a calendar notification alone does not settle a battle.

Source claims, inventory costs and terminal grants are committed in the same SQLite transaction. Cross-role fragment transfer and pass gift/mail delivery include both owners' source state. Replay and eligibility checks run again on the server. Disconnect, role switch and scene exit release event membership; private scenes retain the existing outside return-point persistence policy.

PvP uses the existing emulator damage formula for player defenders (inferred), inside the active event room and opposing configured factions. Learned skill, SP, cooldown, range, actual HP loss and death are server-checked. Logout blocks activity mutations until the next completed role login. Rejoining a surviving shared room replaces the previous membership and outside return point.

Row112 retains its authored level999 condition (LC_ConditionList21000010); its wire progression tests override only fixture eligibility and still execute the real act/encounter validation.

Cash-shop RPCs 2001, 2002, 2003 and 2005 are refused. Client-authored order/payment data never grants a product. Consuming an already-owned entitlement or lottery ticket is distinct from creating a paid order.

## Clock and core policy choices

The following are explicit **(inferred)** emulator choices, bounded by the listed client contracts:

- The realm civil timezone is the injected `TimeProvider.LocalTimeZone`, matching client `os.date('*t')` calculations. Repeating non-month segments use the client's second-based periods. Weekly segments re-anchor to the current local week exactly as `LC_TimeLimitHelper.L16_1` / `Lua_TimeHelper.GetThisWeekTimestamp(1)` do; day/hour retain their opening anchor and fixed-second iteration. This preserves the client's distinction across daylight-saving changes. Intervals are start-inclusive/end-exclusive. Legacy activity tuple type 2 uses the realm opening anchor, agreeing with the corresponding modern TimeLimit rows.
- Seven-day credit increments once per eligible civil day observed at login or while connected, capped at seven; skipped days do not fabricate attendance. Claims use `SevenDaySignUpConfig._iRewardDate/_iRewardID` and persisted markers.
- Level/function-preview claim maps store the claim timestamp; the client tests key existence. The legacy 83200/83201 `placeholder` scalar carries a result because those schemas have no named result field; the modern 82976 envelope carries its real result.
- Initial nil-sensitive defense/guild, exploration node-item and PvP maps use a wire-only zero-key initialization followed by an immediate deletion increment, following the existing `SeasonRefineService` transport pattern. These markers are never persisted or accepted as guilds, filled slots, ranks or entitlements. This is a transport compatibility policy, not original-client runtime proof.

Two clock rules are directly evidenced rather than inferred: `LC_InteractCondition.CheckPlayBook` compares playbook-open seconds plus the condition offset with system time (condition 3030); `LC_DAL_ServerInfo.PackServerBasicInfo` computes `floor((logic-open)/86400)+1` (3031). Login and 30130 use the same opening timestamp, and a day change refreshes server info/function gates.

## Festival Invitation

`DailyActivityConfig[3301]`, function 5901 and `TimeLimitConfig[1083]` define the Candidate/Ready/Answer phases. The map is `DungeonConfig[1010002]`, gameplay 32, with the real true/false regions 27/28 in `sc_prontera_001_dg01_sceneeditor.json`. `RainbowKoiQuestionConfig._iQuestionAnswer` determines the correct region. `LC_DunGeon_RainbowKoi`, `LC_RainbowKoiHelper`, the 43504 bet oneof, and dungeon variables 1102-1111 define presentation.

Candidate selection from eligible connected roles in stable role-ID order, up to the configured map capacity; the first 15 configured questions; 30 seconds to answer and 5 seconds to reveal; tied elimination ranks; and automatic reward delivery are **(inferred)**. The server samples the participant's actual loaded-room position at the deadline. A successful bet receives `PublicParamConfig.LuckyAwardPreview` once **(inferred)**. `RainbowKoiRewardConfig` selects the placement award. The round and reward ledger are durable; each entitlement is marked claimed in its inventory transaction. Pending committed entitlements are delivered on login. An unfinished round after a restart only counts participants actually present in the recreated room, and the preceding occurrence is settled before a later occurrence is selected **(inferred)**.

## Training

Activity 119 admits maps 2100009/2100010 (practice) and 2100011 (challenge). The actual NPC templates are 3500000/3500001 at the authored editor points. `LC_DAL_Piling.StartChallenge` sends 30106 `OnPileChallengeStart`; the challenge starts on this event, not on a client damage total. The timer is `PublicParamConfig.TrainingChallengeTimeLimit` and dungeon variable `CTrainFinishTime` (3500000) is milliseconds. `OnPileChallengeEnterOrExit` is idempotently acknowledged: the client sends it in response to timer/result changes, so it cannot toggle maps or restart the timer.

Keeping the configured recovery-buff dummy alive, preventing retaliation, and computing DPS as authoritative total damage divided by the full configured duration are **(inferred)**. Practice attributes cannot modify the challenge template. Reset cancels an attempt without qualifying it. The best score and `TrainingAimAwardConfig` claims persist; class eligibility follows the client `Lua_CFG_JobConfig.GetRootJob` parent walk to the child of novice1000. Duplicate claims and insufficient DPS are refused. Reconnect follows the existing private-room return policy and retains best score/claimed awards.

## Test realm switches (`Ro3Emu:Testing`)

**Not original behaviour.** Two switches for test realms, off by default so the server stays 1:1 (`TestingOptions`,
`Game/TestingSwitches.cs`). The server logs a warning at startup while one is on. A GM changes them in memory until the
next start: `!testing` shows them, `!testing alwaysopen on|off`, `!testing unlimited on|off`, `!testing reset` (the
configured values). After a change every online role gets its To-Do rows (5004) and dungeon reward counts (5062) again,
so the client shows it without a relog.

Where the To-Do (function 25) is gated:

| Gate | Source | Owner |
| --- | --- | --- |
| A row's occurrence: weekday `_kDay`, `_kTime` HHMM, the first weeks' `DailyActivityCustomConfig`, `_iTimeLimitId` bounds, `_iTimeCtrlId` phases | `DailyActivityConfig`, `TimeLimitConfig` | `TimedTodoSchedule.CurrentOrNext` / `IsOpen` |
| What the client shows: open, upcoming or ended | 5004 `startTime`/`endTime` (`LC_DailyActivityHelper.GetActivityOpenByServer`, `GetTimeStateByDailyActivityData`) | `DailyActivityService.ProjectWindows` from the same schedule |
| Admission and running occurrences | Survivor Battle / Dragon Coffin Island (`TimedBattleService.Admission`, 114/126), Commendation Vault and Rift Crisis (200, `TreasureWindow`), bonfire (116, `GuildCampfireService`), Ilmata (125, `GuildChallengeService`), Wedding Crashers (3400), Hunting Challenge (3303, phases 1074), Festival Invitation (3301, phases 1083), Mock Battle (3304, phases 1084), Limited-Time task progress (`DailyActivityService.ParticipationOpen`) | each service, through `TimedTodoSchedule` or `ActivityClock.Segments` |
| Reward allowances | `TenGameplayRefreshTimeConfig` (Realm of the Gods, Phantom Realm), MVP daily counters (43011), `RestrictConfig` 1020 and `AwardRestricConfig` weekly caps (Commendation Vault), Dodo's Request `remainRingCount` | `DungeonService`, `MvpService`, `TimedBattleService.OnePiece`, `DailyActivityService` |
| Calendars of their own | Guild League (`GuildLeagueCalendar`), Territory Wars stages (`TerritoryWarsClock`), Royal City War (115) | not switched; GM `!league`, `!twpoint`, `!siege open` |

`AlwaysOpen`: `TimedTodoSchedule` gives every To-Do row but the Guild League and Territory Wars ones (with their auction
checks) an occurrence that holds the present. The test day has two parts, 00:00-05:00 and 05:00-23:59:59, each inside one
civil date and one game day as `GetActivityOpenByServer` needs. A row repeats its own `_kTime` length back to back from
the start of each part (a whole-day 500/500 row takes each part whole; the last occurrence of a part is cut at its end). A
row with `_iTimeCtrlId` runs its phases: `ActivityClock.Segments` repeats the first gap-free run of that id's rows back to
back, with their state names and lengths (1083 Candidate 1 min, Ready 4 min, Answer 25 min; 1074 the four hunt phases;
1079 one 30 min Start; 1084 Matching 5 min then Battling 10 min), and a run is never cut, so a part can end with a gap.
Each occurrence is a new one for its owner (a new Festival round, wedding run, hunt, Ilmata cycle). The bonfire's 25
minutes and the Rift rounds run from the occurrence's start instead of 20:00 and 10:00. A GM test window (`!dracula open`,
`!guildchallenge open`, `!guildhunt open`, `!defense round`) still wins over the switch. The client's calendar tab
(`GetTodayCalendarWindow`) reads the times from its own config, so it keeps showing the original hours.

`UnlimitedRewards`: a won Realm of the Gods / Phantom Realm clear pays its personal award without checking or spending the
balance (5062 `useNumber` shows 0); MVP awards ignore and do not count the daily First/Last/Team/Join caps (and nobody's
team score decays for being capped); the Commendation Vault pays its extra award past the daily count and the weekly item
caps without spending them; Dodo's Request keeps `remainRingCount` 1, so a new chain can be accepted after the last one ends.

Not covered: the To-Do's own completion counts (`_iCount`, the liveness points), the Guild Caravan's five daily rounds,
AFK time, Life Skill energy, shop purchase limits, function unlocks by level or server day (`OpenContent` handles those),
and the Guild League / Territory Wars / Royal City War calendars.

## Detailed policies and evidence

The remaining inferred choices are enumerated in [adventures](activities-adventures.md), [Battle Pass/month card](activities-battlepass-monthcard.md), [timed events](activities-timed-events.md), [campfire](activities-guild-campfire.md), and [lottery](activities-lottery.md). The search census and precise scope are recorded in [activities-evidence.md](../research/activities-evidence.md). These documents describe implemented emulator behavior; they do not claim measured original-server parity.

## Tests

The activity tests use the BotClient wire fixtures with isolated data directories and ephemeral loopback ports:

```powershell
dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~TimedEventTests|FullyQualifiedName~NewActivityTests|FullyQualifiedName~BattlePassActivityTests|FullyQualifiedName~GuildCampfireTests|FullyQualifiedName~LotteryActivityTests|FullyQualifiedName~ActivityAdventuresTests|FullyQualifiedName~ServerOptionsTests"
```

The PvP fixture reserves its ports together, checks every cast result, and consumes a bounded stream of actual hit packets with HP/room diagnostics on failure. Production hit randomness is unchanged.

Listeners follow the `ServerOptions.BindAddress` / `ListenAddress()` rules: loopback by default when `PublicHost` is loopback, otherwise wildcard, with an explicit bind override. Test listeners use loopback and the shared `TestPorts` helper.

Production Kafra/Dragon capacities are the configured 100/30 players with five-player teams; wire tests use isolated 2/1 and 4/2 lobby/team fixtures to exercise readiness, opposing factions, real damage and settlement. No full 100/30-player load run has been made, and the behavior has not been checked against the original client. These tests establish the listed emulator behavior, not measured original-server parity.
