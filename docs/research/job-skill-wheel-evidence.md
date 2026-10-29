# 轉職後的技能旁額外按鈕

已定位的 `DerivedSkill` 額外技能區由伺服器提供的 `SkillWheel.whellSkills` 決定；使用者描述的魔法師／牧師按鈕是否就是這個區域，仍為 `UNKNOWN`。不能以「已一轉」直接推定六個職業都應新增同一顆按鈕。目前可確認的職業隊列設定，與額外技能的原服授予規則，必須分開處理。

## 已確認的顯示與資料路徑

- `Logic/Asset/Systems/LC_System_Skill.lua:_onJoystickSkillListChange`，607–612 行，把 `skillWheel.whellSkills` 深複製為 `m_kJoystickSkillList`；`GetSlotToSkillIdMap`，768–774 行，直接回傳該表。
- `Logic/Asset/Systems/LC_System_JoystickSkill.lua`，253–276 行，發現 `SkillWheelPet`、`SkillWheelSkillQueue` 或 `SkillWheelExtraSkill1` 任一格存在時，才加入 `DerivedSkill` widget。
- `UI/Main/Controllers/JoyStickSkillNormalMixin.lua:tDefaultDerivedConfig`，15–37 行，把隊列、額外技能 1、寵物三格依序對應額外按鈕與快捷鍵。
- `Logic/SkillGrowth/LC_SkillGrowthHelper.lua:GetCanUseSkillQueueSkillId`，2526–2554 行，只讀 `whellSkills[SkillWheelSkillQueue].skillConfigId`。它不讀 `SkillWheel.skillQueue` 屬性。
- `MG_SkillGrowth.proto` 的 `SkillWheelSlotType`：一般格為 1–6，隊列為 7，坐騎為 8／9，額外技能 1 為 10，寵物為 11。`SkillWheel.skillQueue` 是另一個獨立欄位，不能代替 map 的第 7 格。

這些條件證明額外區域的顯示來源，但沒有證明所有職業或所有時間都應顯示。第 10 格的技能仍須有伺服器授予來源；不能從格子名稱直接選一個技能塞入。

## 六個一轉職業的隊列比較

下表來自 `Config/DataConfig/JobConfig` 的 `_iQueueSkillId`。最後一欄只是 `SkillConfig._iQueue=1` 的原始設定；動態 buff 的資格修改另由 `LC_DynamicParamMgr` 處理，不以這欄取代動態判斷。

| 職業 | Job ID | 隊列包裝技能 | 一轉技能的原始隊列資格 |
| --- | --- | --- | --- |
| 劍士 | 1100 | 1139901 | 無 |
| 魔法師 | 2100 | 0 | 無 |
| 弓箭手 | 3100 | 1399901 | 13006，心神凝聚 |
| 服事 | 4100 | 1400001 | 14001、14010、14015 |
| 盜賊 | 5100 | 0 | 無 |
| 商人 | 6100 | 1699901 | 16008，大聲吶喊 |

因此不能把四個職業的隊列包裝技能當成魔法師或盜賊一轉缺按鈕的解法。**魔法師的 `_iQueueSkillId=0` 不代表沒有其他附加按鈕**；此表只比較隊列入口，沒有識別玩家所指的其他 widget。`JobConfig` 中魔法師後續職業 2300／2400 才分別設定 1230001／1240001；一轉與後續轉職不可混用。

四個一轉隊列包裝技能共有以下設定：`_iSystemType=6`、`_kScriptLogic="jinengduilie1"`、`_kIcon="icon_skill_public_queue.png"`、`_iRelease=2`、`_iTargetMax=0`、`_iActionTime=500`、`_iCD=5000`、`_iScriptShow=14000`。這些是隊列的入口技能，不是隊列裡第一個實際技能。

`Config/CFG/Lua_CFG_SkillConfig.lua:GetSkillIsSkillQueue`，2066–2098 行，以 `_iSystemType == enum_SkillSystemType.SkillQueue` 辨識入口；它沒有執行 `jinengduilie1`。

## 隊列選擇不等於執行器

`LC_SkillGrowthHelper.GetSkillQueue` 讀取目前方案的 `SkillPlan.skillQueue`。`LC_System_Skill.lua`，463–485 行，只有找到第 7 格入口且方案隊列非空時，才把隊列入口加入手動技能清單。

`Logic/Actor/AutoAttack/LC_AutoAttackHelper.lua:IsSkillValid`，372–412 行，對隊列入口逐一檢查方案裡的實際技能；任一實際技能有效才回傳 `true`。這段只決定入口是否可用，沒有將包裝技能展開為一連串施放。`LC_AutoAttackSkillHelper.GetDefaultAutoBattleSlotList`，456–488 行，也把第 7 格列為自動戰鬥候選，不能只補手動顯示而忽略自動戰鬥。

在已檢視的 G Lua 中，`jinengduilie1` 僅有 `SkillConfig` 宣告，沒有其原服執行函式。隊列內技能的執行順序、各自冷卻與消耗、失敗時是否跳過、施放中斷及重複按壓的行為仍是 `UNKNOWN`。不能以 `pairs()` 的檢查迴圈推定原服執行順序。

## 額外格與額外施放旗標

`SkillWheelExtraSkill1=10` 是 UI 格子。`SkillConfig._iExtraType` 則是另一個概念：`Lua_CFG_SkillConfig.IsExtraSkill`，987–1001 行，只回傳 `_iExtraType == 1`；技能按鈕的 `_checkSkillCanCast`，1391–1405 行，使用它決定是否略過「正在吟唱」檢查。這個旗標沒有提供第 10 格的授予、選擇或轉職解鎖規則。

寵物技能也有自己的第 11 格與授予來源，不能因為它同樣顯示在 `DerivedSkill` 區域，就當成職業一轉的共同追加技能。

## 伺服器缺口與修正邊界

目前 `SkillService.Build` 只把方案隊列裡第一個實際技能放入 `SkillWheel.skillQueue` 屬性，沒有投影第 7 格入口，也沒有重建 `jinengduilie1` 執行器。這是可定位的隊列功能缺口，但僅補 map 第 7 格不足以修好功能。

普通施放仍會經過 `CombatService.Learned` 的職業／已學等級檢查。直接放上職業以外的包裝技能可能回覆 3002；只放寬已學檢查也沒有實作隊列效果。故本輪未硬加可按的隊列入口，未把未知的第 10 格規則當成六職共通按鈕，也未改變職業技能授權。

可接受的後續修正須同時具備：確切職業與來源授權、對應的 wheel map、可驗證的包裝施放規則，以及手動／自動戰鬥的拒絕與冷卻回歸。若目標是一轉魔法師，仍須先辨識玩家所指的 widget／技能，不能用非零隊列職業替代驗收。

## 來源識別與驗證範圍

來源路徑相對於 client 的 `Recovery/LuaPayload`。`JobConfig.lua.bytes` SHA-256 為 `948ae113aac92bbfa8774fe5a91f08ebbf36bf3d55a957f1d49f44a2257b1a0a`；`SkillConfig.lua.bytes` 為 `07a97aca31c74cceb850853660ce3e9c5ff2406c6e74dbe4fb06fbc879558339`。

本輪對目前 `SkillConfig` 重新進行靜態正規化與反編譯，確認四個一轉包裝技能的實際設定；沒有執行 Lua、啟動 client 或以合成結果代替原服行為。玩家按鈕驗收與原服包裝技能執行規則仍為 `UNKNOWN`。
