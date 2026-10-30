# Mount and Divine Totem implementation

`data/` and `re/` are read-only inputs. The season owner remains `SeasonService` and its
configured playbook 101.

## Wire and persistence

| Owner | Requests | Persistent client dataset | World/gameplay effects |
| --- | --- | --- | --- |
| `MountService` | 32743-32745, 32748-32750; action 201 via 31323; quest actions 412/54 | 5012 `DBMountsInfo`; a role blob also retains claims, paid etchant previews and temporary quest ride | 31049 `Rider`, initial `EntityBase.curRider`, ordinary attribute recomputation and movement speed |
| `TotemService` | 82362-82370 | 5032 `DBTotemInfo`; actual GUID instances and itemInfos in 5009 pack 4 | deployment markers, stats, selected skills and source-owned combat effects |
| `WardrobeService` | 31183-31188; action 200 via 31323 | full 5034 ownership, selections, plans and favorites | full appearance map for self, AOI and offline role information |
| `GenreService` | 82800-82828 bound operations | 5008 `DBGenre`, durable child plans and recommendation records | validated atomic application of child plans |

Wardrobe/color evidence is detailed in [wardrobe.md](wardrobe.md).
Genre and child-plan behavior is detailed in [genre.md](genre.md).
Combat interpretation and tests for totem skills are detailed in [totem-effects.md](totem-effects.md).

## Mount behavior and evidence

`MG_Mount.proto`, `MG_DB.proto:633-655`,
`Logic/Mount/LC_MountServer.lua`, `Logic/Asset/LC_DAL_Mount.lua` and `LC_MountHelper.lua`
separate ownership, selected slot and visible riding. In particular:

- 32743's Lua sender omits `tp`; zero means the system slot. `MountsConfig` selects system slot 1,
  `CartRemouldTable` battle slot 2. `MountsConfig._iType` is presentation type, not this slot enum.
- 32745's values are off-system 0, on-system 1, off-battle 2, on-battle 3. A selected mount does
  not contribute riding stats until actually ridden. At most one selected slot is ridden.
- Ownership, expiry, class, function 27, combat/death/scene-loading and map restrictions are checked.
  `Config/CFG/Lua_CFG_MapConfigBase.lua:497-549` maps `_kRiderForbidden` to battle/system/flying.
  This request changes riding; it does not itself start a separate flight action.
- All attribute contributions use `MountsConfig`/`CartRemouldTable._kAttr`, including percentage
  movement attribute 1131. Self and viewers receive the same `curRider` and refreshed properties.
- Item action 201 validates the configured target and consumes the requested GUID together with
  ownership persistence in one transaction. 32747 is a tip, while 5012 is the ownership state.
- A mount and its `WardRobeConfig` type-6 row (`_iSystemID` = mount ID; all 22 rows map 1:1) are one collectible.
  The wardrobe Mount tab lights entries only from 5034 (`UI_Sys_WardrobePanelCtrl.lua:1848-1859`) and rides one by
  sending 31183 then 32743 with that ID (`:1070-1126`, `:1398-1425`); conditions, the luxury serial and the panel's
  `AllMountData` binding read 5012 for the same row (`LC_InteractCondition.lua:4448-4515`, panel `:162`, `:2314-2346`,
  `:4880-4905`). Action 201 therefore commits the item, 5012 and the lit appearance in one transaction, then sends
  5012, 5034 and 32747; no 31189, whose reward tip would duplicate 32747 (`LC_DAL_Fashion.lua:253-265`).
  Both `overdueTimeMs` values are absolute Unix ms, zero permanent (`LC_FashionShowFunc.lua:256-286`); the appearance
  keeps the longer of its existing and the mount's lifetime. Mount expiry removes expired appearances in the same
  commit, and wardrobe login lights any owned mount still missing (repair of roles saved before this link).
  Disabled (`_iDisable`) rows are not listed by the client and are not lit. See `WardrobeService.Mounts.cs`.
- `EtchantPlanMax=4`; the UI uses indices 0-3 and keeps default plan 0 immutable. Configured default
  parts are seeded in each plan. 32748 pays the configured currency cost and persists the server
  preview. 32749 accepts only that paid preview for the exact mount/plan/part. 32750 selects a plan.
