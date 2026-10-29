# Territory wars and auctions

Implementation covers the 57 message-table entries belonging to `MG_TerritoryWars` (including escorts and war hall) and the seven `MG_TerritoryAuction` entries. It uses the existing guild aggregate, character exchanges, combat, world and `InstanceService`. Escort details, its inferences and evidence are in [territory-escort.md](territory-escort.md). This is a reconstruction from the supplied client; numeric/config facts and inferred server policies are distinguished below.

## Protocol and data

| IDs | Behavior |
|---|---|
| 83001/83002 | Selective/full region, state and point tree, real owner guild snapshots, ownership notifications. |
| 83003/83008 | Guild ownership, current auction region/deadline and score. See the descriptor conflict below. |
| 83007 | Camp validation, guild requirement, change/combat cooldowns; combat hostility is checked on the server. |
| 83010/83011/83016 | Legacy currency-126 auction, ranking and deadline; shares qualification seats with the current auction. |
| 83012/83013 | Battle-state enum and stage boundaries in seconds, as the legacy UI subtracts `GetSystemTime`. |
| 83014/83015 | Durable real-battle history/statistics and participant result notification. |
| 83017/83018 | Objective ownership/guard markers and minimap subscription. |
| 83019 | Fatigue cap notification followed by transfer to configured map 10100. |
| 83020-83023 | Capacity queue, admission countdown, cancel, departure-area and exit reasons. |
| 83024 | Proximity-checked donation of carried territory supply; character and guild update share one transaction. |
| 83050-83100 | Escort/trading/cart/score/skill/transfer family; see escort document. 83094 also carries war guild population. |
| 84031-84037 | Currency-129 auction, observed-bid check, incremental holds/refunds, automatic plans and pushed rank/settings. |
| 84081 | Guild donation ranks for UTC week, month and season. |
| 30400/30401/30402, 31013 | Shared instance entry/base state/leave and gated revival. |
| 30110/30114/30116/30117 | Real territory participant damage, healing and score statistics; historical reports check membership. |
| 5013, 5020 | Activity 2000's phase, owned/qualified guild lists; siege honor, valor, qualification and fatigue dataset. |
| 30115, 31102/31101 | Global objective state and the existing skill cast/hit path, including hostile player targets. |
| 30012, 32700 | Existing generic world-map travel and owned supply-pickup interaction; ordinary NPC interaction routing is preserved. |

84032 and 84035 produce 84037 and 84036 respectively, following the Lua DAL. A rejected 84031 produces `CommonErrorNtf` (2), since its normal response always activates the client's success handler. Received notification packets never author ownership, cart damage or results. Forged 83015 and dungeon script events cannot settle a war.

## Positive evidence

- `TerritoryWarStageConfig` has 24 stages. `TimeLimitConfig` is joined by `_iTimeLimitId`, not its row key. `Logic/Activity/LC_TimeLimitHelper.lua` resolves absolute Greenwich time before server-opening/season-stage offsets. The exported territory windows are 2026-05-19 through 2026-05-27. They remain absolute. Only a configured server/season-relative expression uses `SeasonService.Start` and the resolved season-stage boundaries. `PlayBook=101` enables this activity; other playbooks produce off-season.
- `GVGWarZoneConfig` supplies 36 region/state/point/land rows, their parent links and dungeon/map IDs. Point 3 supplies NPC 4905101 and editor point 33. `WorldData` loads editor points and navigation grounding. Ownership is persisted as guild IDs, with current `GuildService.Snapshot` data on the wire.
- `PublicParamConfig` supplies currency 129, minimum bid 1000, minimum guild size 20, seats 9/2/2, ratio 1000, step/delay tables, final interval 27-587 seconds; legacy parameters supply currency 126, opening 100 and increment 100. `LC_TerritoryWarsHelper.IsHasTerritoryWarsAuctionRight` selects guild right 25; FunctionConfig 72 supplies the level gate. The auction DAL consumes `isOccupier`, `selfGuildCanBid`, `isSettled`, returned bids and current guild observations.
- `TerritoryWarResultRuleConfig` supplies final-auction limits 27/9/3 and battle-result limits 9/1/1. `LC_ActivityParser_TerritoryWar` and `LC_TerritoryWarActivityHelper` consume the server's qualified guild list. The precise reduction policy used here is inferred below.
- `GVGConfig` help text describes four occupation fields, shared resources/buffs and last objective ownership at the end. Its numeric fields provide 30-second respawn, 15-second guard, occupation speed 1 capped at 3, amount 30, resource growth 14 plus 4, cap 48000, buff 10301, revival countdown 15 and exit delay 5. Territory, siege and GVG have distinct client controllers; applying these rules to reconstructed territory objectives is explicitly inferred.
- `MG_Dungeon.proto` defines end-cause enums. Scheduled war settlement emits timeout/stage-timeout (1/1), rather than putting round numbers into cause fields. `LC_DAL_TerritoryWars` compares `winEntityId` with the viewer's guild to present victory or defeat.
- `SiegeSettlementPrimaryAward`, `SiegeSettlementHonorAward`, `SiegeSettlementSeniorAward`, `MultiverseGroupConfig`, `AwardConfig` and the stronghold public parameters supply the actual award groups. Fixed entries and drop pools are resolved with binding flags preserved.
- `data/msgtable.tsv` contains AUTO_SYNC `5020` for `DBRoleSiegeData`; the implementation follows that table.

