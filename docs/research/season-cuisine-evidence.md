# 賽季料理身分與次數

權威客戶端為 `G:\Genesis Launcher\ROO_PC`。本項僅核對靜態 Lua 與服務端交易，不代表原生玩家驗收。

原生 `enum_FunctionType.LifeMagicFood` 為 3614；`FunctionConfig[3614]` 使用條件 2000214，其完整 literal 是 `[1002, 34]`，即 Base 34。Base 31 不顯示入口符合原配置。`UI_Sys_RoleAttributesWidgetCtrl` 以 `CheckFunctionIsOpen` 控制入口與開啟動作，再建立原生 `UI_Sys_SeasonCuisineWidget`，無須額外創造一套料理介面。

子面板 `_onClickClose` 呼叫父控制器 `ShowSeasonCuisine(false)`；原函式只切換元件，沒有同步父控制器的 `m_bSeasonCuisineVisible`。相容性修正先執行原生顯示／隱藏，成功後才更新旗標，避免關閉後再開需要多按一次。未更動解鎖條件，也未將原生「獲得」取得導引改成免費發放。升級後的 5056 通知已有受控 Bot 驗證，開窗跨級與玩家實際側欄操作仍須分開驗收。

`LC_DAL_LifeJob` 的 `LifeJobInfo.magicFoodTimes` 自動同步處理會更新 `LifeJobMagicFoodUseTimes`。`LC_LifeJobHelper.GetMagicFoodUseTimes` 讀該資產內各料理的已用次數，缺值回傳 0；`Lua_CFG_LifeMagicFoodAttrConfig.GetMagicFoodUseTimes` 讀目前有效配置的 `_kLimit`，`GetMagicFoodAttrs` 讀 `_kAttrs`，`GetAllMagicFoodInSeason` 只列出 `GetEntryPtr` 可解析的料理。原生料理面板使用「上限減已用次數」，不是每次開啟面板重新開始計算。

本輪原件 SHA-256：

| 原件 | SHA-256 |
| --- | --- |
| `Logic/Asset/LC_DAL_LifeJob.lua.bytes` | `9262a43a633300348d627efba35cbceb88ba4a33f1b37ddb7404c53899e399bb` |
| `Logic/LifeJob/LC_LifeJobHelper.lua.bytes` | `1481d0a0041a9e03e211af38dd75cc44b814eea0d70e6252fffffec0dc5ae8d2` |
| `Config/CFG/Lua_CFG_LifeMagicFoodAttrConfig.lua.bytes` | `17f5b2af527fa3a3df8883464280d8ca5f4b95957804b48a783fcb03d71b55f7` |

`SeasonService.AdvanceCycleAsync` 允許 PlayBook 101 轉入另一個賽季編號。原專案政策是「下次登入換季」，現有在線角色保留登入時捕捉的 `SeasonState`。舊的料理紀錄只保存 `FoodBook`，因此同一本 PlayBook 的新賽季不會重置。

`LifeJobService.RuntimeState.FoodSeasonId` 是本地持久化的賽季識別鍵（inferred），不是額外資產或食用餘額。新登入在 `SeasonState` 尚未載入前明確綁定目前全域賽季；進行中的操作與屬性則使用已捕捉的賽季，避免全域換季時先重置舊在線角色的次數。未改變全域賽季生命週期，也不新增週期掃描。

舊 JSON 缺少此欄位時預設為 0。首次遇到同 PlayBook／未綁定 PlayBook 的舊紀錄，保留食用次數再綁定當季，避免在無歷史身分可證時清除合法加成（inferred migration）。已有非零且不同的賽季編號，或已綁定不同 PlayBook，才會清除食用次數並沿用原有換季頭銜重置。所有變動先提交 `lifejob`、`life_restrict`、`life_runtime`，再由提交回呼更新記憶體；原有交易名稱與收據欄位未改，`RuntimeHash` 仍對完整 JSON 計算。

`LifeFoodSeasonTests` 使用完全自造料理與時鐘，涵蓋同季重登、同 PlayBook 跨季重登、舊在線角色的次數上限、舊 JSON 遷移、換季與食用的交易回滾，以及提交等待期間不得先發布次數或屬性。合成測試與原生 5010 畫面驗收分開；實際食譜、取得機率、原服伺服端的歷史遷移政策仍不能由這些測試證明。
