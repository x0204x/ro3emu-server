# Skill buffs and resources implementation

This note covers `BuffService.cs`, its energy/death partials and `SkillBuffTests.cs`. It distinguishes working server behavior from the client contract and from inferred mechanics. The implementation does not establish original-server parity.

## Wired runtime behavior

* `31104`/`31105`: `AddAsync`, `RemoveAsync`, death/scene clear and expiry retain a stable buff GUID, owner/source, config id, duration and pile count. The owner receives a nonempty direct array; `31048` gives viewers and owner nonempty add/remove deltas. `ResendAsync` skips an empty `buffs` array because `LC_Buff_Component.lua:594` takes its length without guarding nil. `PopulateEntity` fills initial `EntityBase.buff_data` for an already buffed unit (`MG_Entity.proto:135`, `LC_Scene_Sync.lua:5664-5670`). Monster visual buffs retain GUIDs until an expiry removal, but monster damage/stat aggregation still belongs to the combat owner.
* `2701`/`2702`/`2711`: exact known buff groups Stun 9016, Sleep 9022, Silence 9023, Freeze 12003 and Invincible 9034 resolve to `SpecialStatusConfig` states. Each active source contributes to a union; removing one cannot clear another's walk/cast mask. `31048` state/forbid values always contain at least one word, including zero. `HasForbidden` is the server gate for combat/scene consumers. `31115`/`31116`/`31117` are never emitted.
* `31332`: `ClientRemoveBuffReq.buffGuid` must identify a live buff owned by the requester with `BuffConfig._iHandRemove != 0`. Invalid, foreign, expired and protected GUIDs return `ErrorCodes.Parameter` (82); success replies then removes the GUID through the ordinary lifetime path. The exact original refusal code and response/remove ordering are inferred.
* `31333`: a positive Osiris internal cooldown starts when its lethal protection is offered, with the active buff GUID and absolute start/end milliseconds. `BuffEffectCdNotify` is delivered to the owner and not invented for other BuffEffect IDs.
* `31334`: `InitializeEnergyAsync` creates matching `EnergyConfig._iBelongJob` meters at `_iInitializeValue`, bounded by `_iLimitValue`, sends `LoginInit`, and advances `_iAutoRecoverInterval`/`_iAutoRecoverValue` on server tick. `ReconcileEnergyAsync` runs after progression: same-job level changes preserve current values, while job changes retain shared applicable meters and initialize only newly applicable ones. `_kCost` type 8 uses `max(limit * ratio / 10000, flat)` as checked in `LC_Skill_Function.lua:4137-4160`; `CanSpendEnergy`/`SpendEnergyAsync` reject malformed/unknown meters and aggregate repeated charges before acceptance. `_kHitRecoverEnergy` is applied after an accepted hit. The wire includes value, reason and recovery timestamps; each notification also invokes `Changed` for skill parameter/slot updates.
* `31134`/`31135`: BuffConfig groups 20520 and 20521 (Osiris) say the player chooses a revive after lethal skill damage (`Localization_en` 10220300143-145). The server holds at 1 HP, offers a 5-second window (`PublicParamConfig.ImmuneDeadConfirmTimeMs`), validates confirmation, and applies the row's recovery percent; decline/timeout invokes the normal death callback. The 20520 first variant has a 3-second protected 1-HP interval before 20% recovery. Buffs 1430501 and 16340401 explicitly provide immediate one-hit fatal protection, and consume their instance when triggered. `TryPreventLethalAsync` must run before an incoming hit commits HP zero.
* `31331`: `PublicParamConfig.WorldTreeItemId=1002004`, `WorldTreeSkillId=2200101`; the item points to that skill and SkillConfig charges one item. The skill/revive owner calls `NotifyWorldTreeReviveAsync(recipient, revivedPlayerName)` on a *successful* 2200101 target revive. The buff service cannot itself establish whether another player's revival succeeded.

## Integration calls and boundaries

`GameMessageHandler` routes 31332 to `RemoveByRequestAsync` and 31135 to `ConfirmImmuneDeathAsync`; its 31323 route checks `HasForbidden(p, 12)` before item consumption and replies with localized code 180. `CombatService` calls `HasForbidden` for walk/cast/normal-attack/incoming-damage actions (ordinal 3/4/5/9), `CanSpendEnergy` before cast commitment, `SpendEnergyAsync` after acceptance, `RecoverEnergyOnHitAsync` after a real hit, and `TryPreventLethalAsync` before applying zero HP. It assigns `ImmuneDeathDeclined` to its ordinary death transition. Scene entry calls `ClearOnSceneAsync`; role entry calls `InitializeEnergyAsync`; `CharacterService.ProgressHandlers` calls `ReconcileEnergyAsync`. `GameMessageHandler.PlayerEntity` calls static `PopulateEntity` so new viewers see masks and buffs. `Changed` callbacks allow skill parameters/slots to recalculate after any add/refresh/reduction/removal.

