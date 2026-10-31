# 藏寶圖追蹤、導引與分線

主來源為 Genesis 140083 的 `LC_DAL_SeasonTreasure`、`LC_System_SeasonTreasure`、`UI_Sys_Main_AutoFindWidgetCtrl`、`UI_Sys_MainPanelModel`、`UI_Sys_SeasonTreasure_MapPoisonWidgetCtrl`、`LC_TriggerAreaManager` 與 `TreasureHuntMapPointConfig`。這些是靜態客戶端契約，不等於玩家端驗收。

## 使用與原生展示

`ItemConfig` 1112001／1112002 的 action304 透過 31323 使用道具；伺服器將扣圖、冷卻與持久尋寶游標同筆提交，先送使用回覆，再送5064，避免導引被後到的道具使用事件打斷。`DBTreasureInfo.pointId` 為tag1，增量的 `pointId` 為tag10。`LC_DAL_SeasonTreasure` 對 `TreasureInfo` 同時註冊完整及增量處理；增量只在欄名為 `pointId` 時呼叫 `_processPoint`。

`_processPoint` 將 `SeasonTreasureHunt={iPointId,bShowRange=false}` 寫入資產，非零時呼叫 `StartHunt` 並返回主畫面。這條 DAL 不讀 groupId／itemId／eventId 決定初始顯示。pointId 必須是點表的 `_iId`，不是場景的 `_iMapPointId`。

`StartHunt` 及 `_doStartHunt` 都要求點表的地圖等於目前地圖；`GetAreaCenter(map,area)` 也只查目前地圖。`_doStartHunt` 送 `CrossMapAutoFindPath` 後，`StartAutoPath` callback 才設定底部 `TreasureHuntAutoPath` 提示。進入尋寶區後，`_setEnterAreaState` 才將 bShowRange 設為true、通知範圍圈顯示。

因此，同圖沒有候選時改選外圖，會出現「已扣圖、有5064狀態，但原生導引未啟動」的靜態可達路徑。伺服器只在目前公開地圖的可解析配置點中選取；沒有本圖配置時回既有錯誤178，保留物品、冷卻與游標。隨機選點、公開房間限制及拒絕碼選擇屬 **(inferred)** 模擬器政策，並非已取得原服隨機演算法。若本圖有配置但伺服器無法解析位置，回功能未就緒，仍不扣圖。

本版普通圖87點分布9張地圖；珍奇圖3點均在10214（MapConfig名稱SID10080000020，繁中為「波利島」）。伺服器讀配置，不將這份地圖清單寫死。

## 有效進度的重送與恢復

`LC_AssetManager` 在5064的 `syncAllMark` 為true時清除快取，改走 `AutoSyncAll`。完整處理器會再處理非零 `pointId`；`_processPoint` 先結束舊的本地尋寶狀態，再設定 `SeasonTreasureHunt`、呼叫 `StartHunt`。因此，即使point與版本相同，完整同步也能重新觸發同圖原生導引；只有值未變的普通增量不保證執行此流程。

道具回覆另受 `LC_DAL_Item._processItemUse` 處理：`ItemId` 為nil或0時，會在更新道具冷卻與效果前返回；僅將 `ItemCount` 設為0不能抑制效果。模擬器恢復分支採 **(inferred)** 的成功回覆：沿用要求中的GUID，`ItemId`／`ItemCount` 為0，再重送既有尋寶游標的完整5064。快捷列使用31337自己的回覆後再同步，不額外偽造31323回覆；快捷列本身的介面冷卻與道具的持久冷卻是不同狀態。

恢復既有進度仍須通過角色、道具歸屬與鎖定、數量、功能及公開場景檢查。恢復分支不重新消耗圖、不重抽目標、不延長原期限，也不改寫持久道具冷卻、使用次數或待結算怪物獎勵身分。回覆後的投影回呼會重核角色／連線世代、尋寶身分與場景可用狀態，避免舊回呼覆蓋新進度。這是利用已證實客戶端消費路徑提供的相容處理，並非已擷取的原服重按規則或封包。

## 與普通任務的差異

這條尋寶流程使用底部／頭頂提示及範圍圈。普通任務側欄監看 `AllTaskInfo` 等資料；`HiddenTreasureMapInfo` 合成1400010則屬OnePiece／裂隙事件，與action304的 `SeasonTreasureHunt` 不同。不得為了顯示側欄而注入普通任務、偽造領獎或挪用1400010。

`TreasureHuntTracker` 提供模擬器的相容性追蹤列，讀取原生 `SeasonTreasureHunt`，以獨立widget放入任務清單容器，不加入一般任務陣列或建立 `TaskInfo`。同圖點擊只呼叫原生 `StartHunt`；異圖只顯示目的地提示，不發起傳送、接任務、使用道具或領獎。顯示文字使用隨附的繁中覆寫；尋寶結束、控制器關閉或延後建立的widget世代失效時，追蹤列會取消並回收。

PC／行動版原生任務清單prefab的容器具有啟用中的垂直版面與內容尺寸調整元件，可作為靜態版面依據。這不等於追蹤列已在玩家畫面顯示或完成捲動／點擊驗收，也不能將模擬器相容性追蹤列宣稱為原服一般任務生產流程。

## 同一地圖的分線與房間

`MonsterService.SpawnSingleAsync(long scene, ...)` 將配置地圖轉成公開第1線。尋寶必須使用挖掘者的 `RuntimeMap` overload；否則第2線玩家挖出的怪會在第1線生成。NPC／怪物投影及互動限制在公開分線，避免進入同配置地圖的私人副本。NPC有效期與活動游標使用同一個注入時鐘。

## 驗證範圍

`ActivityAdventuresTests` 與尋寶流程測試涵蓋普通／珍奇圖無本圖點位拒絕且狀態不變、珍奇圖同圖接取、使用回覆與5064順序、冷卻期間重按不再扣圖、完整資產重送、登出重登入保持同一游標及只扣一張、實際第2線挖掘與怪物可見範圍隔離、私人房間不產生尋寶目標，以及既有挖掘重送／冷卻／失敗回復用例。舊版第2線測試會重現怪物位於第1線。`TreasureHuntTrackerTests` 以純邏輯測試追蹤顯示、同圖／異圖行為與延後widget世代取消。

這些Bot／伺服器與純邏輯測試驗證收送、持久資料、場景隔離及追蹤決策，沒有執行原生遊戲畫面。底部提示、頭頂顯示、進區範圍圈、追蹤列顯示／捲動／點擊與實際尋路仍須玩家端驗收；恢復導引也不會重設既有跨圖游標、補發道具或更改原有獎勵機率。
