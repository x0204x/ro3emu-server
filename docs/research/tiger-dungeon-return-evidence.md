# 虎王 1022001 通關退場證據

## G 客戶端可證實部分

- `TaskConfig[10102200]` 進入 `1022001`，`DungeonQuestConfig[1022001]` 的末步是擊敗虎王 Eddga。後續 `10102210` 在公共場景 `10220` 的互動點 `40022001` 繼續。
- `Logic/Level/LC_DunGeon_Base.lua:513–537,1227–1240` 的 30403 handler 只派送 `DungeonSettlement`；`:1974–1982` 的 `EndDungeon` 呼叫 DAL。`Logic/Level/LC_DunGeon_Helper.lua:746–780` 的 `TryEndDungeon` 先關閉過場面板，再走目前場景或 DAL。`Logic/Asset/LC_DAL_Dungeon.lua:230–325` 可送玩家手動離開 30402。G 可讀 Lua 沒有虎王專屬自動退場呼叫，也未提供原服自動退場秒數。原服精確退場時序為 `UNKNOWN`。
- Lua 路徑相對於 `Client/ro3_Data/StreamingAssets/Recovery/LuaPayload`，行號對應同原始位元組的可讀反編譯。上述三份 `.lua.bytes` SHA-256 依序為 `1C5366B69C20E418B27A88229AA38370757A5BC22DDBDE19128635522D264C91`、`D9C01A3A3B4E58577150D7BF762FF3A5A35E967709B0E8F64B42E99476D9E841`、`152E5BE9BA191DC6A1CE46042160EC9926CD55B67F8918A2AF5B8BB44BB658AF`。

## 伺服器實作

- `DungeonService.SettleAsync` 在個人獎勵交易提交、`Completed` 任務回呼（5027）及 30403 送出後，才對 `TigerKingSceneEntry` 成功結算排程退場。其他副本、失敗結算、未提交獎勵不進入此流程。
- `(inferred)` 等 3 秒供結算畫面處理，再直接以 `InstanceService.LeaveAsync` 返回原公共場景；不虛構玩家的 30402 請求或回覆。
- 計時器再次核對角色、attachment、原房間、場景、載入狀態與 attempt。手動 30402、斷線或轉場先發生時不重複轉移。

## 測試覆蓋與驗收界線

- `DungeonTests.TigerReturn` 覆蓋 5027 在 30403 前、無 30402 自動回 `10220`，以及手動 30402 先發生時不重複傳送。
- `DungeonTests.Reconstructed` 的虎王故事案例覆蓋返回公共場景後的後續任務互動。
- 原始 G 伺服器封包時序與玩家實機演出驗收仍為 `NOT_RUN`；LAB 協定測試不證明原服退場秒數或玩家畫面。
