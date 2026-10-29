# 卡片培養與進階的回應及動畫證據

## 來源與範圍

本次唯讀比對 G 客戶端的 `Client/ro3_Data/StreamingAssets/Recovery/LuaPayload`，以
`D:/RO3 Asia Launcher/OfflineLab/decompiled_lua` 的可讀解碼檔定位行號。G 原始 bytes 與
`OfflineLab/runtime/client` 中同路徑的副本逐檔 SHA-256 相同；未啟動 G 客戶端。

| G Lua bytes 相對路徑 | SHA-256 |
| --- | --- |
| `Logic/Card/LC_CardServer.lua.bytes` | `3FC6B1D510F593D755F46C0BA2352E8F36CEC6BE09963F71468F32DEE9564316` |
| `Logic/Asset/LC_DAL_Card.lua.bytes` | `EB3BD82E2E671D13122616ADE6FC11F4F58029FD3689AA5F9C61073B8CA5AD1D` |
| `Logic/Asset/LC_DAL_Item.lua.bytes` | `C7E51BB36A0EDD239AFC57103273A177B8E3659DD155DD1FBFC831675FA724C2` |
| `Logic/Card/LC_CardViewHelper.lua.bytes` | `C0607FA48BE6A9442479D6E7EFB2652E5CDAA2159134F1B84630DE7D79DE8A1D` |
| `UI/Card/Controllers/UI_Sys_CardLevelUpWidgetCtrl.lua.bytes` | `52D356F5137615C9921F6F468A5353B2B83E655B4B131806F3C54FFAE5A1EEA4` |
| `UI/Card/Controllers/UI_Sys_CardAdvancedUpWidgetCtrl.lua.bytes` | `ACDA6B6C0E1E8255009BB066276CBECB678ED804E3FF616F85C53179C8CB2AF2` |
| `UI/Card/Models/UI_Sys_CardLevelUpPanelModel.lua.bytes` | `F27540D989151E1D5864C5B7BDD6ECB3970343DD7FA9B416A292299E4CE8457F` |
| `UI/Card/Controllers/UI_Sys_CardLevelUp_listWidgetCtrl.lua.bytes` | `3FB351954E649BBD568A6BC877A5B3DBA1FCF3360272A4E8305B200640EBD06E` |
| `UI/Card/Controllers/UI_Sys_Card_BondsPanelCtrl.lua.bytes` | `FE5E2335E4F163515FD174F757164DBEA4E29EDE3B18F89B0BB7DA9BBB185B07` |

## G 的卡片流程

- `LC_CardServer.lua:9-36,38-65` 的培養與進階請求都傳 `sourceType/guid/slotIndex/gridType`。
  `NW_Helper_PBDefine.lua:3047-3069` 對應 `CARD_LEVELUP=31461` 與 `CARD_STARUP=31462`；
  伺服器 `Protos/romsg/MG_Card.proto:88-119` 定義相同的請求及成功回應欄位。
- `LC_DAL_Card.lua:45-56,239-349` 將培養成功回應的 `newLevel` 寫回卡片並派發
  `Common.CardLevelUp`，將進階成功回應派發 `Common.CardStarUp`，附上來源與 `guid`。
- `UI_Sys_CardLevelUpWidgetCtrl.lua:155-187,321-333,376-444` 的進階成功動畫分兩條：
  包包來源在事件當下呼叫 `_onStarupResult`，裝備孔位來源先設
  `m_bRequestCardStarUp=true`，等待 `m_kCardStorage` 綁定的 `_updateCardSlot` 再呼叫。
  `_onStarupResult` 播 `UIAnim_CardLevelUp` 的 `Custom1`；動畫結束事件開啟進階結果面板。
  `UI_Sys_CardAdvancedUpWidgetCtrl.lua:114-176,468-547` 在結果面板播開場與星級動畫。
- `LC_DAL_Item.lua:68-78,227-292,572-625` 將 5009 的 `storagePack` 更新到
  `CardStoragePackInfo`；`UI_Sys_CardLevelUpPanelModel.lua:31-57` 把該資料集綁成
  `m_kCardStorage`。伺服器 `CharacterService.Sync.cs:141-153` 的 5009 包含孔位卡片資料。