`HasBuffPiles` and `ConsumeBuffPilesAsync` support `_kCost` type 3; it validates and removes whole or partial piles, sends the updated GUID and refreshes attributes. The assumption that the cost tuple id denotes a BuffConfig group is inferred from the client's `GetCostBuff` shape and needs a captured server refusal/consumption trace.

## Evidence, inference and unresolved cases

* `MG_Skill.proto:234-280,424-470` defines buff, widget, death and energy packets. `LC_DAL_Buff.lua:8-104` binds 31104/31105/31332/31333. `LC_Scene_Sync.lua:5222-5295,7088-7194` consumes `31048` state/forbid; `Lua_GameDefine.lua:2163-2225` explicitly names the forbidden bit masks, which do not align with every `ObjectForbidden` ordinal. `SpecialStatusConfig` provides the state-to-forbidden list. The exact buff-to-state mapping and server stacking/attribute multiplication policies are **inferred**; only listed groups get a mapping. Stack types 8/9 remain uninterpreted and refresh one pile. Per-stack expiry and `_iMultiSource` priority need server traces.
* Osiris text explicitly supports a choice and recovery percentages, but the offer packet's ordering relative to `SkillHitInfo`, the exact life-state packet, consumed-charge policy and whether declining starts internal CD are **inferred**. The one-hit immediate buffs derive from `Localization_en` and BuffConfig/SkillConfig rows; exact PvP/dungeon exceptions remain unproven. `BuffEffect.json` exports IDs only, so no periodic DoT/HoT, shield, script or generic effect interpreter was made up.
* `EnergyConfig.json` defines initial/cap/tick/job membership, and `MG_Skill.proto:111-119,450-457` defines change reasons. All 20 career-applicable rows have `_iOfflineSave=0`. The one row with `_iOfflineSave=1`, 3112301, has `_iBelongJob={}` and `_iBelongMount=60060010`; only mount skill 3112301 refers to it. The implemented career meters therefore need no offline state column; mount energy is outside this career-meter implementation. Exact `_iBelongJob` parent inheritance and the `_kEnergyChangeList` interpreter remain unverified. `_kAttackedRecover` has positive rows only at 10001 `[1,1000,3]` and 141001 `[1,100,1]`; a search for `AttackedRecover|EnergyChangeList|OfflineSave|BelongMount` across the config export, scene editor, nav, localization, decompiled code, protos and both Lua trees matches 2 config files and 4 Lua files, and nothing else. Attacked recovery itself occurs in the generated EnergyConfig row; the other Lua matches expose skill energy-change data and its UI, not an attacked-recovery formula, so an attacked-recovery charge is not guessed. `Lua_GameDefine.lua:11351-11362` names cost types 1, 2, 3, 6, 7 and 8; `LC_Skill_Function.lua:4031-4186` checks those types and ignores config's two-element type-4 tuples. SkillConfig has 160 rows containing `[4,101]`, `[4,102]`, `[4,105]`, `[4,1]` or `[4,106]`, none an EnergyConfig key; type 4 is not treated as energy. These are opaque client-ignored markers, not proof that an original server lacked another cost.
* `Lua_GameDefine.lua:2190-2193` names forbidden UseItem mask 2048, corresponding to `SpecialStatusConfig._kForbidden` ordinal 12. `ErrorCodeConfig`/`Localization_en` row 180 is “Item use is prohibited”; its use as the `31323` refusal for this gate remains inferred. The item-use route checks `HasForbidden(p, 12)` before consumption, and the focused test exercises a real Stun buff against 31323, checking that refusal preserves the potion stack and removal restores use.
* `31130`/`31131` stay un-emitted: both `re/clientB/lua-src/Config/DataConfig/WidgetConfig.lua` and `re/lua-src/Config/DataConfig/WidgetConfig.lua` have exactly one row (`_iID=1`, `_iWidgetID=1000`, empty icon/effects); `data/config/WidgetConfig.json` mirrors it at key `0`. `LC_Scene_Sync.lua:8721-8749` and `LC_Buff_System.lua:385-463` prove the receiving route, but no producer links a usable widget config to an actual buff/ground skill. Emitting a guessed widget would fabricate gameplay. Revisit when a producer and config row are identified.
* `31115`/`31116`/`31117` are empty protobuf messages (`MG_Skill.proto:273-280`). An exhaustive name search for `SYNC_FOBBIDEN|ADD_FOBBIDEN|REMOVE_FOBBIDEN|SyncForbiddenNotify|AddForbiddenNotify|RemoveForbiddenNotify` across `data/config/*.json`, `data/sceneeditor`, `data/nav`, `data/loc`, `re/lua-src` (Config and Logic), `re/clientB/lua-src`, `re/decomp` and `src/Ro3Emu.Server/Protos/romsg` found no data/scene/nav/loc/decomp hits; both Lua trees only have PB enum/name maps, while proto has the empty definitions. Live restrictions travel in 31048 instead. This supports leaving them silent, not a claim about the original server's internals.
* The same roots searched for `WidgetConfig|WIDGET_UPDATE_NOTIFY|WIDGET_REMOVE_NOTIFY|WidgetUpdateNotify|WidgetRemoveNotify`: generated Lua config plus display helpers/Scene_Sync and proto carriers are positive; no scene/nav/loc/decomp producer appears. For `ImmuneDead|Death Immunity|lethal skill damage|ImmuneDeadConfirmTimeMs`, data/config has `Localization_en`, `PublicParamConfig`, `DirtyDataConfig`, `data/loc/loc_en.tsv` matches, the Lua revive/panel paths and protobuf messages match; the positive buff/skill rows above were found by resolving localization IDs from *all* BuffConfig/SkillConfig descriptions. For `WorldTree|World Tree|ITEM_WORLD_TREE_REVIVE_NTF`, config localization/public parameters, `LC_ReviveHelper`, `LC_DAL_Item` and proto match.