## Persistence and lifecycle

The world aggregate contains auction holds, finalized qualifiers, map owners, battle counters/objectives, results and donation totals. Guild treasury and world hold/refund changes commit in one SQLite transaction before notifications. A per-role `territory_wars` blob contains season counters, donations, fatigue and reward outboxes. Settlement writes ownership, report and exact rolled goods together. Delivery writes inventory, consumed outbox and paid key in one character exchange transaction. Every subsequent role write reloads the durable blob, so scene cleanup cannot restore an already-paid outbox after an awaited notification.

One `InstanceService.Room` is shared by all qualified guilds contesting a territory. Dynamic admission retains each participant's original return point. Qualification is checked on entry and while present; a role changing guild is removed. Loading, disconnected and wrong-instance sessions cannot inflict PvP damage. Objective HP and ownership are server-owned; the combat service reserves a lethal hit before awaiting notifications, preventing duplicate kill/cargo/capture callbacks. Empty rooms clean up their tracked actors through the existing instance lifecycle.

Participation rewards require the actual 30005 loaded-scene handshake. The combat path retains role/scene identity across awaited cart-damage hooks and syncs current reserved HP, so overlapping AoE cannot restore stale HP or settle death against a different role on the same connection. A damage-hook exception is logged and isolated; lethal monster reservations are finalized/cleared. Tickers recover dead cart, objective and land-activity actors when a departing caster can no longer receive attributed kill callbacks, without granting an abandoned hit to a new role.

An epoch is `(SeasonService.Start, PlayBook)`. Administrative rollover refunds unfinished auction holds once, closes rooms, stops automatic plans and archives ownership/results. Returning to an epoch restores its terminal ledger instead of minting new refunds or rewards. Paid/pending reward keys survive epoch changes. Honor/valor/donations have per-epoch archives.

World supply is sourced by actual scene-editor spawn-group pools containing NPCs 4910101/2/3. Their NPCConfig `_iCAward` groups 120020/21/22 resolve through MultiverseGroupConfig to awards 100120020/21/22: 10/20/40 currency 133. A collected actor is consumed once; the configured carrying cap is 5000. Death deductions and persisted 4910104 pickup contents commit together, and collection removes the persisted drop in the inventory transaction. State is published before awaited item notifications. Epoch changes invalidate scheduled respawns and remove old actors before reseeding occupied maps.

`GVGLandActivityConfig` type-1 rows place activity NPC 4907011 at world-map editor points. Thirty-four rows bind directly. Rows 20011/20036 request point 100001, while the corresponding editors export second monster points 1000001/10001 beside black-hole points 100000/10000. Two explicit inferred aliases resolve these rows; an exact configured point takes precedence. Checked dictionary lookup prevents default-origin placement. Actual kills produce configured award 1010 and eligible owner extra 1011 through the same durable reward outbox. `RestrictConfig` caps claims; `_iRespawnCD` controls the next actor. This makes territorial ownership affect a real reward route in addition to eligibility and battle field buffs.

## Inferred policies

These choices are implementation policy, not a claim of an observed original server algorithm:

1. Time intervals include their start and exclude their end; the hidden final deadline is drawn once inside the configured final stage and persisted. Week/month donation boundaries use UTC. History retains the last 20 reports per zone.
2. A bid holds the increase over the guild's previous bid, eviction refunds the whole held amount, and cancellation stops automation without withdrawing a seated bid. New and legacy auctions share seats. Their unlike currencies are compared by opening-bid multiples (100 legacy = 1000 current), and refunds retain original currency. A current increment is opening price × ratio. Automatic bids use the first configured step at or above the minimum, bounded by maxBid; membership and the setting officer's permission are rechecked at execution.
3. Final-auction qualifiers are capped by their result rule after per-zone seats, ordered by incumbency, held bid, recorded escort score, time and guild ID. Battle-result qualifiers are real winners ordered by score then guild ID, capped at 9/1/1. Per-map ownership remains per-map. The best advancing prior owner in each branch receives one incumbent seat in the next phase; the configured seat count includes that seat. Paid bids remain open to otherwise eligible guilds. Point-phase qualifiers are distributed round-robin across the three stronghold maps in their selected state. This is one explicit interpretation of the differing seat and result-limit configurations, not proven original progression.
4. Zero objective columns use the recovered stronghold-flag template and a grounded non-born editor point. Capital/siege fields use navigation-checked offsets around that objective. Point wars rank capture/hold score; later wars use the final objective owner. Capture gives 50, each occupied point adds one score per active second, kills add one, ties prefer the incumbent then guild ID. Offline time does not farm score; delayed tick accrual is capped at five seconds. No untouched neutral map is awarded merely because a guild qualified.
5. The configured GVG field/respawn/guard/revive behavior is applied to territory rooms. Majority capture uses a five-meter radius; equal presence pauses progress. Queue admission uses five-meter departure checks, five-second expiry and three-second admission countdown. Objective markers reuse `InteractInfo` ownership and active object fields. Field resource counters are maintained with the configured growth/cap.
6. A battle awards configured participation/occupancy groups and settlement rank groups to actual participants; senior awards require a winning capital/siege participant. Honor follows personal score and valor follows kills. These are battle settlement triggers for the supplied groups. Rolled rewards and binding are durable and replay-safe.
7. Season rollover aborts unfinished battles without manufacturing a winner or payout. Restoring a prior epoch restores that terminal state. Donation material conversion and territory-world ownership rewards are documented with their implementation evidence. Escort-specific production, damage, scoring and phase policies are listed separately in the escort document.
8. World supply starts with each exported spawn pool's minimum population, capped by its total; pickup reach is five meters and an over-cap pickup is refused whole. Normal supply respawns at the editor interval. Death-loss rates `[0,0.2,1]` are indexed by Peace/Free/Guild camp and loss is rounded upward; death drops persist until collected. World PvP is restricted to territory maps with configured PvP modes; Free/Guild can initiate against a non-guildmate and Peace cannot initiate. Supply donation converts 133 to guild Materials/126 one-for-one. AuctionMaterials/129 is the guild core treasury, kept distinct from that supply and escort stock; auction tests explicitly provision treasury fixtures.
9. A land-activity killer receives the normal award, and an owner-guild killer in the configured owner radius also receives the extra award. Restrict recovery type 2 is interpreted as a UTC weekly limit (three claims for these rows), scoped to the epoch/activity. Map 30311 point 1000001 (named trap) and map 30336 point 10001 (named spawn) substitute for configured 100001; both are the second exported monster point alongside the black-hole point, matching the arrangement on the other maps. Six type-0 rows set map resource and point to 0; they are not attached to an invented map, and their exact scene binding is a remaining source-evidence boundary.

## Commander tools (siege and territory battles)

The commander's tools come from the client's own handlers (`LC_SceneCom_Command`, added only by the Siege and Territory Wars dungeon scenes). `TerritoryWarsService.Command` keeps them on the battle, so they end with it.

