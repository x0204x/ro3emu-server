# 虎王 1022001 通關與後續任務證據

## G 來源與可證實的任務順序

- 本輪以 `G:\RO3 Asia Launcher\Client\ro3_Data\StreamingAssets\Recovery\LuaPayload` 原件唯讀核對。`Config/DataConfig/TaskConfig.lua.bytes` SHA-256 為 `DBC9F157A40957D38BD9F97480C8A7B9EB3786B08A46EFCBE7CC4BD7316528A1`；可讀副本為 `D:\RO3 Asia Launcher\Live-Validation-LAB\G-Full-Audit-20261029\lua-decompiled\Config\DataConfig\TaskConfig.lua`，逐檔來源繫結記於同目錄 `lua-decompile-binding.json`。`PlotInterpretation.lua.bytes` 與 `DialogueConfig.lua.bytes` 原件 SHA-256 分別為 `2FB3A575840A1A47C3671EA1B2D7A1AD9E9792A55011AA188EB1AE2ED18EDEB6`、`D8713C8880B689E67495458D9F8286AEAC5767C731DD97FC2DF45F3B719B706E`。
- `TaskConfig.lua:42477–42544`：`10102200` 接受動作 `3000` 進入 `1022001`，完成條件是通關 `101018`。`DungeonQuestConfig[5300000,5300001]` 是兩段副本任務，末段擊敗虎王。
- `TaskConfig.lua:42550–42613`：`10102210` 的前置是 `10102200`；目標 `103046` 為在公共斐揚森林 `10220` 操作 `40022001`。`NPCInteractConfig[40022001]` 的後續動作 `46` 回報互動；任務完成動作 `345` 對 `1253018` 播放特殊物品取得演出。`ActionConfig[345]._iEXECHost=0`，該演出由客戶端執行。
- `TaskConfig.lua:42615–42690`：`10102220` 的前置是 `10102210`，在公共 `10220` 的 `1010222001` 任務點完成同 ID 對話。`DialogueConfig[1010222001]` 有六句。任務完成動作 `141` 指向 `10200/1010222101`；`ActionConfig[141]._iEXECHost=0`，客戶端透過 `30131` 跳轉城中。
- `TaskConfig.lua:42692–42751`：`10102230` 的前置是 `10102220`，目標是劇情 `130121` 結束；`PlotInterpretation.lua:824–844` 的 `130121` 觸發條件為 `[10200,1010223001,10102230]`，播放 `StoryData_1003`，之後才接 `10102240`。因此 `130121` 是**轉城後**的另一段劇情，不能拿來作虎王倒下後立即退場或虎皮演出的計時門檻。

## 影片與伺服器邊界

- `D:\RO3 Asia Launcher\Video-Mechanics-Audit-20261029\quest-animation-flow.md:21` 及 `cutscene_1fps/tiger_reward/sheet_000.jpg`、`sheet_001.jpg` 顯示：22:48 左右虎王倒下，22:53–59 精靈對話，23:00 虎王遺體再次出現，23:06–10 虎皮獎勵特寫，23:13–17 角色對話，23:19 載入，23:20 到城中。結合任務表，23:19 載入對應 `10102220` 的轉城動作；影片無法證明 `1022001 → 10220` 私有副本退回公共森林的精確秒數或封包時點。
- `Logic/Level/LC_DunGeon_Base.lua:1227–1240` 收到 30403 僅派送 `DungeonSettlement`；`:1976–1982` 的 `EndDungeon` 呼叫 DAL 退場。`Logic/Level/LC_DunGeon_Helper.lua:747–780` 的 `TryEndDungeon` 先關閉過場面板，再由場景或 DAL 退場；`Logic/Asset/LC_DAL_Dungeon.lua:295–321` 可送玩家 30402。這些 Lua 未給虎王特定的自動退場時刻。
- `DungeonService.SettleAsync` 在個人獎勵交易、任務 `5027` 與 `30403` 完成後排程 `TigerKingReturnDelay=3s`；`DungeonService.TigerReturn.cs:7–25` 已標 `(inferred)`，且再次檢查角色、連線、房間、場景，避免手動離場後重複轉移。**3 秒是否與 G 原服一致仍為 `UNKNOWN`**。不能把 23:19 城中載入誤當此計時器已被證偽，也不能把 LAB 三秒成功當成原服驗證。

## LAB 回歸與剩餘驗收

- `DungeonTests.TigerReturn.cs` 已測 `5027` 在 `30403` 前、成功後回公共 `10220`、手動 30402 先發生時不重複轉移。
- `DungeonTests.TigerChain.cs` 的 `ClientDataFact` 以 LAB 角色準備與速殺，任務進度則依序走 `31214` 虎皮互動、`31208` 對話、`30131` 轉城、`84011` 劇情結束，並核對 `10102240` 開始。2026-10-29 定向 Release 測試 `1/1 PASS`。此測試只證明伺服器協定與任務狀態，不能代替客戶端播放虎皮特寫、對話、城中劇情的畫面驗收。
- 原服 `1022001 → 10220` 的退場請求／通知時序為 `UNKNOWN`；原 G 遊戲目前依使用者要求待稍後再開，原服封包擷取 `NOT_RUN`。正式玩家端的虎皮動畫、轉場、跳過、重連與獎勵只發一次仍待同一角色完整實機驗收。
