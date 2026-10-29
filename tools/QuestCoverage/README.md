# 全任務引擎覆蓋探針

此工具讀取指定的匯出資料，在 LAB 執行 `TaskService` 現有的 `Defs`、`Required`、`Supported`、`TargetsSupported`、`AcceptActionsSupported`、`ServerOpcode` 與 `TryDeliveryActions`，逐筆輸出結果。不能將 `true` 解讀成原服行為一致或玩家流程已通過。

工具不建立網路、資料庫、角色帳號或發獎交易。交付規劃在獨立 `GameLoop` 上，經正常 `OwnerRegistry.EnterAsync` 保留 LAB role key 後執行；未關閉或繞過 `AssertRole`。空白角色的背包與貨幣皆為零，故條件、NPC 距離、道具足夠、活動開放、實際入帳與任務畫面不在此驗證範圍。

為避免大量建構有副作用的遊戲服務，反射僅建立判定與規劃所需的最小唯讀相依。`TaskService` 私有結構若變更，工具應明確失敗，不應猜測結果。來源與載入組件會雜湊綁定，執行期間變更則拒絕輸出混合世代的報告。

```powershell
dotnet run --project tools/QuestCoverage --configuration Release --no-build -- <export-root> <output-json> <repository-root>
```

主專案有其他編譯或測試執行時，先協調建置時段。既有 Server 輸出符合本輪來源後，可用 `dotnet build tools/QuestCoverage -c Release -p:BuildProjectReferences=false` 建置探針；此選項不會重建 Server。