## 已修的裝備卡片進階順序

先前 `CardService.StarUpAsync` 在成功提交後先送 5009，再送 31462。裝備孔位 UI 在收到
5009 時尚未設等待旗標；等 31462 設旗標後，這次進階不再有下一筆孔位卡片資料變動可
觸發 `_updateCardSlot`。這是從 G 事件鏈與伺服器發送順序推導的確定時序缺口；是否正好是
玩家這次看見的情形，仍待同來源卡片實機重現。

`CardService.cs:623-637` 現在只對**已成功提交、裝備孔位來源**先送 31462，再送
5009/5026/5057。包包來源維持資料先於回應，因其收到 31462 後立即打開結果面板並
讀取新卡片資料。失敗回應維持原路徑；沒有新增封包或重送 5009。

`CardPresentationTests` 用真實封包驗證兩種來源的回應欄位及相反的 31462/5009 順序；
現有 `CardProgressTests.SocketedCardUsesItsActualLevelAndTierInStats` 檢查裝備孔位進階的
等級、星級與屬性入帳。這些是 LAB 協定證據，不能代替玩家畫面驗收。

## 培養與剩餘界線

31461 培養成功回應與 G `LC_DAL_Card._processCardLevelUp` 所讀的欄位相符；
`CardPresentationTests` 也核對包包及裝備孔位的成功回應值。已核對的 Lua 中，
`Common.CardLevelUp` 在 `UI_Sys_Card_BondsPanelCtrl.lua:121-131` 用於刷新；
`UI_Sys_CardLevelUpWidgetCtrl.lua` 的動畫入口明確屬於進階 `CardStarUp`。

有界搜尋覆蓋 `UI/Card` 的 66 個 Controllers、14 個 Models、80 個 Views，查找
`CardLevelUp`、`RequestCardLevelUp`、`CardStarUp`、`PlayAll`、`RegisterEventCallback`、
Animator 與進度/培養字樣。候選結果如下：

- `UI_Sys_CardLevelUpWidgetCtrl.lua:8-21` 的 `m_kAllItems` 綁 `_refreshView`；
  `UI_Sys_CardLevelUpPanelModel.lua:33-58` 從 `KnapsackItems`／`EquipGridCardMap`
  更新此屬性。5009 的卡片變更因此會刷新培養面板。
- `UI_Sys_CardLevelUp_listWidgetCtrl.lua:71-88,90-139,159-180` 的屬性列表
  `SetData` 可以排程播放 `UIAnim_UI_Sys_CardLevelUp_list.Custom1`。主 Widget
  在 `UI_Sys_CardLevelUpWidgetCtrl.lua:1042-1052` 設列表資料；這是面板屬性列動畫
  候選，沒有從 31461 成功回應直接觸發的證據。
- `UI_Sys_Card_BondsPanelCtrl.lua:769-808` 的 `Common.CardLevelUp` 處理器
  刷新卡片羈絆顯示，該處理器沒有播放動畫。
- `UI_Sys_CardLevelUpWidgetView.lua:645-657` 確實持有 `UIAnim_CardLevelUp`
  Animator；控制器在 `:321-333` 的 `Custom1` 呼叫由 `CardStarUp` 路徑抵達。
  搜尋到的其他 Card `PlayAll` 分散在收藏合成、卡槽及卡片列表控制器，
  尚未建立與 31461 培養結果的直接邊。

全份既有可讀 Lua 樹中，`RequestCardLevelUp` 僅見 `LC_CardServer.lua:36` 的函式定義；
培養按鈕的實際呼叫者、Prefab Animator 事件及 G 服務端發送順序仍是
`UNKNOWN`。上述靜態覆蓋**不足以確證培養結果應播放哪一段動畫**，也不足以推斷
缺動畫由伺服器造成；因此未加入推測性的額外通知。

**實機驗收：NOT_RUN。** 需玩家在 G 原始客戶端分別以包包及裝備孔位卡片操作培養、進階，
記錄 31461/31462 結果碼與 5009 順序並觀察畫面動畫；特別確認裝備孔位進階是否於
成功回應後播放兩段動畫，以及培養是否原本具有結果動畫。