| IDs | Behavior |
|---|---|
| 30407/30408 | Map marks on the siege map, up to 10 per type. Commander marks need guild right 12 (BattleFieldCommand) and are shared by the side; team-leader marks need the party leader and are shared by that party on the side. The client adds without an id and deletes by a global id (`(type - 1) * 10 + slot`, 0 = all of that type); 30408 is a full snapshot per type with slots 1-10. |
| 30431 | Gather at a mark. The client drops the response and has no 30438 handler; the visible gather is system chat 4110041 (DungeonCollect) with a CHAT_PARAM_GATHER parameter [sender, mark type, slot], which raises the 3D guide on that mark. |
| 30436/30437 | Command letters A-F on targets, right 12. Siege ids are dungeon point ids, territory ids are NPC config ids; 30437 is the side's six positional slots. |
| 31102 + 30439 | Command skills (`TerritoryWarsService.CommandSkills`), right 12. The panel casts with the ordinary 31102 at the caster's position; CombatService's `OwnSkill*` hooks skip the job tree and the SkillConfig cooldowns. Charges and cooldowns are the scene's `DungeonBase.CommandModule` (DungeonLua_2230101 for the siege, DungeonLua_3030101 for territory battles via `PointWarCommandSkillConfigDungeonId`): Blessing of Light 4909001 two charges (skillCd 20 s in the siege, 180 s elsewhere), Battle Command 4909101, Defend the Glory 4909201, Emergency Call 4909301 one each, all with a 5 s common cooldown. 30439 is the side's whole panel in epoch ms. Blessing of Light and Battle Command put their `_kBuff1` on the side within 20 m; Defend the Glory revives up to 20 fallen allies there with 70% HP and SP. |
| 30118-30122 | Emergency Call: 30118 invites the side with the commander's id, 30119 accepts (828 dead, 5203 mounted, 292 expired, 293 full), 30122 counts the accepted (max 20), 30120 clears the tips after 20 s or at 20. An accepted ally lands at the commander's position at the cast. |

Loading into the battle sends the side's commander marks, the party's own marks, the letters and the command skill panel. A full mark list answers `ErrorCodes.Parameter` **(inferred)**: the client stops at 10 itself. Checked in the client: a commander gets the Commander and Management buttons, a letter shows on the target with its 3D guide and the "New marker!" notice, and a commander mark shows on the siege map with "Can Mark: 1/10". **Inferred** for the skills: the panel is the side's (any commander of the guild casts from the same charges), a used charge recovers `skillRecoverCd` after its use, one at a time, with a fresh 30439 then; Blessing of Light's buff group 49090 carries SpecialStatusConfig 66 (光明庇护, no knockback); 292/293 for an expired or full call; an arrival lands on the commander's cast position.

## Battle Merit (Battle Glory)

`TerritoryWarsService.BattleGlory` (function 7205, base level 99). The client shows the merit rank and its rewards from 5001 `DBBattleGloryInfo` (`curRankId`, `isSpecialRank`, the keys of `rankRewardReceiveInfo`) and the points from currency 15 ("War Merit Points"). 83400 claims a reached rank's `_iNormalRewardGroup` once (ranks 2-9, MultiverseGroup 50102); the client has no handler for the reply and reads the claim from 5001.

| Rule | Value |
|---|---|
| Where | maps with a `BattleGloryBase` row (30301-30319), public line 1, between `WorldHostile` players |
| Kill / assist / death | `_iKillBase` 150 / `_iAssistBase` 100 / `_iDeathBase` 100 |
| Repeats | `BattleGloryKillDif` / `BattleGloryDeathDif` by how many times today the same pair met that way: 100%, 80%, 60%, 40%, 20%, then 0 |
| Power | kills and assists times `BattleGloryPowerDif` of the victim's power minus the earner's (the row with the largest threshold reached) |
| Daily cap | `BattleGloryDailyLimit` 1,000,000 per game day (05:00) |
| Rank | the sum of the `_iGloryCondition` of the ranks below: 1000 for rank 2 up to 36000 for rank 9; it never drops |

**Inferred:** all the point rules (the client only displays results); an assist is a hit on the victim in the last 10 s; a death pays rather than costs (the record source enum names Kill, Assist and Death as ways points came); the state is the role's territory `RoleState.Glory`, per season. Not done: rank 10 (`_iRankCondition` 10, the top 10 of leaderboard 2102, which this client ships no LeaderBoardsConfig row for), the season-end SiegeSettlementHonorAward mail 1090, and the "Reach N Battle Merit" quests (target 106610).

## Descriptor and runtime evidence boundaries

