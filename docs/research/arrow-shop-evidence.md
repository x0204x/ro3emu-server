# 元素箭矢補售

來源為 `G:\Genesis Launcher\ROO_PC`。這是使用者明確指定「每支 100 Zeny」的本地補售政策（inferred），不是由原件還原的原服售價。

本輪只補原生選箭清單可使用的五種元素箭：火箭矢 1002012、水靈箭矢 1002013、銀箭矢 1002014、風靈箭矢 1002015、地靈箭矢 1002016。鋼鐵箭矢 1002011 存在物品配置，但不在原生 `PublicParamConfig.ArrowsItemId` 清單；`UI_Sys_Main_UnlimitedWidgetBaseCtrl._refreshArrowItemList` 只迭代該清單，所以此次不販售鋼鐵箭，也不擴增選箭白名單。「閃電箭」的物品身分仍未確認，不建立別名或新物品。

## 原件入口與未售原因

元素箭的 ItemConfig `_iAcquire=[1014]` 對應 `ResourceObtainInfoConfig[1014]`，其 `_iFunction=70104`，`_kParam=['25','70104','TOPBAR','UI_Sys_ShopPanel','1','102','10202']`。`LC_ResourceObtainHelper` 取參數後交給 `LC_InteractHelper` 的 action25，開啟全域 Zeny 商店 type1／Shop102／Class10202；不是 NPC301。

目前原件完整 ShopConfig 與 ShopCommodityConfig 有效值中，既沒有 Shop102／Class10202，也沒有七種箭矢物品的販售列。兩張表原件 SHA-256 分別為 `8ec1ec6ddb1344b15ca25a0d295dd9773366377af59306f88882747dc68007ea`、`a33946e99eb6f0c84c1b46db9627faa648834d4e0d756342b5b4fe33146e2fc0`；與目前 LAB 配置逐欄零差異。這次同時補商店與商品，不把缺少分類誤當成伺服器漏發商品封包。

Function70104 與其條件2000279仍存在，條件為 Base10。補售沿用原有功能判定，沒有強制解鎖。商店名稱沿用該功能名稱 SID10160000268。

## 補充目錄與上游優先

`src/Shared/ArrowShopCatalog.cs` 由 Server 與 ClientHooks 編譯同一份純 C# 目錄：ShopConfig9910200、Shop102、Class10202、type1、付款物品1、單價100；補充商品 ID9910212–9910216 對應物品1002012–1002016。這些商品 ID 是本地保留身分，不是原服商品 ID。

原有同商品 ID、同物品、同 Class10202 的列優先採原價與原條件；若上游在 Class10202 以另一商品 ID 販售同物品，不再增加補充列。其他 Class 的同物品不會消滅 Zeny 商店的補售。補充 ID 撞不同物品或不同 Class 時拒絕補售；ShopConfig9910200 撞不同 Class，或既有 Class10202 指向不同 shop ID／type，也拒絕補售。原始資料沒有被覆寫。

Server 的 `ShopService.Catalogue` 只保存 host 自有、完整原生形狀的配置列，經既有 typed views 驗證物品為 type2／subtype207、付款物品為貨幣。所有購買、計數、收藏與舊計數遷移共用同一個查找入口。GameData 與凍結的資料目錄不變。

這是全域 type1 商店，使用既有 `cd13a/shop-buy` 家族交易，不套 NPC type3 的 E01 身分。付款、商品與 shop 次數仍在同一交易中提交，提交後才更新記憶體。未新增持久化欄位，也未改 DbPlan、收據欄位、既有 operation／source 語意。兩次相同的玩家購買仍是兩筆購買；內部重試才沿用既有交易身分。

ClientHooks 以同一目錄補入記憶體 ShopConfig／ShopCommodityConfig，沿原生 OnInitDatas 重建索引並保留 ReLoadAllCfg 生命週期；沒有修改 signed Lua／proto。只改 Server 無法使原生畫面列出新商品，因此雙端要一起更新。31324 既有切箭與戰鬥元素讀取沒有改動，也沒有自行新增箭矢扣量規則。

## 驗證界線

`ArrowShopTests` 使用自造資料，只沿用共用產品身分：五箭真實 82340 批買、每支100且忽略低報 quote、31324 切換、82333 收藏、5024／重啟保留、SQL 故障整筆回滾與重試、原列優先、Class 範圍及 ID／物品型別碰撞。自造開放條件使用 Base3，驗證服務讀取條件配置而非硬寫 Base10；真實門檻仍由 Function70104 控制。

以上合成測試與靜態接線不能代替原生商店、取得入口跳轉、重新登入／換季後選箭的玩家端驗收。鋼鐵箭原生選單支援、閃電箭身分與原服售價仍為未確認事項。
