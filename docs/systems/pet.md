# Pet implementation

Evidence: the referenced client Lua, protobufs, and exported JSON tables. World following of a deployed pet is investigated separately in [pet-world-follow.md](pet-world-follow.md), including the unavailable pendant configuration and the limits of inferring original-server behavior from Lua. The 111/114 pendant rows and the room-timer wire field are absent from the supplied client build.

Implemented:

- 5017 `DBPetInfo`: durable empty-first roster, selected lineup, room, eggs, history, count and UI preferences. Login defers 5056 until 5027 and 5017 exist, including function sync triggered by automatic quest settlement.
- 33308 fragment combine; 33302 one/ten levels; 33303 progress; 33304 stars; 33311 lineup and swaps; 33314 existing-plan selection; 33315 rename; 33324 collection activation and its configured award; 33312 fragment breakdown.
- 33318 normal egg generation using material 1250709; 33322 completion with acceleration items/token payment; 33319 ready-egg collection; 33317/33320 preferences. Start/acceleration values use Unix seconds, survive reconnect, and use the current room's configured hatch duration. Collection is explicit, not automatically granted at expiry.
- The room levels itself after a 33319/33325 collect fills its proficiency bar: the client has no caller of `LC_DAL_Pet.SendIncubationLvUp` (the level-up panel and its "Go to Upgrade now?" prompt send nothing), so a full bar would otherwise stay full. Each affordable step pays the current row's `_kCost` *(inferred)*. An explicit 33316 is still accepted. 33325 fast hatch is gated on room level 2.
- 33310 rebirth records and refunds the exact book denominations spent on new level-ups; pets levelled before this ledger existed receive an inferred equivalent in whole 500-EXP books.
- Pet skills resolve from the current `PetStarConfig` row; the synchronized roster gives the client its configured protect/passive/active/stage skill choices. No synthetic learned-skill dataset is introduced.
- [Ordinary guardian skills](pet-guardian-skills.md): slot-11 Primary casts, utility Reactive effects and the first utility King Core across the known exported families. Stateful combat and client-derived values retain explicit inference boundaries.
- One type-7 follower with an independent actor UUID, NPC template, positive owner, NPC movement property and owner-bound AOI. Creation follows the owner; removal precedes the owner; accepted walks/stops/teleports send 31081. Concurrent owner entry and lineup changes serialize the ordering.
- Functions 33/3301/3302 gate requests. A persisted setup cannot spawn when 33 is closed. Inventory/currency and pet state commit in one SQLite transaction, with in-memory rollback on failure. The existing ordinary item-exchange path retains its behavior.

Inferred policies (not original-server parity claims):

1. Initial plan indices 0-4 with empty names, `currentPlan=0` (the client's Build 1), `inited=true`, empty ownership and room level 1. `PetService.Normalize` migrates a saved sole plan 1 to 0. Plan names are limited to 32 non-control characters. Five initial plans and their names are inferred from the current plan UI.
2. Client eligibility costs become actual server debits. Levels consume small books first, stop at the requested +1/+10 target and carry all surplus EXP. Star/progress charges use the target row. The level cap is 99 and the owner's level.
3. Slot enforcement intersects the public parameter levels with the setup/support condition rows. Destination occupants swap into an occupied source or return to the roster when the source is none. Displayed per-pet strength follows client arithmetic; plan power is the sum of fight/support strength.
4. Static fight/support attributes are scaled by the slot's configured addition; activated collection attributes sum permanently. Collection's preview award is granted once at activation. Fragment breakdown multiplies the per-item preview by count.
5. A round takes new eggs (33318, 33325) while its `PetIncubationSlotConfig._iActivity` is open in the session's NewActivity projection, the same check the client makes: `LC_PetHelper.GetActivePoolTypeInfo` shows, per `_iSlotType`, a round whose activity `LC_ActivityHelper.IsActivityOpen` reports open. Activities 1015-1023 follow TimeLimitConfig 1065-1073: the Permanent rounds 10001/03/05/07/09 run on server-open days 0-7, 7-14, 14-21, 21-28 and 28-170, the Limited-Time rounds 10002/04/06/08 on days 10-14, 14-21, 21-28 and 28-170. OpenContent makes these windows perpetual, so the client then shows 10009 and 10008 (the very long countdown is OpenContent's open end); `OpenContent:ClosedActivities` 1016, 1018, 1020, 1022 closes the Limited-Time Pool. Acceleration and collection of an egg already placed only need the round to exist, so closing a round strands no paid egg. Each round keeps its own egg per physical slot: `DBSlotInfo.incubateInfo` maps round -> egg and `LC_PetHelper` reads slot data by round, so one pool shows a slot free while the other pool's egg sits there; collecting removes only that round's egg. Egg result selection follows the UI's `min(roomLv+1, draw-count)` index, then `DrawConfig._kSubBanner` -> weighted `DrawPoolConfig` row -> uniform choice of one `[type,id,count]` tuple. Preview lists are not weights. Room-quality weighting and first-draw behavior are unqualified.
   The S-class guarantee follows `UI_Sys_PetGachaWidgetCtrl.RefreshPetHit`: remaining = `DrawConfig._iGuaranteedCnt` minus `AppearanceLotteryPoolPityInfo[key].m_iPityCount`, where key is the draw's `_iType` for the Normal pool and the draw id otherwise. Those records are `DBAppearanceLotteryData.pityRecords`, the store `LotteryService` already uses: a draw at the count rolls only `_kGuaranteedBanner` groups, a hit in them resets it, and the record is committed with the egg and synced by 5011. Counting at egg placement (when the outcome is fixed) is *(inferred)*.