The checked-in `MG_Guild.proto` defines `TerritoryWarsGuildData.occupyWarzone` and places auction fields inside `guildData`. `LC_DAL_TerritoryWars.lua:884-925,988-1008` reads `campDatas` and some wrapper-level auction fields instead. Implementation sends the supplied descriptor, plus the coherent modern 5013/84037 and deadline notifications; exact legacy panel decoding remains unverified. A new field number is not invented.

A search for `campDatas|TerritoryWarsGuildData|TerritoryWarsGetGuildDataResp` across all config JSONs, scene editors and monster-ID maps, nav, loc, the whole `re/lua-src` and `re/clientB/lua-src`, decompiled C# and both proto trees found the Lua reader and current proto shapes, which disagree as described; it does not establish an original packet trace. The escort-stage notify discrepancy is described in [territory-escort.md](territory-escort.md).

The same search roots were checked for `GVGLandActivity|4907000|4907001|4907010|4907011|4910104` and `100001|1000001|30311|30336`. The former finds the configured land activity and client controllers; the latter finds the map/config references and exported editor/monster-ID geometry. Direct comparison of `GVGLandActivityConfig` with `sc_gvg_lingtuzhan_001_{11,36}_sceneeditor.json` yields the two point-ID discrepancies above. The aliases are an explicit reconstruction from those positive editor entries, not an original-server trace.

Repeated empty proto fields use the generated protobuf contract. Empty-list Lua decoding, queue packet-to-UI bridging, objective/cart presentation and legacy panel behavior still need verification in the game client. Wire bots do not prove those UI paths.

## Validation

The territory test classes (`TerritoryWarsTests`, `TerritoryEscortTests`, `TerritoryPvpTests`, `TerritoryWorldTests`, `TerritoryPersistenceCaptureTests`) run real servers with isolated data directories on free loopback ports. Collections run sequentially to bound resource use; the PvP concurrency test still overlaps two real wire casts across an awaited damage callback. The focused cases cover three-guild auction/war/settlement, auction persistence and revoked authority, 24-stage timing, escort market conservation/finite stock/cart damage/cargo redemption, PvP refusals/overlapping lethal casts/throwing hooks, finite world pickup/death recovery, and the two editor-point aliases.

```powershell
dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~Territory" -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
```

Behavior in the game client UI is not covered by these tests.

## Stronghold gameplay points (30129)

Implemented (`TerritoryWarsService.ScenePoints.cs`):

- Proven: `Config/Dungeon/DungeonLua_3030101.lua` `UIModule_NpcData` lists fifteen spawn points, all with NPC 4905010: 34/59/74 SteleBarrier (7), 41 DivinityStatue (8), 49/57/83 SummonPlatform (9), 51 BossPlatform (10), 63/66 AirWallGate (11), 55/56/81/85/86 RevivalPoint (12) (types from `LC_TerritoryWars_GameplayPointHelper.lua`). No other DungeonLua declares types 7-12, so only 3030101 (GVGWarZoneConfig zone 3) gets 30129.
- Proven: the client registers `GlobalScenePointDataNotify` (`LC_DAL_TerritoryWars.lua:456`), merges it point by point into `TerritoryWarsScenePointInfo` (3225-3284) and deletes that dataset on every scene switch (500-510); it never requests it. The server sends the full set from the battle's scene-loaded hook and only the changed point afterwards. `readyTime` is epoch milliseconds (`UI_Sys_TerritoryWars_MapWidgetCtrl.lua:462-470` divides it by 1000 against `GetSystemTime`).
- Proven: revival points are neither monster spawn points nor air walls of the client scene, so the big map takes their position and Self/Enemy/Neutral owner only from the 30115 `GlobalObjectData` whose `dungeonPointId` is the point (`UI_Sys_TerritoryWarsDungeon_MapContainerWidgetCtrl.lua:440-520`, `GetRevivalPointData`). An entry with `hp <= 0` is dropped (`LC_SceneCom_GlobalObjMgr.lua:82-88`).
- Inferred: one 4905010 marker per point at the scene-editor point (`m_kAllPoints` of `sc_gvg_lingtuzhan_001_1_dg01`). Evidence: the npc column of `UIModule_NpcData`, UnitConfig 4905010 "据点战-交互点" (stronghold battle interaction point), NPCConfig subtype 250 MutiInteractMainNpc with `_iNeedNotifyGlobalData 1`. Markers are non-combat room props; their 30115 entry carries the point's owner as `belongGuild`.
- Inferred: every point starts `ready`, `readyTime 0`, owner 0. The client shows `ready` and an `unReady` point without `readyTime` alike. Point state is persisted in the battle record.
- Test aid, not original: `!twpoint <point> unready|ready|complete [readySeconds] [owner|self]` changes a point of the caller's battle so the client display can be checked. No original rule that changes a point is known, so nothing else calls it.

