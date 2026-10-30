# 元素箭矢：官方無限箭與補售歷史

目前以原專案 v1.0.24 為主。`CharacterService.UnlimitedArrow` 依 `PublicParamConfig.ArrowsItemId` 與 `ArrowsCanUseJob` 授權，弓箭手不必持有物品即可切換，回覆數量 `-1`。官方 `SkillCombatTests.ArcherStats` 也不再先購買火箭矢。沒有新增鋼鐵箭、閃電箭別名或擴充原件白名單。

## 已退役的 100 Zeny 政策

先前使用者指定的每支 100 Zeny 是本地政策（inferred），不是原服價格。以下只供辨識歷史交易、收藏與收據，不再啟用補售：

| 歷史商品 ID | 物品 ID | 歷史品項 | 歷史單價 |
| --- | --- | --- | --- |
| 9910212 | 1002012 | 火箭矢 | 100 Zeny |
| 9910213 | 1002013 | 水靈箭矢 | 100 Zeny |
| 9910214 | 1002014 | 銀箭矢 | 100 Zeny |
| 9910215 | 1002015 | 風靈箭矢 | 100 Zeny |
| 9910216 | 1002016 | 地靈箭矢 | 100 Zeny |

舊分類為 ShopConfig9910200／Shop102／Class10202／type1。`src/Shared/ArrowShopCatalog.cs` 保留此不可變歷史身分且 `Enabled=false`；客戶端安裝接線已移除，保留的相容入口與純計畫器也拒絕產生新列。共享檔仍在來源戳記閉包，日後變更會使預建 DLL 戳記失效。

## 原件證據與修正方向

唯讀來源為 `G:\Genesis Launcher\ROO_PC`。原件 ItemConfig 的 `_iAcquire=[1014]` 導向全域 Zeny Shop102／Class10202，但完整 ShopConfig 與 ShopCommodityConfig 沒有相應箭矢商品。兩表 SHA256 分別為 `8ec1ec6ddb1344b15ca25a0d295dd9773366377af59306f88882747dc68007ea`、`a33946e99eb6f0c84c1b46db9627faa648834d4e0d756342b5b4fe33146e2fc0`。單憑取得入口不能推定原服需要付費購箭；v1.0.24 已以原生選箭清單的無限數量修正這項誤解。

`ShopService.Catalogue` 現只查既有商品、分類，不再回退到補售配置。一般商城原始列、價格、條件與交易保留；凍結 GameData JSON 沒有被改寫。舊的購買計數與收藏留在資料庫，不刪除交易歷史，也不自行決定退款。同步只送現行配置可解析的商品，避免退役 ID 進入新客戶端收藏頁；有效的一般商品仍同步。

## 驗證範圍

自造 `ArrowShopTests` 覆核五個歷史 ID 拒購且不扣款、不建立計數；一般商品依原價購買、重啟後保留、付款／商品／計數 SQL 故障回滾，以及舊紀錄不再送到目前商品頁但仍保存在資料庫。`ArrowShopPlanTests` 覆核退役後不注入商店或商品，也不覆寫既有列。`GameDataTextMergeTests` 同時保護上游新 Raw 欄位 API 與既有繁中 resolver，避免合併後把中文文字改用英文索引。

本輪只完成來源整合與測試程式，統一建置及執行由主代理進行。原生選箭、數量顯示、商城收藏與重新登入的玩家端驗收仍為 `NOT_RUN`。
