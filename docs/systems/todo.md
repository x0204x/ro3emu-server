# 必做與活躍獎勵

「必做」由 `DailyActivityService`、`TimedTodoSchedule` 與各活動的實際進度服務共同負責。原生主畫面的必做按鈕開啟 `UI_Sys_ActivityPanel`，面板顯示每日必做、週曆、限時活動與活躍寶箱；不需要另外新增一個自製面板。

入口使用 `FunctionConfig[25]` 的條件 `2000022`：任務 `10105090` 必須已交付。任務仍在執行或只是達成目標，都不符合此條件。伺服器先送出每日活動與已領寶箱資料，才透過功能清單開放入口，避免原生面板讀到尚未初始化的資料。

| 訊息 | 用途 |
| --- | --- |
| 5004 `DBDailyActivityInfoIncrement` | 活動進度、開始／結束時間、活動服務投影的階段狀態 |
| 5059 `DBStageRewardInfoIncrement` | 共用階段獎勵的已領清單；初次空清單仍明確送出 |
| 5056 `FunctionOpenInfoIncrement` | 功能 25 的入口解鎖 |
| 31178 `StageRewardGainReq/Resp` | 領取單一活躍寶箱 |
| 31175 `StageRewardBatchGainReq/Resp` | 批次領取階段獎勵 |
| 43504 `CommonDailyActivityReq/Res` | 共用活動請求；委託接取與其他活動由各自服務分派 |

活躍值是道具／貨幣 ID `5`。`GenericStageRewardsConfig` 的類型 `7` 對應寶箱 `7001..7005`，門檻依序為 `20/40/60/80/100`。達到門檻不會扣除活躍值。實際任務獎勵由 `TaskService` 交易發放，必做服務只同步交付後的活動進度，不重複支付 `_iPerTaskPoint`。

領獎的資產變更與已領標記在同一筆交易提交。重送或重連後再次領取會被拒絕；批次請求去除重複 ID，全部資格成立才一次提交。交易失敗保留原狀供重試。

每日重置使用伺服器時區的 05:00；每週使用週一 05:00。週一規則與重置代碼的伺服器解讀屬明示推定（inferred）。重置同時處理活動進度、追蹤任務、活躍值與類型 7 的已領標記，保留其他永久階段獎勵。掛機／酒館已有舊日待發獎勵時，先由原進度服務完成舊日結算。

限時活動依 `DailyActivityConfig`、`DailyActivityCustomConfig`、`TimeLimitConfig` 與開服時間計算目前或下一個時窗。曆表顯示與可參加資格是兩件事：實際進場、戰鬥與獎勵仍由對應活動服務驗證，不能因為列在必做中就直接發獎。

直接相關回歸測試是 `DailyTests` 與 `DailyWindowsTests`。其中 `TodoEntryRequiresDeliveredQuestAndReadyDailySnapshots` 驗入口資格，`TodoReconnectPublishesDailySnapshotsBeforeUnlockedEntry` 驗重登的實際訊息順序；測試建立的歷史交付資料不代表玩家已完成主線驗收。原生按鈕、快捷鍵、面板視覺與每個活動的完整玩家流程仍需分別實測。來源與靜態範圍見 [必做證據](../research/todo-evidence.md)。