What the client and config show about each object, and what is unknown (a decision or a capture is needed for each):

| Type | Evidence | Unknown |
|---|---|---|
| SteleBarrier 34/59/74 | NpcLevelPointRespawnConfig 4905003-4905005 place 4905012 "Barrier Stele" (destructible: HP bar, `_kAttriScale` 101 x1.7) at these points. Scene groups 38/39/40 "结界-交互组" hold the point plus three sub points (35-37, 60-62, 75-77). Sub NPCs 4905011/4905014 (MutiInteractSubNpc, NPCInteractConfig 4905011/4905012, conditions 7000031/7000032, NPC conditions 490021/490022 on condition var 9012) exist without a placement row. ChatConfig 4111007 "Stronghold barrier activated. The stronghold will be locked in 5 minutes", 4111008 barrier destroyed, 4111025 stone monument activated, 4111026 defensive stone monument destroyed. The map hides the icon until the point is owned and shows `Preparing` neutral (`MapWidgetCtrl:150-230`). | Who may activate it and how (where sub NPCs stand, what var 9012 is), the 5-minute lock's effect on the flag, stele HP regeneration/respawn, the 30129 state after destruction. |
| DivinityStatue 41 | Groups 45 "神像-攻击-交互组1" (points 41-44) and 47 "神像-防御-交互组1" (78-80, not in UIModule_NpcData). ChatConfig 4111012 "Statue Ready!", 4111017 statue activated. No NPC or spawn row. | Its effect, cooldown (`readyTime`), who may use it. |
| SummonPlatform 49/57/83 | NpcLevelPointRespawnConfig 4905008-4905010 place vehicle 4900402 (subtype 103, NPCType 2, interact 4900022/4900025). Groups 48/53 "载具-交互组", 82 "载具-防守方" (defenders). ChatConfig 4111013-4111015 southern/eastern/base vehicle point ready, 4111018-4111020 activated. | Vehicles are not implemented server-side (`SiegeVehicleUse` answers FeatureUnavailable); ready timers and sides. |
| BossPlatform 51 | NpcLevelPointRespawnConfig 4905011 places 4905053 "Vanguard" (Pioneer subtype 213, AI 20000011, skills 4905002-4905004). Group 54 "先锋-交互组1". ChatConfig 4111016 altar ready, 4111021 "a Behemoth is advancing toward the stronghold", 4111027 behemoth fallen. | Activation condition, route, timer. |
| AirWallGate 63/66 | Client air walls 63/66 in groups 65/68 "门-1/门-2" with spawn points 64/67, where NpcLevelPointRespawnConfig 4905001/4905002 place Knight Guardian 4905051 (camp 2). ChatConfig 4111022/4111023 east/west gate breached. | Whether the guardian's death opens the gate; the server loads no air walls (`m_kAirWalls`) at all. |
| RevivalPoint 55/56/81/85/86 | `m_kResurrectionPointList`; NpcLevelPointRespawnConfig 4905006/4905007 place Bow Guardian 4905052 at 55/56. ChatConfig 4111024 "A Revive Point has changed hands!". The generic per-player revive list is CustomData 11 `Dungeon_Player_RevivePoint_ListLong` (`LC_CustomDataHandler_Revive.lua`, `LC_DunGeon_Helper.SetRevivePointList`). | The capture rule and initial owners. Today every territory participant revives at `MapWorld.Revive`, the first resurrection point (55), regardless of owner; restricting revival to owned points (and the default point otherwise) needs that rule. |

The guardians, stele, vehicles and vanguard of NpcLevelPointRespawnConfig are not spawned: their placement is configured, but when they appear and what their deaths change is not, and spawning hostile camp-2 guardians without those rules would add fights with no consequence. The command skills of `DungeonLua_3030101` `CommandModule` and its two-stage `DungeonStageModule` (45 min / battle) are outside this change.

