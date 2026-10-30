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
| Leaderboard | 2102 (`enum_LeaderBoardType.BattleGlory`): this season's earned points, ties by role id; up to 3000 rows |
| Champion (rank 10) | 5001 `isSpecialRank`: rank 9 reached and a place within the champion row's `_iRankCondition` (10) on board 2102 |
| Season end | MailConfig 1090 with the place as `${1}` and the `SiegeSettlementHonorAward` row whose `_kRank` holds it (places 1-50), once per role and season |
| Quests | target 106610 "Reach N Battle Merit" (TaskConfig 120001-120005, Realm Campaign 107): this season's earned points |

Evidence for the rest: `LC_DAL_BattleGlory._updateRealLevel` shows `Lua_CFG_BattleGloryDan.GetFinallyLevelId` (the row with an `_iRankCondition`) while `isSpecialRank` is set, `curRankId` staying the points rank. `UI_Sys_TerritoryWars_BattleFeatsSettlementPanelModel` asks board 2102 for the player's own place (`GetMyRankByBoardId`), and its panel shows the `SiegeSettlementHonorAward` award list for that place (`GetConfigIdByRank`, `GetAwardListById`) with the 1090 texts. Board 2102 has no LeaderBoardsConfig row, so `RankService` gives it the other role boards' 3000 rows.

**Inferred:** all the point rules (the client only displays results); an assist is a hit on the victim in the last 10 s; a death pays rather than costs (the record source enum names Kill, Assist and Death as ways points came); the state is the role's territory `RoleState.Glory`, per season. The board's tie-break and size; the champion also needing rank 9; mailing the standings when the Territory War season rolls over (`WorldState.GlorySettle` keeps the seasons still to pay, retried by the reconciliation); a target 106610 reading earned points rather than the spendable currency 15.

## War warehouse and machine permissions

`GuildService.Wartime`, the guild's `guildWartimeInfo` (DBGuildData field 36): the warehouse view, the Tactics panel, reserving and building a cannon; firing, vehicles and machine HP follow below.

| Message | Use |
|---|---|
| 32273 | The warehouse: every machine item and its count (`LC_DAL_Guild.ReceiveWartimeStoreItemNtf` -> `GNWartimeStoreItems`). The War Manager page counts firearms and vehicles from it, the siege battle bag lists it. Sent with every full guild info (login, join, refresh); a former member gets an empty one. |
| 32284 | Both machine pages (Firearm 1, Vehicle 2), each a list of operator slots (`roleGuildId`, `roleUsingToolId`). The client stores slot *i* at list position *i*, and later messages address it as `bagIndex`. Any member may read it: the guild window's War Machine page asks too. |
| 32282 | Put a member in a slot or clear it (`roleGuildId` 0). |
| 32283 | Swap two slots of one page: dragging a name onto another slot; an empty slot moves the name. |
| 32286 | The changed slots, to every online member. |
| 32285 | To a member whose own page changed: the machine type they may use (`SiegePermissionSelfData`; the battle bag shows only that type), None when revoked. Also sent with the full guild info while the member holds a slot. |

Editing needs GuildPermissionConfig 13 War Machine Management (`ErrorCodes.GuildPermission` 466); outside a guild every request is 6001 as before. A bad page or slot is 82, a non-member 454. A member who leaves or is kicked frees the slot.

**Inferred:**
- 10 slots per page. No config row gives a number and the panel shows what it receives.
- A guild that never used its warehouse holds `SiegeInitItemList` (50 each of cannons 1006001-1006004).
- A member holds at most one slot. The client only offers members without a slot; anything else is 82.
- Slots are guild-wide: the siege `dungeonId` in 32282 is not used.
- `roleUsingToolId` is the member's standing machine of the page's type: a cannon on the Firearm page, a vehicle on the
  Vehicle page, never one under the other.
- Revoking takes the machine away. The panel asks before changing a slot whose holder has a machine
  (`LC_SiegeHelper.SendModifyPermissionReq`: UIWords 61027 "Revoke Access", 61028 "This Member is currently using a war
  machine. Revoke their access?"), for clearing the slot or giving it to someone else. The holder's cannon or vehicle
  is then destroyed as a replaced one is (UIWords 61001): whoever is on it gets off, it leaves the map and nothing goes
  back to the warehouse; 32285 and 32286 show nothing in use. A machine left standing without its slot would stay the
  former holder's (interaction 4900005 checks `CheckBuilder` against the entity's owner, not the permission) or nobody's,
  and the client keeps no machine outside a slot. A reserved machine and a cannon still being built go back to the
  warehouse (the builder goes back to Idle). Leaving or being kicked from the guild does the same.

### Reserving a machine (32270, 32272)

Clicking a cannon in the siege battle bag (`LC_WartimeItemHelper.TryBuildCannon`) sends 32270 with that item. On
success one machine leaves the warehouse and is held for the member (`prepayInfo`, `GuildStore.State.WartimePrepay`);
the answer's `prepayItem` starts the blueprint (`ReceiveWartimeStoreItemPrepay` -> `DoPrepayAction` ->
`LC_GVGBuildCannon.StartBuild` with the item's GVGCannonConfig row), and every online member gets the new 32273. A
refusal shows its code instead (`LC_AssetManager` calls the DAL's `DecodeError` for a nonzero `result`). Esc on the
blueprint sends 32272 (`LC_WartimeItemHelper.CancelPrepay`) and the machine goes back. Leaving or being kicked from the
guild gives a held machine back too.

Refusals: outside a guild 6001, not loaded into a running siege or active type-18 territory battle the member fights 28, an item that is not a cannon of
GVGCannonConfig (`_iItemID`) 82, no Firearm permission 466, none left 535 "Not enough guild supplies".

**Inferred:**
- Reserving needs the Firearm permission: the battle bag only lists the member's own machine type (32285).
- One machine per member: a second reservation gives the first one back.
- The first reservation turns the initial list into the guild's own stock.
- Cancelling with nothing held answers 0: the client sends it unconditionally and has no handler for the answer.
- A machine held when the blueprint closes on death or a scene change (the client exits without 32272) stays held
  until the member reserves again, cancels or builds.

### Building a cannon (32271)

Confirming the blueprint (`LC_GVGBuildCannon.Build`) sends 32271 with its position and facing. On success the reserved
machine is used up and the answer closes the blueprint (`LC_DunGeon_Siege.GUILD_ATTACK_CITY_BUILD`). The builder then
works for GVGCannonConfig `_iBuildTime` (3 s): 32702 `InteractChangeNtf` BuildingCannon (status 1, `_iBuildStatusID`)
with `buildCannonInfo` {Building, the GVGCannonConfig id, the end time}, to the builder and everyone who sees it
(`LC_GamePlayStatusMgr.BuildingCannon`). After it the cannon stands and the builder goes back to Idle.

The cannon is the row's `_iNPCID` (NPCConfig type 2, sub type 102), a unit of the siege room, so it ends with the siege.
Its entity names the builder as `owner` and carries the builder's camp, which interaction 4900005 checks (`CheckBuilder`
reads the head info Owner, `CheckCamp` the EntityCamp), and wears the NPC's `_kBornEventBuff`. Its `name` is the
builder's name: `LC_Unit_NPC.GetUnitName` titles a GVGCANNON (sub type 102) with a name as UIWords 61000 "${1}'s ${2}"
(that name, then the NPC's `_kName`), as the client's own blueprint does with the local player
(`LC_GVGBuildCannon`). Vehicles keep their NPC name. The builder's slot shows
it as `roleUsingToolId` in 32284/32286 and in the builder's 32285. A member with a standing cannon who builds another
loses the first (UIWords 61001, "Building another will destroy the current one").

Refusals: 6001 outside a guild, 28 outside a running siege or type-18 territory battle, 82 without a reserved machine or while building, 530
"Cannot place this here." for a spot the client would refuse: farther than `_iBuildRadius` from the builder, not walkable,
or inside another cannon's `_iExclusiveRadius` (both circles, as the client's `Circle.CheckCircleIntersection`).

