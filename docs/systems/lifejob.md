# Life jobs, gathering and production

Content selection is `SeasonService.PlayBook = 101`.

## Protocol and durable state

`LifeJobService` handles 33501-33508 and 33510-33515. 33513/33514 return 33515 with both real cost maps. 33509 is declared `romsg.null` in `data/msgtable.tsv`; the service returns the typed `CheckCanCollectNtf` parameter refusal because that request identifies no collection. Actual collection eligibility is checked on authoritative 32700 and again at settlement. 33511 uses `ChooseTitlePointReq`, matching the message table. `GatheringService` owns 33516 and the collection branch of 32700, with 32702 presentation and 31037/31049 actor updates.

5010 contains real active slots, each profession's levels/EXP/title choices, collection/recipe/drop unlocks, map/point counts and magic-food counts. 5018 presents profession vigor (restriction 1004 plus unspent Vigor currency 407) and potion-use restriction 1005. Three role columns (`lifejob`, `life_restrict`, `life_runtime`) persist profession state and reset epochs; currency 407 remains in the role's currency balance. Inventory costs, awards, progression, counters and restrictions share the inventory transaction. In-memory publication follows commit. Tests inject a SQLite failure to verify rollback.

All 335xx requests require a loaded selected character, and the life consumable hook independently applies the same check to 31323. Before role entry and after role exit, typed refusals preserve the previous character's profession state, vigor and inventory. Health refusals still use 33515 with both metadata maps.

Starting a forge presentation (33510) only reserves a recipe/scene/time ticket. 33504 rechecks the recipe, active job, time, count, ingredients and vigor; consumes the ticket after one successful transaction; and returns actual awarded items/quantities. Output references go through `AwardService` with book 101, including repeated pool groups and bind state. Collection settlement reserves the shared node before any await, then revalidates under the character lock. Movement, skill requests (including refused skills), scene leave, death/combat detected at settlement, exhausted shared/personal limits and replays prevent rewards.

## Source-backed behavior

- `PublicParamConfig.LifeEnergy=1004`, `LifeEnergyItem=1291002`, `LifeEnergyLimitNum=1005`, `VitalityInitCount=500`, `VitalityExtraLimit=5000`. `RestrictConfig[1004]` has ordinary cap 1000 and recovery 0. G 實包的 5018 上限為 5000，生活 UI 亦讀此限制；顯示／可用基底以 VitalityExtraLimit 為準，缺參數資料集才回退普通上限。Its balance is not item 5. Item 1291002 action `[315,1004,1000]` restores vigor with its use condition, extra cap and five-use restriction.
- `TaskConfig[500070]` targets `[106906,1004,100]`; `DailyActivity[107]` is the Life Skills row. `TaskObjectives` consumes actual daily vigor spending. Existing task delivery grants the configured reward, including 20 units of item 5, once. Cost quotes, refused actions, and potion use do not advance this target.
- `LifeJobConfig`, `LifeJobLevelConfig`, `LifeJobTitleConfig` and the resolved condition list own profession eligibility, switching costs, title costs, thresholds and base-level gates. Condition 11001 requires an active selected profession. Unselecting a profession retains its progress.
- All 34 exported `LifeFormulaConfig` rows select job 2. The eight exported collection definitions select jobs 1/5. The implementation follows these rows without manufacturing cross-profession recipes. Formula 20001 consumes the configured ingredients and resolves multiverse group 112001; 112001 is not granted as an item.
- Magic foods 1001801-1001805 use action 607, book 101, `_kLimit=50`, and attributes from `LifeMagicFoodAttrConfig`. Accepted counts contribute to ordinary stat calculation and persist on reconnect. 料理依登入時捕捉的賽季身分生效；下次登入若賽季編號或 PlayBook 改變，才清除舊次數。同一本 PlayBook 的新賽季也會重置；舊 JSON 缺少賽季身分時保留同 PlayBook 紀錄再綁定。Ordinary cooked foods retain the existing item/buff pipeline. Localization 110112 also specifies seasonal reset of life titles/skills. 來源與遷移界線見 [賽季料理證據](../research/season-cuisine-evidence.md)。
- `LifeSkillSlots` supplies offer pools and refresh prices. `LifeSkillConfig` plus localized descriptions supplies speed, cost, EXP, main-output, byproduct, currency, refund, vigor-cap/potion and counter effects. Type 43 preserves the offered `originalSkill` while upgrading the effective `selectedSkill`; type 46 creates the specified extra slots and removes their entitlement when the granting skill is removed. Counters are staged in `SkillExtraInfo` in the same transaction.