- 32744 validates every unique threshold index and atomically grants each `MountsSpecialConfig`
  award once. Its claimed set persists across reconnects.
- `TaskConfig[103012]` executes 412 for mount 60040033; 103014 recycles it with action 54.
  Task reward, task completion and temporary ride persistence share `ApplyAwardAsync`'s SQLite
  transaction. Mount publication follows its commit. It does not create permanent collection ownership.
- A rider who casts or attacks gets off a system mount first and is not refused. The client never sends the
  dismount for an ordinary skill (only an interaction skill sends `OffSystem`, `UI_Sys_MainRoleSkillWidgetCtrl.lua:481-488`),
  and it holds a skill while its unit rides a system mount until the ride ends, then plays it sped up by the time spent
  waiting (`LC_State_Unit_Skill.lua:51-102`, `m_bRideSystemMount`). The client plays the dismount itself when `curRider`
  drops to 0 (`LC_MountComponent.GetDown`, the `RideMountFxPath` effect). So `CombatService.BeforeCast` (set by
  `MountService.DismountForCastAsync`) commits the dismount and publishes 5012 and 31049 before the cast's reply, and the
  cast's timeline (31100) and hit (31101) start `MountService.DismountMs` later. A battle mount (slot 2, `m_kMountType` Battle),
  and a flying rider keep the ride. The cast is only delayed; a refused cast (cooldown, cost, target) dismounts nobody.
  The fly mounts' take-off and landing skills (`MountsConfig._kFlySwitchSkills`, injected by `LC_MountComponent._injectSkillPos`)
  have no server path that grants them yet, so a cast of them is refused as not learned before this runs; once one does,
  those skills must be excluded here.
- Connected-session expiry runs every second. Scene restrictions are reconciled after 30005 but
  before ENTITYSELF/AOI. Class changes revalidate riding. Role selection clears previous-role state.

Mount implementation inferences, rather than captured original-server behavior:

1. Action-201 durations are seconds, based on exported 60/600 trial durations; zero is permanent.
   Repeated timed unlocks extend from the later of now/current expiry; permanent ownership rejects duplicates.
2. Owned `uniqueId` equals the configured mount ID, matching the DAL's config-ID indexing.
3. `_iWeight` determines independent etchant draws; paid previews survive reconnects. Private claim,
   preview and quest fields use a JSON envelope around the wire protobuf, without inventing wire fields.
4. Legacy score rewards use the permanent mount appearances' `WardRobeConfig._iFashionScore`.
5. Quest auto-ride persists until the matching recycle action or an explicit mount selection/dismount.
   The action names and arguments establish ride/recycle; this bounded lifetime is an implementation choice.
6. Exact refusal-code mapping and notification order use the matching `ErrorCodeConfig` texts and DAL expectations.
7. The dismount lead `MountService.DismountMs` (500 ms) is inferred: the dismount is a Unity animation plus an effect, with
   no duration in the config tables or Lua. That every accepted cast of a system-mount rider dismounts it (not only damaging
   ones) follows the client holding any skill, and that a flying rider keeps its mount is an implementation choice.

## Divine Totem behavior and evidence

`MG_Totem.proto`, `MG_DB.proto:786-798`,
`Logic/Asset/LC_DAL_Totem.lua`, `Logic/Totem/LC_TotemHelper.lua` and the `UI/totem` controllers
provide the client contract. `TotemConfig`, `TotemSkillConfig`, `TotemLvConfig`,
`TotemSkillNumRandom`, `TotemNewGridPerConfig`, `ItemConfig` and `PublicParamConfig` supply its rules.

- Five plans use indices 0-4 and explicit positions 1-3, unlocked at levels 40/50/60. Function 29
  opens at level 40. `Config/CFG/Lua_CFG_JobConfig.lua:463-482` returns `_iJobRank` directly;
  novice rank 1 can equip rank-1 totems. No rank subtraction is performed.
- Totems stay real inventory GUIDs in pack 4, grid 0. The current plan sets itemInfos 10001=1,
  10002=position, 10003=Unix milliseconds. An inactive plan still reserves its GUIDs against
  unrelated item consumption. Movement between positions clears the prior position.
- Attribute IDs occupy 220-229, values 230-239, skills 240-249, bounds 250-269 and used refinement
  count 270. Generated attributes are stored per instance, not rerolled at sync or reconnect.
