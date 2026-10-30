# 必做的原生契約

來源為 Genesis 2026.10.29.1 的實際客戶端唯讀原件。Lua 經正規化及 unluac 靜態反編譯；未執行遊戲、Lua VM 或原生匯出器。

| 原件 | SHA-256 | 已證實內容 |
| --- | --- | --- |
| `UI/Main/Controllers/UI_Sys_Main_MainMenuWidgetBaseCtrl.lua.bytes` | `4702a2ffe1f571f1359069f62e2b881af8eec1bc1604f33177e3a52999542444` | `m_kBTN_MustDo` 使用 `enum_FunctionType.DailyActivity` 與 `UI_Sys_ActivityPanel`，入口型別為 `UnlockCondition` |
| `UI/Activity/Models/UI_Sys_ActivityPanelModel.lua.bytes` | `9b9cf987fe8ecec70a36679d35fdf762c83c38038e9a76d44c062df6ac2465c8` | 讀取 `DailyActivityList`、`StageRewardReceiveMap`、`AllOpenFunctionInfo` 及活躍道具數；建立每日、限時與週曆清單 |
| `Logic/Asset/LC_DAL_DailyActivity.lua.bytes` | `20640e757e0251b87615ebfa349473178c0e74a7a9ee0c26235308c2b27a3f79` | `DecodeDailyActivity` 讀取 `activityId/taskCompleteTimes/startTime/endTime/activityState/activityInfo` |
| `Config/DataConfig/DailyActivityConfig.lua.bytes` | `e61d3e2ee5a9c157340d8c08367661f3d45129f9e27cd0afab2789c0331186db` | 60 筆有效活動列，含完整 defaults；宣告數 62 與實際列數不一致，未憑空補兩列 |
| `Config/DataConfig/GenericStageRewardsConfig.lua.bytes` | `404919ba148638eac9dab5cf90d7c1c0a5a25b2da1d1324bb5f31cf186f30f16` | 166 筆有效階段獎勵列；宣告數 169 的原因未知。類型 7 的五個活躍門檻為 20/40/60/80/100 |

`UI_Sys_ActivityCalender_BoxWidgetCtrl.SetData` 先查已領清單，再比較 `m_iDailyActivityValue >= _iScore`；按鈕呼叫 `LC_StageRewardHelper.GetStageRewardById`，送出 `PLAYER_STAGEREWARDGAIN` 的 `stageId`。批次 helper 使用 `stageIds`。`LC_DAL_StageReward` 以 5059 更新已領清單，領獎回覆處理函式本身不更新清單。

本輪完整還原 `FunctionConfig` 的 339 筆壓縮資料，入口 25 與目前有效資料一致；`LC_ConditionList[2000022]` 的直接 literal 是 `[4004,10105090]`。`LC_InteractCondition.CheckTask` 呼叫 `LC_TaskHelper.CheckTaskDeliveryed`，因此入口是交付條件，不是單看等級或任務達標。原生按鈕旁的鍵名屬輸入綁定；此輪未完成特定按鈕 InputAction 的唯一綁定證明，不把截圖看似 `L` 當作快捷鍵契約。

有效 `DailyActivityConfig` 與 `GenericStageRewardsConfig` 全列、全 defaults 與目前 LAB 資料均無差異；`DailyActivityGroupConfig` 的三列與入口條件亦相同。這表示本輪檢查的必做配置及入口已有實作，不能據此宣稱每個活動的原服流程全部相同。週一重置、追蹤任務重發、批領全有或全無等伺服器政策仍保留各服務的 inferred 標示。

靜態結果不代表玩家端驗收。每日／週曆切頁、限時倒數、快捷鍵、05:00 畫面更新、寶箱動畫、主線自然解鎖及各活動實際進度仍屬獨立驗收範圍。沒有新增封包擷取或原服操作。
