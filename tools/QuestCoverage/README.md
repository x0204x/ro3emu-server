# 全任務引擎覆蓋探針

此工具讀取指定的匯出資料，在 LAB 執行 `TaskService` 現有的 `Defs`、`Required`、`Supported`、`TargetsSupported`、`AcceptActionsSupported`、`ServerOpcode` 與 `TryDeliveryActions`，逐筆輸出結果。不能將 `true` 解讀成原服行為一致或玩家流程已通過。

工具不建立網路、資料庫、角色帳號或發獎交易。交付規劃在獨立 `GameLoop` 上，經正常 `OwnerRegistry.EnterAsync` 保留 LAB role key 後執行；未關閉或繞過 `AssertRole`。空白角色的背包與貨幣皆為零，故條件、NPC 距離、道具足夠、活動開放、實際入帳與任務畫面不在此驗證範圍。

為避免大量建構有副作用的遊戲服務，反射僅建立判定與規劃所需的最小唯讀相依。`TaskService` 私有結構若變更，工具應明確失敗，不應猜測結果。來源與載入組件會雜湊綁定，執行期間變更則拒絕輸出混合世代的報告。

```powershell
dotnet run --project tools/QuestCoverage --configuration Release --no-build -- <export-root> <output-json> <repository-root>
```

主專案有其他編譯或測試執行時，先協調建置時段。既有 Server 輸出符合本輪來源後，可用 `dotnet build tools/QuestCoverage -c Release -p:BuildProjectReferences=false` 建置探針；此選項不會重建 Server。

## 外部追蹤與149筆缺口分類

新欄位 `configured_external_owners`、`owner_kind`、`owner_status`、`owner_evidence_kind`、`owner_evidence` 及
`external_tracker_scene` 與原 `engine_targets_supported` 分開輸出。探針不會為了降低未支援數量而改寫正式判定。

- `external_dungeon_tracker`：實際唯讀呼叫 `TaskService.DungeonTaskChain(scene)`，列出正式helper映射的任務；
  source owner是 `DungeonRunTasks`／`ExtraTasks`。沒有呼叫 `OnDungeonRun`、建立fake attachment或執行場景。
- `client_derived_tracker`：G `LC_OnePieceHelper.GetOnePieceTaskId` 決定1400010，
  `LC_TaskHelper._getOtherClientTask` 以 `HiddenTreasureMapInfo` 建立畫面任務；這是G來源契約註記，不是server交付。
- `development_task`：G賽馬9001501／9001502，場景2220102，保留目前沒有映射server producer的界線。
- `stored_quest`：可找到設定接取入口；`UNKNOWN` 表示探針沒有列到接取owner，不能把TaskDef存在當作可接取。

ExploreNode／MultiverseSettlementTask所列的任務也補上 `configured_accept_owners`，只說明設定入口，並未執行角色條件、
活動狀態或任何Accept方法。所有新分類均為 `SOURCE_AND_CONFIGURATION_ONLY`，runtime與玩家驗收仍 `NOT_RUN`。
副本helper映射不等於副本波次、弱點、鉤爪或原服動畫已完成；它們的實際producer仍須獨立驗證。

本輪149清單的完整分類由LAB `classify_149.py <fresh-probe-json> [output-directory]` 讀取新probe產生，
保留同步前149個ID及generic判定前後差異。工具來源、TaskConfig／ExploreNode／Settlement型別view也一起記錄SHA，
避免修改分類器後仍沿用舊組件收據。
