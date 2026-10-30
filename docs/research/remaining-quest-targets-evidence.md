# 剩餘任務目標與休眠事件來源

本輪權威來源為唯讀 `G:\Genesis Launcher\ROO_PC`，以官方已驗簽 sequence 140083 的逐檔 SHA 綁定。未執行原服、native Lua 或封包擷取。此文件區分條件設定、畫面追蹤器及已提交的伺服器事件；未找到靜態來源不等於原服沒有該功能。

## 現存範圍

提交 `8d0fc2d01a544eec6f45af02493c492970d5838c` 的實際引擎重查 1,072 個任務，54 個 tuple 不受一般任務判定支援：41 個有各自副本追蹤 owner，另 13 個如下。此輪沒有新增可安全啟用的通過條件，沒有減少計數或放寬拒絕。

| code | 任務 | 已確認 consumer／producer | 缺口與處理 |
| --- | --- | --- | --- |
| 103012 | 100015 | `LC_ConditionDefine.CheckMINIJoinCnt`；成長任務描述標為預留。`LC_MapModule_AOIUnit` 的 MINI=3 只映射小地圖類型。 | 未閉合參與及提交事件，不借用 MVP 計數。 |
| 103017 | 100020 | `LC_DAL_OnHook.OnGetOnHookAward` 發出 `RECEIVE_OFFLINE_REWARD`（43405）；畫面讀 `offlineRewards`。 | 獎勵產生、時間與一次性結算規則未閉合；不以請求本身計數。 |
| 103018 | 100021 | `GuildPrayConfig` 有成本／公會資金設定；本輪只找到 PB map 的 `GUILD_PRAY_SHOW`。 | 未找到請求→owner→提交鏈；不把 Valhalla／英靈祈禱當公會祈禱。 |
| 107102 | 1102001–1102003 | Task／Progress 設定為指定資源的採集；`3010` 是該目標的參數，不能當 NPC ID。 | 未找到條件執行註冊或 3010 的已提交採集 owner，不能接所有野外採集。 |
| 101023 | 1102021–1102023 | 舊 type25 任務描述要求擊敗其他公會玩家。 | 未閉合舊任務的接受、週期及擊殺結算來源，不接任意 PvP kill。 |
| 103048 | 1400010 | `LC_OnePieceHelper.GetOnePieceTaskId`；`LC_TaskHelper._getOtherClientTask` 讀 `HiddenTreasureMapInfo.bIsOpen/iNpcCount`，本地建立追蹤器。 | 已有來源 owner 是 `TimedBattleService` 的 Rift map 同步；一般 `TargetsSupported=false` 不代表少一個可領獎任務，不能另造 server accept。 |
| 101008 | 500021 | `LC_ClimbTowerHelper.SendSweepRequest`→32804；DailyActivityConfig104 可引用此任務。 | 230 筆 PetFloorConfig 的 `_iDailyReward` 皆為0，掃蕩獎勵與資格未閉合；首通／普通通關不得代替。 |
| 200002 | 9001501 | TaskConfig 指向2220102的開發流程；配置含目標與時間參數。 | 未有正式玩法 producer，不以無條件累計／自造計時實作。 |
| 210001 | 9001502 | 同2220102開發流程；未找到一般條件執行註冊。 | 未閉合 GM／階段入口及權威結算，不直接接受。 |

上述13筆的重複數量依序為1、1、1、3、3、1、1、1、1。閒置的兩個來源1901180／1901190另列，不重複算入13筆。

## 領土戰不是同一套任務 owner

`LC_DunGeon_TerritoryWars.OnLoadSceneConfigs`（fresh output 361–379）從場景 `DungeonQuest` 靜態參數建立 `TerritoryWarsDungeonQuest` 目錄。`UI_Sys_Main_TeamUp_TerritoryWarsTaskWidgetBaseCtrl.OnUpdateTerritoryWarsDungeonTaskQuest`（155–202）依階段及攻守資格查此目錄，再呼叫 `Lua_CFG_DungeonQuestConfig.GetQuestInfo`。