6. A chosen result persists at generation. One accelerator means 300 seconds; finish uses owned items first and buys each missing unit for 100 token-3 currency, matching the client confirmation path. All costs are atomic. An already-ready acceleration retry succeeds without spending. The wire has no nonce to distinguish an old retry after a new egg occupies the same round/slot.
7. Collection grants configured result EXP once per egg, increments count and records collection time. A duplicate pet becomes `PetBaseConfig._kDecompose` fragments in the bag; `brokenItems` names the duplicate pet id with the fragment count, because `UI_Sys_Pet_CongratulationPanelCtrl` and `UI_Sys_PetGachaWidgetCtrl` resolve the shard from the pet's `_kDecompose`; directly drawn items use `normalItems`. Acquisition uses 5017 without an additional 33309 append. Combine's append reply precedes its 5017 roster replacement.
8. The lowest deployed setup slot selects one follower; support slots do not create actors. Visibility follows the owner's visibility, and a missing friendly camp falls back to 1. The follower keeps a separate follow position next to the owner (see "World follow" below and [pet-world-follow.md](pet-world-follow.md) for the distance, presentation-policy evidence and unresolved original-server details). Server pet combat, target choice and skill damage are not inferred from the UI.

Explicitly unavailable:

- Timed room upgrades: the client reads `incubationLvUpTime`, absent from both supplied DBPetInfo protos. 33316 therefore completes immediately after its configured EXP/cost as an inferred client-compatible policy; no 33321 mapping or timer field is fabricated.
- 33323 pet breakthrough: empty break-stage table and disabled client eligibility.
- 33305 legacy GUID breakdown and 33307 legacy lottery return `FeatureUnavailable` rather than empty success. 33325 fast hatch is implemented for every open round, gated on room level 2, with the UI's projected material and accelerator costs.
- Original RNG parity, camp synergies and full combat-skill parity. The supported ordinary guardian families and their limits are documented separately; tower lineup remains separate.

## World follow

[pet-world-follow.md](pet-world-follow.md) explains why a synthetic `pendantInfo` entry cannot replace the existing actor with the supplied client tables.

- Keep the separate type-7 actor in 31037 and owner-first / pet-first removal ordering. At entry,
  deployment and teleport, place it 3 m diagonally beside/behind the owner. The exported
  `PetConfigFollow=300` suggests the distance; its centimetre interpretation and the 135-degree
  placement angle are **(inferred)**.
- Retain that formation offset through ordinary walking, stops, reversals and turns. Compute each
  position directly from the accepted owner position plus the offset, avoiding cumulative drift.
  A 31081 walk starts at that distinct position and uses the same finite one-second projection as
  the owner's AOI movement. Both projected trajectories remain separated. This synchronized
  formation policy and cadence are **(inferred)**, not recovered original pet AI.
- Send start/stop state changes even when coordinates are identical, refresh moving keepalives,
  and suppress repeated unchanged idle stops. Use 31014 for pet turns so turning does not restart
  the pet's movement clock. `SceneService.TeleportInMapAsync` explicitly signals a pet teleport,
  including a short jump below the ordinary 10 m AOI teleport heuristic.
- Keep the existing active-plan selection, undeploy/replacement, function gate, range exit/reentry,
  reconnect, map cleanup and private tower instance boundaries. No new dataset, learned skill,
  stat formula, incubation rule or pendant configuration is introduced.

This keeps the pet from sharing the owner's position. Native-client visual acceptance, exact original
server cadence/offset policy, and pet navigation around obstacles remain unqualified. Packet geometry
cannot guarantee separate name boards from every possible camera angle.

## Tests

`PetTests` and `PetAoiTests` are server/bot wire tests covering timer boundaries, reconnect, duplicate requests, stale sources, closed gates, a concurrent owner-entry race and injected SQLite rollback. The collection activation test verifies the one-time Zeny grant (`AwardConfig[id]` contains an array of rows; the reader uses the sole configured object row). The Bobo wire case covers spawn, walking and projected endpoints, same-position stop, reversal, turns (including owner/peer 31014), short teleport, late viewer, range exit/reentry, map exit/return, plan switch and undeploy. Focused AOI tests preserve owner-entry ordering and private-instance isolation, and cover same-position walk start/stop plus repeated idle suppression. Empty-panel rendering and visual pet behavior in the native client are not covered.

```text
dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~PetAoiTests|FullyQualifiedName~PetTests"
```