**Inferred:**
- The server allows 3 m beyond `_iBuildRadius`: the builder may walk while placing.
- No invalid areas: dungeon var 501 (`CInvalidBuildCannonAreasID`) is a server list the client does not hold.
- A builder who dies or leaves the siege before the build time ends gets no cannon; the machine goes back to the
  warehouse.
- `roleUsingToolId` is not saved: cannons only stand during their siege.

### Firing the cannons (32700, 32703, 32705)

Code: `TerritoryWarsService.CannonFire.cs`, `TerritoryWarsService.CannonShots.cs`.

- **Getting on.** The builder uses the cannon's interaction (NPCConfig `_iInteractID` 4900005). NPCInteractConfig
  `_iStatusID` 2 is GameplayStatusConfig type 2 (GVGCannon). Its NPC condition 4900001 is checked on the server:
  `CheckBuilder` 2201, `CheckCamp` 2200, and not already in a gameplay status (12001 inverted). The player must be within
  NPCConfig `_iInteractDistance`, and a cannon has one operator. The answer is 32702 `InteractChangeNtf` GVGCannon with
  `enterCannonInfo` {cannon uid, GVGCannonConfig id}. `LC_GamePlayStatusMgr.OperateCannon` then calls
  `LC_GVGFireCannon.StartFire`, which enters the camera `_iChange3CConfigID` on the client.
- **Bullets.** The cannon carries custom data 4900014 `GVGCannonBulletRestNum`, the fire button's cost (SkillConfig
  `_kCost` [6, 4900014, 1]). It starts at `_iBulletNumLimit`, each volley costs one, and 31018 sends the new count.
- **Firing.** The fire button (GameplayStatusButtonConfig `_iButtonFuncType` 2, its `_iSkill` the cannon's
  `_iSkillID`) sends 32703 with `cannonUseSkill`: the skill and one `bulletExtraParam` per bullet of the volley, at most
  `_iSingleShotNum`. Each param holds gravity in cm/s^2 (`LC_GVGFireCannon` multiplies `_fGravity` by 100), speed in cm/s
  and the aim direction.
- **The bullet.** Each bullet is a unit of SkillConfig `_kLogicData[2]`, an NPCConfig `_iNPCType` 10 row (4900101 and
  others). Its entity carries a `MoveTypeMove` with `startVelocity` and `accVelocity` (0, -g, 0).
  `LC_Movement_MoveSyncMove` draws that as pos + v t + a t^2 / 2. The shooter gets custom data 4900006
  `CameraFollowBullet_ListLong` with the first bullet's uid, which `LC_GVGFireCannon.HandleBulletFollowData` follows.
- **Cooldown.** 32705 `NpcSkillsCdNotify` gives the cannon skill's cooldown on the cannon NPC.
- **Getting off.** The quit button (ActionConfig 5004 QuitStatus) or the exit interaction `_iExitInteractID` (4900002,
  ActionConfig 6 LeaveFiredCannon) sends the player back to Idle. Death, leaving the scene and the cannon's removal do
  the same, with `Interrupt`.
- **Refusals.** 806 for a wrong interaction, button, skill or volley size, or another player on the cannon. 804 when too
  far, 801 for a cannon of another room. 3012 during the cooldown and 3013 without bullets.

**Inferred:**
- Where a bullet lands: the server integrates the flight against the nav mesh (else the cannon's height) and starts it
  at `_kFireOffset` (side, up, forward along the aim). It gives up after 12 s.
- The explosion: GVG_S0_CannonFire is a server script. The cannon skill hits through the ordinary extra-cast paths, on
  the monsters and the hostile players of the siege room within `_kLogicData[4]` cm of the landing point.
- The cooldown after a volley is the longer of SkillConfig `_iCD` and `_iReloadTime`.
- The camera follow values: a distance of 1000 cm, no screen offset.
- An empty cannon stays standing.
- PublicParamConfig `ArtilleryAttackCityGateReduceDamage` and `OutCannonHpPercent` are server parameters the client
  never reads; see "Machine HP, destruction and repair" for why neither is applied.

### War vehicles (32281, 32700, 32708, 31074)

Code: `GuildService.WarVehicles.cs`, `TerritoryWarsService.WarVehicles.cs`.

- **Using one.** A vehicle item of the siege battle bag (ItemConfig `_iSubType` 1402) sends 32281 from its "use" button
  (`UI_Com_ItemTipsWidgetCtrl_Operation._gameplayUseVehicle` -> `LC_WartimeItemHelper.TryUseVehicle`). Its `_iUseEffect`
  [606, skill] names a skill whose script `GVG_S0_BuildMecha` raises the NPC of its `_kLogicData[0]`: Tank 1006008 ->
  4903501 -> NPC 4900301, Madogear 1006005..007 -> 4903201..401 -> 4900401..403, Jet Backpack 1006009 -> 4903601 ->
  4900501. One leaves the warehouse (32273), and the member's slot and own permission name the vehicle
  (`roleUsingToolId` in 32285 and 32286). The answer only clears the bag's busy state (`ReceiveGuildVehicleUseRsp`).
  Refusals: 6001 outside a guild, 28 outside a running siege or type-18 territory battle, 82 for an item that is not a vehicle, 466 without the
  Vehicle permission, 535 without stock.
- **The vehicle.** An NPC of `_iNPCSubType` 103 (`enum_NpcSubType.Vehicle`): `LC_Unit_NPC` gives it `LC_VehicleComponent`
  with VehicleConfig `_iSubTypeValue` -> `_kSeats` (VehicleSeatConfig rows, 0-based seat index,
  `LC_VehicleComponent.GetSeatConfig`). Its entity names its owner and camp and carries `_kBornEventBuff`.
- **Seats.** The NPC's interactions 4900022..24 (NPCInteractConfig `_iStatusID` 3, GameplayStatusConfig type 3, seat
  `_iStatusLocation`) take a seat. Their conditions are checked on the server: `CheckTakeOnVehicleSeat` 12003 (riding
  nothing yet), `CheckGVGPermission` 2202 (the Vehicle permission), `VehicleHasEmptySeat` 12002 (a free seat of that
  `enum_PassengerType` on a vehicle that is not an enemy) and `CheckCamp` 2200 (the same camp; the client ignores its
  argument). Who sits where is AOI carry info (31036, `LC_VehicleHelper.ProcessVehicle`): the rider's `be_carried`
  {vehicle, slot, `carry_type` = the seat's `_iType`} and the vehicle's `add_carries` / `remove_carries` (`all_carries` in
  its entity). Then 32702 Vehicle (state 3, the interaction, the vehicle) and the seat's camera (32704 `_iChange3C`).
- **Driving.** The driver controls the vehicle (`LC_PassengerComponent`: `ChangeControlUnit`), so its 31082 walks are
  the vehicle's: the vehicle moves for everyone else, and the other riders follow it. 31074 turns the vehicle.
- **Seat skills.** The seat's `_kMainSkills` and `_kSkills` are cast through 31102 like any skill, without being learned.
- **Changing seats.** 32708 `ChangeVehicleSeatReq` with a free 0-based seat.
- **Getting off.** The seat's `_iExitNpcAction` (4900025, ActionConfig 5004 QuitStatus, sent by
  `LC_VehicleServer.QuitStatus` as an interaction on the vehicle) or a QuitStatus button of the seat. 32702 Idle goes
  first (`LC_GamePlayStatusMgr.ExitVehicle` caches it on the passenger), then `be_carried` with id 0. Death, leaving the
  scene and the vehicle's removal do the same.