`Lua_CFG_DungeonQuestConfig.GetQuestInfo`（100–165）以 `DungeonQuestConfig._iDungeonID/_iQuestID` 建索引；`_getQuestInfoByID` 將 `_kQuestGoal` 的參數索引加1後交給畫面。這些是另一套目錄及副本變數 consumer，沒有證據把它們等同舊 TaskConfig type25 的接受與交付。不得因畫面名稱相同就把舊六筆目標接到新副本目標計數。

再次追查入口時，actual 原件仍有另一條舊任務畫面鏈：`Lua_GameDefine.enum_TaskType.TerritoryWarsSubTask = 25`；`UI_Sys_Main_TeamUp_FactionTaskWidgetCtrl._refreshSubTaskList` 從 `AllTaskInfo.TaskInfos` 篩選此類型，交給 `UI_Sys_Main_TeamUp_FactionTaskListWidgetCtrl` 顯示；`LC_TerritoryWarsHelper.GetTerritoryWarsTaskProgress` 也直接讀該任務的 `completeNum[1]`。因此不能把「另一套 DungeonQuest 不能代替」寫成「type25 沒有 consumer」。這條顯示鏈需要伺服器先送入任務，尚不能證明其接受、重置、採集或擊殺結算時點。

六個 type25 設定列的接取模式是 `[0]`（`TaskReciveType_Other`），沒有前置任務；三個預留 type6 與本地 Rift type14 的接取模式皆為空。既有一般 31202 只接受 Npc／UI 模式，不能靠傳入這些 ID 啟用玩法。Rift 的鏈則已閉合：83350 `HiddenTreasureMapInfoNotify` → `LC_DAL_OnePiece._processTreasureMapInfo` → `bIsOpen/iNpcCount/iEndTime` → `LC_TaskHelper._getOtherClientTask` 本地建立1400010；不應另建一般可領獎任務。

## 兩個尚無本地 producer 的 Connect 事件

1901180／1901190 的目標形式皆可解析，指向9300201。`OnHookDungeonConfig` 只在 `_kDungeonBuff` 中引用該 NPC；第一列的 `_iDungeon` 是2100004，本輪 MapConfig／DungeonConfig 及 Lua module inventory 都沒有相應地圖／副本定義。`LC_DAL_OnHook` 的冒險 UI 及43405請求仍存在，因此不能把缺少本地地圖推成原服功能已移除。

本地尚無這個離線冒險的已提交模擬／結算 owner；MVPConfig 沒有證明這是普通世界 MVP。兩個條件維持既有支援形狀與未完成狀態，不用 GM spawn、任意世界 MVP 或組隊閒置分鐘代替。

## 調查與驗證邊界

- 唯讀 SQLite 索引涵蓋13,491個 actual Lua、2,026,675個常數；本輪保存全部指定 code／名稱的命中與有界模式查詢，不只取前幾筆當陰性證據。
- 27個設定／DAL／helper／UI／network模組從 actual 原始位元組重新 normalize 及反編譯，全部SHA符合已驗簽140083。來源、工具、輸出SHA及逐行證據保存在本輪 LAB 收據，不提交客戶端檔案。
- REA 對 actual `Assembly-CSharp.dll`（SHA `5627e799d0b8d1c0ab5843d3149cdeb9c89425daa1442a36253a351fbc207bbe`、MVID `e110a0b2-21ac-4fc3-a019-467cdaaaa9a1`）的 `LC_OnHookHelper` 取得9個方法完整CIL、14條call edges；內容是SDK／網路／FPS輔助，沒有閉合離線獎勵結算。未對未選類別作不存在宣稱。
- 獨立 Coverage helper只引用既有最終Server DLL（SHA `8e87a1705792b2005799088cbb0bf9b3caa2c12a58c0ef3db6f6915f488f4dc6`），執行既有判定與交付規劃；不啟動網路、開資料庫或提交獎勵。建置0警告／0錯誤，完整1,072筆判定輸出成功。
- 本輪只修正來源說明的過度斷言，沒有新增持久欄位、schema、目標支援、發獎、時間公式或事件計數。沒有行為修改，因此不為註解新增鏡像測試；玩家及原服端驗收仍 `NOT_RUN`。
