# G 組隊打怪 Base／Job 經驗值證據

狀態：`partial`。G 原件與同雜湊離線匯出可證實客戶端配置、提示及入帳顯示；
原服權威結算公式與實際玩家入帳仍 `UNKNOWN`。以下 Lua 路徑相對於
`G:\Genesis Launcher\ROO_PC\ro3_Data\StreamingAssets\Recovery\LuaPayload`，行號對應相同原始位元組的可讀反編譯。

## 來源

- `Logic/Team/LC_TeamHelper.lua.bytes` SHA-256：`025F733A50EB687572A64EDC89D0DBAAD818680FF8D188781F44D3377706C716`。`GetMemberOnlineState` 位於可讀 Lua `:738–839`，五席分組位於 `:178–221`。
- `Config/ComplexConfig/PublicParamConfig.lua.bytes` SHA-256：`0A227E020DFC494D77CACB927D811F3F6299C9BFD0B455566BC6F03D2E0D1270`。`TeamExpBuffNum` 位於 `:1401–1413`、`TeamBuffInfo` 位於 `:1416–1440`、`RoleFOVMax` 位於 `:9372–9374`、`TeamAloofLimit` 位於 `:9985–9986`。
- `UI/Team/Controllers/UI_Sys_TeamEXPTipsWidgetCtrl.lua.bytes` SHA-256：`E91747F2688EBF734B54E064C75619FA4B6B521B3E9BBF988A987AA1F8A60F51`。`:90–139` 依自己所屬五席計數非零 GUID 並顯示人數加成。

## 客戶端與伺服器對照

| 項目 | G 可證實內容 | 我們目前伺服器 |
| --- | --- | --- |
| 人數加成 | `PublicParamConfig.TeamExpBuffNum=[10000,12500,15000,17500,20000]`，目前 `TeamBuffInfo` 提示 1–5 人 Base/Job +0/25/50/75/100% | `PublicParamConfig.TeamExpBuffNum` 具名 reader 讀取五個級距，`TeamService.ExpBonus` 依合格名單選值；不再硬編碼。缺省手工配置使用中性倍率是 inferred fallback，格式錯誤則拒絕。 |
| 40 人團 | UI 只按自己所屬固定五席（1–5、6–10 等）計算提示 | `ExpGroupRange` 依同樣五席選擇，不以 40 人全部計。 |
| 場景與位置 | 新提示寫同一野外地圖；UI 人數計算本身沒有檢查地圖、分線、距離。隊員 `Faraway` 顯示可由不同地圖／分線或超過 `RoleFOVMax=55 m` 觸發；這是 UI 狀態。`TeamAloofLimit=50` 未找到發獎呼叫。 | 擊殺者及同五席、在線、角色已載入且附著、同一 public field `WorldScene`、非副本；沒有距離、死亡、貢獻判定。因此同場景隊友超過 55 m 仍符合目前分享資格，甚至 UI 可能顯示 `Faraway`。這是自訂的資格規則，未證實符合 G 原服。 |
| EXP 結算 | G 客戶端接收權威 `5066 RoleBaseInfoIncrement`，更新 Base/Job 欄位；無可證實的客戶端分攤公式。 | 怪物掉落中虛擬 Base EXP 項目 100 先依擊殺者等級差修正，再乘隊伍加成；Job EXP 取加成後、追趕加成前的 Base 值。Base 另經擊殺者追趕加成；之後兩種 EXP 各經伺服器倍率縮放、平均分給資格名單，餘數依席位順序分配。此公式為 **inferred**。 |
| 發放 | 5066 可顯示每位玩家自己的 Base/Job 變動；原服交易邊界未知。 | 擊殺者份額隨擊殺交易提交；其他隊員先持久化個別義務，後續冪等發放，斷線可於登入後恢復。 |
| 掉落 | 提示另寫 1–5 人掉率 +0/30/60/90/120%，舊文案 24043 寫 +20/30/40/50% 並與現行提示衝突。 | 目前沒有依 `TeamDropBuffWeights` 組隊加成掉落；不應把掉落提示當作已完成的實測規則。 |

G 原始 `LvExpCorrectionConfig` 有等級差係數，但未找到其在原服組隊發獎流程的
呼叫。原服是否等分／每人全額、先乘後分、取整、死亡／距離／貢獻與個別等級差，
均為 `UNKNOWN`。舊文案 24043 的 20/30/40/50% 不用於目前 UI 提示。

**遠離隊友結論：**現行伺服器只排除不同 `WorldScene` 或不在線等情況，
不以 50 或 55 m 截斷組隊經驗值；G 客戶端的 `Faraway` 與 `RoleFOVMax`
屬顯示／視野狀態，沒有證據可當作原服經驗值半徑。

現有測試 `TeamExpTests` 覆蓋五席分組；`DungeonTests.PartyExperience` 以合成
擊殺回饋驗證同場隊員 Base/Job 變動及 5066。它不驗證原服公式、真實怪物掉落、
離線重試或玩家畫面。原服與玩家端雙角色擊殺樣本仍 `NOT_RUN`。


## 本輪補驗與邊界

2026-10-30 重新以 actual G 原件 SHA 綁定並靜態解碼。舊文件的 PublicParamConfig
`870E6C…` 屬舊來源，不能當作本輪原件；目前是上列 `0A227E…`，五個倍率仍相同。
來源與逐檔前後雜湊見本機 `Genesis-Remaining-Static-LAB-20261030/team-exp` 證據；不把
同雜湊的其它檔案擴稱整個客戶端全相同。

新 `PartyExperiencePipelineTests.Eligibility` 沿既有自造 drop chain 補現行政策的資格檢查：
超過視野距離、死亡與不同個人等級目前不排除；不同分線（相同 SceneEntry、不同 WorldScene）、
載入中、私人副本、撤銷 attachment 或未載入進度則排除，並依合格數讀自造的配置倍率。
這些是本地政策的 regression contract，**不是 G 原服資格規則已獲證明**。
另以自造 Base×2／Job×3 驗倍率在分帳前只套一次與席位餘數；擊殺交易凍結份額後撤銷收件人
租約，再重啟／登入／重試，要求同一來源只入帳一次。所有數值是合成測試，未複製 G 配置列。
本輪組隊、語系與頭銜相關的 36 項 LAB 測試已通過；這不代表原服分帳公式已完成玩家端驗證。

5066 經 `NW_AutoSyncClient_Define` 的 `romsg.RoleBaseInfoIncrement`、
`LC_AssetAutoSyncDataDefine` 的 `lc_dal_playerbasicinfo` 路由、
`LC_DAL_PlayerBasicInfo` 的 RoleBaseInfo 全量／差分監聽，更新 `exp`／`jobExp` 並通知UI。
這能證明權威結果的接收與顯示，不能反推出擊殺者或每位隊友的等級修正公式。
本輪 target-free Managed metadata/CIL 精確名稱搜尋未找到這些 Lua 名稱的直接 bridge；
`HappyEngineClient` 亦不是已確認的 Assembly-CSharp 宣告型別，該 Managed bridge 保留 UNKNOWN，
不以未命中名稱宣稱程式不存在或公式已完整逆向。