**Inferred:**
- Using a vehicle needs the Vehicle permission, stock and a running siege, as for cannons. A second vehicle replaces
  the member's first one (UIWords 61001 says so of cannons).
- The vehicle stands 2 m in front of its owner: `GVG_S0_BuildMecha` is a server script. Nobody is seated by using it.
- The vehicle moves at its driver's speed. The other riders' positions follow the driver's walks without a walk of
  their own.
- A gunner's 31074 face is its seat's `fire_dir` in the vehicle's AOI detail.
- Any free seat can be changed to with 32708.
- An empty vehicle keeps standing.

### Machine HP, destruction and repair

Code: `TerritoryWarsService.MachineDamage.cs`, `TerritoryWarsService.WarRepair.cs`, `MonsterService.MachineTemplate`.

- **HP.** Cannons (`_iNPCSubType` 102) and vehicles (103) are NPCConfig `_iNPCType` 2 rows whose `_iAttributeVariated`
  and `_iLevel` select an AttriMonsterConfig row as a monster's do (222059: 69 170 HP, scaled by `_kAttriScale`).
  Their entity carries properties 101 (max HP), 2000 (HP) and 2002 (level), so the client shows an HP bar, and
  `AOIAttrChange` 2000 updates it.
- **Damage.** On the server a machine is a monster unit (`CannonUnit` and `VehicleUnit` derive from `MonsterUnit`): the
  attacks, skills and cannon shots of the other camp's siege members damage it through the monster pipeline, with its
  template's defences. Its own camp cannot hit it (`LC_CommonCondition.CheckCamp` compares `EntityCamp`: the machines of
  the own camp are allies). The NPC rows drop nothing (`_iDrop` 0, `_iNormalDrop` empty), so a kill grants no reward.
- **Destruction.** At 0 HP the machine dies as a monster does (HP 0, state Dead). Its operator or riders are put back on
  foot, its owner's slot shows nothing in use (`roleUsingToolId` 0 in 32285 and 32286) and the wreck leaves after
  `_iBodyRemainTime` (1.5 s for a cannon, 10 s for a vehicle).