Focused wire tests are in `SkillBuffTests.cs`: cast/add/nonempty AOI, foreign/protected/manual removal, overlapping 2702 masks, item-use refusal/recovery, Osiris offer/confirmation/CD, and energy initialize/charge.

## 一般技能與寵物技能共用的狀態效果

`BuffConfig._kChangeAttri` 的怪物屬性覆寫由所有有效來源共同提供，不再僅讀取 `IsGuardianOwned`。
`MonsterStats`、命中計算、傷害減免與移動速度都讀取同一份有效狀態；移除或到期後回復模板，
不修改共用的 `MonsterTemplate`。`IsGuardianOwned` 仍供寵物專屬反應使用，不會因一般技能產生後續狀態而改成寵物來源。

已知群組對應的暈眩、睡眠、沉默、冰凍、石化、定身與免疫使用共用狀態路徑。
行動限制讀取 `SpecialStatusConfig._kForbidden`，由伺服器實際拒絕移動、普攻或施法，並以 31048 同步遮罩。
冰凍／石化／睡眠遇正 HP 傷害解除，前兩者以 `_kLogicData` 套用後續狀態；原有抵抗與遞減規則仍生效。
怪物抵抗控制時先保留 `ControlResisted` 事件，避免元素控制的抵抗後追擊因共用免疫而消失。
群組到特殊狀態的配對沿用既有資料推定，不代表已取得原服全部狀態程式。

暴風雪 `1210801..1210810` 的每波倍率為 5.5% 至 10%、固定項為 67 至 130；
Lv.3 為 MATK × 6.5% + 81，Lv.10 為 MATK × 10% + 130，之後仍需套防禦、元素等戰鬥規則。
`_kBuff2` 指向減速 `1211101`（`1131=-2000`，3000 ms，每波刷新）及冰凍 `902001`
（200/10000，1000 ms，受抵抗與免疫影響）。`_kLogicData` 的 1000 ms 間隔及 5000 ms 生命週期
對應時間 0、1、2、3、4 秒的五波，不應把演出分段數 `_kShowDmg` 當成額外傷害波數。

`SkillCombatTests.StormGust` 使用真資料及 TCP 施放，分別核對 Lv.3／Lv.10 的傷害、五波、半徑、
五目標上限、199／200 的觸發邊界、實際速度與控制解除；
`SkillCombatTests.OrdinaryControls` 核對非寵物來源的控制、免疫與受擊解除。
這些是 LAB 協定測試；原生客戶端動畫、移動表現及全職業所有等級的玩家驗收仍須另外記錄。

怪物硬直另由 `_iStaggerType` 同步 `StaggerInfo` 與初次可見的 `ObjStagger`。
冰凍、石化、暈眩不一定含 `_kForbidden` 的施法位元；伺服器分開套用硬直優先於施法的判定，
保持原始禁止行動遮罩不變。解除或到期必須同步零遮罩，避免客戶端永久停留在控制動作。

`LC_Unit_Player.CanBlockSkill` 明確豁免群組 93010「解控」；控制施法驗證保留此例外，
但不跳過學習、生命狀態、場景與冷卻檢查，也不宣稱已還原其未知玩法腳本。
