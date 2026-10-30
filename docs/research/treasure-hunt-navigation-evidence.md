# 藏寶圖追蹤、導引與分線

主來源為 Genesis 140083 的 `LC_DAL_SeasonTreasure`、`LC_System_SeasonTreasure`、`UI_Sys_Main_AutoFindWidgetCtrl`、`UI_Sys_MainPanelModel`、`UI_Sys_SeasonTreasure_MapPoisonWidgetCtrl`、`LC_TriggerAreaManager` 與 `TreasureHuntMapPointConfig`。這些是靜態客戶端契約，不等於玩家端驗收。

## 使用與原生展示

`ItemConfig` 1112001／1112002 的 action304 透過 31323 使用道具；伺服器提交扣圖、冷卻與持久尋寶游標，再送5064。`DBTreasureInfo.pointId` 為tag1，增量的 `pointId` 為tag10。`LC_DAL_SeasonTreasure` 對 `TreasureInfo` 同時註冊完整及增量處理；增量只在欄名為 `pointId` 時呼叫 `_processPoint`。

`_processPoint` 將 `SeasonTreasureHunt={iPointId,bShowRange=false}` 寫入資產，非零時呼叫 `StartHunt` 並返回主畫面。這條 DAL 不讀 groupId／itemId／eventId 決定初始顯示。pointId 必須是點表的 `_iId`，不是場景的 `_iMapPointId`。

`StartHunt` 及 `_doStartHunt` 都要求點表的地圖等於目前地圖；`GetAreaCenter(map,area)` 也只查目前地圖。`_doStartHunt` 送 `CrossMapAutoFindPath` 後，`StartAutoPath` callback 才設定底部 `TreasureHuntAutoPath` 提示。進入尋寶區後，`_setEnterAreaState` 才將 bShowRange 設為true、通知範圍圈顯示。

因此，同圖沒有候選時改選外圖，會出現「已扣圖、有5064狀態，但原生導引未啟動」的靜態可達路徑。伺服器只在目前公開地圖的可解析配置點中選取；沒有本圖配置時回既有錯誤178，保留物品、冷卻與游標。隨機選點、公開房間限制及拒絕碼選擇屬 **(inferred)** 模擬器政策，並非已取得原服隨機演算法。若本圖有配置但伺服器無法解析位置，回功能未就緒，仍不扣圖。

本版普通圖87點分布9張地圖；珍奇圖3點均在10214（MapConfig名稱SID10080000020，繁中為「波利島」）。伺服器讀配置，不將這份地圖清單寫死。

## 與普通任務的差異

這條尋寶流程使用底部／頭頂提示及範圍圈。普通任務側欄監看 `AllTaskInfo` 等資料；`HiddenTreasureMapInfo` 合成1400010則屬OnePiece／裂隙事件，與action304的 `SeasonTreasureHunt` 不同。不得為了顯示側欄而注入普通任務、偽造領獎或挪用1400010。

## 同一地圖的分線與房間

`MonsterService.SpawnSingleAsync(long scene, ...)` 將配置地圖轉成公開第1線。尋寶必須使用挖掘者的 `RuntimeMap` overload；否則第2線玩家挖出的怪會在第1線生成。NPC／怪物投影及互動限制在公開分線，避免進入同配置地圖的私人副本。NPC有效期與活動游標使用同一個注入時鐘。

## 驗證範圍

實際資料的遊戲閘道測試涵蓋普通／珍奇圖無本圖點位拒絕且狀態不變、珍奇圖同圖接取、5064完整資產重送、登出重登入保持同一游標及只扣一張、實際第2線挖掘與怪物可見範圍隔離、私人房間不產生尋寶目標，以及既有挖掘重送／冷卻／失敗回復用例。舊版第2線測試會重現怪物位於第1線。

這些Bot／伺服器測試驗證收送、持久資料及場景隔離，沒有執行原生遊戲畫面。底部提示、頭頂顯示、進區範圍圈與實際尋路仍須玩家端驗收；本次也沒有變更既有跨圖游標、補發道具或更改原有獎勵機率。