- **Repair.** The repair item 1006010 "Madogear Pilot Repair" sits in the vehicle bag (`_iSubType` 1402), so its use
  button sends 32281 like a vehicle (`LC_WartimeItemHelper.TryUseVehicle`). Its `_iUseEffect` [606, 4901901] names skill
  4901901 `GVG_S0_RepairArea`; the item text reads "Use to create a Madogear Pilot repair zone for @{1} seconds.
  Madogear Pilots within the zone can be repaired." with @{1} = 30. It needs the Vehicle permission, stock and a running
  siege, and leaves the warehouse (32273). BuffConfig 4903501 "Madogear Pilot Repair" ("The Madogear Pilot is being
  repaired.") carries `T_AXadd0_etc` [2, 100, 101, 2]: 100 / 10 000 of the recipient's attribute 101 (max HP).

**Inferred:**
- The repair zone is the NPC named "Madogear Repair Zone" (4901010, `_iNPCType` 4, a ground object) at the member's
  feet for the description's @{1} seconds; `GVG_S0_RepairArea` is a server script and names no NPC. Its radius is the
  skill's `_iDistanceMax` (5 m).
- Every war vehicle of the member's camp inside the zone carries buff 4903501 and heals 1% of its max HP each second
  (the cadence of the other `T_AXadd0_etc` heals and drains). Cannons are not repaired (the item and the buff name the
  Madogear); the tank and the Jet Backpack are, as the client's "Vehicles can be repaired here." (10370001103)
  suggests.
- A destroyed machine's wreck stays for its `_iBodyRemainTime`, and damage to machines counts in the siege's damage
  statistics like damage to the crystal.
- Damage dealt to the other side's machines also counts as the hitter's MachineryDamage (statistics 1021003), and the
  last hit on one as their DestroyMachinery (1021002), in a siege and in a territory battle. The client only displays
  the two counters (`UI_Sys_TerritoryWars_PushUp_Achievements_ListWidgetCtrl`).
- `OutCannonHpPercent` (80) sits among the GVE cannon parameters (`GVECannonExitInteractId`,
  `GVESwitchBulletInteractIds`), not the siege ones, so it is not applied to siege cannons.
- `ArtilleryAttackCityGateReduceDamage` [3002, 3003, -9000] lowers cannon damage against the gates, the barricades
  and the other tag 3003 NPCs (see "Damage by tag" under "Siege castle").
- Restocking the warehouse: 32275 has no client sender and no shop or award sells the machine items (the "War Machine
  Shop" text has no data behind it), so the stock is only `SiegeInitItemList` and what the machines leave or return.
- When the siege settles, its standing machines leave with whoever is on them and their slots show nothing in use, as
  in a territory battle (below).

### War machines in territory battles

Code: `TerritoryWarsService.WarRooms.cs` (`WarRoomOf`), the machine files above.

- **Which battles.** Of the 36 `GVGWarZoneConfig._iDungeonID` battles, four are DungeonConfig `_iGamePlayType` 18
  (`enum_DungeonGameplayType.TerritoryWars`): 3030401 (zone 2), 3030101 (3), 3030201 (6) and 3030301 (7); the others are
  type 6 (Single). Type 18 mounts the UI template `Battle_TerritoryWars` (`LC_MainUIDefine`), whose
  `UI_Sys_Dungeon_TerritoryWars_ButtonWidget` has the siege's three buttons: Manage (the Tactics panel), Command and
  Instrument (shown for the Firearm permission, it opens `UI_Sys_Siege_BattleBagPanel`). `LC_DAL_TerritoryWars` keeps
  the 32285 permission as the siege DAL does, and the personal record shows DestroyMachinery and MachineryDamage. So the
  reservations, builds, vehicles and repair zones (32270, 32271, 32272, 32281) work for a member of an active type-18
  battle as in a running siege.
- **Same warehouse and slots.** No config row gives territory battles their own stock or rules (no Territory machine
  list in PublicParamConfig, no map field in GVGCannonConfig, VehicleConfig or the items), and the client's 32282 always
  names the siege dungeon (`LC_SiegeHelper`), so one warehouse and one Tactics permission set serve both.
- **Sides by guild.** Every territory player is camp 1, and the client tells friend from enemy by guild
  (`LC_CampRelationStrategy_TerritoryWars`: the unit's head info Guild, else custom data 104 `BelongGuild`,
  `LC_CampRelationUtils`). A machine keeps camp 1 and carries its builder's guild as `guildId` (EntityBase 59) and custom
  data 104. `CheckCamp` (2200) on a cannon or a seat and `VehicleHasEmptySeat` (12002, "not an enemy") compare that
  guild; another guild's members damage and destroy the machine, the own guild cannot; a repair zone heals the vehicles
  of its guild; the cannon's explosion hits the players hostile to the shooter (other guilds of the battle).
- **The end.** When the battle settles, its standing cannons and vehicles leave with whoever is on them, and their
  owners' slots show nothing in use (32285, 32286). Machines die with the room anyway.
- **Statistics.** The territory report (30117, 30114 and the result's `sceneStatistics`) carries the personal record's
  `extraData`: 1001001-1001005 (damage, damage taken, healing, player kills, assists), 1021002 DestroyMachinery, 1021003
  MachineryDamage and 1031001 `TerritoryWarPersonIntegral`.

**Inferred:**
- Type-6 battles refuse machines with 28: they have no Instrument button or battle bag.
- The guild relation for a machine's seat and cannon conditions, and its `guildId`/`BelongGuild` (the client reads
  them for any unit; the original's machine entity is not captured). Siege machines carry them too.
- The machines leaving at the settlement (the siege as well): nothing says they outlive the battle, and the room closes
  shortly after.
- 1031001 is the participant's battle score; territory battles count no assists (always 0).

## Siege castle

Code: `TerritoryWarsService.SiegeObjects.cs`.

- **Placement.** `NpcLevelPointRespawnConfig` places the castle: `_iMapid` is the DungeonConfig id (2230101) and
  `_iRespawnpointid` a point of its scene editor (`sc_gvg_gongchengzhan_001_dg01`). The siege room spawns, as monsters of
  their NPCConfig rows: the City Gate 4900005 at 36 (the gateway, "城门刷新点") and 214 (the attackers' camp gate,
  "进攻方城门"), the Bow Guardians 4901012 at 38-40 and the Knight Guardians 4901011 at 41-43, the ten Obstacles 4900022
  (barricades: 321, 80, 81, 83, 90, 93, 97, 101, 103, 107), the revive point 4900010, the building markers 4903002 (254,
  255, 256, 273, 274, 299, 308), the revive markers 4901021 (attackers: 267, and 268 under the wall once the gate
  fell, inferred) / 4901022 (defenders: 270, 271), the ladders 4900012 (46) and 4900000 (47), the Battering Ram 4900002
  (48), the 38 archers 4901001 and the guide arrows 4903001 (below: "Ladders, the ram and the archers", "Guide arrows").
  The fixed vehicles 4901004-4901009 stand as vehicles ("Fixed vehicles"). The crystal 4900009 at 44 keeps its own
  spawn. A destroyed object stays destroyed for the rest of the siege, also in a room opened again for it (the ladders
  and the ram come back), and a damaged one keeps its HP (`SiegeRunState.Destroyed`, `ObjectHp`).
- **Not spawned:** the invisible markers 4900017 (camp 0, points 247, 265, 275, 277, 292, buff 901301): nothing in the
  client reads them.
- `DungeonLua_2230101.lua` `UIModule_NpcData` (not exported, kept as code): 36 CityGate, 38-40 OuterTower, 41-43
  InnerTower, 44 Crystal, 46/47 Ladder, 48 Car, 286 Revive (`enum_SiegeObjectType`, `Lua_GameDefine.lua:14875`).
- **30115.** The siege UI knows an object only through `GlobalObjectDataNotify` (`TerritoryWarsService.SiegeGlobalData.cs`):
  `LC_SiegeHelper.GetNpcUIData` takes HP, max HP, position and camp from the entry at the object's point and counts the
  object dead when there is none; an entry with hp 0 deletes it (`LC_SceneCom_GlobalObjMgr.lua:82-138`). Loading in
  sends every standing object (all spawned NPCs have `_iNeedNotifyGlobalData` 1, the crystal too); HP changes go out
  with the one-second siege clock (inferred cadence), a spawn and a death at once. `LC_DunGeon_Siege._updateDestoryPerform`
  shows the intact or the broken model of the `DestoryModelPerformParams` buildings by whether an object stands at the
  point: without 30115 every bastion, workshop and arrow tower was drawn broken; the markers 4903002 keep the workshops
  and towers standing, the Knight Guardians the bastions.

**Inferred:**
- Row 4900016 places the revive point at 45, which is a group id and no point of the scene; `UIModule_NpcData` puts
  4900010 at 286, which is used.
- The attackers' marker of the contested revive point (269, "争夺复活点-进攻方") stands on the same spot as the
  defenders' 270: the point starts with the defenders and is not spawned for the attackers (its capture is unknown).
- Who hits what: a camp 2 object (gates, guardians, barricades) by the attackers only, a camp 1 object by the
  defenders only, the camp 4 revive point and anything with `_iNoSelected` 1 (the markers) by nobody.
- A castle object keeps its damage for the whole siege: a field monster that loses its target and stands at home again
  heals to full, a castle object does not (`MonsterUnit.KeepsDamage`). One whose AIConfig has `_iCanInBattle` 0 (the
  City Gate's 4900000 "GVG_S0_Gate"; the barricades, markers and revive point have no AI) never takes a target, so a
  hit neither locks its attacker (31052) nor starts the leash (`MonsterUnit.StaysOutOfBattle`); the guardians
  (20000009) and the crystal (4900009) have `_iCanInBattle` 1 and fight back. The client never reads `_iCanInBattle`.
  Before this a cannon shot from 70 m engaged the gate, which dropped its attacker at once (leash 15 m) and healed.

### Siege stages

Code: `TerritoryWarsService.SiegeStages.cs`.

- `DungeonLua_2230101.lua` `DungeonBase.DungeonStageModule`: "0" 准备阶段 Prepare (maxTime 300 000 ms), "1" 城门阶段 Gate
  (1 500 000), "2" 内城阶段 inner city (3 000 000), "3" 华丽阶段 the crystal (3 000 000); the top bar calls them Prepare,
  Gate, Stronghold, Crystal. 30401 carries the stage in `stage` and `stageModuleData` (start, end); a change goes out as
  `eChangeType_Change` (the client handles both types alike). The client pushes the stage's chat itself when the stage
  changes (`UIModule_StageSwitchChatIdMap`: 4110141-4110146, "Phase I Destroy the Outer Gate" / "Defend the Gate" and on).
- The top bar's countdown is var 512 `CSiegeTopBarTimestamp` (epoch ms) in Prepare and var 542 `CSiegeStageCountdown`
  [end ms, ms added] after it (`UI_Sys_Siege_TopBarWidgetCtrl._refreshStage`: when the end moves and the second value is
  above 0 it shows the old end, then plays "+N S"). A guardian's fall adds time (see "Kill time").
- Var 511 `CSiegeTopBarState` (a number) goes out with the stage. Its only reader, `UI_Sys_Dungeon_Main_GoWidgetCtrl`,
  shows a defender a "go" button while it is 0; the button walks them (`CrossMapAutoFindPath`) in turn to the points of
  `UIModule_DefFrontPointIdList` "0" (304-307, on the city wall at the ladder lanes). Vars 514
  `CSiegeTopBarNpcUuidList` and 515 `CSiegeTopBarCountdownUIWord` have no reader in the client and are not sent.
- Dungeon task 4900000 "Reach Dungeon Stage 1" is met when the Gate stage starts.

**Inferred:**
- Prepare runs from the siege's start for its 300 s; the Gate stage follows; the Stronghold stage starts when the gate at
  36 falls and the Crystal stage when the three Knight Guardians (41-43, InnerTower) fall: the stage names and each
  stage's top bar objects ("Destroy the Outer Gate", "Defeat the Protector", "Destroy the Ornate Metal").
- A stage's countdown ends at its maxTime or at the siege's end, whichever is first. Only the siege's end (60 min,
  `AttackCityEndTime`) settles the battle for the defenders: whether a Gate stage that runs out (30 min in) already
  ends the siege is not known (`DungeonResultModule` has a type 4 time-out result without a stage).
- The crystal can be hit in the Crystal stage only.
- The state of var 511 is the stage: the go button's point list is keyed "0" like the other stage-keyed lists of the
  dungeon script, and sending the defenders to the wall's ladder lanes is what Prepare is for.

### Gates

- City Gate 4900005 (NPCConfig): `_iNPCType` 1 sub 204, camp 2, idle AI `GVG_S0_Gate`, speed 0, level 59,
  AttriMonsterConfig 220059 (771 920 HP) scaled by `_kAttriScale` [101, 5 750 000] = 443 854 000 HP, `_kNpcTag` [3003],
  `_iBodyRemainTime` 1. Its born buffs include 4904401 "Battle Start Protection" ("Immune to damage and debuffs",
  `fx_gvg_buff_hudun`); the buff has no attribute, so its immunity is a server rule: nothing hits a unit carrying it.
- The top bar's gate is `UIModule_NpcData` "36" [4900005, CityGate, area 319]: in the Gate stage it shows the HP of the
  30115 entry at point 36 (`UI_Sys_Siege_TopBarWidgetCtrl.lua:449`). Var 513 `CSiegeGateNpcUid` carries the gate's uid
  (`LC_DunGeon_VarDefine`; `DungeonVariableConfig` swaps the names of 513 and 514, the decoder is VarDefine).
- When the gate at 36 falls, its 30115 entry goes out with hp 0 and the inner city's stage begins.

**Inferred:**
- Battle Start Protection stays until Prepare ends, then the gate at 36 loses it and the attackers can break it (the
  defenders cannot: it is their camp's). Who breaks it in the original: the Battering Ram (its end point 35 is right
  in front of the gate) and attacking players.
- The attackers' camp gate at 214 leaves the field when Prepare ends, with the camp's air walls; it is not spawned
  again in a later room.
- AttackCityValue (statistics 1021001): an attacker's damage to the castle's objects, the crystal included, not to the
  war machines (those count as MachineryDamage). DefenseValue (1021004), its mirror: a defender's damage to the
  attackers' ram and ladders (the camp 1 castle units). The client only displays the two values.

### Air walls

Code: `TerritoryWarsService.SiegeWalls.cs`, `Data/Config/SceneEditorAirWalls.cs` (`MapWorld.AirWalls`).

- The castle's navigation grid is open through the gateway (cells x 585-615, z 141-158), the barricades and the
  bastions. They block only through the scene editor's air walls (`m_kAirWalls`: id, `gridLevel`, `GroupId`, `name`,
  the 1 m cells of `wallPoints`), which a dungeon switches on with var 133 `CAirWallList`: a list of wall ids with
  `DungeonVar.groupType`/`groupId` saying who may pass (All 0, Camp 1, Guild 2, Player 3); a wall of the group missing
  from a new list is removed (`LC_SceneCom_AirWall.lua:322-480`, which lists 2210101 and 2230101 specially).
- The siege's 30401 carries var 133 with the standing walls for everyone (groupType 0) on load and on every change.
  The server treats their cells as blocked: a skill whose line enters one is refused (`SkillBlocked`), and a walk into
  one is reported or refused by `ServerOptions.WalkCheck` (`SceneService.WalkBlocked`).
- Walls of the scene: 72 "空气墙_城门阻挡(内)" and 74 "(外)" at the gateway (x 597-600, z 136-165); 71 "进攻方阻挡" and
  264/265 "进攻方营地墙" around the attackers' camp; the ten barricade walls of group 77 "拒马&相应空气墙" (322, 117,
  231-235, 243, 245, 247), each paired by name and position with an Obstacle's spawn point; 65 "空气墙_左中" and 19
  "空气墙_右中" on the city wall at the two ladder ends; the fortress walls 221, 224, 225, 236-241 behind the Knight
  Guardians; 29/31/61/66 along the city wall; 67/68 "空气墙_左车" and 32/33 "空气墙_右车", the rails of the two ladder
  lanes (x 597-626, either side of z 91-94 and z 205-208).

**Inferred** (no client script switches the walls; the original's are server side):
- Standing walls: the camp's (71, 264, 265) in Prepare; the gate's (72, 74) until the gate at 36 falls; 65 and 19 until
  a ladder stands against the wall there, then the rails of its lane (67/68, 32/33) instead, and back when it falls; a
  barricade's wall until it falls; fortress 4/5/6's walls (221/240/241, 224/238/239, 225/236/237) until the Knight
  Guardian of the same number (41/42/43) falls, as `DestoryModelPerformParams` breaks the bastion with it. The walls
  29/31/61/66 (mostly on unwalkable cells) stay off.
- A wall whose footprint holds the walker's or caster's own cell never blocks, nor, for a skill, one holding the
  target's: the gate and the barricades stand inside their own walls.
- The navigation grid still stops skills at the castle's solid parts, with the crystal's own blocked cell allowed as
  before.

### Damage by tag

Code: `TerritoryWarsService.SiegeTags.cs` (a `CombatService.DamageAdjusters` entry).

- `PublicParamConfig` has two rows the client never reads: `ArtilleryAttackCityGateReduceDamage` [3002, 3003, -9000]
  and `GuardAttackOtherReduceDamage` [3004, 3005, -5000]. NPCConfig `_kNpcTag` 3002 is every siege cannon (4900013-4900016,
  4900023-4900027) and bullet (4900101-4900108, `_iNPCType` 10); 3003 the City Gate 4900005, the ram 4900002, the ladders
  4900000/4900012, the Obstacle 4900022 and the Barrier Stele 4905012; 3004 the archers 4901001; 3005 the ladders.

**Inferred:**
- Both rows read [attacker tag, target tag, change / 10 000]: a cannon shot on a tag 3003 NPC deals a tenth, and an
  archer's shot on a ladder half (the archers shoot the ladders, "Ladders, the ram and the archers").
- A player's cast carries the tags of the cannons whose `GVGCannonConfig._iSkillID` it is and of its bullet NPC
  (`SkillConfig._kLogicData[2]`); every other skill, the war vehicles' seat skills included, carries none.

### Ladders, the ram and the archers

Code: `TerritoryWarsService.SiegeUnits.cs`, `MonsterService.March.cs` (the route), `CombatService.UnitStrikes.cs` (a
monster's blow on a monster).

- **The units.** NpcLevelPointRespawnConfig places the Siege Ladders 4900012 (idle AI `GVG_S0_LeftLadder`) at 46 "云梯1"
  and 4900000 (`GVG_S0_Ladder`) at 20 and 47 "云梯2", the Battering Ram 4900002 (`GVG_S0_SiegeTrain`) at 34 "冲车出生点"
  and 48 "冲车", and the archers 4901001 (`_iAISpecial` `GVG_S0_Archer`, AIConfig 4900003) at the 38 points of group 248
  "刷守卫" (76-124: ten on the outer city wall at the ladder lanes, the rest on the inner walls). The ladders and the ram
  are camp 1 (the attackers'), `_iNPCType` 1, level 59, AIConfig 4900000 / 4900002 (`_iCanInBattle` 0), speed 300 / 100
  cm/s, `_iNeedNotifyGlobalData` 1, tags [3005, 3003] / [3003]; no skill, no interaction. The archers are camp 2, speed
  0, AIConfig 4900003 (view and attack distance 45 m, `_iCanInBattle` 1), normal attack 4905101 (`GVG_S0_GuardianAttack`,
  50 m, 5 s cooldown), tag 3004, `_iNeedNotifyGlobalData` 0 (no 30115 entry).
- **The UI.** `UIModule_NpcData` follows 46/47 (Ladder) and 48 (Car); `UIModule_DispAtkNpcPointIdList` /
  `DispDefNpcPointIdList` list them for each camp. `UIModule_NpcRefreshDuration` gives 46/47 [70000, 75000] and 48
  [20000, 60000] ms (every other point [0]). A dead object whose point has a 30401 `npcRefreshModuleData` entry
  (`npcRefreshTime`: point, `refreshStartTime`, `refreshEndTime` in epoch ms; `LC_SceneCom_UIModule_Siege` replaces its
  map with every such message) is shown counting down (`LC_SiegeHelper`, `enum_SiegeObjectUIType.Common_Countdown`)
  instead of destroyed. The server sends the module in every siege 30401.
- **The ladders' ends.** 18 "云梯2-终点" (613, 93) and 30 "云梯1-终点" (613, 207) at the city wall; the editor's names cross
  1 and 2, the positions pair 46 with 18 and 47 with 30. The wall there is 65 / 19, each between the rails of its lane
  (67/68, 32/33). The navigation grid has the wall's top at 29 m (x 597 and in) and the ground at 14 m (x 603 and out)
  with no walkable cells between.
- **The ram's end.** 35 "冲车-终点" (610, 150), 10.4 m before the gate at 36. The ram carries born buff 4900301
  (`fx_gvg_rangecircle_green`, zoom 480 000), the circle the guardians carry at zoom 225 000 for the 15 m of their scan
  skill 4904001. Its `_kAttriScale` raises its PATK and MATK ninetyfold (210 240 at level 59) though it has no skill.
- **Archers and ladders.** `GuardAttackOtherReduceDamage` [3004, 3005, -5000] halves an archer's damage on a ladder.

**Inferred** (the AI scripts run on the original server; the client holds none of them):
- Only one unit per spot: the rows at 20 and 34 stand on 47's and 48's spots; the UI follows 47 and 48.
- Everything waits out Prepare at its point. From the Gate stage on, the ladders walk to their ends along the
  navigation grid (`NavGrid.FindPath`, the Battle for Prontera's march). A ladder at its end stands against the wall: the
  wall's air wall (65 / 19) goes and its lane's rails (67/68, 32/33) come (var 133), and a siege member's walk that
  starts or ends in the lane's box is not held to the grid (`SceneService.WalkBridged`; the ladder carries the climb).
- The ram moves while an attacker stands within its circle (32 m: 15 m x 480 000 / 225 000) and waits otherwise, as a
  pushed ram. At its end it strikes the gate once per AIConfig 4900002 `_iAttackTickTime` (1 s) for its ATK (a monster's
  blow without a skill row), while the gate stands without Battle Start Protection. Its reach is the two models'
  `_iPrefabRadius` (1000 + 500 cm). Nobody is credited with its damage (no AttackCityValue).
- The defenders break the ladders and the ram (camp 1 castle units, as the attackers break the camp 2 ones). A dead
  ladder comes back at its point after a time drawn between the two of `UIModule_NpcRefreshDuration`, and so does the ram
  while the gate stands (afterwards it has nothing to strike). A ladder that falls takes its lane with it.
- The archers shoot the attackers they see (aggressive, their view and attack distance being 45 m, and they cannot
  move), never the defenders (`MonsterService.Spares`: a castle unit takes only the other camp's siege members), and let
  go of a target out of their reach. An archer that holds no player shoots a ladder within its reach. A dead archer does
  not come back (`UIModule_NpcRefreshDuration` names none of their points).

### Kill time

- `UIModule_KillAddTimeList` "0": [120, 300]. The top bar's info tips (`_onInfoClick`, outside Prepare) show its two
  values as UIWords 61140 "Defeat the Archer Guardian +${1} sec." and 61141 "Defeat the Knight Guardian +${1} sec.":
  a Bow Guardian 4901012 (38-40, OuterTower) adds 120 s, a Knight Guardian 4901011 (41-43, InnerTower) 300 s.

**Inferred:** the seconds go to the countdown running after the kill (var 542 and `stageModuleData.stageEndTime`; the
last Knight Guardian's to the Crystal stage it opens), 542's second value being the addition in ms for "+N S". The
countdown still ends at the siege's end at the latest; an addition that moves nothing is not shown. A new stage starts
its own countdown without them.

### Guide arrows

- NpcLevelPointRespawnConfig places 4903001 (UnitConfig "引导箭头NPC", the guide arrow NPC; camp 4, `_iNoSelected` 1,
  `_iNeedNotifyGlobalData` 1) at 215-220, 223, 226, 227, 229, 230, 16 and 272, all `_iNotGroundBorn` 1: the unit keeps
  the scene editor's height (`InstanceService.SpawnAsync` `grounded: false`). No point is created by the client
  (`isCreateOnSceneInit` false), so the server spawns them. They lead from the defenders' born point along the road to
  the gate and stand in the attackers' camp (215). Nobody can select or hit them.

**Inferred:** 16 and 272 are no spawn points (their `m_kAllPoints` entries sit at the origin), so those two rows are left
out; the arrows stand the whole siege.

### Fixed vehicles

Code: `TerritoryWarsService.SiegeVehicles.cs`.

- NpcLevelPointRespawnConfig places the Steel, Mithril and Adamantine Madogears 4901004-4901006 (camp 1) at 301, 251, 250
  and 4901007-4901009 (camp 2) at 253, 302, 303: `_iNPCType` 2 sub 103 (Vehicle), VehicleConfig 2, interactions 4900022
  (seat) and 4900025 (exit), the warehouse Madogears' units 4900401-4900403, `_iNeedNotifyGlobalData` 1. They stand from
  the start as vehicles (`TerritoryWarsService.WarVehicles.cs`), owned by nobody, of their camp and its guild; their
  camp's members with the Vehicle permission board them as a vehicle from the war bag (same conditions); the other camp
  destroys them.

**Inferred:** a destroyed one stays destroyed for the siege (no refresh duration names their points) and a damaged one
keeps its HP; they leave at the settlement with the other machines.

The territory battle 3030101's vehicles 4900402 at its SummonPlatform points 49/57/83 are raised by activating the
platform: "Summon platforms" (below, under "Stronghold gameplay points").

### Test aids (not original)

- `!siege prepare`: the running sieges' Prepare ends now; the Gate stage starts at once (the ladders and the ram set off).
- `!siege objects`: the castle units one can hit in the open siege rooms (`point=npc#uid hp/max`, `marching`, `held`,
  `arrived`), the living archers as a count and the ladders and ram waiting to come back. The uids serve the GM panel's
  entity ops (`GmChangeHp`, `GmKillEntity`).

### Bag NPCs

Code: `TerritoryWarsService.SiegeBags.cs`.

- NPCConfig 4900201 (camp 1) and 4900202 (camp 2): `_iNPCType` 2 sub 1, `_iInteractID` [4900004], `_iInteractDistance`
  3. NPCInteractConfig 4900004 opens the war bag (`_iEXECAction` [1, TOPBAR, `UI_Sys_Siege_BattleBagPanel`], which the
  client runs on the server's 32700 answer) when its condition 4900004 (no buff of group 49012 or 49013) and its NPC
  condition 4900003 (`CheckCamp` 2200 and `CheckGVGPermission` 2202 with 2 = Vehicle) hold; otherwise 32700 answers
  `NpcParameter`. A Vehicle-permission member has no other way to the bag in the siege: the siege button widget shows
  the bag for the Firearm permission only (`UI_Sys_Dungeon_Siege_ButtonWidgetCtrl.lua` `_checkShowInstrument`).
- Note: NpcLevelPointRespawnConfig rows 4900201/4900202 are row ids placing Obstacles, not these NPCs.

- Their uids stay below 2^53, like every interactable unit's: `LC_InteractManager.AddRole` hands the uuid to the native
  overlap test through a double array and `_calAllUnitInDistance` looks the returned id up in `m_kUnitDic`; a uid a
  double cannot hold comes back changed, the lookup is nil and the client throws every frame (seen in the client with
  a first uid of 2^53 + 2^52 + 1).

**Inferred:** no table places the two NPCs; each stands 3 m in front of its camp's born point (75 the attackers', 73
the defenders'), facing it, once per siege room. They are not combat units, but their entity carries the template HP of
their NPCConfig stats (properties 101, 2000, 2002) for the HP bar the client draws.

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
- Test aid, not original: `!twpoint <point> unready|ready|complete [readySeconds] [owner|self]` changes a point of the caller's battle so the client display can be checked. The SummonPlatforms change their own points ("Summon platforms"); no rule is known for the others.

What the client and config show about each object, and what is unknown (a decision or a capture is needed for each):

| Type | Evidence | Unknown |
|---|---|---|
| SteleBarrier 34/59/74 | NpcLevelPointRespawnConfig 4905003-4905005 place 4905012 "Barrier Stele" (destructible: HP bar, `_kAttriScale` 101 x1.7) at these points. Scene groups 38/39/40 "结界-交互组" hold the point plus three sub points (35-37, 60-62, 75-77). Sub NPCs 4905011/4905014 (MutiInteractSubNpc, NPCInteractConfig 4905011/4905012, conditions 7000031/7000032, NPC conditions 490021/490022 on `CheckSameAtkGuild` 9012, the rings of "Summon platforms") exist without a placement row. ChatConfig 4111007 "Stronghold barrier activated. The stronghold will be locked in 5 minutes", 4111008 barrier destroyed, 4111025 stone monument activated, 4111026 defensive stone monument destroyed. The map hides the icon until the point is owned and shows `Preparing` neutral (`MapWidgetCtrl:150-230`). | Who may activate it (the platforms' ring rules are inferred; the stele has three ring points), the 5-minute lock's effect on the flag, stele HP regeneration/respawn, the 30129 state after destruction. |
| DivinityStatue 41 | Groups 45 "神像-攻击-交互组1" (points 41-44) and 47 "神像-防御-交互组1" (78-80, not in UIModule_NpcData). ChatConfig 4111012 "Statue Ready!", 4111017 statue activated. No NPC or spawn row. | Its effect, cooldown (`readyTime`), who may use it. |
| SummonPlatform 49/57/83 | Built: "Summon platforms" below (rings 4905011/4905014, status 22, vehicle 4900402, ChatConfig 4111013-4111020). | The recharge time, the progress numbers and who may use the defenders' platform are inferred there. |
| BossPlatform 51 | NpcLevelPointRespawnConfig 4905011 places 4905053 "Vanguard" (Pioneer subtype 213, AI 20000011, skills 4905002-4905004). Group 54 "先锋-交互组1". ChatConfig 4111016 altar ready, 4111021 "a Behemoth is advancing toward the stronghold", 4111027 behemoth fallen. | Activation condition, route, timer. |
| AirWallGate 63/66 | Client air walls 63/66 in groups 65/68 "门-1/门-2" with spawn points 64/67, where NpcLevelPointRespawnConfig 4905001/4905002 place Knight Guardian 4905051 (camp 2). ChatConfig 4111022/4111023 east/west gate breached. | Whether the guardian's death opens the gate; the server loads no air walls (`m_kAirWalls`) at all. |
| RevivalPoint 55/56/81/85/86 | `m_kResurrectionPointList`; NpcLevelPointRespawnConfig 4905006/4905007 place Bow Guardian 4905052 at 55/56. ChatConfig 4111024 "A Revive Point has changed hands!". The generic per-player revive list is CustomData 11 `Dungeon_Player_RevivePoint_ListLong` (`LC_CustomDataHandler_Revive.lua`, `LC_DunGeon_Helper.SetRevivePointList`). | The capture rule and initial owners. Today every territory participant revives at `MapWorld.Revive`, the first resurrection point (55), regardless of owner; restricting revival to owned points (and the default point otherwise) needs that rule. |

The guardians, stele and vanguard of NpcLevelPointRespawnConfig are not spawned (the vehicles are, by their platforms): their placement is configured, but when they appear and what their deaths change is not, and spawning hostile camp-2 guardians without those rules would add fights with no consequence. The command skills of `DungeonLua_3030101` `CommandModule` and its two-stage `DungeonStageModule` (45 min / battle) are outside this change.

### Summon platforms

Code: `TerritoryWarsService.SummonPlatforms.cs`.

Evidence:

- The three SummonPlatform points (49 south, 57 east, 83 base) each have a scene group of two points at one position:
  48 [49, 50] and 53 [57, 58] "载具-交互组" (vehicle interaction group), 82 [83, 84] "载具-防守方" (vehicle, defending
  side). The stele's groups hold the point and three ring points (35-37) the same way.
- The MutiInteractSubNpc rows 4905011 and 4905014 (`_iNPCSubType` 251, UnitConfig 35004 "跳舞位置圈", a ring to stand in,
  `_iInteractDistance` 3) carry NPCInteractConfig 4905011 / 4905012: button 13060000006 "Activate", `_iStatusID` 22.
  GameplayStatusConfig 22 is "领土战-多人交互-仅单阵营可交互" (territory war multi-person interaction, one side only),
  `_iStatusType` 18 = enum_InteractState TerritoryWarsInteract = `InteractInfo.EState` MultiPersonProgress
  (`LC_GamePlayStatusMgr.TerritoryWarsInteract`, `LC_State_Unit_Interact._startTerritoryWarsInteract`: the player is
  linked to `activeObjId`). Row 23 is the same for all sides.
- Conditions: the interaction's 7000031 / 7000032 are "in no gameplay status" (`CheckInteractState` 12001 with 0,
  inverted). The ring's 490021 is `CheckSameAtkGuild` 9012 with 0 OR its inverse (always true: any guild may start);
  490022 is 9012 with 1: the ring's custom data 104 BelongGuild must be the player's guild, else UIWords 61612 "Another
  faction is currently using this." (`LC_InteractCondition.CheckSameAtkGuild`). `m_eType` 1/2/3 are AND/OR/DATA
  (`LC_ConditionDefine.enum_NodeType`).
- Custom data 10000 InteractionProgress ("the multi-person interaction's total progress"), 10001
  InteractionCurrentProgress ("current progress") and 10002 InteractionRate ("one interactor's speed, points per
  second"; DirtyDataConfig comments) give the unit `LC_PerformGfx_StrongholdProgress`, which opens
  `UI_Sys_TerritoryWars_NewProgressWidget` over it while 10001 is above 0, coloured by the unit's guild.
- NpcLevelPointRespawnConfig 4905008-4905010 put the Mithril Madogear 4900402 at 49, 57 and 83: the warehouse Madogear
  (VehicleConfig 2, seat 4900022 with condition 4900005 = not seated + the Vehicle permission, NPC condition 7100011 =
  a free seat on a vehicle that is not an enemy + `CheckCamp`; exit 4900025). Its NPCConfig camp 4 is the warehouse
  Madogears' too (4900401-4900403): the server gives a vehicle its side.
- ChatConfig 4111013/4111014/4111015 "Southern / Eastern / Base vehicle point ready!" and 4111018/4111019/4111020 "The
  southern / eastern / Base vehicle has been activated!!!". The map shows a point `ready` as Available, `unReady` with
  `readyTime` as Preparing with a countdown and `complete` as Unavailable, in its owner's colour (`LC_DAL_TerritoryWars`
  3225-3260, `UI_Sys_TerritoryWars_MapWidgetCtrl`).

Implemented:

- A ready platform has its ring at the group's other point (50, 58, 84): 4905011 on 49 and 57, 4905014 on 83.
- "Activate" (32700) by a battle member within 3 m (+1), on a ready platform, in no other state (riding, a cannon, a
  ring), under both conditions, answers 0 and puts the player into MultiPersonProgress (32702: status 22, the
  interaction, the ring as `activeObjId`; also shown to new viewers). The ring then carries the guild in 104 and its
  entity `guildId`; another guild is refused (806) while it does. A walk that moves the player, death, leaving the
  scene or the battle, or standing more than 4 m from the ring ends it (Idle, `interrupt`).
- Each second of the battle clock the ring fills by min(activators x `GVGFieldOccupySpeed` 1, `GVGFieldOccupyMaxSpeed`
  3) up to `GVGFieldOccupyAmount` 30 (31018 with 104 and 10000-10002). Full: the activators go Idle, the ring leaves,
  4900402 stands at the platform point for the guild (camp 1, owner 0, `guildId` and 104 BelongGuild: a war vehicle as
  in "War machines in territory battles"), 30129 shows the point `complete` with the guild as owner and the battle gets
  4111018-4111020.
- The guild's members with the Vehicle permission board it (the seat's conditions; the client's `CheckCamp` compares
  EntityCamp 1 = 1, its free-seat check the guild); other guilds cannot, and damage and destroy it.
- Once the vehicle is gone (destroyed, or its room gone) the point is `unReady` with `readyTime` now + 120 s, owner 0;
  at `readyTime` it is `ready` again, 4111013-4111015 go out and a new ring stands.
- The settlement takes the rings and their activators away; the vehicles leave with the other machines.

**Inferred:**

- The rings' placement (no row places 4905011/4905014) and which ring stands where: the open one on the two attack
  platforms, the guild one on the defenders' platform 83, whose BelongGuild is the guild holding the stronghold's flag
  (the battle's point 0), so only that guild may activate it (nobody while no one holds it); a change of holder ends
  that ring's activation.
- One guild at a time on an open ring, refused with 806 (status 22's name; its condition 490021 lets anyone start).
- The numbers: the GVG field occupation's amount 30, speed 1 per activator and cap 3 (the territory flag fields use
  them already); a ring nobody stands in starts over; the 120 s recharge after the vehicle is gone; the vehicle stands
  at the platform point, not the ring point (both are one position).
- South / east / base: 49 lies south (z 227), 57 east (x 726), 83 is the defenders' group by the stronghold.
- Walking ends an activation as it ends a gathering: status 22's exit events (2008, 2009, 2026, 2028) are server
  events the client does not name.
- The vehicle's 30115 entry (NPCConfig `_iNeedNotifyGlobalData` 1) is not sent: the territory map reads the platforms
  from 30129.