- 82370 identifies eligible unappraised instances in one transaction. Repeated appraisal and duplicate
  input GUIDs fail without changes. 82362 requires appraisal before deployment.
- Refinement uses the exact configured material/count and action-205 range, clamped to global
  attribute bounds. The cap comes from `TotemRefineNumPerMultiverse`. A separate persisted epoch
  derived from season playbook/start resets count 270 when the season changes, including offline roles;
  the initial migration retains existing counts.
- Skill learning reads action 206 from the consumed book. `TotemSkillInitMax` and localization
  10960000087 prohibit blue-totem learning. Superior skills, existing skills, born skills, initial
  capacity and configured new-slot probabilities are respected. The book and changed attributes
  commit together.
- EXP is accumulated from `TotemQualityExp` and `TotemCurrencyExp`. `Lua_CFG_TotemLvConfig`
  establishes cumulative level thresholds and percentage base-attribute enhancement. Deployed,
  locked, referenced, duplicate or unowned input GUIDs are rejected.
- `_iIfCompose == 0` permits combination (`Lua_CFG_TotemConfig.lua:343-356`), the reverse of what
  the field name might suggest. Both inputs must be eligible, same quality and same class rank.
  Two source GUIDs and the attributed output commit together. Plan references cannot dangle.
- Selected skills suppress configured inferior/noncoexisting duplicates. Totem effects own their
  passive sources and reconcile them after every state change; see [totem-effects.md](totem-effects.md).

Totem implementation inferences:

1. All five plan slots are initially usable; explicit zero GUIDs keep the client maps safe.
2. Attribute rolls and weighted skill selection use independent server randomness within exported
   intervals/weights. Refining chooses an inclusive action-205 multiplier. Combination uses the
   mean of matching input attributes, then the configured minus/plus range and global bounds.
3. The left input supplies the combined template and born skills. The configured combined skill-count
   table is used where it has a matching row; otherwise the distinct input skill count is retained.
   The higher used-refinement count is retained. Permanent binding dominates temporary binding;
   temporary binding keeps the later expiry.
4. A learned superior replaces an inferior; otherwise configured new-slot chance decides expansion,
   then a non-born skill is replaced. Born skills remain protected.
5. EXP beyond maximum level is returned in descending configured EXP-item denominations via the
   transaction and `UpgradeTotemSystemResp.gainItems`. The right-panel controller stores this map
   at lines 1010-1018 and displays it at lines 1667-1719; exact original denomination choice is not captured.
6. Exact error mappings, season epoch storage, private transaction boundaries and cooldown enforcement
   are emulator choices supporting the demonstrated client contract. Combat-specific inferences are listed separately.

## Search record and evidence boundaries

A repository-wide discovery search for `mount|rider|etchant|totem|genre|scheme` covered every relevant scope,
including both Lua exports and both proto copies:

| Scope | Files | Matching files / lines |
| --- | ---: | ---: |
| `data/config/*.json` | 517 | 40 / 40 |
| `data/sceneeditor/*`, including monster ID maps | 381 | 0 / 0 |
| `data/nav` | 172 | 0 / 0 |
| `data/loc` | 2 | 1 / 404 |
| entire `re/lua-src` | 13,488 | 867 / 11,861 |
| entire `re/clientB/lua-src` | 13,488 | 867 / 11,907 |
| `re/decomp` | 2,277 | 13 / 32 |
| `src/Ro3Emu.Server/Protos` | 147 | 52 / 491 |
| `re/proto` | 144 | 52 / 491 |

This broad search is a discovery record, not proof that a particular feature lacks an implementation.
Specific color and combat boundaries include their own patterns/classification in the linked documents.
Static client evidence and emulator protocol tests are distinct from original-client visual or
packet-capture acceptance, which has not been done.

## Tests

`MountTests` covers 5012, unlock/refusal, selected versus ridden state, speed, viewer AOI, reconnect,
expiry, paid etchant previews, a storage-abort rollback and the real 412/54 quest action paths.
`TotemTests` covers 5032, all nine operations, actual inventory markers and attributes, reconnect,
quality/gate/slot/ownership/material refusals, reservation, refinement cap and EXP overflow conservation.
`WardrobeTests`, `TotemEffectsTests` and `GenreTests` cover the corresponding owners and integration.