## Initialization and client compatibility

The first 5010 represents unchosen professions. Wire-only deletion entries materialize empty top-level maps through `LC_PbHelper.ParseMapTypeIncrement`; these entries do not enter persisted state. A newly learned profession sends a fresh full 5010 because `LC_DAL_LifeJob.lua:800` dereferences its prior career asset on the incremental path. Nested title/tool/gather/build messages are explicit; no placeholder recipe or drop is granted to populate a map.

Empty nested maps were checked against the actual decoder setup: `Network/NW_GameServerHandler.lua:332-354` selects `no_decode_default_array`, but not `no_default_values`; the client and server DB protos are matching proto3 schemas. In the [upstream lua-protobuf decoder](https://github.com/starwing/lua-protobuf/blob/master/pb.c), proto3 default-table construction includes map tables independently of the repeated-array option. **Inferred compatibility:** the bundled bridge follows this proto3 map behavior. The tests parse the actual wire with generated protobuf types; behavior in the native client bridge has not been verified.

## Explicit inferred policies

The data and UI establish inputs and displayed effects; these policies complete the server behavior:

- Fresh roles choose their professions; a newly chosen gathering job starts with its condition-zero collections and free condition-zero drops. Other unlocks remain explicit requests.
- Daily vigor-use accounting and potion recovery use the existing To-Do 05:00 local-day boundary. Vigor itself has no timed refill because its configured recovery is zero. 寶箱與藥水活力可超過一般上限，但仍受 `VitalityExtraLimit` 限制；既有職業加成保留原推定規則。
- `ItemConfig[407]` is currency subtype `ItemSubTypeVitality`; its localized description says it is consumed during gathering and crafting. Its unspent balance is included in profession vigor, up to `VitalityExtraLimit` (5000) in total, and is spent before restriction 1004. A balance above the limit is kept and counts again once vigor is spent, and a potion is refused when it would take the shown vigor over the limit. Daily stage rewards therefore add their configured 407 quantity to usable profession vigor. This connection is **(inferred)** from the item description, the stage reward tables, RestrictConfig 1004 ("Obtain it from Recommend-Activity Chests") and the life panel, which shows item 407's icon with restriction 1004's value (`UI_Com_RestrictItemWidget`); the client data does not explicitly define a conversion to restriction 1004.
- `_iNeedExp` is gathering EXP. A `LifeJobLevelConfig` row holds what it takes to reach that level, its threshold and its condition (`LC_LifeJobHelper` reads level + 1); excess EXP is retained at a locked level. A production batch uses the single duration displayed by `UI_Sys_Life_ContentWidgetCtrl.lua:1592-1597`, not duration multiplied by quantity. The server bounds a batch to 1-1000 operations and expires an unused production ticket after 30 minutes.
- Drop eligibility rolls independently in basis points. Formula outcome tuples use relative weights, then the existing reward graph. Skill offer weight is slot weight multiplied by `_iProbability`, without replacement. Runtime percentages use denominator 100000, matching values such as 5000 displayed as 5%; they do not use the offer probability.
- Skill reductions stack, speed reduces the displayed duration, and one operation advances each applicable counter once. Gathering bonuses add individual main-product units, not another complete four-roll award pool. Variable Gathering uses a uniform configured main-quantity range. Skill 21 executes its configured multiverse group 1019 (Award 1001019 grants currency 2) even though the localized label describes a mystery box.
- Skill Ascension combines active-career effects, excluding the skill's own increase. Extra-slot entitlement begins at ordinary title slots and follows their grants; an extra slot cannot sustain itself after the originating grant is removed. Extra slots belong to the granting profession. Rerolling an offer preserves the current selected skill and its counter until a different skill is chosen.
- Magic-food attributes add once per successful consumption. 料理加成、次數與頭銜技能在下次登入換季時一起重置，保留職業 EXP／等級；全域換季不會先清掉仍在線角色的食用上限。舊存檔的賽季身分綁定與屬性累加為本地推定政策，未證實原服歷史遷移演算法。
- Gathering placement, point identity, refresh timing, minigame report timing and terminal interaction order are detailed in [gathering.md](gathering.md). Minigame ratings are bounded to a selected game, configured rows and elapsed time; the score behind the reported rating is not independently validated.

## Known gap: special-recipe talent type 35

All five `LifeSkillConfig` ranks of talent type 35 refer to recipe 20054. That recipe definition is not present in the exported data or client sources, so these ranks are excluded from offers/selection rather than producing a fictitious recipe. An exact-token search for `\b20054\b` across `data/config`, `data/sceneeditor` (including monster maps), `data/nav`, `data/loc`, both client Lua trees, the decompiled C# and both proto trees finds only the five skill references plus an unrelated `TradeBasePrice` row. Direct `LifeFormulaConfig` and `ItemConfig` searches return zero exact-token hits. `TradeBasePrice` row 20054 points to SkillId 10011104, so it does not define the referenced life formula. Implementing that special recipe requires its ingredients, costs, eligibility and output definition; the other configured skills remain active.

## Tests

`LifeJobTests`, `LifeJobSkillTests`, `LifeJobOwnerEconomyTests` and `GatheringTests` cover the protocol, persistence, rollback and gathering paths. `LifeFoodSeasonTests` 另以自造資料驗證同 PlayBook 換季、舊存檔綁定與交易先提交後發布。The ordinary-client presentation, original-server random distribution and original-server refresh timings remain unverified.


## 活躍度寶箱活力入帳

G 目前原件的活躍度 20／40／60 寶箱固定給物品代號 407 各 200／200／600 點，80／100 寶箱不給活力；五個寶箱的掉落池亦不額外給 407。這是完成活動後領取的獎勵，沒有每日登入自動 1000 點的功能；本輪依使用者要求不新增自然補充。

一般活躍度寶箱領取的 407 留在 `Progress.Currency[407]`，與寶箱領取記錄及其他獎勵同筆提交。5018 的 `RestrictInfo[1004]` 對玩家顯示限制額度加未花用 407，總顯示值受 `VitalityExtraLimit` 限制；超出顯示上限的幣別仍留在餘額，之後可以花用。採集、製造優先扣 407，再扣限制額度，並將成本與成果同筆提交。這是根據物品說明與生活 UI 的本地推定，尚無原服交易封包證實。

既有 `Progress.Currency[407]` 保留，不推測來源、不回溯轉存或清除。一般 ApplyAward／Exchange 的 407 獎勵也保留為幣別，允許 407 作為交易成本。資料庫拒絕時，領取記錄、幣別、物品與限制額度一起回復；提交成功後才發布記憶體及 5018。

郵件領取（包含 1058 未領寶箱補償信）是明確例外：本次 407 附件轉入限制額度 1004，不動用角色既有的 407 幣別。轉入與郵件已領記錄共用交易及來源證據，重領不再入帳；轉入仍受現有額度上限約束。這項本服相容轉換不能推論原服一般 407 獎勵也會轉換。

`DailyTests.VigorRewards` 覆蓋一般領取、批次去重、幣別支付、顯示上限與既有超額、重登、5018 同步、補償郵件一次性轉入，以及資料庫拒絕後整筆回復與重試。`LifeJobTests` 覆蓋生活職業優先花用 407、新角色初始額度與上限顯示。
